"""Minimal MCP Streamable-HTTP client for poking at a running dnSpy.Mcp instance.

    python tests/mcp_client.py                      # list tools
    python tests/mcp_client.py exec "Modules"       # run C#
    python tests/mcp_client.py ref TypeDef          # reference lookup
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

URL = os.environ.get("DNSPY_MCP_URL", "http://127.0.0.1:13380/mcp")
TOKEN = os.environ.get("DNSPY_MCP_TOKEN")


class Client:
    def __init__(self, url=URL):
        self.url = url
        self.session = None
        self.next_id = 1

    def _post(self, payload):
        data = json.dumps(payload).encode()
        req = urllib.request.Request(self.url, data=data, method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("Accept", "application/json, text/event-stream")
        if self.session:
            req.add_header("Mcp-Session-Id", self.session)
        if TOKEN:
            req.add_header("Authorization", "Bearer " + TOKEN)
        with urllib.request.urlopen(req, timeout=600) as resp:
            sid = resp.headers.get("Mcp-Session-Id")
            if sid:
                self.session = sid
            body = resp.read().decode()
            return json.loads(body) if body else None

    def request(self, method, params=None):
        msg = {"jsonrpc": "2.0", "id": self.next_id, "method": method}
        self.next_id += 1
        if params is not None:
            msg["params"] = params
        resp = self._post(msg)
        if "error" in resp:
            raise RuntimeError(resp["error"])
        return resp["result"]

    def initialize(self):
        res = self.request("initialize", {
            "protocolVersion": "2025-06-18",
            "capabilities": {},
            "clientInfo": {"name": "smoke-test", "version": "0"},
        })
        self._post({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return res

    def call(self, tool, **args):
        res = self.request("tools/call", {"name": tool, "arguments": args})
        text = "\n".join(c.get("text", "") for c in res["content"])
        return text, res.get("isError", False)


def wait_ready(url=URL, timeout=60):
    health = url.rsplit("/", 1)[0] + "/health"
    end = time.time() + timeout
    while time.time() < end:
        try:
            req = urllib.request.Request(health)
            if TOKEN:
                req.add_header("Authorization", "Bearer " + TOKEN)
            with urllib.request.urlopen(req, timeout=2) as r:
                return json.loads(r.read())
        except (urllib.error.URLError, ConnectionError, TimeoutError):
            time.sleep(0.5)
    raise TimeoutError(f"{health} not reachable")


if __name__ == "__main__":
    wait_ready()
    c = Client()
    c.initialize()
    if len(sys.argv) < 2:
        for t in c.request("tools/list")["tools"]:
            print(t["name"], "-", t["description"][:100])
    elif sys.argv[1] == "exec":
        text, err = c.call("execute_csharp", code=sys.argv[2])
        print(text)
    elif sys.argv[1] == "ref":
        print(c.call("reference", query=" ".join(sys.argv[2:]))[0])
    else:
        print(c.call(sys.argv[1], **json.loads(sys.argv[2] if len(sys.argv) > 2 else "{}"))[0])
