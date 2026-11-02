using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Mcp.Api {
	public sealed partial class ScriptGlobals {
		/// <summary>Names of the available decompilers (languages), e.g. "C#", "Visual Basic", "IL".</summary>
		public IEnumerable<string> Languages => host.DecompilerService.AllDecompilers.Select(d => d.UniqueNameUI).Distinct();

		/// <summary>
		/// Decompiles a type, method, field, property, event, module or assembly (or a spec string /
		/// token accepted by <see cref="Def"/>) and returns the source text.
		/// <paramref name="language"/>: "C#" (default), "VB", "IL", or any name from <see cref="Languages"/>.
		/// </summary>
		public string Decompile(object target, string? language = null) {
			var decompiler = FindDecompiler(language);
			var output = new StringBuilderDecompilerOutput();
			var ctx = new DecompilationContext {
				CancellationToken = Token,
				GetDisableAssemblyLoad = () => host.DocumentService.DisableAssemblyLoad(),
			};
			string header = string.Empty;
			switch (target) {
			case AssemblyDef asm:
				decompiler.Decompile(asm, output, ctx);
				break;
			case ModuleDef mod:
				decompiler.Decompile(mod, output, ctx);
				break;
			default:
				var def = Def(target);
				try {
					header = CommentHeader(def);
				}
				catch {
				}
				switch (def) {
				case TypeDef t:
					decompiler.Decompile(t, output, ctx);
					break;
				case MethodDef m:
					decompiler.Decompile(m, output, ctx);
					break;
				case FieldDef f:
					decompiler.Decompile(f, output, ctx);
					break;
				case PropertyDef p:
					decompiler.Decompile(p, output, ctx);
					break;
				case EventDef e:
					decompiler.Decompile(e, output, ctx);
					break;
				case var other:
					throw new ArgumentException($"Can't decompile a {other.GetType().Name}");
				}
				break;
			}
			return header + output.ToString();
		}

		/// <summary>Decompiles every type of a namespace in a module.</summary>
		public string DecompileNamespace(ModuleDef module, string ns, string? language = null) {
			var decompiler = FindDecompiler(language);
			var output = new StringBuilderDecompilerOutput();
			var ctx = new DecompilationContext { CancellationToken = Token };
			var types = module.Types.Where(t => t.Namespace == ns).ToList();
			decompiler.DecompileNamespace(ns, types, output, ctx);
			return output.ToString();
		}

		IDecompiler FindDecompiler(string? language) {
			var all = host.DecompilerService.AllDecompilers.ToList();
			if (string.IsNullOrWhiteSpace(language))
				language = "C#";
			var lang = language.Trim();
			string alias = lang.ToLowerInvariant() switch {
				"cs" or "csharp" or "c#" => "C#",
				"vb" or "vb.net" or "visualbasic" => "Visual Basic",
				"il" or "msil" or "cil" => "IL",
				_ => lang,
			};
			var d = all.FirstOrDefault(x => string.Equals(x.UniqueNameUI, alias, StringComparison.OrdinalIgnoreCase))
				?? all.FirstOrDefault(x => string.Equals(x.GenericNameUI, alias, StringComparison.OrdinalIgnoreCase))
				?? all.FirstOrDefault(x => x.UniqueNameUI.StartsWith(alias, StringComparison.OrdinalIgnoreCase));
			return d ?? throw new KeyNotFoundException($"No decompiler named '{language}'. Available: {string.Join(", ", all.Select(x => x.UniqueNameUI))}");
		}

		/// <summary>
		/// Compact IL listing straight from dnlib: header, locals, instructions ("IL_0000: ldstr ...")
		/// and exception handlers. Use Decompile(m, "IL") for dnSpy's full IL view.
		/// </summary>
		public string IL(object method) {
			var m = Def(method) as MethodDef ?? throw new ArgumentException("IL() needs a method");
			var sb = new StringBuilder();
			sb.AppendLine($"// {m.FullName}  [0x{m.MDToken.Raw:X8}]  RVA 0x{(uint)m.RVA:X}");
			if (!m.HasBody) {
				sb.AppendLine(m.IsAbstract ? "// abstract" : m.IsPinvokeImpl ? $"// P/Invoke {m.ImplMap?.Module?.Name}!{m.ImplMap?.Name}" : "// no body");
				return sb.ToString();
			}
			var body = m.Body;
			sb.Append(CommentHeader(m, includeOffsets: false));
			var offsetComments = OffsetComments(m);
			sb.AppendLine($"// maxstack {body.MaxStack}, initlocals {body.InitLocals}, code size {body.GetCodeSize()} bytes");
			if (body.Variables.Count > 0) {
				sb.AppendLine("// locals:");
				foreach (var v in body.Variables)
					sb.AppendLine($"//   V_{v.Index}: {v.Type?.FullName}{(string.IsNullOrEmpty(v.Name) ? "" : " " + v.Name)}");
			}
			var handlersByStart = body.ExceptionHandlers.ToLookup(h => h.TryStart);
			foreach (var instr in body.Instructions) {
				foreach (var h in handlersByStart[instr])
					sb.AppendLine($"  // .try IL_{h.TryStart?.Offset:X4}-IL_{h.TryEnd?.Offset ?? (uint)body.GetCodeSize():X4} {DescribeHandler(h)}");
				if (offsetComments.TryGetValue(instr.Offset, out var note))
					sb.AppendLine("  // NOTE: " + note.Replace("\n", "\n  //       "));
				sb.Append("  ").AppendLine(instr.ToString());
			}
			return sb.ToString();
		}

		static string DescribeHandler(ExceptionHandler h) {
			var range = $"IL_{h.HandlerStart?.Offset:X4}";
			return h.HandlerType switch {
				ExceptionHandlerType.Catch => $"catch {h.CatchType?.FullName} handler {range}",
				ExceptionHandlerType.Filter => $"filter IL_{h.FilterStart?.Offset:X4} handler {range}",
				ExceptionHandlerType.Finally => $"finally {range}",
				ExceptionHandlerType.Fault => $"fault {range}",
				_ => range,
			};
		}
	}
}
