using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using dnSpy.Mcp.Api;

namespace dnSpy.Mcp.Reference {
	/// <summary>
	/// Backs the `reference` tool: curated topics (embedded markdown) plus live reflection over the
	/// assemblies scripts can use, with summaries from the XML doc files dnSpy ships.
	/// </summary>
	sealed class ReferenceProvider {
		const int MaxSearchHits = 60;

		readonly Lazy<Dictionary<string, string>> topics = new Lazy<Dictionary<string, string>>(LoadTopics);
		readonly Lazy<List<Type>> types;
		readonly Dictionary<Assembly, XmlDocs?> xmlDocs = new Dictionary<Assembly, XmlDocs?>();

		static readonly string[] ApiAssemblies = {
			"dnlib", "dnSpy.Contracts.Logic", "dnSpy.Contracts.DnSpy", "dnSpy.Contracts.Debugger",
			"dnSpy.Contracts.Debugger.DotNet", "dnSpy.Contracts.Debugger.DotNet.CorDebug", "dnSpy.Contracts.Debugger.DotNet.Mono",
		};

		public ReferenceProvider() {
			types = new Lazy<List<Type>>(() => {
				var asms = AppDomain.CurrentDomain.GetAssemblies()
					.Where(a => !a.IsDynamic && ApiAssemblies.Contains(a.GetName().Name))
					.Append(typeof(ScriptGlobals).Assembly)
					.Distinct();
				var list = new List<Type>();
				foreach (var asm in asms) {
					try {
						list.AddRange(asm.GetExportedTypes());
					}
					catch (ReflectionTypeLoadException ex) {
						list.AddRange(ex.Types.OfType<Type>().Where(t => t.IsPublic || t.IsNestedPublic));
					}
				}
				return list;
			});
		}

		public string Lookup(string query) {
			query = query.Trim();
			if (query.Length == 0)
				query = "overview";
			var key = query.ToLowerInvariant();

			if (key is "globals" or "api" or "scriptglobals")
				return DescribeType(typeof(ScriptGlobals), header: "Script globals: every member below is callable directly in execute_csharp code.");
			if (topics.Value.TryGetValue(key, out var topic))
				return topic + "\n\nTopics: " + string.Join(", ", topics.Value.Keys.Append("globals"));

			// "Type.Member" / "Type::Member"
			var memberQuery = query.Replace("::", ".");
			int dot = memberQuery.LastIndexOf('.');
			if (dot > 0) {
				var typePart = memberQuery.Substring(0, dot);
				var memberPart = memberQuery.Substring(dot + 1);
				var owner = FindTypesByName(typePart).FirstOrDefault();
				if (owner is not null) {
					var text = DescribeMember(owner, memberPart);
					if (text is not null)
						return text;
				}
			}

			var exact = FindTypesByName(query).ToList();
			if (exact.Count == 1)
				return DescribeType(exact[0]);
			if (exact.Count > 1)
				return string.Join("\n\n" + new string('-', 60) + "\n\n", exact.Take(3).Select(t => DescribeType(t)));

			// A globals member by name, e.g. "Decompile"
			var globalsMember = DescribeMember(typeof(ScriptGlobals), query);
			if (globalsMember is not null)
				return globalsMember;

			return Search(query);
		}

