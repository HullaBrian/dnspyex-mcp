using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnSpy.Mcp.Api;
using dnSpy.Mcp.Reference;
using dnSpy.Mcp.Scripting;

namespace dnSpy.Mcp.Server {
	sealed class McpSession : IDisposable {
		readonly object lockObj = new object();
		ScriptSession? script;

		public string Id { get; }

		public McpSession(string id) => Id = id;

		public ScriptSession GetScript(DnSpyHost host) {
			lock (lockObj)
				return script ??= new ScriptSession(host);
		}

		public void Dispose() {
			lock (lockObj) {
				script?.Dispose();
				script = null;
			}
		}
	}

	sealed class McpTools {
		const int DefaultTimeoutSeconds = 120;
		const int DefaultMaxOutputChars = 60_000;

		readonly DnSpyHost host;
		readonly ReferenceProvider reference;

		public McpTools(DnSpyHost host) {
			this.host = host;
			reference = new ReferenceProvider();
		}

		public const string ServerInstructions =
@"dnSpyEx MCP (code mode). You drive a live dnSpyEx instance by running C# script code.

- `execute_csharp` runs C# (Roslyn scripting) inside dnSpy. State persists between calls: variables, functions, classes and usings carry over. A trailing expression without a semicolon is returned as the result; print(x) or Console.WriteLine also produce output.
- Globals give you the loaded assemblies and dnSpy's features: Modules, Types(pattern), Type(name), Methods(pattern), Method(""Ns.Type::Name""), Decompile(x), IL(m), XrefsTo(x), Callers(x), Strings(pattern), DerivedTypes(t), Load(path), GoTo(x), Current/Selected, Save(module), Debugger, Resolve<T>(). dnlib.DotNet and dnlib.DotNet.Emit are imported.
- Call `reference` before guessing an API. `reference(""overview"")` shows the globals and recipes; `reference(""TypeDef"")` or `reference(""ScriptGlobals.XrefsTo"")` shows signatures and docs for any dnlib/dnSpy type.
- Results are capped (max_output_chars), so filter with LINQ (Where/Take/Select) instead of dumping whole modules.
- Malware / unknown binaries: start with the `triage` tool, then follow reference(""malware""). Payload helpers: Resources(), ResourceBytes(), InitializedArrays(), FieldData(), Identify(), Xor/AesDecrypt/Gunzip..., Dump(), LoadBytes() (opens a decrypted stage). Analyze statically: never Assembly.Load or invoke code from the sample.
- Record findings as you go with Rename(x, name), Comment(x, text), Note(text). They persist per file (by SHA-256) across sessions and dnSpy restarts; Annotations() shows earlier work, and list_documents says when there is some.";

		public JsonArray ListTools() => new JsonArray {
			Tool("execute_csharp",
				"Execute C# script code inside the running dnSpyEx instance and return the result plus captured output. " +
				"Call reference(query) first; do not guess the API. State (variables, methods, types, usings) persists across calls in this session. " +
				"A trailing expression (no semicolon) is returned. Globals: Modules, Types(), Type(), Methods(), Method(), Decompile(), IL(), XrefsTo(), Callers(), Strings(), Load(), GoTo(), Current, Save(), Debugger, Resolve<T>(), UI(() => ...). " +
				"Top-level await is supported. Check Token.IsCancellationRequested in long loops.",
				new JsonObject {
					["code"] = Prop("string", "C# script code. Example: `Types(\"Crypto\").Select(t => t.FullName)` or `Decompile(Method(\"App.License::Validate\"))`"),
					["timeout_seconds"] = Prop("number", $"Execution timeout in seconds (default {DefaultTimeoutSeconds})"),
					["reset"] = Prop("boolean", "Discard all script state from previous calls before running (default false)"),
					["max_output_chars"] = Prop("integer", $"Truncate output/result text beyond this many characters (default {DefaultMaxOutputChars})"),
				}, "code"),
			Tool("reference",
				"Look up the dnSpy scripting API and return a plain-text reference. Query a topic (overview, globals, dnlib, xrefs, editing, debugger, ui, recipes), " +
				"a type (TypeDef, MethodDef, CilBody, Instruction, IDsDocumentService, DbgManager...), a member (\"TypeDef.Methods\", \"ScriptGlobals.Decompile\") or any keyword.",
				new JsonObject {
					["query"] = Prop("string", "Topic, type, member or keyword. Empty for the overview."),
				}),
			Tool("list_documents",
				"List the assemblies/modules currently loaded in dnSpy with their file paths, plus what the user has open (active tab and selection).",
				new JsonObject()),
			Tool("open_assembly",
				"Open an assembly/module file in dnSpy (like File > Open). Returns the loaded module. Already-open files are reused.",
				new JsonObject {
					["path"] = Prop("string", "Absolute path of the .dll/.exe/.netmodule to open"),
				}, "path"),
			Tool("close_assembly",
				"Close (unload) an assembly from dnSpy.",
				new JsonObject {
					["name"] = Prop("string", "Assembly name, module name, file name or full path"),
				}, "name"),
			Tool("triage",
				"Malware/unknown-binary triage of a .NET module in one call: hashes and PE facts, obfuscation indicators, probable string decryptors, " +
				"P/Invokes and notable APIs by category (dynamic loading, injection, network, crypto, persistence, anti-analysis...) with callers, " +
				"categorized strings (URLs, IPs, registry, commands, credential targets, base64 blobs), possible embedded payloads, earlier notes, and where to start. " +
				"Opens the file first if given a path. Same as execute_csharp `Triage(module)`, whose result also exposes the raw lists.",
				new JsonObject {
					["target"] = Prop("string", "Path of a file to open, or the name of a loaded module. Optional when exactly one sample is loaded."),
					["max_per_category"] = Prop("integer", "Max items listed per category (default 12)"),
				}),
			Tool("save_module",
				"Write a (possibly edited) module to disk. Defaults to overwriting the original file.",
				new JsonObject {
					["name"] = Prop("string", "Assembly name, module name, file name or full path of a loaded module"),
					["output_path"] = Prop("string", "Where to write it (default: overwrite the module's own file)"),
				}, "name"),
		};

