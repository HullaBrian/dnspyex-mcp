#!/usr/bin/env python3
"""stdio MCP bridge for dnSpy.Mcp (Python 3.9+, standard library only).

The dnSpy extension already speaks MCP over HTTP (http://127.0.0.1:13380/mcp). This bridge is for
clients that only support stdio, and it adds multi-instance support:

  * discovers every running dnSpy that has the extension (via %LOCALAPPDATA%/dnSpy.Mcp/instances/*.json)
  * routes each tool call to the selected instance (optional `instance` argument = dnSpy's pid)
  * can launch dnSpy when none is running (set DNSPY_PATH to dnSpy.exe)

Configure, e.g. for Claude Code:
  claude mcp add dnspy -- python C:/path/to/dnspy_mcp_bridge.py
"""
import glob
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

PROTOCOL_VERSIONS = ("2025-06-18", "2025-03-26", "2024-11-05")
INSTANCES_DIR = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), "dnSpy.Mcp", "instances")
TOKEN = os.environ.get("DNSPY_MCP_TOKEN")
LAUNCH_WAIT_SECONDS = 90

BRIDGE_INSTRUCTIONS_SUFFIX = (
    "\n\nThis server is a bridge: list_instances shows running dnSpy instances; tools accept an optional "
    "`instance` (pid) argument; launch_dnspy starts a new dnSpy. open_assembly launches dnSpy if none is running."
)

INSTANCE_PROP = {
    "type": "integer",
    "description": "Target dnSpy instance (process id from list_instances). Default: the current instance.",
}


def tool_metadata(title, *, read_only, destructive, open_world):
    return {
        "title": title,
        "annotations": {
            "title": title,
            "readOnlyHint": read_only,
            "destructiveHint": destructive,
            "openWorldHint": open_world,
        },
    }


BRIDGE_TOOLS = [
    {
        "name": "list_instances",
        **tool_metadata("List instances", read_only=True, destructive=False, open_world=False),
        "description": "List running dnSpy instances that have the MCP extension, and which one is current.",
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "select_instance",
        **tool_metadata("Select instance", read_only=False, destructive=False, open_world=False),
        "description": "Make a dnSpy instance the default target for subsequent tool calls.",
        "inputSchema": {"type": "object", "properties": {"instance": INSTANCE_PROP}, "required": ["instance"]},
    },
    {
        "name": "launch_dnspy",
        **tool_metadata("Launch dnSpy", read_only=False, destructive=False, open_world=True),
        "description": "Start a new dnSpy instance (optionally opening files) and make it current. Needs DNSPY_PATH or dnspy_path.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "paths": {"type": "array", "items": {"type": "string"}, "description": "Files to open"},
                "dnspy_path": {"type": "string", "description": "Path to dnSpy.exe (default: DNSPY_PATH env var)"},
            },
        },
    },
]

# Used for tools/list when no dnSpy is running yet, so the client still sees the main tools.
FALLBACK_REMOTE_TOOLS = [
    {
        "name": "execute_csharp",
        **tool_metadata("Execute C#", read_only=False, destructive=True, open_world=True),
        "description": "Execute C# script code inside the running dnSpyEx instance. Call reference(query) first. "
                       "State persists across calls; a trailing expression is returned.",
        "inputSchema": {"type": "object", "properties": {
            "code": {"type": "string"},
            "timeout_seconds": {"type": "number"},
            "reset": {"type": "boolean"},
            "max_output_chars": {"type": "integer"},
        }, "required": ["code"]},
    },
    {
        "name": "reference",
        **tool_metadata("Look up API reference", read_only=True, destructive=False, open_world=False),
        "description": "Look up the dnSpy scripting API (topics, dnlib/dnSpy types and members).",
        "inputSchema": {"type": "object", "properties": {"query": {"type": "string"}}},
    },
    {
        "name": "list_documents",
        **tool_metadata("List documents", read_only=True, destructive=False, open_world=False),
        "description": "List the assemblies loaded in dnSpy.",
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "open_assembly",
        **tool_metadata("Open assembly", read_only=False, destructive=False, open_world=True),
        "description": "Open an assembly in dnSpy (launches dnSpy if none is running).",
        "inputSchema": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]},
    },
    {
        "name": "close_assembly",
        **tool_metadata("Close assembly", read_only=False, destructive=True, open_world=False),
        "description": "Close an assembly in dnSpy.",
        "inputSchema": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]},
    },
    {
        "name": "save_module",
        **tool_metadata("Save module", read_only=False, destructive=True, open_world=False),
        "description": "Write a module to disk.",
        "inputSchema": {"type": "object", "properties": {
            "name": {"type": "string"}, "output_path": {"type": "string"}}, "required": ["name"]},
    },
]


def log(msg):
    print(f"[dnspy-bridge] {msg}", file=sys.stderr, flush=True)


