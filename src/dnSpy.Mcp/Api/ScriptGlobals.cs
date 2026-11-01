using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Mcp.Scripting;

namespace dnSpy.Mcp.Api {
	/// <summary>
	/// The globals of every script. All public members are directly callable from script code,
	/// e.g. <c>Types("Login")</c> or <c>Decompile(Method("MyApp.Auth::Check"))</c>.
	/// </summary>
	public sealed partial class ScriptGlobals {
		readonly DnSpyHost host;
		CapturedOutput? output;

		internal ScriptGlobals(DnSpyHost host) => this.host = host;

		internal void BeginExecution(CancellationToken token, CapturedOutput capture) {
			Token = token;
			output = capture;
		}

		// ------------------------------------------------------------------ environment

		/// <summary>Cancelled when the current call times out. Check it in long loops.</summary>
		public CancellationToken Token { get; private set; }

		/// <summary>Low-level access to dnSpy: MEF services and the UI dispatcher.</summary>
		public DnSpyHost Host => host;

		/// <summary>Resolves any dnSpy MEF service, e.g. <c>Resolve&lt;IDocumentTabService&gt;()</c>.</summary>
		public T Resolve<T>() where T : class => host.Resolve<T>();

		/// <summary>Like <see cref="Resolve{T}"/> but returns null if the service doesn't exist.</summary>
		public T? TryResolve<T>() where T : class => host.TryResolve<T>();

		/// <summary>Runs code on dnSpy's UI thread. Required for anything touching WPF/UI objects.</summary>
		public void UI(Action action) => host.UI(action);

		/// <summary>Runs code on dnSpy's UI thread and returns its result.</summary>
		public T UI<T>(Func<T> func) => host.UI(func);

		/// <summary>Writes a value (formatted like a result) followed by a newline to the call's output.</summary>
		public void print(object? value = null) => output?.Append(ResultFormatter.Format(value) + Environment.NewLine);

		/// <summary>Same as <see cref="print(object)"/>.</summary>
		public void Print(object? value = null) => print(value);

		/// <summary>Formats a value the way results are formatted (members show full name + token, sequences are listed).</summary>
		public string Format(object? value, int maxItems = 200) => ResultFormatter.Format(value, maxItems);

		/// <summary>dnSpy services used most often</summary>
		public IDsDocumentService DocumentService => host.DocumentService;
		/// <summary>Tabs: open/refresh/follow references</summary>
		public IDocumentTabService DocumentTabService => host.DocumentTabService;
		/// <summary>The assembly explorer tree</summary>
		public IDocumentTreeView DocumentTreeView => host.DocumentTreeView;
		/// <summary>All decompilers (C#, VB, IL, ...)</summary>
		public IDecompilerService DecompilerService => host.DecompilerService;

		// ------------------------------------------------------------------ documents

		/// <summary>Top-level documents (assemblies/modules/other files) in the assembly explorer.</summary>
		public IDsDocument[] Documents => host.DocumentService.GetDocuments();

		/// <summary>Every loaded .NET module, including the non-manifest modules of multi-module assemblies.</summary>
		public IReadOnlyList<ModuleDef> Modules => AllDocuments().Select(d => d.ModuleDef).OfType<ModuleDef>().Distinct().ToList();

		/// <summary>Every loaded assembly.</summary>
		public IReadOnlyList<AssemblyDef> Assemblies => Modules.Select(m => m.Assembly).OfType<AssemblyDef>().Distinct().ToList();

		IEnumerable<IDsDocument> AllDocuments() {
			var stack = new Stack<IDsDocument>(host.DocumentService.GetDocuments().Reverse());
			while (stack.Count > 0) {
				var doc = stack.Pop();
				yield return doc;
				IDsDocument[] children;
				try {
					children = doc.Children.ToArray();
				}
				catch {
					continue;
				}
				for (int i = children.Length - 1; i >= 0; i--)
					stack.Push(children[i]);
			}
		}

		/// <summary>
		/// Loaded modules that are not part of dnSpy or the .NET framework, i.e. the files under analysis.
		/// Helpers that take an optional module (Triage, Resources, Note, ...) default to the only one of these.
		/// </summary>
		public IReadOnlyList<ModuleDef> SampleModules => Modules.Where(IsSampleModule).ToList();

		static readonly string[] frameworkDirs = new[] {
			AppContext.BaseDirectory,
			Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "",
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Reference Assemblies"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Reference Assemblies"),
		}.Where(d => d.Length > 0).Select(d => Path.GetFullPath(d).TrimEnd('\\') + "\\").ToArray();

