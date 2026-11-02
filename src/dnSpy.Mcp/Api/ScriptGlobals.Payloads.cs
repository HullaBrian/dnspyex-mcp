using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Resources;

namespace dnSpy.Mcp.Api {
	/// <summary>A manifest resource of a module (see <see cref="ScriptGlobals.Resources"/>).</summary>
	public sealed class ResourceInfo {
		public ModuleDef Module { get; }
		public Resource Resource { get; }
		public string Name => Resource.Name;
		/// <summary>Embedded, Linked (separate file) or AssemblyLinked (in another assembly).</summary>
		public string Kind { get; }
		/// <summary>Size in bytes (embedded resources only).</summary>
		public long Size { get; }
		/// <summary>Shannon entropy in bits per byte (0-8). Above ~7.2 usually means encrypted or compressed.</summary>
		public double Entropy { get; }
		/// <summary>Detected format, see <see cref="ScriptGlobals.Identify"/>.</summary>
		public string Format { get; }
		/// <summary>Entries when the resource is a .resources file (resx), else empty.</summary>
		public IReadOnlyList<ResourceEntryInfo> Entries { get; }

		internal ResourceInfo(ModuleDef module, Resource resource, string kind, long size, double entropy, string format, IReadOnlyList<ResourceEntryInfo> entries) {
			Module = module;
			Resource = resource;
			Kind = kind;
			Size = size;
			Entropy = entropy;
			Format = format;
			Entries = entries;
		}

		public override string ToString() {
			var s = Kind == "Embedded" ? $"{Name}  {Size} bytes, entropy {Entropy:0.00}, {Format}" : $"{Name}  ({Kind})";
			if (Entries.Count > 0)
				s += $", {Entries.Count} entries: " + string.Join("; ", Entries.Take(8).Select(e => e.ToString())) + (Entries.Count > 8 ? "; ..." : "");
			return s;
		}
	}

	/// <summary>One entry of a .resources (resx) resource.</summary>
	public sealed class ResourceEntryInfo {
		public string Resource { get; }
		public string Name { get; }
		/// <summary>Type code, e.g. String, ByteArray, Stream, or a serialized user type name.</summary>
		public string Type { get; }
		public int Size { get; }
		public double Entropy { get; }
		public string Format { get; }
		/// <summary>Start of the value for string entries.</summary>
		public string? Preview { get; }

		internal ResourceEntryInfo(string resource, string name, string type, int size, double entropy, string format, string? preview) {
			Resource = resource;
			Name = name;
			Type = type;
			Size = size;
			Entropy = entropy;
			Format = format;
			Preview = preview;
		}

		public override string ToString() => Preview is not null
			? $"{Name} = {ResultFormatter.Quote(Preview, 60)}"
			: $"{Name} ({Type}, {Size} bytes, entropy {Entropy:0.00}, {Format})";
	}

	/// <summary>A static array initialized from a data blob (RuntimeHelpers.InitializeArray / RVA field).</summary>
	public sealed class ArrayInit {
		/// <summary>Method that initializes the array (null if only the field was found)</summary>
		public MethodDef? Method { get; }
		public uint Offset { get; }
		/// <summary>The RVA field that holds the data</summary>
		public FieldDef Field { get; }
		/// <summary>Element type of the array, e.g. System.Byte (null if unknown)</summary>
		public string? ElementType { get; }
		public byte[] Bytes { get; }
		public double Entropy { get; }
		public string Format { get; }

		internal ArrayInit(MethodDef? method, uint offset, FieldDef field, string? elementType, byte[] bytes) {
			Method = method;
			Offset = offset;
			Field = field;
			ElementType = elementType;
			Bytes = bytes;
			Entropy = ScriptGlobals.ComputeEntropy(bytes);
			Format = ScriptGlobals.IdentifyBytes(bytes);
		}

