using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Documents;

namespace dnSpy.Mcp.Api {
	/// <summary>Turns script results into text that is useful to an agent.</summary>
	public static class ResultFormatter {
		public static string Format(object? value, int maxItems = 200) {
			switch (value) {
			case null:
				return "null";
			case string s:
				return s;
			case IDictionary dict:
				return FormatDictionary(dict, maxItems);
			case IEnumerable seq when !IsScalar(value):
				return FormatSequence(seq, maxItems);
			default:
				return FormatInline(value);
			}
		}

		static bool IsScalar(object value) => value is string || value is IMemberRef || value is ModuleDef || value is AssemblyDef || value is UTF8String;

		static string FormatSequence(IEnumerable seq, int maxItems) {
			var sb = new StringBuilder();
			int n = 0;
			bool more = false;
			foreach (var item in seq) {
				if (n == maxItems) {
					more = true;
					break;
				}
				sb.AppendLine(FormatInline(item));
				n++;
			}
			int? total = seq is ICollection c ? c.Count : null;
			if (more)
				sb.AppendLine(total is int t ? $"... ({t - maxItems} more, {t} total)" : $"... (more than {maxItems} items; use .Take()/.Skip() or Count())");
			else if (n == 0)
				return "(empty)";
			else
				sb.Append($"({n} item{(n == 1 ? "" : "s")})");
			return sb.ToString().TrimEnd();
		}

		static string FormatDictionary(IDictionary dict, int maxItems) {
			var sb = new StringBuilder();
			int n = 0;
			foreach (DictionaryEntry kv in dict) {
				if (n++ == maxItems) {
					sb.AppendLine($"... ({dict.Count - maxItems} more)");
					break;
				}
				sb.AppendLine($"{FormatInline(kv.Key)}: {FormatInline(kv.Value)}");
			}
			return n == 0 ? "(empty)" : sb.ToString().TrimEnd();
		}

		/// <summary>Single-line description of a value.</summary>
		public static string FormatInline(object? value) {
			switch (value) {
			case null:
				return "null";
			case string s:
				return s.Contains('\n') ? Quote(s) : s;
			case UTF8String u:
				return u.String ?? string.Empty;
			case TypeDef t:
				return $"{t.FullName}  [0x{t.MDToken.Raw:X8}] ({t.Module?.Name})";
			case IMemberDef md:
				return $"{md.FullName}  [0x{md.MDToken.Raw:X8}]";
			case IMemberRef mr:
				return mr.FullName;
			case ModuleDef mod:
				return $"{mod.Name}  ({mod.Location})";
			case AssemblyDef asm:
				return asm.FullName;
			case IDsDocument doc:
				return doc.Filename;
			case Instruction instr:
				return instr.ToString();
			case Local local:
				return $"V_{local.Index} {local.Type?.FullName}";
			case Parameter p:
				return $"{p.Name}: {p.Type?.FullName}";
			case Exception ex:
				return $"{ex.GetType().Name}: {ex.Message}";
			case IFormattable f:
				return f.ToString(null, CultureInfo.InvariantCulture);
			case byte[] bytes:
				return $"byte[{bytes.Length}] {BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 64)).Replace("-", " ")}{(bytes.Length > 64 ? " ..." : "")}";
			case IEnumerable seq when value is not IMemberRef: {
				var items = seq.Cast<object?>().Take(11).Select(FormatInline).ToList();
				var more = items.Count > 10 ? ", ..." : "";
				return "[" + string.Join(", ", items.Take(10)) + more + "]";
			}
			}
			var str = value.ToString();
			var type = value.GetType();
			if (str is null || str == type.FullName || str == type.ToString())
				return DumpProperties(value);
			return str.Replace("\r", "").Replace("\n", " ");
		}

		static string DumpProperties(object value) {
			var type = value.GetType();
			var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0).Take(20);
			var parts = new List<string>();
			foreach (var p in props) {
				string v;
				try {
					var pv = p.GetValue(value);
					v = pv is null ? "null" : pv is string s ? Quote(s, 80) : pv.GetType().IsPrimitive || pv is Enum ? pv.ToString()! : pv is IMemberRef mr ? mr.FullName : pv.GetType().Name;
				}
				catch (Exception ex) {
					v = $"<{ex.GetType().Name}>";
				}
				parts.Add($"{p.Name} = {v}");
			}
			return $"{type.Name} {{ {string.Join(", ", parts)} }}";
		}

		public static string OperandText(object? operand) => operand switch {
			null => string.Empty,
			string s => Quote(s, 120),
			Instruction i => $"IL_{i.Offset:X4}",
			IList<Instruction> targets => "(" + string.Join(", ", targets.Select(t => $"IL_{t?.Offset:X4}")) + ")",
			IMemberRef mr => mr.FullName,
			Local l => $"V_{l.Index}",
			Parameter p => string.IsNullOrEmpty(p.Name) ? $"A_{p.Index}" : p.Name,
			IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
			_ => operand.ToString() ?? string.Empty,
		};

		/// <summary>C#-style quoted string literal, optionally truncated.</summary>
		public static string Quote(string s, int maxLength = int.MaxValue) {
			var sb = new StringBuilder(s.Length + 2);
			sb.Append('"');
			int n = 0;
			foreach (var c in s) {
				if (n++ >= maxLength) {
					sb.Append("...");
					break;
				}
				switch (c) {
				case '"': sb.Append("\\\""); break;
				case '\\': sb.Append("\\\\"); break;
				case '\n': sb.Append("\\n"); break;
				case '\r': sb.Append("\\r"); break;
				case '\t': sb.Append("\\t"); break;
				case '\0': sb.Append("\\0"); break;
				default:
					if (char.IsControl(c))
						sb.Append($"\\u{(int)c:X4}");
					else
						sb.Append(c);
					break;
				}
			}
			sb.Append('"');
			return sb.ToString();
		}
	}

	/// <summary>Pattern used by Types()/Methods()/Strings(): substring, wildcard or /regex/ (all case-insensitive).</summary>
	sealed class NameMatcher {
		readonly Func<string, bool> isMatch;

		NameMatcher(Func<string, bool> isMatch) => this.isMatch = isMatch;

		public bool IsMatch(string? s) => s is not null && isMatch(s);

		public static NameMatcher Create(string? pattern) {
			if (string.IsNullOrEmpty(pattern))
				return new NameMatcher(_ => true);
			if (pattern.Length >= 2 && pattern[0] == '/' && pattern[pattern.Length - 1] == '/') {
				var re = new Regex(pattern.Substring(1, pattern.Length - 2), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
				return new NameMatcher(re.IsMatch);
			}
			if (pattern.IndexOfAny(new[] { '*', '?' }) >= 0) {
				var re = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
				return new NameMatcher(re.IsMatch);
			}
			return new NameMatcher(s => s.Contains(pattern, StringComparison.OrdinalIgnoreCase));
		}
	}
}