class ToolError(Exception):
    pass


class Instance:
    def __init__(self, info):
        self.pid = info["pid"]
        self.url = info["url"]
        self.exe = info.get("exe")
        self.started = info.get("started", "")
        self.session = None
        self.next_id = 1

    def _post(self, payload, timeout):
        req = urllib.request.Request(self.url, data=json.dumps(payload).encode(), method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json, text/event-stream")
        if self.session:
            req.add_header("Mcp-Session-Id", self.session)
        if TOKEN:
            req.add_header("Authorization", "Bearer " + TOKEN)
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            sid = resp.headers.get("Mcp-Session-Id")
            if sid:
                self.session = sid
            body = resp.read().decode("utf-8")
            return json.loads(body) if body else None

    def _ensure_session(self):
        if self.session:
            return
        self._raw_request("initialize", {
            "protocolVersion": PROTOCOL_VERSIONS[0],
            "capabilities": {},
            "clientInfo": {"name": "dnspy-mcp-bridge", "version": "0.1.0"},
        }, timeout=15)
        self._post({"jsonrpc": "2.0", "method": "notifications/initialized"}, timeout=15)

    def _raw_request(self, method, params, timeout):
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params}
        self.next_id += 1
        resp = self._post(msg, timeout)
        if "error" in resp:
            raise ToolError(resp["error"].get("message", str(resp["error"])))
        return resp["result"]

    def request(self, method, params, timeout=30):
        self._ensure_session()
        try:
            return self._raw_request(method, params, timeout)
        except urllib.error.HTTPError as e:
            if e.code == 404:  # dnSpy restarted or the session expired
                self.session = None
                self._ensure_session()
                return self._raw_request(method, params, timeout)
            raise

    def alive(self):
        try:
            health = self.url.rsplit("/", 1)[0] + "/health"
            req = urllib.request.Request(health)
            if TOKEN:
                req.add_header("Authorization", "Bearer " + TOKEN)
            with urllib.request.urlopen(req, timeout=2) as r:
                return json.loads(r.read()).get("pid") == self.pid
        except (urllib.error.URLError, OSError, ValueError):
            return False