		public override string ToString() =>
			$"{ElementType ?? "?"}[] {Bytes.Length} bytes, entropy {Entropy:0.00}, {Format}  field {Field.Name} [0x{Field.MDToken.Raw:X8}]" +
			(Method is null ? "" : $"  <- {Method.FullName} @IL_{Offset:X4}");
	}

	public sealed partial class ScriptGlobals {
		string? dumpDirectory;

		/// <summary>
		/// Where Dump()/DumpResource()/LoadBytes() write files. Default: %LOCALAPPDATA%\dnSpy.Mcp\dumps.
		/// Settable per script session.
		/// </summary>
		public string DumpDirectory {
			get => dumpDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dnSpy.Mcp", "dumps");
			set => dumpDirectory = value;
		}

		// ------------------------------------------------------------------ resources

		/// <summary>
		/// Manifest resources of a module (default: the only loaded sample), with size, entropy, detected
		/// format and, for .resources files, their entries. Encrypted payloads usually show up here as
		/// large high-entropy blobs.
		/// </summary>
		public IReadOnlyList<ResourceInfo> Resources(ModuleDef? module = null) {
			var mod = DefaultModule(module, "Resources()");
			var list = new List<ResourceInfo>();
			foreach (var res in mod.Resources) {
				Token.ThrowIfCancellationRequested();
				switch (res) {
				case EmbeddedResource er: {
					var data = er.CreateReader().ToArray();
					var entries = ReadResourceEntries(mod, er);
					list.Add(new ResourceInfo(mod, res, "Embedded", data.Length, ComputeEntropy(data), entries.Count > 0 ? ".NET .resources" : IdentifyBytes(data), entries));
					break;
				}
				case LinkedResource lr:
					list.Add(new ResourceInfo(mod, res, $"Linked ({lr.File?.Name})", 0, 0, "", Array.Empty<ResourceEntryInfo>()));
					break;
				case AssemblyLinkedResource alr:
					list.Add(new ResourceInfo(mod, res, $"AssemblyLinked ({alr.Assembly?.FullName})", 0, 0, "", Array.Empty<ResourceEntryInfo>()));
					break;
				}
			}
			return list;
		}

		static List<ResourceEntryInfo> ReadResourceEntries(ModuleDef mod, EmbeddedResource er) {
			var result = new List<ResourceEntryInfo>();
			try {
				var reader = er.CreateReader();
				if (!ResourceReader.CouldBeResourcesFile(reader))
					return result;
				var set = ResourceReader.Read(mod, er.CreateReader());
				foreach (var e in set.ResourceElements) {
					var bytes = EntryBytes(e.ResourceData);
					string? preview = e.ResourceData is BuiltInResourceData { Code: ResourceTypeCode.String, Data: string s } ? s : null;
					result.Add(new ResourceEntryInfo(er.Name, e.Name, EntryType(e.ResourceData), bytes.Length, ComputeEntropy(bytes), IdentifyBytes(bytes), preview));
				}
			}
			catch {
				// Not a valid .resources file
			}
			return result;
		}

		static string EntryType(IResourceData data) => data switch {
			BuiltInResourceData b => b.Code.ToString(),
			BinaryResourceData bin => bin.TypeName ?? "Binary",
			_ => data.GetType().Name,
		};

		static byte[] EntryBytes(IResourceData data) => data switch {
			BuiltInResourceData { Data: byte[] b } => b,
			BuiltInResourceData { Data: string s } => Encoding.UTF8.GetBytes(s),
			BuiltInResourceData b => Encoding.UTF8.GetBytes(Convert.ToString(b.Data, System.Globalization.CultureInfo.InvariantCulture) ?? ""),
			BinaryResourceData bin => bin.Data,
			_ => Array.Empty<byte>(),
		};

