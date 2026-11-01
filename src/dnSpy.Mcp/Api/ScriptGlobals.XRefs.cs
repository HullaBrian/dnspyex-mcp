using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Mcp.Api {
	/// <summary>One instruction that references something.</summary>
	public sealed class XRef {
		/// <summary>The method containing the instruction</summary>
		public MethodDef Method { get; }
		/// <summary>The instruction</summary>
		public Instruction Instruction { get; }
		public uint Offset => Instruction.Offset;
		public OpCode OpCode => Instruction.OpCode;
		public object? Operand => Instruction.Operand;

		public XRef(MethodDef method, Instruction instruction) {
			Method = method;
			Instruction = instruction;
		}

		public override string ToString() => $"{Method.FullName} @IL_{Offset:X4}: {OpCode.Name} {ResultFormatter.OperandText(Operand)}";
	}

	/// <summary>A string literal (ldstr) found in a method body.</summary>
	public sealed class StringRef {
		public string Value { get; }
		public MethodDef Method { get; }
		public uint Offset { get; }

		public StringRef(string value, MethodDef method, uint offset) {
			Value = value;
			Method = method;
			Offset = offset;
		}

		public override string ToString() => $"{ResultFormatter.Quote(Value)}  <- {Method.FullName} @IL_{Offset:X4}";
	}

	public sealed partial class ScriptGlobals {
		IEnumerable<MethodDef> MethodsWithBodies(ModuleDef? module) {
			foreach (var mod in Scope(module)) {
				foreach (var t in mod.GetTypes()) {
					Token.ThrowIfCancellationRequested();
					foreach (var m in t.Methods) {
						if (m.HasBody)
							yield return m;
					}
				}
			}
		}

		/// <summary>
		/// Every instruction (in all loaded modules, or <paramref name="module"/>) that references
		/// <paramref name="target"/>: a method (calls, ldftn, newobj...), field (ld/st), type
		/// (newobj/castclass/ldtoken/... and uses of its members), property or event (its accessors).
		/// Target can be a definition, a reference, or a spec string. Like dnSpy's Analyzer "Used By".
		/// </summary>
		public IReadOnlyList<XRef> XrefsTo(object target, ModuleDef? module = null) {
			var def = Def(target);
			var results = new List<XRef>();
			switch (def) {
			case MethodDef md:
				Scan(module, results, op => op is IMethod im && SameMethod(im, md));
				break;
			case FieldDef fd:
				Scan(module, results, op => op is IField f && f.Name == fd.Name && f.ResolveFieldDef() == fd);
				break;
			case PropertyDef pd: {
				var accessors = pd.GetMethods.Concat(pd.SetMethods).Concat(pd.OtherMethods).ToHashSet();
				Scan(module, results, op => op is IMethod im && accessors.Any(a => SameMethod(im, a)));
				break;
			}
			case EventDef ed: {
				var accessors = new[] { ed.AddMethod, ed.RemoveMethod, ed.InvokeMethod }.Concat(ed.OtherMethods).OfType<MethodDef>().ToList();
				Scan(module, results, op => op is IMethod im && accessors.Any(a => SameMethod(im, a)));
				break;
			}
			case TypeDef td:
				Scan(module, results, op => ReferencesType(op, td));
				break;
			}
			return results;
		}

		/// <summary>Methods that call/reference <paramref name="target"/> (distinct <see cref="XRef.Method"/> of <see cref="XrefsTo"/>).</summary>
		public IReadOnlyList<MethodDef> Callers(object target, ModuleDef? module = null) =>
			XrefsTo(target, module).Select(x => x.Method).Distinct().ToList();

		/// <summary>Every instruction in <paramref name="method"/> that references a method, field, type or string.</summary>
		public IReadOnlyList<XRef> XrefsFrom(object method) {
			var m = Def(method) as MethodDef ?? throw new ArgumentException("XrefsFrom() needs a method");
			if (!m.HasBody)
				return Array.Empty<XRef>();
			return m.Body.Instructions
				.Where(i => i.Operand is IMethod or IField or ITypeDefOrRef or string or MethodSig)
				.Select(i => new XRef(m, i)).ToList();
		}

		/// <summary>Distinct methods called (call/callvirt/newobj/ldftn...) by <paramref name="method"/>.</summary>
		public IReadOnlyList<IMethod> Callees(object method) =>
			XrefsFrom(method).Select(x => x.Operand).OfType<IMethod>().Where(im => im.IsMethod).Distinct(MethodEqualityComparer.CompareDeclaringTypes).ToList();

		/// <summary>
		/// String literals (ldstr) in method bodies. <paramref name="pattern"/>: case-insensitive
		/// substring, '*'/'?' wildcards, or "/regex/". Null returns all strings.
		/// </summary>
		public IEnumerable<StringRef> Strings(string? pattern = null, ModuleDef? module = null) {
			var matcher = NameMatcher.Create(pattern);
			foreach (var m in MethodsWithBodies(module)) {
				IList<Instruction> instrs;
				try {
					instrs = m.Body.Instructions;
				}
				catch {
					continue; // Invalid body (obfuscated)
				}
				foreach (var instr in instrs) {
					if (instr.OpCode.Code == Code.Ldstr && instr.Operand is string s && matcher.IsMatch(s))
						yield return new StringRef(s, m, instr.Offset);
				}
			}
		}

		/// <summary>Types that derive from <paramref name="type"/> or implement it (if it's an interface), transitively.</summary>
		public IReadOnlyList<TypeDef> DerivedTypes(object type, ModuleDef? module = null) {
			var baseType = Def(type) as TypeDef ?? throw new ArgumentException("DerivedTypes() needs a type");
			var all = Scope(module).SelectMany(m => m.GetTypes()).ToList();
			var result = new List<TypeDef>();
			var known = new HashSet<TypeDef> { baseType };
			bool changed = true;
			while (changed) {
				changed = false;
				foreach (var t in all) {
					if (known.Contains(t))
						continue;
					bool derives = t.BaseType?.ResolveTypeDef() is TypeDef b && known.Contains(b);
					derives |= t.Interfaces.Any(ii => ii.Interface?.ResolveTypeDef() is TypeDef it && known.Contains(it));
					if (derives) {
						known.Add(t);
						result.Add(t);
						changed = true;
					}
				}
			}
			return result;
		}

		/// <summary>Methods that override or implement <paramref name="method"/> (virtual/interface methods).</summary>
		public IReadOnlyList<MethodDef> Overrides(object method, ModuleDef? module = null) {
			var m = Def(method) as MethodDef ?? throw new ArgumentException("Overrides() needs a method");
			var result = new List<MethodDef>();
			foreach (var t in DerivedTypes(m.DeclaringType, module)) {
				foreach (var cand in t.Methods) {
					if (!cand.IsVirtual)
						continue;
					bool explicitImpl = cand.Overrides.Any(o => SameMethod(o.MethodDeclaration, m));
					bool implicitImpl = cand.Name == m.Name && new SigComparer().Equals(cand.MethodSig, m.MethodSig);
					if (explicitImpl || implicitImpl)
						result.Add(cand);
				}
			}
			return result;
		}

		void Scan(ModuleDef? module, List<XRef> results, Func<object, bool> matches) {
			foreach (var m in MethodsWithBodies(module)) {
				IList<Instruction> instrs;
				try {
					instrs = m.Body.Instructions;
				}
				catch {
					continue;
				}
				foreach (var instr in instrs) {
					var op = instr.Operand;
					if (op is null || op is Instruction || op is string || op is Local || op is Parameter || op is Instruction[] || op.GetType().IsPrimitive)
						continue;
					if (matches(op))
						results.Add(new XRef(m, instr));
				}
			}
		}

		static bool SameMethod(IMethod candidate, MethodDef target) {
			if (candidate is MethodSpec spec)
				candidate = spec.Method;
			if (candidate is MethodDef md)
				return md == target;
			if (candidate.Name != target.Name)
				return false;
			return candidate.ResolveMethodDef() == target;
		}

		static bool ReferencesType(object op, TypeDef td) {
			switch (op) {
			case ITypeDefOrRef tr:
				if (tr is TypeSpec ts)
					return ts.TypeSig?.ToTypeDefOrRef()?.ResolveTypeDef() == td || SigContains(ts.TypeSig, td);
				return tr.Name == td.Name && tr.ResolveTypeDef() == td;
			case IMethod im when im.DeclaringType is ITypeDefOrRef dt:
				return ReferencesType(dt, td);
			case IField f when f.DeclaringType is ITypeDefOrRef dt:
				return ReferencesType(dt, td);
			default:
				return false;
			}
		}

		static bool SigContains(TypeSig? sig, TypeDef td) {
			for (int depth = 0; sig is not null && depth < 32; depth++) {
				if (sig is GenericInstSig gis) {
					if (gis.GenericType?.TypeDefOrRef.ResolveTypeDef() == td)
						return true;
					return gis.GenericArguments.Any(a => SigContains(a, td));
				}
				if (sig is TypeDefOrRefSig tdr)
					return tdr.TypeDefOrRef.ResolveTypeDef() == td;
				sig = sig.Next;
			}
			return false;
		}
	}
}