		IEnumerable<Type> FindTypesByName(string name) {
			name = name.Replace('+', '.');
			foreach (var t in types.Value) {
				var full = (t.FullName ?? t.Name).Replace('+', '.');
				if (string.Equals(full, name, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(StripArity(t.Name), name, StringComparison.OrdinalIgnoreCase))
					yield return t;
			}
		}

		string Search(string query) {
			var sb = new StringBuilder();
			var typeHits = types.Value.Where(t => (t.FullName ?? t.Name).Contains(query, StringComparison.OrdinalIgnoreCase))
				.OrderBy(t => t.Name.Length).Take(MaxSearchHits).ToList();
			var memberHits = new List<string>();
			foreach (var t in types.Value.Prepend(typeof(ScriptGlobals))) {
				foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
					if (m is MethodInfo mi && mi.IsSpecialName)
						continue;
					if (m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
						memberHits.Add($"{TypeName(t)}.{m.Name}  ({m.MemberType})");
				}
				if (memberHits.Count > MaxSearchHits * 4)
					break;
			}
			memberHits = memberHits.Distinct().OrderBy(s => s.Length).Take(MaxSearchHits).ToList();
			var topicHits = topics.Value.Where(kv => kv.Value.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();

			if (typeHits.Count == 0 && memberHits.Count == 0 && topicHits.Count == 0)
				return $"Nothing matches '{query}'. Topics: {string.Join(", ", topics.Value.Keys.Append("globals"))}. Try a dnlib type (TypeDef, MethodDef, CilBody, Instruction, OpCodes) or a globals member (reference(\"globals\")).";
			if (topicHits.Count > 0)
				sb.AppendLine($"Topics mentioning '{query}': {string.Join(", ", topicHits)}\n");
			if (typeHits.Count > 0) {
				sb.AppendLine($"Types matching '{query}':");
				foreach (var t in typeHits)
					sb.AppendLine($"  {t.FullName}{Summary(t, GetDocId(t), " - ")}");
				sb.AppendLine();
			}
			if (memberHits.Count > 0) {
				sb.AppendLine($"Members matching '{query}':");
				foreach (var m in memberHits)
					sb.AppendLine("  " + m);
			}
			sb.AppendLine("\nQuery an exact type (\"TypeDef\") or member (\"TypeDef.FindMethod\") for signatures.");
			return sb.ToString().TrimEnd();
		}

		string DescribeType(Type t, string? header = null) {
			var sb = new StringBuilder();
			if (header is not null)
				sb.AppendLine(header).AppendLine();
			var kind = t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : t.IsAbstract && t.IsSealed ? "static class" : t.IsAbstract ? "abstract class" : "class";
			sb.Append($"{kind} {TypeName(t)}");
			var bases = new List<string>();
			if (t.BaseType is not null && t.BaseType != typeof(object) && t.BaseType != typeof(ValueType) && t.BaseType != typeof(Enum))
				bases.Add(TypeName(t.BaseType));
			bases.AddRange(t.GetInterfaces().Where(i => i.IsPublic && (t.BaseType is null || !i.IsAssignableFrom(t.BaseType))).Select(TypeName).Take(8));
			if (bases.Count > 0)
				sb.Append(" : " + string.Join(", ", bases));
			sb.AppendLine($"    // {t.Namespace}, {t.Assembly.GetName().Name}");
			var summary = Summary(t, GetDocId(t), "");
			if (summary.Length > 0)
				sb.AppendLine(summary);
			sb.AppendLine();

			if (t.IsEnum) {
				foreach (var name in Enum.GetNames(t)) {
					var value = Convert.ToUInt64(Enum.Parse(t, name)) is var v ? $"0x{v:X}" : "";
					sb.AppendLine($"  {name} = {value}");
				}
				return sb.ToString().TrimEnd();
			}

			const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
			bool isGlobals = t == typeof(ScriptGlobals);
			var members = t.GetMembers(flags | (isGlobals ? BindingFlags.DeclaredOnly : 0))
				.Where(m => m is not MethodInfo mi || !mi.IsSpecialName)
				.Where(m => m.DeclaringType != typeof(object))
				.OrderBy(m => m.MemberType == MemberTypes.Constructor ? 0 : m.MemberType == MemberTypes.Property ? 1 : m.MemberType == MemberTypes.Method ? 2 : 3)
				.ThenBy(m => m.Name)
				.ToList();
			int count = 0;
			foreach (var m in members) {
				if (m is Type)
					continue;
				if (++count > 150) {
					sb.AppendLine($"  ... ({members.Count - 150} more)");
					break;
				}
				sb.Append("  ").Append(Signature(m));
				var s = Summary(m.DeclaringType ?? t, GetDocId(m), "");
				if (s.Length > 0)
					sb.Append("\n      // ").Append(s.Replace("\n", "\n      // "));
				sb.AppendLine();
			}
			if (isGlobals)
				sb.AppendLine("\nAlso: everything in dnlib.DotNet / dnlib.DotNet.Emit (imported), System.Linq, and any dnSpy service via Resolve<T>().");
			return sb.ToString().TrimEnd();
		}

		string? DescribeMember(Type owner, string memberName) {
			var members = owner.GetMember(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.IgnoreCase)
				.Where(m => m is not MethodInfo mi || !mi.IsSpecialName).ToList();
			if (members.Count == 0)
				return null;
			var sb = new StringBuilder();
			sb.AppendLine($"{TypeName(owner)}  ({owner.Namespace}, {owner.Assembly.GetName().Name})\n");
			foreach (var m in members) {
				sb.AppendLine(Signature(m));
				var doc = FullDoc(m.DeclaringType ?? owner, GetDocId(m));
				if (doc.Length > 0)
					sb.AppendLine("    " + doc.Replace("\n", "\n    "));
				sb.AppendLine();
			}
			// Point at the member's type so the agent can keep drilling down
			var related = members.Select(m => m switch {
				PropertyInfo p => p.PropertyType,
				MethodInfo mi => mi.ReturnType,
				FieldInfo f => f.FieldType,
				_ => null,
			}).OfType<Type>().Select(UnwrapType).Where(t => types.Value.Contains(t)).Distinct().Take(3).ToList();
			if (related.Count > 0)
				sb.AppendLine("See also: " + string.Join(", ", related.Select(r => $"reference(\"{StripArity(r.Name)}\")")));
			return sb.ToString().TrimEnd();
		}

		static Type UnwrapType(Type t) {
			if (t.IsArray || t.IsByRef)
				return UnwrapType(t.GetElementType()!);
			if (t.IsGenericType && t.GetGenericArguments().Length == 1)
				return UnwrapType(t.GetGenericArguments()[0]);
			return t;
		}

		// ------------------------------------------------------------------ signatures

		static string StripArity(string name) {
			int tick = name.IndexOf('`');
			return tick >= 0 ? name.Substring(0, tick) : name;
		}

		static readonly Dictionary<Type, string> aliases = new Dictionary<Type, string> {
			[typeof(void)] = "void", [typeof(object)] = "object", [typeof(string)] = "string", [typeof(bool)] = "bool",
			[typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(char)] = "char", [typeof(short)] = "short",
			[typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint", [typeof(long)] = "long",
			[typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double", [typeof(decimal)] = "decimal",
		};

		static string TypeName(Type t) {
			if (aliases.TryGetValue(t, out var alias))
				return alias;
			if (t.IsByRef)
				return TypeName(t.GetElementType()!);
			if (t.IsArray)
				return TypeName(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
			if (t.IsPointer)
				return TypeName(t.GetElementType()!) + "*";
			if (t.IsGenericParameter)
				return t.Name;
			var name = StripArity(t.Name);
			if (t.IsNested && !t.IsGenericParameter)
				name = TypeName(t.DeclaringType!.IsGenericType ? t.DeclaringType.GetGenericTypeDefinition() : t.DeclaringType) + "." + name;
			if (t.IsGenericType) {
				if (t.GetGenericTypeDefinition() == typeof(Nullable<>))
					return TypeName(t.GetGenericArguments()[0]) + "?";
				var args = t.GetGenericArguments();
				if (t.IsNested)
					args = args.Skip(t.DeclaringType!.GetGenericArguments().Length).ToArray();
				if (args.Length > 0)
					name += "<" + string.Join(", ", args.Select(TypeName)) + ">";
			}
			return name;
		}

		static string Signature(MemberInfo m) {
			switch (m) {
			case ConstructorInfo c:
				return $"new {StripArity(c.DeclaringType!.Name)}({Params(c.GetParameters())})";
			case MethodInfo mi: {
				var stat = mi.IsStatic ? "static " : "";
				var gen = mi.IsGenericMethod ? "<" + string.Join(", ", mi.GetGenericArguments().Select(a => a.Name)) + ">" : "";
				return $"{stat}{TypeName(mi.ReturnType)} {mi.Name}{gen}({Params(mi.GetParameters())})";
			}
			case PropertyInfo p: {
				var acc = (p.GetMethod?.IsPublic == true ? "get; " : "") + (p.SetMethod?.IsPublic == true ? "set; " : "");
				var stat = (p.GetMethod ?? p.SetMethod)?.IsStatic == true ? "static " : "";
				var idx = p.GetIndexParameters();
				var name = idx.Length > 0 ? $"this[{Params(idx)}]" : p.Name;
				return $"{stat}{TypeName(p.PropertyType)} {name} {{ {acc}}}";
			}
			case FieldInfo f:
				return $"{(f.IsLiteral ? "const " : f.IsStatic ? "static " : "")}{(f.IsInitOnly ? "readonly " : "")}{TypeName(f.FieldType)} {f.Name}";
			case EventInfo e:
				return $"event {TypeName(e.EventHandlerType!)} {e.Name}";
			default:
				return m.Name;
			}
		}

		static string Params(ParameterInfo[] ps) => string.Join(", ", ps.Select(p => {
			var prefix = p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : p.GetCustomAttribute<ParamArrayAttribute>() is not null ? "params " : "";
			var def = p.HasDefaultValue ? " = " + (p.DefaultValue is null ? "null" : p.DefaultValue is string s ? $"\"{s}\"" : p.DefaultValue is bool b ? (b ? "true" : "false") : p.DefaultValue.ToString()) : "";
			return $"{prefix}{TypeName(p.ParameterType)} {p.Name}{def}";
		}));

		// ------------------------------------------------------------------ XML docs

		string Summary(Type owner, string? docId, string prefix) {
			if (docId is null)
				return string.Empty;
			var docs = GetDocs(owner.Assembly);
			var s = docs?.GetSummary(docId);
			return string.IsNullOrEmpty(s) ? string.Empty : prefix + s;
		}

		string FullDoc(Type owner, string? docId) {
			if (docId is null)
				return string.Empty;
			return GetDocs(owner.Assembly)?.GetFull(docId) ?? string.Empty;
		}

		XmlDocs? GetDocs(Assembly asm) {
			lock (xmlDocs) {
				if (!xmlDocs.TryGetValue(asm, out var docs)) {
					docs = XmlDocs.TryLoad(asm);
					xmlDocs[asm] = docs;
				}
				return docs;
			}
		}

		static string? GetDocId(MemberInfo m) {
			try {
				switch (m) {
				case Type t:
					return "T:" + DocTypeName(t, false);
				case MethodBase mb: {
					var name = mb is ConstructorInfo ? "#ctor" : mb.Name;
					var gen = mb is MethodInfo mi && mi.IsGenericMethod ? "``" + mi.GetGenericArguments().Length : "";
					var ps = mb.GetParameters();
					var p = ps.Length == 0 ? "" : "(" + string.Join(",", ps.Select(x => DocTypeName(x.ParameterType, true))) + ")";
					return $"M:{DocTypeName(mb.DeclaringType!, false)}.{name}{gen}{p}";
				}
				case PropertyInfo pi: {
					var ps = pi.GetIndexParameters();
					var p = ps.Length == 0 ? "" : "(" + string.Join(",", ps.Select(x => DocTypeName(x.ParameterType, true))) + ")";
					return $"P:{DocTypeName(pi.DeclaringType!, false)}.{pi.Name}{p}";
				}
				case FieldInfo f:
					return $"F:{DocTypeName(f.DeclaringType!, false)}.{f.Name}";
				case EventInfo e:
					return $"E:{DocTypeName(e.DeclaringType!, false)}.{e.Name}";
				}
			}
			catch {
			}
			return null;
		}

		static string DocTypeName(Type t, bool inSignature) {
			if (t.IsByRef)
				return DocTypeName(t.GetElementType()!, inSignature) + "@";
			if (t.IsPointer)
				return DocTypeName(t.GetElementType()!, inSignature) + "*";
			if (t.IsArray)
				return DocTypeName(t.GetElementType()!, inSignature) + (t.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", t.GetArrayRank())) + "]");
			if (t.IsGenericParameter)
				return (t.DeclaringMethod is not null ? "``" : "`") + t.GenericParameterPosition;
			if (inSignature && t.IsGenericType && !t.IsGenericTypeDefinition) {
				var def = t.GetGenericTypeDefinition();
				var baseName = (def.FullName ?? def.Name).Replace('+', '.');
				baseName = Regex.Replace(baseName, @"`\d+", "");
				return baseName + "{" + string.Join(",", t.GetGenericArguments().Select(a => DocTypeName(a, true))) + "}";
			}
			if (t.IsGenericTypeDefinition && inSignature) {
				// Open generic in a signature: written with the type's own parameters
				var baseName = Regex.Replace((t.FullName ?? t.Name).Replace('+', '.'), @"`\d+", "");
				return baseName + "{" + string.Join(",", t.GetGenericArguments().Select(a => DocTypeName(a, true))) + "}";
			}
			return (t.FullName ?? (t.Namespace + "." + t.Name)).Replace('+', '.');
		}

		static Dictionary<string, string> LoadTopics() {
			var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var asm = typeof(ReferenceProvider).Assembly;
			const string prefix = "dnSpy.Mcp.Topics.";
			foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(n => n)) {
				using var stream = asm.GetManifestResourceStream(res)!;
				using var reader = new StreamReader(stream);
				var name = res.Substring(prefix.Length);
				name = name.Substring(0, name.Length - ".md".Length);
				dict[name] = reader.ReadToEnd().Trim();
			}
			return dict;
		}

		sealed class XmlDocs {
			readonly Dictionary<string, XElement> members;

			XmlDocs(Dictionary<string, XElement> members) => this.members = members;

			public static XmlDocs? TryLoad(Assembly asm) {
				try {
					if (string.IsNullOrEmpty(asm.Location))
						return null;
					var xml = Path.ChangeExtension(asm.Location, ".xml");
					if (!File.Exists(xml))
						return null;
					var doc = XDocument.Load(xml);
					var dict = new Dictionary<string, XElement>(StringComparer.Ordinal);
					foreach (var m in doc.Descendants("member")) {
						var name = (string?)m.Attribute("name");
						if (name is not null)
							dict[name] = m;
					}
					return new XmlDocs(dict);
				}
				catch {
					return null;
				}
			}

			XElement? Find(string docId) {
				if (members.TryGetValue(docId, out var e))
					return e;
				// Fall back to the first overload with the same name
				int paren = docId.IndexOf('(');
				var prefix = paren >= 0 ? docId.Substring(0, paren) : docId;
				return members.FirstOrDefault(kv => kv.Key.StartsWith(prefix + "(", StringComparison.Ordinal) || kv.Key == prefix).Value;
			}

			public string? GetSummary(string docId) {
				var e = Find(docId)?.Element("summary");
				return e is null ? null : Clean(e);
			}

			public string GetFull(string docId) {
				var e = Find(docId);
				if (e is null)
					return string.Empty;
				var sb = new StringBuilder();
				if (e.Element("summary") is XElement s)
					sb.AppendLine(Clean(s));
				foreach (var p in e.Elements("param")) {
					var text = Clean(p);
					if (text.Length > 0)
						sb.AppendLine($"{(string?)p.Attribute("name")}: {text}");
				}
				if (e.Element("returns") is XElement r && Clean(r) is { Length: > 0 } rt)
					sb.AppendLine("returns: " + rt);
				return sb.ToString().TrimEnd();
			}

			static string Clean(XElement e) {
				var sb = new StringBuilder();
				foreach (var node in e.Nodes()) {
					switch (node) {
					case XText text:
						sb.Append(text.Value);
						break;
					case XElement el when el.Name == "see" || el.Name == "seealso":
						var cref = (string?)el.Attribute("cref") ?? (string?)el.Attribute("langword") ?? (string?)el.Attribute("href") ?? "";
						if (cref.Length > 2 && cref[1] == ':')
							cref = cref.Substring(2);
						int paren = cref.IndexOf('(');
						if (paren >= 0)
							cref = cref.Substring(0, paren);
						sb.Append(el.Value.Length > 0 ? el.Value : cref.Split('.').Last());
						break;
					case XElement el when el.Name == "paramref" || el.Name == "typeparamref":
						sb.Append((string?)el.Attribute("name"));
						break;
					case XElement el:
						sb.Append(Clean(el));
						break;
					}
				}
				return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
			}
		}
	}
}