		/// <summary>
		/// The raw bytes of an embedded resource, or of one entry of a .resources resource
		/// (strings are returned as UTF-8). Name matching is exact first, then case-insensitive substring.
		/// </summary>
		public byte[] ResourceBytes(string resource, string? entry = null, ModuleDef? module = null) {
			var mod = DefaultModule(module, "ResourceBytes()");
			var er = FindResource(mod, resource);
			if (entry is null)
				return er.CreateReader().ToArray();
			var set = ResourceReader.Read(mod, er.CreateReader());
			var elems = set.ResourceElements.ToList();
			var hit = elems.FirstOrDefault(e => e.Name == entry)
				?? elems.SingleOrDefault(e => e.Name.Contains(entry, StringComparison.OrdinalIgnoreCase))
				?? throw new KeyNotFoundException($"No entry '{entry}' in {er.Name}. Entries: {string.Join(", ", elems.Select(e => e.Name))}");
			return EntryBytes(hit.ResourceData);
		}

		static EmbeddedResource FindResource(ModuleDef mod, string name) {
			var all = mod.Resources.OfType<EmbeddedResource>().ToList();
			var exact = all.FirstOrDefault(r => r.Name == name);
			if (exact is not null)
				return exact;
			var hits = all.Where(r => r.Name.String.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
			if (hits.Count == 1)
				return hits[0];
			throw hits.Count == 0
				? new KeyNotFoundException($"No embedded resource '{name}' in {mod.Name}. Resources: {string.Join(", ", all.Select(r => r.Name.String))}")
				: new System.Reflection.AmbiguousMatchException($"'{name}' matches several resources: {string.Join(", ", hits.Select(r => r.Name.String))}");
		}

		/// <summary>Writes a resource (or .resources entry) to <see cref="DumpDirectory"/> and returns the path.</summary>
		public string DumpResource(string resource, string? entry = null, ModuleDef? module = null) {
			var mod = DefaultModule(module, "DumpResource()");
			var data = ResourceBytes(resource, entry, mod);
			var name = entry is null ? FindResource(mod, resource).Name.String : $"{resource}.{entry}";
			return Dump(data, $"{Path.GetFileNameWithoutExtension(mod.Name)}_{name}");
		}

		/// <summary>
		/// Writes bytes to a file and returns its full path. A bare file name goes into
		/// <see cref="DumpDirectory"/>; an existing file is not overwritten (a suffix is added).
		/// </summary>
		public string Dump(byte[] data, string name) {
			if (string.IsNullOrWhiteSpace(name))
				throw new ArgumentException("Dump(): name is empty");
			string path;
			if (Path.IsPathFullyQualified(name))
				path = Path.GetFullPath(name);
			else if (Path.IsPathRooted(name))
				throw new ArgumentException($"Dump(): '{name}' is drive- or root-relative; pass a full path or a bare file name");
			else {
				var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
				if (safe.Trim('.').Length == 0)
					safe = "dump.bin";
				path = Path.Combine(DumpDirectory, safe);
			}
			var dir = Path.GetDirectoryName(path);
			if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(Path.GetFileName(path)))
				throw new ArgumentException($"Dump(): '{name}' doesn't name a file");
			if (Directory.Exists(path))
				throw new ArgumentException($"Dump(): '{path}' is a directory");
			Directory.CreateDirectory(dir);
			var unique = path;
			for (int i = 1; File.Exists(unique); i++)
				unique = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}_{i}{Path.GetExtension(path)}");
			File.WriteAllBytes(unique, data);
			return unique;
		}

		/// <summary>
		/// Opens a .NET module from bytes (e.g. a decrypted second stage) in dnSpy: writes it to
		/// <see cref="DumpDirectory"/> and loads it. Returns the module. The bytes are only parsed, never executed.
		/// </summary>
		public ModuleDef LoadBytes(byte[] data, string name) {
			var kind = IdentifyBytes(data);
			if (!kind.StartsWith("PE", StringComparison.Ordinal))
				throw new ArgumentException($"LoadBytes(): data doesn't look like a PE file ({kind}). Decrypt/decompress it first.");
			if (!kind.Contains(".NET"))
				throw new ArgumentException($"LoadBytes(): {kind} is a native PE; dnSpy can only open .NET modules. Use Dump() and a native disassembler.");
			if (!Path.HasExtension(name))
				name += kind.Contains("DLL") ? ".dll" : ".exe";
			var path = Dump(data, name);
			return Load(path);
		}

