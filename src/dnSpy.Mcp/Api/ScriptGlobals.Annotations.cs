using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;

namespace dnSpy.Mcp.Api {
	public sealed partial class ScriptGlobals {
		AnnotationStore Store => host.Annotations;

		/// <summary>Folder holding the persisted annotations (one JSON file per input file SHA-256).</summary>
		public string AnnotationsDirectory => Store.Directory;

		/// <summary>
		/// Renames a type, method, field, property or event, in dnSpy and persistently: the rename is
		/// stored per input file (by SHA-256) and re-applied whenever the file is opened again.
		/// For types, "Ns.Name" also changes the namespace. Returns a description of the change.
		/// </summary>
		public string Rename(object target, string newName) {
			if (string.IsNullOrWhiteSpace(newName))
				throw new ArgumentException("newName is empty");
			var def = Def(target);
			if (def is not (TypeDef or MethodDef or FieldDef or PropertyDef or EventDef))
				throw new ArgumentException($"Can't rename a {def.GetType().Name}");
			if (def is MethodDef { IsConstructor: true })
				throw new ArgumentException("Constructors can't be renamed");
			var module = def.Module;
			var before = def.FullName;
			Store.Update(module, a => {
				var key = AnnotationKeys.TokenKey(def);
				if (!a.Renames.TryGetValue(key, out var entry)) {
					entry = new RenameEntry { Kind = AnnotationKeys.Kind(def), OriginalName = def.Name };
					if (def is TypeDef t0)
						entry.OriginalNamespace = t0.Namespace;
					a.Renames[key] = entry;
				}
				var name = newName.Trim();
				if (def is TypeDef t && t.DeclaringType is null && name.LastIndexOf('.') is int dot and > 0) {
					entry.Namespace = name.Substring(0, dot);
					entry.Name = name.Substring(dot + 1);
					t.Namespace = entry.Namespace;
				}
				else {
					entry.Name = name;
					entry.Namespace = null;
				}
				entry.Updated = DateTime.UtcNow;
				AnnotationKeys.SetName(def, entry.Name);
				return 0;
			});
			RefreshQuietly(module);
			return $"Renamed {AnnotationsReport.Printable(before)} -> {def.FullName}";
		}

		/// <summary>Renames a method parameter (by index, 0 = first declared parameter, or by current name), persistently.</summary>
		public string RenameParameter(object method, object parameter, string newName) {
			var m = Def(method) as MethodDef ?? throw new ArgumentException("RenameParameter() needs a method");
			var ps = m.Parameters.Where(p => p.IsNormalMethodParameter).ToList();
			var p = parameter switch {
				int i when i >= 0 && i < ps.Count => ps[i],
				string s => ps.FirstOrDefault(x => x.Name == s) ?? throw new KeyNotFoundException($"No parameter '{s}' in {m.FullName}"),
				_ => throw new ArgumentException($"Parameter must be an index 0..{ps.Count - 1} or a name"),
			};
			var old = p.Name;
			Store.Update(m.Module, a => {
				var key = AnnotationKeys.ParamKey(m, p.Index);
				if (!a.Renames.TryGetValue(key, out var entry))
					a.Renames[key] = entry = new RenameEntry { Kind = "Parameter", OriginalName = p.Name ?? "" };
				entry.Name = newName.Trim();
				entry.Updated = DateTime.UtcNow;
				p.CreateParamDef();
				p.Name = entry.Name;
				return 0;
			});
			RefreshQuietly(m.Module);
			return $"Renamed parameter {old} -> {p.Name} in {m.FullName}";
		}

		/// <summary>Restores the original name of something renamed with <see cref="Rename"/> and forgets the rename.</summary>
		public string Unrename(object target) {
			var def = Def(target);
			var key = AnnotationKeys.TokenKey(def);
			var result = Store.Update(def.Module, a => {
				if (!a.Renames.Remove(key, out var entry))
					return $"{def.FullName} has no stored rename";
				AnnotationKeys.SetName(def, entry.OriginalName);
				if (def is TypeDef t && entry.OriginalNamespace is not null)
					t.Namespace = entry.OriginalNamespace;
				return $"Restored {def.FullName}";
			});
			RefreshQuietly(def.Module);
			return result;
		}