		static JsonObject Prop(string type, string description) => new JsonObject { ["type"] = type, ["description"] = description };

		static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) {
			var schema = new JsonObject {
				["type"] = "object",
				["properties"] = properties,
			};
			if (required.Length > 0)
				schema["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());
			return new JsonObject {
				["name"] = name,
				["description"] = description,
				["inputSchema"] = schema,
			};
		}

		public async Task<JsonObject> CallAsync(McpSession session, string name, JsonObject args) {
			try {
				switch (name) {
				case "execute_csharp":
					return await ExecuteAsync(session, args).ConfigureAwait(false);
				case "reference":
					return TextResult(reference.Lookup(GetString(args, "query") ?? string.Empty));
				case "list_documents":
					return RunWithGlobals(g => ListDocuments(g));
				case "open_assembly": {
					var path = RequireString(args, "path");
					return RunWithGlobals(g => {
						var mod = g.Load(path);
						host.Log($"Opened {mod.Location}");
						return $"Loaded {ResultFormatter.FormatInline(mod)}\nAssembly: {mod.Assembly?.FullName}\nTypes: {mod.GetTypes().Count()}";
					});
				}
				case "close_assembly": {
					var target = RequireString(args, "name");
					return RunWithGlobals(g => {
						var mod = g.Module(target);
						var loc = mod.Location;
						g.Unload(mod);
						return $"Closed {loc}";
					});
				}
				case "triage": {
					var target = GetString(args, "target");
					var max = (int)Math.Clamp(GetDouble(args, "max_per_category") ?? 12, 1, 1000);
					return RunWithGlobals(g => {
						ModuleDef? mod = null;
						if (!string.IsNullOrWhiteSpace(target))
							mod = File.Exists(target) ? g.Load(target) : g.Module(target);
						return g.Triage(mod, max).ToText();
					});
				}
				case "save_module": {
					var target = RequireString(args, "name");
					var output = GetString(args, "output_path");
					return RunWithGlobals(g => {
						var written = g.Save(g.Module(target), string.IsNullOrWhiteSpace(output) ? null : output);
						host.Log($"Saved {written}");
						return $"Saved to {written}";
					});
				}
				default:
					throw new McpException(JsonRpc.InvalidParams, $"Unknown tool: {name}");
				}
			}
			catch (McpException) {
				throw;
			}
			catch (Exception ex) {
				return TextResult(ScriptSession.FormatException(ex), isError: true);
			}
		}

		JsonObject RunWithGlobals(Func<ScriptGlobals, string> func) => TextResult(func(new ScriptGlobals(host)));

		async Task<JsonObject> ExecuteAsync(McpSession session, JsonObject args) {
			var code = RequireString(args, "code");
			var timeout = TimeSpan.FromSeconds(Math.Clamp(GetDouble(args, "timeout_seconds") ?? DefaultTimeoutSeconds, 1, 24 * 3600));
			var maxChars = (int)Math.Clamp(GetDouble(args, "max_output_chars") ?? DefaultMaxOutputChars, 1_000, 10_000_000);
			bool reset = args["reset"] is JsonValue rv && rv.TryGetValue<bool>(out var b) && b;

			host.Log("execute_csharp:\n" + Indent(code.Length > 2000 ? code.Substring(0, 2000) + "\n..." : code));
			var result = await session.GetScript(host).ExecuteAsync(code, timeout, maxChars, reset).ConfigureAwait(false);

			var sb = new StringBuilder();
			if (result.SessionWasReset)
				sb.AppendLine("[note: the previous script timed out, so the session state was reset]");
			if (result.Stdout.Length > 0) {
				sb.Append(result.Stdout);
				if (!result.Stdout.EndsWith("\n"))
					sb.AppendLine();
				if (result.DroppedStdoutChars > 0)
					sb.AppendLine($"[output truncated: {result.DroppedStdoutChars} more chars]");
			}
			if (result.Success && result.HasValue) {
				string formatted;
				try {
					formatted = ResultFormatter.Format(result.Value);
				}
				catch (Exception ex) {
					formatted = $"<could not format result: {ex.Message}>";
				}
				var typeName = result.Value is null ? "null" : FriendlyTypeName(result.Value.GetType());
				if (sb.Length > 0)
					sb.AppendLine();
				sb.AppendLine($"[result: {typeName}]");
				sb.AppendLine(formatted);
			}
			if (!result.Success) {
				if (sb.Length > 0)
					sb.AppendLine();
				sb.AppendLine($"[error: {result.ErrorKind}]");
				sb.AppendLine(result.Error);
			}
			if (sb.Length == 0)
				sb.AppendLine("[ok: no output; end with an expression (no semicolon) to return a value]");

			var text = sb.ToString().TrimEnd();
			if (text.Length > maxChars)
				text = text.Substring(0, maxChars) + $"\n[truncated: {text.Length - maxChars} more chars; filter the result or raise max_output_chars]";
			text += $"\n[{result.Elapsed.TotalMilliseconds:0} ms]";
			return TextResult(text, isError: !result.Success);
		}

		static string ListDocuments(ScriptGlobals g) {
			var sb = new StringBuilder();
			var docs = g.Documents;
			sb.AppendLine($"{docs.Length} document(s) loaded:");
			foreach (var doc in docs) {
				var mod = doc.ModuleDef;
				if (mod is null) {
					sb.AppendLine($"- {doc.Filename} (not a .NET module)");
					continue;
				}
				var kind = doc.IsAutoLoaded ? ", auto-loaded" : string.Empty;
				bool sample = ScriptGlobals.IsSampleModule(mod);
				sb.AppendLine($"- {mod.Assembly?.FullName ?? mod.Name.String}  [{mod.RuntimeVersion}{kind}{(sample ? ", sample" : "")}]");
				sb.AppendLine($"    {doc.Filename}");
				if (sample) {
					try {
						if (g.Host.Annotations.TryGet(mod) is SampleAnnotations a)
							sb.AppendLine($"    earlier work: {a.Notes.Count} notes, {a.Renames.Count} renames, {a.Comments.Count} comments -> Annotations(Module(\"{mod.Name}\"))");
					}
					catch {
					}
				}
			}
			try {
				var current = g.Current;
				var selected = g.Selected;
				if (current is not null)
					sb.AppendLine($"Active tab: {ResultFormatter.FormatInline(current)}");
				if (selected is not null && selected != current)
					sb.AppendLine($"Selected in tree: {ResultFormatter.FormatInline(selected)}");
			}
			catch {
			}
			return sb.ToString().TrimEnd();
		}

		static string FriendlyTypeName(Type t) {
			if (t.Name.Contains("AnonymousType"))
				return "anonymous type";
			// LINQ iterators and other private implementation types: show the sequence type instead
			if (!t.IsVisible && t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(System.Collections.Generic.IEnumerable<>)) is Type seq)
				return FriendlyTypeName(seq);
			if (!t.IsGenericType)
				return t.Name;
			var name = t.Name;
			int tick = name.IndexOf('`');
			if (tick >= 0)
				name = name.Substring(0, tick);
			return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(FriendlyTypeName))}>";
		}

		static string Indent(string s) => "    " + s.Replace("\n", "\n    ");

		static JsonObject TextResult(string text, bool isError = false) => new JsonObject {
			["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
			["isError"] = isError,
		};

		static string? GetString(JsonObject args, string name) =>
			args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

		static string RequireString(JsonObject args, string name) =>
			GetString(args, name) is string s && s.Length > 0 ? s : throw new McpException(JsonRpc.InvalidParams, $"Missing required argument '{name}'");

		static double? GetDouble(JsonObject args, string name) {
			if (args[name] is not JsonValue v)
				return null;
			if (v.TryGetValue<double>(out var d))
				return d;
			if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
				return d;
			return null;
		}
	}
}