		// ------------------------------------------------------------------ static data blobs

		/// <summary>
		/// Static arrays initialized from data blobs: RuntimeHelpers.InitializeArray(arr, ldtoken field)
		/// (how C# compiles <c>new byte[] { ... }</c>) and other RVA fields read with ldsflda.
		/// Encrypted configs, keys and shellcode often live here. Default module: the only loaded sample.
		/// </summary>
		public IReadOnlyList<ArrayInit> InitializedArrays(ModuleDef? module = null, int minSize = 0) {
			var mod = DefaultModule(module, "InitializedArrays()");
			var result = new List<ArrayInit>();
			var seen = new HashSet<FieldDef>();
			foreach (var m in MethodsWithBodies(mod)) {
				IList<Instruction> instrs;
				try {
					instrs = m.Body.Instructions;
				}
				catch {
					continue;
				}
				for (int i = 0; i < instrs.Count; i++) {
					var ins = instrs[i];
					if ((ins.OpCode.Code == Code.Ldtoken || ins.OpCode.Code == Code.Ldsflda) && ins.Operand is IField f && f.ResolveFieldDef() is FieldDef fd && fd.HasFieldRVA) {
						var data = fd.InitialValue;
						if (data is null || data.Length < minSize)
							continue;
						string? elem = null;
						for (int j = i - 1; j >= 0 && j >= i - 4; j--) {
							if (instrs[j].OpCode.Code == Code.Newarr && instrs[j].Operand is ITypeDefOrRef et) {
								elem = et.FullName;
								break;
							}
						}
						seen.Add(fd);
						result.Add(new ArrayInit(m, ins.Offset, fd, elem, data));
					}
				}
			}
			// RVA fields never referenced from code (e.g. read through reflection)
			foreach (var t in mod.GetTypes()) {
				foreach (var fd in t.Fields) {
					if (fd.HasFieldRVA && !seen.Contains(fd) && fd.InitialValue is byte[] data && data.Length >= Math.Max(minSize, 1))
						result.Add(new ArrayInit(null, 0, fd, null, data));
				}
			}
			return result;
		}

		/// <summary>The initial data of an RVA field (what a static array literal is initialized from).</summary>
		public byte[] FieldData(object field) {
			var fd = Def(field) as FieldDef ?? throw new ArgumentException("FieldData() needs a field");
			return fd.InitialValue ?? throw new ArgumentException($"{fd.FullName} has no initial data (not an RVA field)");
		}

		// ------------------------------------------------------------------ identification

		/// <summary>Shannon entropy of the data in bits per byte (0-8).</summary>
		public double Entropy(byte[] data) => ComputeEntropy(data);

		internal static double ComputeEntropy(byte[] data) {
			if (data.Length == 0)
				return 0;
			var counts = new int[256];
			foreach (var b in data)
				counts[b]++;
			double e = 0;
			foreach (var c in counts) {
				if (c == 0)
					continue;
				double p = (double)c / data.Length;
				e -= p * Math.Log2(p);
			}
			return e;
		}

		/// <summary>
		/// Guesses what bytes are: "PE32 .NET DLL", "PE32+ native EXE", "ZIP", "GZip", "zlib", "7z", "RAR",
		/// "CAB", "PNG", "JPEG", ".NET .resources", "text", "base64 text", "high entropy (encrypted or compressed)", ...
		/// </summary>
		public string Identify(byte[] data) => IdentifyBytes(data);