class Bridge:
    def __init__(self):
        self.instances = {}  # pid -> Instance
        self.current_pid = None
        self.remote_instructions = None

    # ------------------------------------------------------------------ discovery

    def refresh(self):
        found = {}
        for path in glob.glob(os.path.join(INSTANCES_DIR, "*.json")):
            try:
                with open(path, encoding="utf-8") as f:
                    info = json.load(f)
            except (OSError, ValueError):
                continue
            inst = self.instances.get(info.get("pid")) or Instance(info)
            if inst.url != info["url"]:
                inst = Instance(info)
            if inst.alive():
                found[inst.pid] = inst
        self.instances = found
        if self.current_pid not in found:
            # Default to the most recently started instance
            self.current_pid = max(found.values(), key=lambda i: i.started).pid if found else None
        return found

    def target(self, args):
        pid = args.pop("instance", None)
        self.refresh()
        if pid is not None:
            inst = self.instances.get(int(pid))
            if not inst:
                raise ToolError(f"No dnSpy instance with pid {pid}. Running: {sorted(self.instances) or 'none'}")
            return inst
        if self.current_pid is None:
            raise ToolError("No dnSpy instance with the MCP extension is running. Start dnSpy, or call launch_dnspy "
                            "(set DNSPY_PATH to dnSpy.exe).")
        return self.instances[self.current_pid]

    def launch(self, paths=None, dnspy_path=None):
        exe = dnspy_path or os.environ.get("DNSPY_PATH")
        if not exe or not os.path.isfile(exe):
            raise ToolError("dnSpy.exe not found: pass dnspy_path or set the DNSPY_PATH environment variable.")
        before = set(self.refresh())
        proc = subprocess.Popen([exe] + list(paths or []), close_fds=True,
                                creationflags=getattr(subprocess, "DETACHED_PROCESS", 0))
        deadline = time.time() + LAUNCH_WAIT_SECONDS
        while time.time() < deadline:
            time.sleep(1)
            new = set(self.refresh()) - before
            if proc.pid in new or new:
                pid = proc.pid if proc.pid in new else new.pop()
                self.current_pid = pid
                return self.instances[pid]
            if proc.poll() is not None:
                raise ToolError(f"dnSpy exited with code {proc.returncode} before the MCP server started.")
        raise ToolError(f"dnSpy started (pid {proc.pid}) but no MCP server registered within {LAUNCH_WAIT_SECONDS}s. "
                        "Is dnSpy.Mcp installed in its bin/Extensions folder?")

    # ------------------------------------------------------------------ MCP handlers

    def list_tools(self):
        remote = None
        try:
            if self.refresh():
                inst = self.instances[self.current_pid]
                remote = inst.request("tools/list", {})["tools"]
        except Exception as e:  # noqa: BLE001 - fall back to the static list
            log(f"tools/list from dnSpy failed: {e}")
        tools = [dict(t) for t in (remote or FALLBACK_REMOTE_TOOLS)]
        for t in tools:
            schema = json.loads(json.dumps(t.get("inputSchema", {"type": "object", "properties": {}})))
            schema.setdefault("properties", {})["instance"] = INSTANCE_PROP
            t["inputSchema"] = schema
            if t["name"] == "open_assembly":
                t["annotations"] = {**t.get("annotations", {}), "openWorldHint": True}
        return BRIDGE_TOOLS + tools

    def call_tool(self, name, args):
        args = dict(args or {})
        if name == "list_instances":
            found = self.refresh()
            if not found:
                return text_result("No dnSpy instances running. Use launch_dnspy (or open_assembly) to start one.")
            lines = []
            for inst in sorted(found.values(), key=lambda i: i.started):
                mark = "*" if inst.pid == self.current_pid else " "
                lines.append(f"{mark} pid {inst.pid}  {inst.url}  started {inst.started}  {inst.exe or ''}")
            return text_result("\n".join(lines) + "\n(* = current)")
        if name == "select_instance":
            inst = self.target(args)
            self.current_pid = inst.pid
            return text_result(f"Current instance: pid {inst.pid}")
        if name == "launch_dnspy":
            inst = self.launch(args.get("paths"), args.get("dnspy_path"))
            return text_result(f"Launched dnSpy pid {inst.pid} ({inst.url}); it is now the current instance.")

        if name == "open_assembly" and "instance" not in args and not self.refresh():
            # Like IDA's open_database: start a worker when nothing is running
            inst = self.launch([args["path"]] if args.get("path") else None)
            time.sleep(1)
        inst = self.target(args)
        timeout = float(args.get("timeout_seconds") or 120) + 30
        result = inst.request("tools/call", {"name": name, "arguments": args}, timeout=timeout)
        return result

    def initialize(self, params):
        requested = (params or {}).get("protocolVersion")
        version = requested if requested in PROTOCOL_VERSIONS else PROTOCOL_VERSIONS[0]
        try:
            if self.refresh():
                inst = self.instances[self.current_pid]
                inst.session = None
                inst._ensure_session()
        except Exception as e:  # noqa: BLE001
            log(f"could not reach dnSpy: {e}")
        return {
            "protocolVersion": version,
            "capabilities": {"tools": {"listChanged": False}},
            "serverInfo": {"name": "dnspy", "title": "dnSpyEx (bridge)", "version": "0.1.0"},
            "instructions": DEFAULT_INSTRUCTIONS + BRIDGE_INSTRUCTIONS_SUFFIX,
        }

    def handle(self, msg):
        method = msg.get("method")
        mid = msg.get("id")
        if method is None or mid is None:
            return None  # notification or a response
        try:
            if method == "initialize":
                result = self.initialize(msg.get("params"))
            elif method == "ping":
                result = {}
            elif method == "tools/list":
                result = {"tools": self.list_tools()}
            elif method == "tools/call":
                p = msg.get("params") or {}
                try:
                    result = self.call_tool(p.get("name"), p.get("arguments"))
                except ToolError as e:
                    result = text_result(str(e), is_error=True)
                except (urllib.error.URLError, OSError) as e:
                    result = text_result(f"Lost connection to dnSpy: {e}", is_error=True)
            elif method in ("resources/list", "prompts/list"):
                result = {method.split("/")[0]: []}
            else:
                return {"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": f"Method not found: {method}"}}
            return {"jsonrpc": "2.0", "id": mid, "result": result}
        except Exception as e:  # noqa: BLE001
            return {"jsonrpc": "2.0", "id": mid, "error": {"code": -32603, "message": str(e)}}


DEFAULT_INSTRUCTIONS = (
    "dnSpyEx MCP (code mode). You drive dnSpyEx by running C# script code with execute_csharp; state persists "
    "between calls and a trailing expression is returned. Call reference() before guessing an API "
    "(reference(\"overview\") lists the globals: Modules, Types(), Method(), Decompile(), XrefsTo(), Strings(), ...)."
)


def text_result(text, is_error=False):
    return {"content": [{"type": "text", "text": text}], "isError": is_error}


def main():
    bridge = Bridge()
    stdin = sys.stdin.buffer
    stdout = sys.stdout.buffer
    for line in stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except ValueError:
            resp = {"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Parse error"}}
        else:
            if isinstance(msg, list):
                resp = [r for r in (bridge.handle(m) for m in msg) if r is not None] or None
            else:
                resp = bridge.handle(msg)
        if resp is not None:
            stdout.write(json.dumps(resp, ensure_ascii=False).encode("utf-8") + b"\n")
            stdout.flush()


if __name__ == "__main__":
    main()