		internal static bool IsSampleModule(ModuleDef m) {
			var loc = m.Location;
			if (string.IsNullOrEmpty(loc))
				return true; // in-memory
			string full;
			try {
				full = Path.GetFullPath(loc);
			}
			catch {
				return true;
			}
			return !frameworkDirs.Any(d => full.StartsWith(d, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>The module to use when a helper's module argument is omitted: the only sample module.</summary>
		ModuleDef DefaultModule(ModuleDef? module, string what) {
			if (module is not null)
				return module;
			var samples = SampleModules;
			if (samples.Count == 1)
				return samples[0];
			if (samples.Count == 0)
				throw new ArgumentException($"{what}: no sample is loaded (only framework/dnSpy assemblies). Use Load(path) first, or pass a module.");
			throw new ArgumentException($"{what}: several samples are loaded, pass a module: " + string.Join(", ", samples.Select(s => $"Module(\"{s.Name}\")")));
		}

		/// <summary>The document (file) that owns a module, or null.</summary>
		public IDsDocument? DocumentOf(ModuleDef module) => AllDocuments().FirstOrDefault(d => d.ModuleDef == module);

		/// <summary>
		/// Finds a loaded module by module name ("Foo.dll"), assembly name ("Foo"), file name or full path.
		/// Throws if not found or ambiguous.
		/// </summary>
		public ModuleDef Module(string name) {
			var mods = Modules;
			var hits = mods.Where(m => ModuleMatches(m, name)).ToList();
			if (hits.Count == 1)
				return hits[0];
			if (hits.Count > 1) {
				// Prefer the manifest module of an assembly
				var manifest = hits.Where(m => m.IsManifestModule).ToList();
				if (manifest.Count == 1)
					return manifest[0];
				throw new System.Reflection.AmbiguousMatchException($"'{name}' matches {hits.Count} modules:\n" + string.Join("\n", hits.Select(m => "  " + m.Location)));
			}
			throw new KeyNotFoundException($"No loaded module named '{name}'. Loaded: {string.Join(", ", mods.Select(m => m.Name.String))}. Use Load(path) to open one.");
		}

		static bool ModuleMatches(ModuleDef m, string name) {
			var cmp = StringComparison.OrdinalIgnoreCase;
			if (string.Equals(m.Name, name, cmp) || string.Equals(m.Assembly?.Name, name, cmp))
				return true;
			var loc = m.Location;
			if (string.IsNullOrEmpty(loc))
				return false;
			return string.Equals(loc, name, cmp) || string.Equals(Path.GetFileName(loc), name, cmp) || string.Equals(Path.GetFileNameWithoutExtension(loc), name, cmp);
		}

		/// <summary>Opens a file in dnSpy (like File > Open) and returns its module. Returns the existing one if already loaded.</summary>
		public ModuleDef Load(string path) {
			var fullPath = Path.GetFullPath(path);
			if (!File.Exists(fullPath))
				throw new FileNotFoundException("File not found", fullPath);
			var doc = host.UI(() => {
				var d = host.DocumentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(fullPath));
				if (d is not null) {
					host.Dispatcher.BeginInvoke(new Action(() => {
						var node = host.DocumentTreeView.FindNode(d);
						if (node is not null)
							host.DocumentTreeView.TreeView.SelectItems(new[] { node });
					}), System.Windows.Threading.DispatcherPriority.Background);
				}
				return d;
			});
			if (doc is null)
				throw new InvalidOperationException($"dnSpy could not open '{fullPath}'");
			var module = doc.ModuleDef ?? throw new InvalidOperationException($"'{fullPath}' was opened but isn't a .NET module");
			// Saved renames are also applied in the background on load; do it now so the caller sees them
			try {
				if (host.Annotations.ApplyRenames(module) > 0)
					RefreshQuietly(module);
			}
			catch {
			}
			return module;
		}

		/// <summary>Closes a module/assembly (ModuleDef, AssemblyDef, IDsDocument or name) in dnSpy.</summary>
		public void Unload(object target) {
			IDsDocument? doc = target switch {
				IDsDocument d => d,
				ModuleDef m => DocumentOf(m),
				AssemblyDef a => DocumentOf(a.ManifestModule),
				string s => DocumentOf(Module(s)),
				_ => throw new ArgumentException($"Can't unload a {target?.GetType().Name ?? "null"}"),
			};
			if (doc is null)
				throw new KeyNotFoundException("Document not found");
			// Close the top-level document that contains it
			var top = host.DocumentService.GetDocuments().FirstOrDefault(d => d == doc || d.Children.Contains(doc)) ?? doc;
			host.UI(() => {
				var node = host.DocumentTreeView.FindNode(top);
				if (node is not null)
					host.DocumentTreeView.Remove(new[] { node });
				else
					host.DocumentService.Remove(top.Key);
			});
		}

		// ------------------------------------------------------------------ lookup

		IEnumerable<ModuleDef> Scope(ModuleDef? module) => module is null ? Modules : new[] { module };

		/// <summary>
		/// All types (including nested types) whose full name matches <paramref name="pattern"/>.
		/// Pattern: case-insensitive substring; '*'/'?' wildcards (whole name); or "/regex/".
		/// </summary>
		public IEnumerable<TypeDef> Types(string? pattern = null, ModuleDef? module = null) {
			var m = NameMatcher.Create(pattern);
			foreach (var mod in Scope(module)) {
				foreach (var t in mod.GetTypes()) {
					Token.ThrowIfCancellationRequested();
					if (m.IsMatch(t.FullName))
						yield return t;
				}
			}
		}

		/// <summary>All methods whose full name ("RetType Ns.Type::Name(Params)") matches the pattern. See <see cref="Types"/>.</summary>
		public IEnumerable<MethodDef> Methods(string? pattern = null, ModuleDef? module = null) => MembersOf(pattern, module, t => t.Methods);

		/// <summary>All fields whose full name matches the pattern.</summary>
		public IEnumerable<FieldDef> Fields(string? pattern = null, ModuleDef? module = null) => MembersOf(pattern, module, t => t.Fields);

		/// <summary>All properties whose full name matches the pattern.</summary>
		public IEnumerable<PropertyDef> Properties(string? pattern = null, ModuleDef? module = null) => MembersOf(pattern, module, t => t.Properties);

		/// <summary>All events whose full name matches the pattern.</summary>
		public IEnumerable<EventDef> Events(string? pattern = null, ModuleDef? module = null) => MembersOf(pattern, module, t => t.Events);

		IEnumerable<T> MembersOf<T>(string? pattern, ModuleDef? module, Func<TypeDef, IEnumerable<T>> getMembers) where T : IMemberDef {
			var m = NameMatcher.Create(pattern);
			foreach (var mod in Scope(module)) {
				foreach (var t in mod.GetTypes()) {
					Token.ThrowIfCancellationRequested();
					foreach (var member in getMembers(t)) {
						if (m.IsMatch(member.FullName))
							yield return member;
					}
				}
			}
		}

		/// <summary>
		/// Finds one type. Accepts a full name ("Ns.Outer/Inner" or "Ns.Outer+Inner") or a unique simple name.
		/// Throws (listing candidates) if not found or ambiguous.
		/// </summary>
		public TypeDef Type(string name, ModuleDef? module = null) {
			var hits = FindTypes(name, module);
			if (hits.Count == 1)
				return hits[0];
			if (hits.Count > 1)
				throw new System.Reflection.AmbiguousMatchException($"'{name}' matches {hits.Count} types (pass a module to disambiguate):\n" + string.Join("\n", hits.Take(30).Select(t => $"  {t.FullName}  ({t.Module.Name})")));
			var similar = Types(name.Split('.', '/', '+').Last(), module).Take(15).Select(t => "  " + t.FullName).ToList();
			throw new KeyNotFoundException($"Type '{name}' not found." + (similar.Count > 0 ? " Similar:\n" + string.Join("\n", similar) : string.Empty));
		}

		/// <summary>Like <see cref="Type"/> but returns null instead of throwing.</summary>
		public TypeDef? TryType(string name, ModuleDef? module = null) {
			var hits = FindTypes(name, module);
			return hits.Count == 1 ? hits[0] : null;
		}

		List<TypeDef> FindTypes(string name, ModuleDef? module) {
			var norm = name.Replace('+', '/').Trim();
			var exact = new List<TypeDef>();
			var simple = new List<TypeDef>();
			foreach (var mod in Scope(module)) {
				foreach (var t in mod.GetTypes()) {
					if (t.FullName == norm)
						exact.Add(t);
					else if (t.Name == norm)
						simple.Add(t);
				}
			}
			return exact.Count > 0 ? exact : simple;
		}

		/// <summary>
		/// Finds one method. Spec forms: "Ns.Type::Name", "Ns.Type.Name", "Type.Name",
		/// optionally with parameter types "Ns.Type::Name(String,Int32)", or a full dnlib name
		/// "System.Void Ns.Type::Name(System.String)". Throws (listing overloads) if ambiguous.
		/// </summary>
		public MethodDef Method(string spec, ModuleDef? module = null) {
			var hits = FindMethods(spec, module);
			if (hits.Count == 1)
				return hits[0];
			if (hits.Count > 1)
				throw new System.Reflection.AmbiguousMatchException($"'{spec}' matches {hits.Count} methods. Add parameter types, e.g. \"Type::Name(String,Int32)\", or use Methods(...):\n" + string.Join("\n", hits.Take(30).Select(m => $"  {m.FullName}  [0x{m.MDToken.Raw:X8}]")));
			throw new KeyNotFoundException($"Method '{spec}' not found.");
		}

		/// <summary>Like <see cref="Method"/> but returns null instead of throwing.</summary>
		public MethodDef? TryMethod(string spec, ModuleDef? module = null) {
			var hits = FindMethods(spec, module);
			return hits.Count == 1 ? hits[0] : null;
		}

		List<MethodDef> FindMethods(string spec, ModuleDef? module) {
			spec = spec.Trim();
			// Full dnlib name: "RetType Ns.Type::Name(Params)"
			if (spec.Contains(' ') && spec.Contains("::")) {
				var full = Methods(null, module).Where(m => m.FullName == spec).ToList();
				if (full.Count > 0)
					return full;
			}
			string? paramList = null;
			int paren = spec.IndexOf('(');
			if (paren >= 0) {
				paramList = spec.Substring(paren + 1).TrimEnd(')', ' ');
				spec = spec.Substring(0, paren);
			}
			if (!SplitMemberSpec(spec, out var typeName, out var memberName))
				throw new ArgumentException($"Method spec '{spec}' should look like \"Ns.Type::Name\" or \"Ns.Type.Name\"");
			var types = FindTypes(typeName, module);
			var hits = types.SelectMany(t => t.Methods).Where(m => m.Name == memberName).ToList();
			if (paramList is not null)
				hits = hits.Where(m => ParametersMatch(m, paramList)).ToList();
			return hits;
		}

		static bool SplitMemberSpec(string spec, out string typeName, out string memberName) {
			int sep = spec.LastIndexOf("::", StringComparison.Ordinal);
			if (sep >= 0) {
				typeName = spec.Substring(0, sep);
				memberName = spec.Substring(sep + 2);
				return typeName.Length > 0 && memberName.Length > 0;
			}
			foreach (var ctor in new[] { ".ctor", ".cctor" }) {
				if (spec.EndsWith("." + ctor, StringComparison.Ordinal)) {
					typeName = spec.Substring(0, spec.Length - ctor.Length - 1);
					memberName = ctor;
					return typeName.Length > 0;
				}
			}
			int dot = spec.LastIndexOf('.');
			if (dot <= 0 || dot == spec.Length - 1) {
				typeName = memberName = string.Empty;
				return false;
			}
			typeName = spec.Substring(0, dot);
			memberName = spec.Substring(dot + 1);
			return true;
		}

		static bool ParametersMatch(MethodDef m, string paramList) {
			var wanted = paramList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var actual = m.MethodSig?.Params;
			if (actual is null || actual.Count != wanted.Length)
				return false;
			for (int i = 0; i < wanted.Length; i++) {
				var a = actual[i];
				if (wanted[i] != a.FullName && wanted[i] != a.TypeName && wanted[i] != a.ReflectionName && !CSharpAlias(wanted[i], a.FullName))
					return false;
			}
			return true;
		}

		static bool CSharpAlias(string alias, string fullName) => alias switch {
			"string" => fullName == "System.String",
			"object" => fullName == "System.Object",
			"bool" => fullName == "System.Boolean",
			"byte" => fullName == "System.Byte",
			"sbyte" => fullName == "System.SByte",
			"char" => fullName == "System.Char",
			"short" => fullName == "System.Int16",
			"ushort" => fullName == "System.UInt16",
			"int" => fullName == "System.Int32",
			"uint" => fullName == "System.UInt32",
			"long" => fullName == "System.Int64",
			"ulong" => fullName == "System.UInt64",
			"float" => fullName == "System.Single",
			"double" => fullName == "System.Double",
			"decimal" => fullName == "System.Decimal",
			"byte[]" => fullName == "System.Byte[]",
			"string[]" => fullName == "System.String[]",
			"object[]" => fullName == "System.Object[]",
			_ => false,
		};

		/// <summary>Finds one field: "Ns.Type::Name" or "Ns.Type.Name".</summary>
		public FieldDef Field(string spec, ModuleDef? module = null) => FindOneMember(spec, module, t => t.Fields, "Field");

		/// <summary>Finds one property: "Ns.Type::Name" or "Ns.Type.Name".</summary>
		public PropertyDef Property(string spec, ModuleDef? module = null) => FindOneMember(spec, module, t => t.Properties, "Property");

		/// <summary>Finds one event: "Ns.Type::Name" or "Ns.Type.Name".</summary>
		public EventDef Event(string spec, ModuleDef? module = null) => FindOneMember(spec, module, t => t.Events, "Event");

		T FindOneMember<T>(string spec, ModuleDef? module, Func<TypeDef, IEnumerable<T>> getMembers, string kind) where T : class, IMemberDef {
			if (!SplitMemberSpec(spec.Trim(), out var typeName, out var memberName))
				throw new ArgumentException($"{kind} spec '{spec}' should look like \"Ns.Type::Name\"");
			var hits = FindTypes(typeName, module).SelectMany(getMembers).Where(m => m.Name == memberName).ToList();
			if (hits.Count == 1)
				return hits[0];
			if (hits.Count == 0)
				throw new KeyNotFoundException($"{kind} '{spec}' not found.");
			throw new System.Reflection.AmbiguousMatchException($"'{spec}' matches {hits.Count} members:\n" + string.Join("\n", hits.Select(h => "  " + h.FullName)));
		}

		/// <summary>
		/// Resolves anything to a definition: a member spec string (type, method, field, property or event),
		/// an "0x06001234" token (searched in all modules, or in <paramref name="module"/>), or an
		/// IMemberRef/MemberRef/MethodSpec/TypeRef (resolved through the assembly resolver).
		/// </summary>
		public IMemberDef Def(object target, ModuleDef? module = null) {
			switch (target) {
			case IMemberDef def:
				return def;
			case MethodSpec ms:
				return Def(ms.Method, module);
			case IMethod im when im.IsMethod:
				return im.ResolveMethodDefThrow();
			case IField f:
				return f.ResolveFieldDefThrow();
			case ITypeDefOrRef tr:
				return tr.ResolveTypeDefThrow();
			case uint or int:
				return ByToken(Convert.ToUInt32(target), module);
			case string s:
				return DefFromString(s, module);
			default:
				throw new ArgumentException($"Can't resolve a {target?.GetType().FullName ?? "null"} to a definition");
			}
		}

		IMemberDef DefFromString(string s, ModuleDef? module) {
			s = s.Trim();
			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var tok))
				return ByToken(tok, module);
			if (TryType(s, module) is TypeDef t)
				return t;
			if (s.Contains('(') || s.Contains(' '))
				return Method(s, module);
			if (!SplitMemberSpec(s, out var typeName, out var memberName))
				return Type(s, module); // throws with suggestions
			var members = FindTypes(typeName, module).SelectMany(ty =>
				ty.Methods.Cast<IMemberDef>().Concat(ty.Fields).Concat(ty.Properties).Concat(ty.Events).Concat(ty.NestedTypes))
				.Where(m => m.Name == memberName).ToList();
			if (members.Count == 1)
				return members[0];
			if (members.Count == 0)
				throw new KeyNotFoundException($"Nothing named '{s}' found (tried types and members).");
			throw new System.Reflection.AmbiguousMatchException($"'{s}' matches {members.Count} members:\n" + string.Join("\n", members.Take(30).Select(m => $"  {m.FullName}  [0x{m.MDToken.Raw:X8}]")));
		}

		IMemberDef ByToken(uint token, ModuleDef? module) {
			var hits = new List<IMemberDef>();
			foreach (var mod in Scope(module)) {
				if (mod.ResolveToken(token) is IMemberDef def)
					hits.Add(def);
			}
			if (hits.Count == 1)
				return hits[0];
			if (hits.Count == 0)
				throw new KeyNotFoundException($"Token 0x{token:X8} isn't a type/member definition in the searched modules.");
			throw new System.Reflection.AmbiguousMatchException($"Token 0x{token:X8} exists in {hits.Count} modules, pass the module:\n" + string.Join("\n", hits.Select(h => $"  {h.FullName}  ({h.Module.Name})")));
		}
	}
}
