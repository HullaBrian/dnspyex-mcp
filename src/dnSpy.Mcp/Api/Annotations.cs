using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using dnlib.DotNet;

namespace dnSpy.Mcp.Api {
	/// <summary>A persisted rename (see <see cref="ScriptGlobals.Rename"/>).</summary>
	public sealed class RenameEntry {
		/// <summary>Type, Method, Field, Property, Event or Parameter</summary>
		public string Kind { get; set; } = "";
		public string OriginalName { get; set; } = "";
		public string? OriginalNamespace { get; set; }
		public string Name { get; set; } = "";
		public string? Namespace { get; set; }
		public DateTime Updated { get; set; }
	}

	/// <summary>A persisted comment on a definition or an IL offset in a method.</summary>
	public sealed class CommentEntry {
		/// <summary>Full name of the target when the comment was written (for display)</summary>
		public string Target { get; set; } = "";
		public string Text { get; set; } = "";
		public DateTime Updated { get; set; }
	}

	/// <summary>A free-form finding about a sample.</summary>
	public sealed class NoteEntry {
		public DateTime Time { get; set; }
		public string Text { get; set; } = "";
		public override string ToString() => $"[{Time.ToLocalTime():yyyy-MM-dd HH:mm}] {Text}";
	}

	/// <summary>Everything recorded about one file (keyed by its SHA-256).</summary>
	public sealed class SampleAnnotations {
		public string Sha256 { get; set; } = "";
		/// <summary>Last known path of the file</summary>
		public string? File { get; set; }
		/// <summary>SHA-256 of the file this one was saved from (after Save())</summary>
		public string? DerivedFrom { get; set; }
		/// <summary>Key: definition token ("0x06000012"), or "0x06000012#p1" for parameter 1 of a method</summary>
		public SortedDictionary<string, RenameEntry> Renames { get; set; } = new();
		/// <summary>Key: definition token ("0x06000012") or "0x06000012@IL_001A" for an instruction</summary>
		public SortedDictionary<string, CommentEntry> Comments { get; set; } = new();
		public List<NoteEntry> Notes { get; set; } = new();

		[JsonIgnore]
		public bool IsEmpty => Renames.Count == 0 && Comments.Count == 0 && Notes.Count == 0;
	}

	/// <summary>
	/// Persists renames, comments and notes per input file (by SHA-256 of the file on disk) in
	/// %LOCALAPPDATA%\dnSpy.Mcp\annotations, and re-applies renames whenever the file is opened.
	/// Shared by all script sessions.
	/// </summary>
	public sealed class AnnotationStore {
		static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

		readonly object lockObj = new();
		// Cached per file hash, with the JSON file's write time so external edits/deletions are picked up
		readonly Dictionary<string, (SampleAnnotations data, DateTime? stamp)> cache = new(StringComparer.OrdinalIgnoreCase);
		readonly ConditionalWeakTable<ModuleDef, string> hashes = new();
		// Modules whose file was overwritten by Save() with renumbered tokens: their in-memory tokens no
		// longer match the file, so annotations can't be keyed until the file is reopened.
		readonly ConditionalWeakTable<ModuleDef, string> staleModules = new();
		// Serializes read-modify-write of the JSON files across dnSpy instances
		static readonly Mutex fileMutex = new(false, @"Local\dnSpy.Mcp.annotations");

		public string Directory { get; }

		public AnnotationStore(string? directory = null) =>
			Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dnSpy.Mcp", "annotations");