		internal static string IdentifyBytes(byte[] d) {
			if (d.Length == 0)
				return "empty";
			bool Starts(params byte[] sig) => d.Length >= sig.Length && sig.Select((b, i) => d[i] == b).All(x => x);
			if (Starts(0x4D, 0x5A))
				return DescribePE(d);
			if (Starts(0x50, 0x4B, 0x03, 0x04) || Starts(0x50, 0x4B, 0x05, 0x06))
				return "ZIP";
			if (Starts(0x1F, 0x8B))
				return "GZip";
			if (d.Length >= 2 && d[0] == 0x78 && (d[1] == 0x01 || d[1] == 0x5E || d[1] == 0x9C || d[1] == 0xDA))
				return "zlib";
			if (Starts(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C))
				return "7z";
			if (Starts(0x52, 0x61, 0x72, 0x21))
				return "RAR";
			if (Starts(0x4D, 0x53, 0x43, 0x46))
				return "CAB";
			if (Starts(0xCE, 0xCA, 0xEF, 0xBE))
				return ".NET .resources";
			if (Starts(0x89, 0x50, 0x4E, 0x47))
				return "PNG";
			if (Starts(0xFF, 0xD8, 0xFF))
				return "JPEG";
			if (Starts(0x47, 0x49, 0x46, 0x38))
				return "GIF";
			if (Starts(0x42, 0x4D) && d.Length > 14 && BitConverter.ToInt32(d, 2) == d.Length)
				return "BMP";
			if (Starts(0x00, 0x00, 0x01, 0x00))
				return "ICO";
			if (Starts(0x25, 0x50, 0x44, 0x46))
				return "PDF";
			if (Starts(0x7B, 0x5C, 0x72, 0x74, 0x66))
				return "RTF";
			if (Starts(0xD0, 0xCF, 0x11, 0xE0))
				return "OLE (Office 97-2003/MSI)";
			if (Starts(0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF))
				return "BinaryFormatter serialized object";
			if (Starts(0xEF, 0xBB, 0xBF) || LooksLikeText(d)) {
				var text = Encoding.UTF8.GetString(d, 0, Math.Min(d.Length, 4096)).TrimStart('﻿').Trim();
				if (text.StartsWith("<", StringComparison.Ordinal))
					return "XML/HTML text";
				if (text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal))
					return "JSON-like text";
				if (d.Length >= 16 && IsBase64(text))
					return "base64 text";
				if (d.Length >= 16 && text.All(Uri.IsHexDigit))
					return "hex text";
				return "text";
			}
			var e = ComputeEntropy(d);
			if (d.Length >= 64 && e > 7.2)
				return "high entropy (encrypted or compressed)";
			return "unknown binary";
		}

		static bool LooksLikeText(byte[] d) {
			int n = Math.Min(d.Length, 4096), printable = 0;
			for (int i = 0; i < n; i++) {
				var b = d[i];
				if (b == 9 || b == 10 || b == 13 || (b >= 32 && b < 127) || b >= 0x80)
					printable++;
				else if (b == 0)
					return false;
			}
			return printable == n && ComputeEntropy(d.Take(n).ToArray()) < 6.5;
		}

		static bool IsBase64(string s) {
			s = s.Trim();
			if (s.Length % 4 != 0)
				return false;
			foreach (var c in s) {
				if (!(char.IsAsciiLetterOrDigit(c) || c == '+' || c == '/' || c == '='))
					return false;
			}
			return true;
		}

		static string DescribePE(byte[] d) {
			try {
				int lfanew = BitConverter.ToInt32(d, 0x3C);
				if (lfanew <= 0 || lfanew + 24 > d.Length || BitConverter.ToUInt32(d, lfanew) != 0x00004550)
					return "MZ (DOS executable or truncated PE)";
				ushort characteristics = BitConverter.ToUInt16(d, lfanew + 22);
				int opt = lfanew + 24;
				ushort magic = BitConverter.ToUInt16(d, opt);
				bool pe64 = magic == 0x20B;
				int dataDirs = opt + (pe64 ? 112 : 96);
				int clrDir = dataDirs + 14 * 8;
				bool dotnet = clrDir + 8 <= d.Length && BitConverter.ToUInt32(d, clrDir) != 0;
				bool dll = (characteristics & 0x2000) != 0;
				return $"{(pe64 ? "PE32+" : "PE32")} {(dotnet ? ".NET" : "native")} {(dll ? "DLL" : "EXE")}";
			}
			catch {
				return "PE (unparseable headers)";
			}
		}

		/// <summary>Hex dump (offset, hex bytes, ASCII) of part of the data.</summary>
		public string Hex(byte[] data, int offset = 0, int length = 256) {
			var sb = new StringBuilder();
			offset = Math.Clamp(offset, 0, data.Length);
			length = Math.Max(0, length);
			int end = (int)Math.Min(data.Length, (long)offset + length);
			for (int row = offset; row < end; row += 16) {
				sb.Append($"{row:X8}  ");
				for (int i = 0; i < 16; i++)
					sb.Append(row + i < end ? $"{data[row + i]:X2} " : "   ");
				sb.Append(' ');
				for (int i = 0; i < 16 && row + i < end; i++) {
					var b = data[row + i];
					sb.Append(b >= 32 && b < 127 ? (char)b : '.');
				}
				sb.AppendLine();
			}
			if (end < data.Length)
				sb.AppendLine($"... ({data.Length - end} more bytes)");
			return sb.ToString();
		}

		// ------------------------------------------------------------------ decoding helpers

		/// <summary>XORs data with a repeating key.</summary>
		public byte[] Xor(byte[] data, byte[] key) {
			if (key.Length == 0)
				throw new ArgumentException("Empty key");
			var r = new byte[data.Length];
			for (int i = 0; i < data.Length; i++)
				r[i] = (byte)(data[i] ^ key[i % key.Length]);
			return r;
		}

		/// <summary>XORs data with a repeating UTF-8 string key.</summary>
		public byte[] Xor(byte[] data, string key) => Xor(data, Encoding.UTF8.GetBytes(key));

		/// <summary>XORs data with a single byte.</summary>
		public byte[] Xor(byte[] data, byte key) => Xor(data, new[] { key });

		/// <summary>Decompresses GZip data.</summary>
		public byte[] Gunzip(byte[] data) => Decompress(new GZipStream(new MemoryStream(data), CompressionMode.Decompress));

		/// <summary>Decompresses raw DEFLATE data (DeflateStream, no header).</summary>
		public byte[] Inflate(byte[] data) => Decompress(new DeflateStream(new MemoryStream(data), CompressionMode.Decompress));

		/// <summary>Decompresses zlib data (2-byte header + DEFLATE).</summary>
		public byte[] Zlib(byte[] data) => Decompress(new ZLibStream(new MemoryStream(data), CompressionMode.Decompress));

		static byte[] Decompress(Stream s) {
			using (s) {
				var ms = new MemoryStream();
				s.CopyTo(ms);
				return ms.ToArray();
			}
		}

		/// <summary>Decodes base64, tolerating whitespace, missing padding and the URL-safe alphabet.</summary>
		public byte[] FromBase64(string s) {
			var t = new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/');
			t = t.PadRight(t.Length + (4 - t.Length % 4) % 4, '=');
			return Convert.FromBase64String(t);
		}

		/// <summary>Decodes a hex string ("4D5A90", "4d 5a 90", "0x4D,0x5A").</summary>
		public byte[] FromHex(string s) {
			var t = s.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
			t = new string(t.Where(Uri.IsHexDigit).ToArray());
			return Convert.FromHexString(t);
		}

		/// <summary>Bytes as text (UTF-8 by default; pass "unicode" for UTF-16LE, "ascii", "latin1", ...).</summary>
		public string Text(byte[] data, string encoding = "utf-8") =>
			(encoding.ToLowerInvariant() switch {
				"unicode" or "utf-16" or "utf16" => Encoding.Unicode,
				_ => Encoding.GetEncoding(encoding),
			}).GetString(data);

		/// <summary>
		/// AES decryption. Mode "CBC" (default), "ECB" or "CFB"; padding "PKCS7" (default), "None", "Zeros".
		/// The key length selects AES-128/192/256. CFB uses <paramref name="cfbFeedbackBits"/> (8 = .NET Aes default,
		/// 128 = RijndaelManaged default and most other libraries).
		/// </summary>
		public byte[] AesDecrypt(byte[] data, byte[] key, byte[]? iv = null, string mode = "CBC", string padding = "PKCS7", int cfbFeedbackBits = 8) {
			using var aes = Aes.Create();
			aes.Key = key;
			var pad = Enum.Parse<PaddingMode>(padding, ignoreCase: true);
			return mode.ToUpperInvariant() switch {
				"ECB" => aes.DecryptEcb(data, pad),
				"CBC" => aes.DecryptCbc(data, iv ?? new byte[16], pad),
				"CFB" => aes.DecryptCfb(data, iv ?? new byte[16], pad, cfbFeedbackBits),
				_ => throw new ArgumentException($"Unsupported mode {mode}"),
			};
		}

		/// <summary>DES / 3DES (key length 8 = DES, 16/24 = 3DES) decryption, CBC or ECB.</summary>
		public byte[] DesDecrypt(byte[] data, byte[] key, byte[]? iv = null, string mode = "CBC", string padding = "PKCS7") {
			using SymmetricAlgorithm alg = key.Length == 8 ? DES.Create() : TripleDES.Create();
			alg.Key = key;
			var pad = Enum.Parse<PaddingMode>(padding, ignoreCase: true);
			return mode.ToUpperInvariant() switch {
				"ECB" => alg.DecryptEcb(data, pad),
				"CBC" => alg.DecryptCbc(data, iv ?? new byte[8], pad),
				_ => throw new ArgumentException($"Unsupported mode {mode}"),
			};
		}

		/// <summary>RC4 (also used as "ARC4") encryption/decryption.</summary>
		public byte[] Rc4(byte[] data, byte[] key) {
			if (key.Length == 0)
				throw new ArgumentException("Empty key");
			var s = new byte[256];
			for (int i = 0; i < 256; i++)
				s[i] = (byte)i;
			for (int i = 0, j = 0; i < 256; i++) {
				j = (j + s[i] + key[i % key.Length]) & 0xFF;
				(s[i], s[j]) = (s[j], s[i]);
			}
			var r = new byte[data.Length];
			for (int k = 0, i = 0, j = 0; k < data.Length; k++) {
				i = (i + 1) & 0xFF;
				j = (j + s[i]) & 0xFF;
				(s[i], s[j]) = (s[j], s[i]);
				r[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
			}
			return r;
		}

		/// <summary>PBKDF2 (Rfc2898DeriveBytes) key derivation; hash "SHA1" (the .NET Framework default), "SHA256", "SHA512".</summary>
		public byte[] Pbkdf2(byte[] password, byte[] salt, int iterations, int length, string hash = "SHA1") =>
			Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, new HashAlgorithmName(hash.ToUpperInvariant()), length);

		/// <summary>PBKDF2 with a UTF-8 string password.</summary>
		public byte[] Pbkdf2(string password, byte[] salt, int iterations, int length, string hash = "SHA1") =>
			Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, length, hash);

		/// <summary>Hash of data as lowercase hex: "md5", "sha1", "sha256" (default), "sha512".</summary>
		public string Hash(byte[] data, string algorithm = "sha256") => Convert.ToHexString(algorithm.ToLowerInvariant() switch {
			"md5" => MD5.HashData(data),
			"sha1" => SHA1.HashData(data),
			"sha256" => SHA256.HashData(data),
			"sha384" => SHA384.HashData(data),
			"sha512" => SHA512.HashData(data),
			_ => throw new ArgumentException($"Unknown hash {algorithm}"),
		}).ToLowerInvariant();
	}
}