		/// <summary>
		/// Attaches a persistent comment to a definition, or to an IL offset inside a method
		/// (<paramref name="ilOffset"/>). Comments show up in Decompile() and IL() output and in Annotations().
		/// Null or empty text deletes the comment.
		/// </summary>
		public string Comment(object target, string? text, uint? ilOffset = null) {
			var def = Def(target);
			string key, display;
			if (ilOffset is uint off) {
				var m = def as MethodDef ?? throw new ArgumentException("An IL offset needs a method");
				key = AnnotationKeys.OffsetKey(m, off);
				display = $"{m.FullName} @IL_{off:X4}";
			}
			else {
				key = AnnotationKeys.TokenKey(def);
				display = def.FullName;
			}
			return Store.Update(def.Module, a => {
				if (string.IsNullOrWhiteSpace(text))
					return a.Comments.Remove(key) ? $"Deleted comment on {display}" : $"No comment on {display}";
				a.Comments[key] = new CommentEntry { Target = display, Text = text.Trim(), Updated = DateTime.UtcNow };
				return $"Commented {display}";
			});
		}

		/// <summary>Records a finding about a sample (default: the only loaded sample). Notes persist across sessions.</summary>
		public string Note(string text, ModuleDef? module = null) {
			var mod = DefaultModule(module, "Note()");
			return Store.Update(mod, a => {
				a.Notes.Add(new NoteEntry { Time = DateTime.UtcNow, Text = text.Trim() });
				return $"Note {a.Notes.Count - 1} added to {mod.Name}";
			});
		}

		/// <summary>The notes recorded for a sample (default: the only loaded sample).</summary>
		public IReadOnlyList<NoteEntry> Notes(ModuleDef? module = null) =>
			Store.TryGet(DefaultModule(module, "Notes()"))?.Notes.ToList() ?? new List<NoteEntry>();

		/// <summary>Deletes a note by index (see <see cref="Notes"/>).</summary>
		public string DeleteNote(int index, ModuleDef? module = null) {
			var mod = DefaultModule(module, "DeleteNote()");
			return Store.Update(mod, a => {
				if (index < 0 || index >= a.Notes.Count)
					throw new ArgumentOutOfRangeException(nameof(index), $"There are {a.Notes.Count} notes");
				a.Notes.RemoveAt(index);
				return $"Deleted note {index}";
			});
		}

		/// <summary>
		/// Everything recorded about a sample: notes, renames and comments. With no module, covers every
		/// loaded sample. Call this at the start of a session to pick up earlier work.
		/// </summary>
		public AnnotationsReport Annotations(ModuleDef? module = null) {
			var mods = module is null ? SampleModules : new[] { module };
			return new AnnotationsReport(mods.Select(m => (m, Store.TryGet(m))).ToList());
		}

		void RefreshQuietly(ModuleDef module) {
			try {
				Refresh(module);
			}
			catch {
			}
		}

		// ------------------------------------------------------------------ used by Decompile()/IL()

		/// <summary>Comments that apply to a definition (and its members, for a type), as "// " lines.</summary>
		string CommentHeader(IMemberDef def, bool includeOffsets = true) {
			var a = Store.TryGet(def.Module);
			if (a is null || a.Comments.Count == 0)
				return string.Empty;
			var tokens = new HashSet<uint> { def.MDToken.Raw };
			if (def is TypeDef t) {
				foreach (var nt in t.GetTypes().Prepend(t)) {
					tokens.Add(nt.MDToken.Raw);
					foreach (var m in nt.Methods) tokens.Add(m.MDToken.Raw);
					foreach (var f in nt.Fields) tokens.Add(f.MDToken.Raw);
					foreach (var p in nt.Properties) tokens.Add(p.MDToken.Raw);
					foreach (var e in nt.Events) tokens.Add(e.MDToken.Raw);
				}
			}
			var lines = a.Comments.Where(kv => {
				var (tok, _, off) = AnnotationKeys.ParseKey(kv.Key);
				return tokens.Contains(tok) && (includeOffsets || off is null);
			}).ToList();
			if (lines.Count == 0)
				return string.Empty;
			var sb = new StringBuilder("// [annotations]\n");
			foreach (var (_, c) in lines)
				sb.Append("//   ").Append(c.Target).Append(": ").Append(c.Text.Replace("\n", "\n//     ")).Append('\n');
			return sb.Append('\n').ToString();
		}

		/// <summary>IL-offset comments of a method.</summary>
		Dictionary<uint, string> OffsetComments(MethodDef m) {
			var result = new Dictionary<uint, string>();
			var a = Store.TryGet(m.Module);
			if (a is null)
				return result;
			foreach (var (key, c) in a.Comments) {
				var (tok, _, off) = AnnotationKeys.ParseKey(key);
				if (tok == m.MDToken.Raw && off is uint o)
					result[o] = c.Text;
			}
			return result;
		}
	}
}