		/// <summary>SHA-256 of the module's file (computed once per loaded module), or null for in-memory modules.</summary>
		public string? KeyOf(ModuleDef module) {
			lock (lockObj) {
				if (hashes.TryGetValue(module, out var h))
					return h;
				var loc = module.Location;
				if (string.IsNullOrEmpty(loc) || !File.Exists(loc))
					return null;
				try {
					using var fs = new FileStream(loc, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
					h = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
				}
				catch {
					return null;
				}
				hashes.AddOrUpdate(module, h);
				return h;
			}
		}

		string RequireKey(ModuleDef module) {
			if (staleModules.TryGetValue(module, out var path))
				throw new InvalidOperationException($"{module.Name} was saved over its own file ({path}) and its tokens were renumbered. " +
					"Close and reopen it (close_assembly + open_assembly, or Unload + Load) before adding annotations; existing ones were carried over.");
			return KeyOf(module) ?? throw new InvalidOperationException($"{module.Name} has no file on disk, so annotations can't be stored. Save it (Save(module, path)) and Load() that file.");
		}

		/// <summary>
		/// A snapshot of the annotations of a module's file (an empty object if there are none). Changes to
		/// the snapshot are not saved; use <see cref="Update"/>.
		/// </summary>
		public SampleAnnotations Get(ModuleDef module) {
			var key = RequireKey(module);
			lock (lockObj) {
				var a = Clone(GetByKey(key));
				a.File = module.Location;
				return a;
			}
		}

		/// <summary>A snapshot of a module's annotations if any exist, else null.</summary>
		public SampleAnnotations? TryGet(ModuleDef module) {
			if (staleModules.TryGetValue(module, out _))
				return null;
			var key = KeyOf(module);
			if (key is null)
				return null;
			lock (lockObj) {
				var a = GetByKey(key);
				return a.IsEmpty ? null : Clone(a);
			}
		}

		static SampleAnnotations Clone(SampleAnnotations a) =>
			JsonSerializer.Deserialize<SampleAnnotations>(JsonSerializer.Serialize(a, jsonOptions), jsonOptions)!;

		DateTime? StampOf(string key) {
			var path = PathOf(key);
			return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
		}

		SampleAnnotations GetByKey(string key) {
			var stamp = StampOf(key);
			if (cache.TryGetValue(key, out var cached) && cached.stamp == stamp)
				return cached.data;
			SampleAnnotations? a = null;
			var path = PathOf(key);
			if (stamp is not null) {
				try {
					a = JsonSerializer.Deserialize<SampleAnnotations>(File.ReadAllText(path), jsonOptions);
				}
				catch (Exception ex) {
					Debug.WriteLine($"dnSpy.Mcp: bad annotations file {path}: {ex.Message}");
				}
			}
			a ??= new SampleAnnotations();
			a.Sha256 = key;
			cache[key] = (a, stamp);
			return a;
		}

		string PathOf(string key) => Path.Combine(Directory, key + ".json");

		/// <summary>Runs <paramref name="edit"/> on a module's annotations under the store lock, then saves them.</summary>
		public T Update<T>(ModuleDef module, Func<SampleAnnotations, T> edit) {
			var key = RequireKey(module);
			lock (lockObj) {
				using var _ = LockFiles();
				// Re-read inside the locks so edits made by other sessions/instances aren't lost
				var a = GetByKey(key);
				a.File = module.Location;
				var result = edit(a);
				Persist(a);
				return result;
			}
		}

		static IDisposable LockFiles() {
			bool owned;
			try {
				owned = fileMutex.WaitOne(TimeSpan.FromSeconds(10));
			}
			catch (AbandonedMutexException) {
				owned = true;
			}
			return new Releaser(owned);
		}

		sealed class Releaser : IDisposable {
			bool owned;
			public Releaser(bool owned) => this.owned = owned;
			public void Dispose() {
				if (owned)
					fileMutex.ReleaseMutex();
				owned = false;
			}
		}

		void Persist(SampleAnnotations a) {
			System.IO.Directory.CreateDirectory(Directory);
			var path = PathOf(a.Sha256);
			if (a.IsEmpty) {
				try {
					File.Delete(path);
				}
				catch {
				}
			}
			else {
				var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
				File.WriteAllText(tmp, JsonSerializer.Serialize(a, jsonOptions));
				File.Move(tmp, path, overwrite: true);
			}
			cache[a.Sha256] = (a, StampOf(a.Sha256));
		}

		/// <summary>
		/// Re-applies stored renames to a freshly loaded module (only where the definition still has its
		/// original name). Returns the number of names changed.
		/// </summary>
		public int ApplyRenames(ModuleDef module) {
			if (staleModules.TryGetValue(module, out _))
				return 0;
			var fileKey = KeyOf(module);
			if (fileKey is null)
				return 0;
			int n = 0;
			lock (lockObj) {
				var a = GetByKey(fileKey);
				foreach (var (key, r) in a.Renames) {
					try {
						if (AnnotationKeys.ApplyOne(module, key, r))
							n++;
					}
					catch (Exception ex) {
						Debug.WriteLine($"dnSpy.Mcp: rename {key} failed: {ex.Message}");
					}
				}
			}
			return n;
		}

		/// <summary>
		/// Copies a module's annotations to the file it was just saved as, translating tokens through
		/// <paramref name="newToken"/> (returns null if the definition no longer exists).
		/// </summary>
		internal void CopyToSavedFile(ModuleDef module, byte[] savedBytes, string savedPath, Func<uint, uint?> newToken) {
			var src = TryGet(module);
			if (src is null)
				return;
			var newKey = Convert.ToHexString(SHA256.HashData(savedBytes)).ToLowerInvariant();
			lock (lockObj) {
				using var _ = LockFiles();
				var dst = GetByKey(newKey);
				dst.File = savedPath;
				dst.DerivedFrom = src.Sha256;
				foreach (var (key, r) in src.Renames) {
					if (AnnotationKeys.Retoken(key, newToken) is string k)
						dst.Renames[k] = r;
				}
				foreach (var (key, c) in src.Comments) {
					if (AnnotationKeys.Retoken(key, newToken) is string k)
						dst.Comments[k] = c;
				}
				foreach (var note in src.Notes) {
					if (!dst.Notes.Any(x => x.Time == note.Time && x.Text == note.Text))
						dst.Notes.Add(note);
				}
				Persist(dst);
			}
		}

		/// <summary>
		/// Called after Save() overwrote the module's own file. With preserved tokens the in-memory module
		/// still matches the new file, so it is re-keyed; otherwise it is marked stale until reopened.
		/// </summary>
		internal void OnSavedInPlace(ModuleDef module, byte[] savedBytes, string savedPath, bool tokensPreserved) {
			lock (lockObj) {
				if (tokensPreserved) {
					hashes.AddOrUpdate(module, Convert.ToHexString(SHA256.HashData(savedBytes)).ToLowerInvariant());
					staleModules.Remove(module);
				}
				else
					staleModules.AddOrUpdate(module, savedPath);
			}
		}
	}

	/// <summary>Helpers shared by the store and the script globals.</summary>
	static class AnnotationKeys {
		public static string TokenKey(IMDTokenProvider def) => $"0x{def.MDToken.Raw:X8}";
		public static string ParamKey(MethodDef m, int index) => $"0x{m.MDToken.Raw:X8}#p{index}";
		public static string OffsetKey(MethodDef m, uint offset) => $"0x{m.MDToken.Raw:X8}@IL_{offset:X4}";

		/// <summary>Parses "0x06000012", "0x06000012#p1" or "0x06000012@IL_001A".</summary>
		public static (uint token, int? param, uint? offset) ParseKey(string key) {
			int sep = key.IndexOfAny(new[] { '#', '@' });
			var tok = Convert.ToUInt32((sep < 0 ? key : key.Substring(0, sep)).Substring(2), 16);
			if (sep < 0)
				return (tok, null, null);
			if (key[sep] == '#')
				return (tok, int.Parse(key.Substring(sep + 2)), null);
			return (tok, null, Convert.ToUInt32(key.Substring(sep + 4), 16));
		}

		public static string? Retoken(string key, Func<uint, uint?> newToken) {
			var (tok, param, offset) = ParseKey(key);
			if (newToken(tok) is not uint nt)
				return null;
			var k = $"0x{nt:X8}";
			if (param is int p)
				k += $"#p{p}";
			if (offset is uint o)
				k += $"@IL_{o:X4}";
			return k;
		}

		/// <summary>Applies one stored rename if the target still has its original name.</summary>
		public static bool ApplyOne(ModuleDef module, string key, RenameEntry r) {
			var (tok, param, _) = ParseKey(key);
			var def = module.ResolveToken(tok);
			if (param is int pi) {
				if (def is not MethodDef m || pi < 0 || pi >= m.Parameters.Count)
					return false;
				var p = m.Parameters[pi];
				if (p.Name == r.Name || (p.Name != r.OriginalName && !string.IsNullOrEmpty(p.Name)))
					return false;
				p.CreateParamDef();
				p.Name = r.Name;
				return true;
			}
			switch (def) {
			case TypeDef t:
				if (t.Name == r.Name && (r.Namespace is null || t.Namespace == r.Namespace))
					return false;
				if (t.Name != r.OriginalName)
					return false;
				t.Name = r.Name;
				if (r.Namespace is not null)
					t.Namespace = r.Namespace;
				return true;
			case IMemberDef md when md is MethodDef or FieldDef or PropertyDef or EventDef:
				if (md.Name != r.OriginalName)
					return false;
				SetName(md, r.Name);
				return true;
			}
			return false;
		}

		public static void SetName(IMemberDef md, string name) {
			switch (md) {
			case TypeDef t: t.Name = name; break;
			case MethodDef m: m.Name = name; break;
			case FieldDef f: f.Name = name; break;
			case PropertyDef p: p.Name = name; break;
			case EventDef e: e.Name = name; break;
			default: throw new ArgumentException($"Can't rename a {md.GetType().Name}");
			}
		}

		public static string Kind(IMemberDef md) => md switch {
			TypeDef => "Type",
			MethodDef => "Method",
			FieldDef => "Field",
			PropertyDef => "Property",
			EventDef => "Event",
			_ => md.GetType().Name,
		};
	}

	/// <summary>Text summary of a sample's annotations (see <see cref="ScriptGlobals.Annotations"/>).</summary>
	public sealed class AnnotationsReport : IReport {
		readonly List<(ModuleDef module, SampleAnnotations? data)> items;

		internal AnnotationsReport(List<(ModuleDef, SampleAnnotations?)> items) => this.items = items;

		public string ToText() {
			var sb = new StringBuilder();
			foreach (var (module, a) in items) {
				sb.AppendLine($"== {module.Name}  ({module.Location})");
				if (a is null || a.IsEmpty) {
					sb.AppendLine("   (no annotations yet)");
					continue;
				}
				sb.AppendLine($"   sha256 {a.Sha256}{(a.DerivedFrom is null ? "" : $"  (saved from {a.DerivedFrom})")}");
				if (a.Notes.Count > 0) {
					sb.AppendLine($"Notes ({a.Notes.Count}):");
					for (int i = 0; i < a.Notes.Count; i++)
						sb.AppendLine($"  {i}. {a.Notes[i]}");
				}
				if (a.Renames.Count > 0) {
					sb.AppendLine($"Renames ({a.Renames.Count}):");
					foreach (var (key, r) in a.Renames) {
						var orig = r.OriginalNamespace is { Length: > 0 } ns ? $"{ns}.{r.OriginalName}" : r.OriginalName;
						var now = r.Namespace is { Length: > 0 } ns2 ? $"{ns2}.{r.Name}" : r.Name;
						sb.AppendLine($"  {key}  {r.Kind} {Printable(orig)} -> {now}");
					}
				}
				if (a.Comments.Count > 0) {
					sb.AppendLine($"Comments ({a.Comments.Count}):");
					foreach (var (key, c) in a.Comments)
						sb.AppendLine($"  {key}  {c.Target}: {c.Text}");
				}
			}
			return sb.ToString().TrimEnd();
		}

		/// <summary>Obfuscated names are often unprintable; show them escaped.</summary>
		internal static string Printable(string s) =>
			s.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c > 0x7E) ? ResultFormatter.Quote(s) : s;

		public override string ToString() => ToText();
	}
}
