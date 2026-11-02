using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.PE;

namespace dnSpy.Mcp.Api {
	/// <summary>Uses of one notable API found by <see cref="ScriptGlobals.Triage"/>.</summary>
	public sealed class ApiHit {
		public string Category { get; }
		/// <summary>"Type::Method" of the API, or "dll!function" for P/Invokes</summary>
		public string Api { get; }
		/// <summary>Methods that use it</summary>
		public IReadOnlyList<MethodDef> Callers { get; }
		public int Count { get; }

		internal ApiHit(string category, string api, IReadOnlyList<MethodDef> callers, int count) {
			Category = category;
			Api = api;
			Callers = callers;
			Count = count;
		}

		public override string ToString() => $"[{Category}] {Api} x{Count}";
	}

	/// <summary>An interesting string found by <see cref="ScriptGlobals.Triage"/>.</summary>
	public sealed class StringHit {
		public string Category { get; }
		public string Value { get; }
		public MethodDef Method { get; }
		public uint Offset { get; }
		/// <summary>For base64/hex blobs: what the decoded bytes look like</summary>
		public string? Decoded { get; }

		internal StringHit(string category, string value, MethodDef method, uint offset, string? decoded) {
			Category = category;
			Value = value;
			Method = method;
			Offset = offset;
			Decoded = decoded;
		}

		public override string ToString() => $"[{Category}] {ResultFormatter.Quote(Value, 100)}{(Decoded is null ? "" : $" ({Decoded})")}  <- {Method.FullName}";
	}

	/// <summary>A data blob that may be an embedded payload.</summary>
	public sealed class BlobHit {
		/// <summary>resource, resource entry, array, or base64 string</summary>
		public string Source { get; }
		public string Where { get; }
		public int Size { get; }
		public double Entropy { get; }
		public string Format { get; }

		internal BlobHit(string source, string where, int size, double entropy, string format) {
			Source = source;
			Where = where;
			Size = size;
			Entropy = entropy;
			Format = format;
		}

		public override string ToString() => $"{Source} {Where}: {Size} bytes, entropy {Entropy:0.00}, {Format}";
	}

	/// <summary>A single-byte XOR key that makes data readable (see <see cref="ScriptGlobals.XorBruteforce"/>).</summary>
	public sealed class XorCandidate {
		public byte Key { get; }
		public double Score { get; }
		/// <summary>Start of the decoded text, or the recognized format</summary>
		public string Preview { get; }

		internal XorCandidate(byte key, double score, string preview) {
			Key = key;
			Score = score;
			Preview = preview;
		}

		public override string ToString() => $"0x{Key:X2}: {Preview}";
	}

	/// <summary>A method that looks like a string decryptor: returns string, primitive arguments, many call sites.</summary>
	public sealed class DecryptorCandidate {
		public MethodDef Method { get; }
		public int CallSites { get; }

		internal DecryptorCandidate(MethodDef method, int callSites) {
			Method = method;
			CallSites = callSites;
		}

		public override string ToString() => $"{Method.FullName} [0x{Method.MDToken.Raw:X8}]  called from {CallSites} sites";
	}

	/// <summary>Result of <see cref="ScriptGlobals.Triage"/>. Print it for the report; the properties hold the raw findings.</summary>
	public sealed class TriageReport : IReport {
		public ModuleDef Module { get; internal set; } = null!;
		public string Sha256 { get; internal set; } = "";
		public string Md5 { get; internal set; } = "";
		public IReadOnlyList<string> FileInfo { get; internal set; } = Array.Empty<string>();
		public IReadOnlyList<string> Obfuscation { get; internal set; } = Array.Empty<string>();
		public IReadOnlyList<DecryptorCandidate> DecryptorCandidates { get; internal set; } = Array.Empty<DecryptorCandidate>();
		public IReadOnlyList<ApiHit> PInvokes { get; internal set; } = Array.Empty<ApiHit>();
		public IReadOnlyList<ApiHit> Apis { get; internal set; } = Array.Empty<ApiHit>();
		public IReadOnlyList<StringHit> Strings { get; internal set; } = Array.Empty<StringHit>();
		public IReadOnlyList<BlobHit> Blobs { get; internal set; } = Array.Empty<BlobHit>();
		public IReadOnlyList<string> StartingPoints { get; internal set; } = Array.Empty<string>();
		public string? AnnotationSummary { get; internal set; }
		internal int MaxPerCategory { get; set; } = 12;

		public string ToText() {
			var sb = new StringBuilder();
			sb.AppendLine($"=== Triage: {Module.Name}");
			foreach (var line in FileInfo)
				sb.AppendLine("  " + line);
			if (AnnotationSummary is not null)
				sb.AppendLine($"  Earlier work: {AnnotationSummary}  (Annotations() shows it)");

			Section(sb, "Obfuscation / packing indicators", Obfuscation.Select(o => o).ToList(), "none found");
			if (DecryptorCandidates.Count > 0)
				Section(sb, "Probable string decryptors (static, returns string, primitive args, many call sites)", DecryptorCandidates.Take(8).Select(d => d.ToString()).ToList(), "");

			sb.AppendLine();
			sb.AppendLine("--- P/Invoke (native imports)");
			if (PInvokes.Count == 0)
				sb.AppendLine("  none");
			foreach (var g in PInvokes.GroupBy(p => p.Category).OrderBy(g => g.Key == "Other" ? 1 : 0)) {
				sb.AppendLine($"  [{g.Key}] " + string.Join(", ", g.Select(p =>
					p.Count == 0 ? $"{p.Api} (never called)" : $"{p.Api} <- {string.Join(", ", p.Callers.Take(2).Select(Short))}")));
			}

			sb.AppendLine();
			sb.AppendLine("--- Notable API usage (category: API xcount  <- example callers)");
			if (Apis.Count == 0)
				sb.AppendLine("  none");
			foreach (var g in Apis.GroupBy(a => a.Category)) {
				sb.AppendLine($"  [{g.Key}]");
				foreach (var a in g.OrderByDescending(a => a.Count).Take(MaxPerCategory)) {
					var callers = string.Join(", ", a.Callers.Take(3).Select(Short));
					sb.AppendLine($"    {a.Api} x{a.Count}  <- {callers}{(a.Callers.Count > 3 ? $" (+{a.Callers.Count - 3})" : "")}");
				}
			}

			sb.AppendLine();
			sb.AppendLine("--- Interesting strings");
			if (Strings.Count == 0)
				sb.AppendLine("  none (strings may be encrypted; see decryptor candidates)");
			foreach (var g in Strings.GroupBy(s => s.Category)) {
				var all = g.ToList();
				sb.AppendLine($"  [{g.Key}] ({all.Count})");
				foreach (var s in all.Take(MaxPerCategory))
					sb.AppendLine($"    {ResultFormatter.Quote(s.Value, 140)}{(s.Decoded is null ? "" : $"  => {s.Decoded}")}  <- {Short(s.Method)}");
				if (all.Count > MaxPerCategory)
					sb.AppendLine($"    ... {all.Count - MaxPerCategory} more (report.Strings)");
			}

			Section(sb, "Possible embedded payloads (large, high-entropy or recognizable blobs)", Blobs.Select(b => b.ToString()).ToList(), "none");
			Section(sb, "Where to start", StartingPoints.ToList(), "");
			sb.AppendLine();
			sb.AppendLine("Next: Decompile(...) the starting points; Resources()/InitializedArrays() + ResourceBytes()/FieldData() to extract blobs;");
			sb.AppendLine("record findings with Note()/Rename()/Comment(). reference(\"malware\") has the workflow.");
			return sb.ToString().TrimEnd();
		}

		static void Section(StringBuilder sb, string title, List<string> lines, string empty) {
			sb.AppendLine();
			sb.AppendLine($"--- {title}");
			if (lines.Count == 0 && empty.Length > 0)
				sb.AppendLine("  " + empty);
			foreach (var l in lines)
				sb.AppendLine("  " + l);
		}

		internal static string Short(MethodDef m) => $"{AnnotationsReport.Printable(m.DeclaringType?.Name ?? "")}::{AnnotationsReport.Printable(m.Name)}";

		public override string ToString() => $"TriageReport {Module.Name} (print it for the full report)";
	}

	public sealed partial class ScriptGlobals {
		// "Type::Method" (Method may end with *), or "Type" for any member/constructor of the type
		static readonly (string category, string pattern)[] apiTable = {
			("Dynamic code loading", "System.Reflection.Assembly::Load"),
			("Dynamic code loading", "System.Reflection.Assembly::LoadFile"),
			("Dynamic code loading", "System.Reflection.Assembly::LoadFrom"),
			("Dynamic code loading", "System.Reflection.Assembly::UnsafeLoadFrom"),
			("Dynamic code loading", "System.Reflection.Assembly::get_EntryPoint"),
			("Dynamic code loading", "System.AppDomain::Load"),
			("Dynamic code loading", "System.AppDomain::ExecuteAssembly*"),
			("Dynamic code loading", "System.Runtime.Loader.AssemblyLoadContext::LoadFrom*"),
			("Dynamic code loading", "System.Reflection.Emit.DynamicMethod"),
			("Dynamic code loading", "System.Reflection.Emit.AssemblyBuilder"),
			("Dynamic code loading", "Microsoft.CSharp.CSharpCodeProvider"),
			("Dynamic code loading", "Microsoft.VisualBasic.VBCodeProvider"),
			("Dynamic code loading", "System.CodeDom.Compiler.CodeDomProvider::CompileAssembly*"),
			("Reflection invoke", "System.Reflection.MethodBase::Invoke"),
			("Reflection invoke", "System.Reflection.MethodInfo::Invoke"),
			("Reflection invoke", "System.Type::InvokeMember"),
			("Reflection invoke", "System.Type::GetMethod"),
			("Reflection invoke", "System.Activator::CreateInstance"),
			("Reflection invoke", "System.Delegate::DynamicInvoke"),
			("Reflection invoke", "Microsoft.VisualBasic.CompilerServices.NewLateBinding"),
			("Reflection invoke", "System.Runtime.InteropServices.Marshal::GetDelegateForFunctionPointer*"),
			("Embedded resources", "System.Reflection.Assembly::GetManifestResourceStream"),
			("Embedded resources", "System.Reflection.Assembly::GetManifestResourceNames"),
			("Embedded resources", "System.Resources.ResourceManager::GetObject"),
			("Embedded resources", "System.Resources.ResourceManager::GetStream"),
			("Process execution", "System.Diagnostics.Process::Start"),
			("Process execution", "System.Diagnostics.ProcessStartInfo::.ctor"),
			("Process execution", "Microsoft.VisualBasic.Interaction::Shell"),
			("Process execution", "System.Management.ManagementClass::InvokeMethod"),
			("Network", "System.Net.WebClient"),
			("Network", "System.Net.WebRequest::Create*"),
			("Network", "System.Net.HttpWebRequest"),
			("Network", "System.Net.Http.HttpClient"),
			("Network", "System.Net.Sockets.TcpClient"),
			("Network", "System.Net.Sockets.TcpListener"),
			("Network", "System.Net.Sockets.UdpClient"),
			("Network", "System.Net.Sockets.Socket"),
			("Network", "System.Net.Dns"),
			("Network", "System.Net.Mail.SmtpClient"),
			("Network", "System.Net.FtpWebRequest"),
			("Network", "System.Net.Security.SslStream"),
			("Network", "System.Net.ServicePointManager::set_ServerCertificateValidationCallback"),
			("Network", "System.Net.ServicePointManager::set_SecurityProtocol"),
			("Network", "System.Net.NetworkInformation.Ping"),
			("Crypto", "System.Security.Cryptography.Aes::Create"),
			("Crypto", "System.Security.Cryptography.RijndaelManaged"),
			("Crypto", "System.Security.Cryptography.AesManaged"),
			("Crypto", "System.Security.Cryptography.AesCryptoServiceProvider"),
			("Crypto", "System.Security.Cryptography.DESCryptoServiceProvider"),
			("Crypto", "System.Security.Cryptography.TripleDESCryptoServiceProvider"),
			("Crypto", "System.Security.Cryptography.RC2CryptoServiceProvider"),
			("Crypto", "System.Security.Cryptography.RSACryptoServiceProvider"),
			("Crypto", "System.Security.Cryptography.CryptoStream"),
			("Crypto", "System.Security.Cryptography.Rfc2898DeriveBytes"),
			("Crypto", "System.Security.Cryptography.SymmetricAlgorithm::CreateDecryptor"),
			("Crypto", "System.Security.Cryptography.SymmetricAlgorithm::CreateEncryptor"),
			("Crypto", "System.Security.Cryptography.HMACSHA256"),
			("Crypto", "System.Security.Cryptography.MD5CryptoServiceProvider"),
			("Crypto", "System.Security.Cryptography.MD5::Create"),
			("Credential theft", "System.Security.Cryptography.ProtectedData::Unprotect"),
			("Encoding / compression", "System.Convert::FromBase64String"),
			("Encoding / compression", "System.IO.Compression.GZipStream"),
			("Encoding / compression", "System.IO.Compression.DeflateStream"),
			("Registry", "Microsoft.Win32.RegistryKey::SetValue"),
			("Registry", "Microsoft.Win32.RegistryKey::CreateSubKey"),
			("Registry", "Microsoft.Win32.RegistryKey::DeleteValue"),
			("Registry", "Microsoft.Win32.RegistryKey::DeleteSubKey*"),
			("Registry", "Microsoft.Win32.Registry::SetValue"),
			("File system", "System.IO.File::WriteAllBytes"),
			("File system", "System.IO.File::Copy"),
			("File system", "System.IO.File::Move"),
			("File system", "System.IO.File::Delete"),
			("File system", "System.IO.File::SetAttributes"),
			("File system", "System.IO.Directory::Delete"),
			("File system", "System.IO.FileInfo::Delete"),
			("Anti-analysis", "System.Diagnostics.Debugger::get_IsAttached"),
			("Anti-analysis", "System.Diagnostics.Debugger::IsLogging"),
			("Anti-analysis", "System.Diagnostics.Process::GetProcesses"),
			("Anti-analysis", "System.Diagnostics.Process::GetProcessesByName"),
			("Anti-analysis", "System.Diagnostics.Process::Kill"),
			("Anti-analysis", "System.Threading.Thread::Sleep"),
			("Anti-analysis", "System.Management.ManagementObjectSearcher"),
			("Anti-analysis", "System.Environment::FailFast"),
			("Anti-analysis", "System.Environment::get_ProcessorCount"),
			("Recon", "System.Environment::get_UserName"),
			("Recon", "System.Environment::get_MachineName"),
			("Recon", "System.Environment::get_OSVersion"),
			("Recon", "System.Environment::get_UserDomainName"),
			("Recon", "System.Security.Principal.WindowsPrincipal::IsInRole"),
			("Recon", "System.IO.DriveInfo::GetDrives"),
			("Recon", "System.Net.NetworkInformation.NetworkInterface::GetAllNetworkInterfaces"),
			("Surveillance", "System.Windows.Forms.Clipboard"),
			("Surveillance", "System.Drawing.Graphics::CopyFromScreen"),
			("Surveillance", "System.Windows.Forms.Screen"),
			("Surveillance", "System.Windows.Forms.SendKeys"),
			("Unmanaged memory", "System.Runtime.InteropServices.Marshal::Copy"),
			("Unmanaged memory", "System.Runtime.InteropServices.Marshal::AllocHGlobal"),
			("Unmanaged memory", "System.Runtime.InteropServices.Marshal::WriteByte"),
			("Unmanaged memory", "System.Runtime.InteropServices.Marshal::WriteIntPtr"),
			("Services / logs", "System.ServiceProcess.ServiceController"),
			("Services / logs", "System.Diagnostics.EventLog::Clear"),
			("Persistence", "Microsoft.Win32.TaskScheduler"),
		};

		static readonly (string category, string[] names)[] pinvokeTable = {
			("Process injection / memory", new[] { "VirtualAlloc", "VirtualAllocEx", "VirtualProtect", "VirtualProtectEx", "WriteProcessMemory", "ReadProcessMemory",
				"CreateRemoteThread", "CreateRemoteThreadEx", "NtCreateThreadEx", "RtlCreateUserThread", "QueueUserAPC", "NtQueueApcThread", "SetThreadContext",
				"GetThreadContext", "Wow64SetThreadContext", "Wow64GetThreadContext", "ResumeThread", "SuspendThread", "NtUnmapViewOfSection", "ZwUnmapViewOfSection",
				"NtWriteVirtualMemory", "NtAllocateVirtualMemory", "NtProtectVirtualMemory", "NtMapViewOfSection", "OpenProcess", "CreateProcess", "CreateThread",
				"CreateFileMapping", "MapViewOfFile", "NtResumeThread" }),
			("Dynamic API resolution", new[] { "LoadLibrary", "LoadLibraryEx", "GetProcAddress", "GetModuleHandle", "LdrLoadDll", "LdrGetProcedureAddress" }),
			("Anti-debug / anti-analysis", new[] { "IsDebuggerPresent", "CheckRemoteDebuggerPresent", "NtQueryInformationProcess", "ZwQueryInformationProcess",
				"NtSetInformationThread", "OutputDebugString", "NtQuerySystemInformation", "BlockInput", "NtClose", "GetTickCount", "QueryPerformanceCounter" }),
			("Keylogging / input", new[] { "GetAsyncKeyState", "GetKeyState", "SetWindowsHookEx", "CallNextHookEx", "UnhookWindowsHookEx", "GetKeyboardState",
				"MapVirtualKey", "ToUnicodeEx", "ToUnicode", "GetKeyboardLayout", "RegisterRawInputDevices", "GetRawInputData" }),
			("Window / screen capture", new[] { "GetForegroundWindow", "GetWindowText", "BitBlt", "GetDC", "GetWindowDC", "PrintWindow", "capCreateCaptureWindow",
				"capGetDriverDescription", "ShowWindow", "FindWindow" }),
			("Privileges / tokens", new[] { "AdjustTokenPrivileges", "OpenProcessToken", "LookupPrivilegeValue", "RtlAdjustPrivilege", "ImpersonateLoggedOnUser",
				"DuplicateToken", "DuplicateTokenEx", "CreateProcessWithToken", "CreateProcessAsUser" }),
			("Critical process / system", new[] { "RtlSetProcessIsCritical", "NtSetInformationProcess", "NtRaiseHardError", "ExitWindowsEx", "SystemParametersInfo",
				"SetFileAttributes", "MoveFileEx", "DeleteFile" }),
			("AMSI / ETW tampering", new[] { "AmsiScanBuffer", "AmsiInitialize", "EtwEventWrite" }),
			("Credentials / crypto", new[] { "CryptUnprotectData", "CredEnumerate", "CredRead", "BCryptDecrypt", "BCryptOpenAlgorithmProvider", "CryptDecrypt" }),
		};

		static readonly (string category, Regex regex)[] stringTable = {
			("URL", new Regex(@"\b(?:https?|ftp|wss?|tcp)://[^\s""'<>]+", RegexOptions.IgnoreCase)),
			("Network service", new Regex(@"(Mozilla/\d|User-Agent|api\.telegram\.org|/bot\d+:|discord(app)?\.com/api/webhooks|pastebin|\.onion\b|ngrok|duckdns|no-ip|portmap|smtp\.|ip-api\.com|ipify|icanhazip|checkip)", RegexOptions.IgnoreCase)),
			("Registry", new Regex(@"(HKEY_|\bHKLM\b|\bHKCU\b|SOFTWARE\\|\\CurrentVersion\\Run|\\Policies\\|\\Winlogon)", RegexOptions.IgnoreCase)),
			("Command / LOLBin", new Regex(@"\b(cmd(\.exe)?\s*/[ck]|powershell|pwsh|schtasks|reg(\.exe)?\s+add|vssadmin|bcdedit|wbadmin|wmic|netsh|attrib\s|rundll32|regsvr32|mshta|certutil|bitsadmin|taskkill|timeout\s+/t|ping\s+-n|del\s+/[fq]|cscript|wscript|installutil|regasm|msbuild|-enc(odedcommand)?\s)", RegexOptions.IgnoreCase)),
			("Credential / wallet target", new Regex(@"(Login Data|Web Data|\bCookies\b|Local State|logins\.json|key[34]\.db|signons|wallet\.dat|\\Exodus|Electrum|Ethereum\\|Atomic\\|Telegram Desktop|\\tdata|\\leveldb|FileZilla|recentservers\.xml|sitemanager\.xml|NordVPN|OpenVPN|\\Steam|\\\.ssh|Outlook\\|Thunderbird|Pidgin|accounts\.xml|Bitcoin|Monero|MetaMask)", RegexOptions.IgnoreCase)),
			("Anti-analysis", new Regex(@"(vmware|virtualbox|vbox|qemu|sandboxie|SbieDll|wireshark|procmon|processhacker|x64dbg|x32dbg|ollydbg|ida64|dnspy|de4dot|fiddler|cuckoo|any\.run|joebox|Win32_ComputerSystem|Win32_VideoController|Win32_BIOS|Win32_DiskDrive|SELECT \* FROM|SbieDll\.dll|cmdvrt|snxhk)", RegexOptions.IgnoreCase)),
			("IP address", new Regex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?::\d{1,5})?\b")),
			("Path / location", new Regex(@"(%[A-Z_]+%|\\AppData\\|\\Temp\\|\\Startup\\|\\Start Menu\\|C:\\(Windows|Users|ProgramData)|\\System32\\)", RegexOptions.IgnoreCase)),
			("Mutex / named object", new Regex(@"^(Global|Local)\\")),
			("Email", new Regex(@"\b[\w.+-]+@[\w-]+\.[a-z]{2,}\b", RegexOptions.IgnoreCase)),
			("Ransom note", new Regex(@"(your files (have been|are) encrypted|bitcoin|decrypt(ion)? (key|tool)|ransom|\.onion)", RegexOptions.IgnoreCase)),
		};

		static readonly string[] obfuscatorMarkers = {
			"ConfusedByAttribute", "ConfuserEx", "DotfuscatorAttribute", "PoweredByAttribute", "BabelAttribute", "BabelObfuscatorAttribute",
			"ObfuscatedByGoliath", "ProcessedByXenocode", "YanoAttribute", "CryptoObfuscator", "ObfuscatedByAgileDotNet", "SmartAssembly",
			"NETGuard", "Beds-Protector", "KoiVM", "DNGuard", "ObfuscatorAttribute", "Eazfuscator", "Reactor", "MaxtoCode", "ILProtector",
			"VMProtect", "Themida", "AgileDotNet", "Skater", "SecureTeam", "CodeVeil", "Spices", "OrangeHeap", "MindLated", "Dotfuscated",
		};

		/// <summary>
		/// One-call malware triage of a module (default: the only loaded sample): file/PE facts and hashes,
		/// obfuscation indicators, probable string decryptors, P/Invokes and notable APIs by category
		/// (with callers), categorized interesting strings (URLs, IPs, registry, commands, credential
		/// targets, anti-analysis, base64 blobs...), possible embedded payloads and where to start reading.
		/// Print the result for the report; its properties hold the raw lists.
		/// </summary>
		public TriageReport Triage(ModuleDef? module = null, int maxPerCategory = 12) {
			var mod = DefaultModule(module, "Triage()");
			var report = new TriageReport { Module = mod, MaxPerCategory = maxPerCategory };
			var methods = mod.GetTypes().SelectMany(t => t.Methods).ToList();

			// --- file facts
			var info = new List<string>();
			byte[]? fileBytes = null;
			if (!string.IsNullOrEmpty(mod.Location) && File.Exists(mod.Location)) {
				fileBytes = File.ReadAllBytes(mod.Location);
				report.Sha256 = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
				report.Md5 = Convert.ToHexString(MD5.HashData(fileBytes)).ToLowerInvariant();
				info.Add($"File: {mod.Location} ({fileBytes.Length:N0} bytes)");
				info.Add($"SHA256: {report.Sha256}  MD5: {report.Md5}");
			}
			info.Add($"Assembly: {mod.Assembly?.FullName ?? "(netmodule)"}");
			info.Add(DescribeImage(mod));
			var tfm = mod.Assembly?.CustomAttributes.Find("System.Runtime.Versioning.TargetFrameworkAttribute")?.ConstructorArguments.FirstOrDefault().Value?.ToString();
			info.Add($"Runtime: {mod.RuntimeVersion}{(tfm is null ? "" : $", target {tfm}")}{(mod.Assembly?.PublicKey is { IsNullOrEmpty: false } ? ", strong-named" : "")}");
			var attrs = AssemblyAttributes(mod);
			if (attrs.Length > 0)
				info.Add($"Attributes: {attrs}");
			var types = mod.GetTypes().ToList();
			int noBody = methods.Count(m => !m.HasBody && !m.IsAbstract && !m.IsPinvokeImpl && !m.IsRuntime && !m.IsInternalCall && m.CodeType == MethodImplAttributes.IL && (m.DeclaringType?.IsInterface != true));
			info.Add($"Contents: {types.Count} types, {methods.Count} methods, {mod.Resources.Count} resources, {mod.GetAssemblyRefs().Count()} assembly refs" +
				(noBody > 0 ? $", {noBody} non-abstract methods WITHOUT a body (decrypted at runtime?)" : ""));
			var refs = mod.GetAssemblyRefs().Select(a => a.Name.String).Where(n => n is not ("mscorlib" or "System" or "System.Core" or "System.Runtime" or "netstandard")).ToList();
			if (refs.Count > 0)
				info.Add($"References: {string.Join(", ", refs)}");
			if (mod.EntryPoint is MethodDef ep)
				info.Add($"Entry point: {ep.FullName} [0x{ep.MDToken.Raw:X8}]");
			var cctor = mod.GlobalType?.FindStaticConstructor();
			if (cctor is { HasBody: true })
				info.Add($"<Module>::.cctor present ({cctor.Body.Instructions.Count} instructions): runs before anything else; packers/protectors hook here");
			report.FileInfo = info;
			if (Store.TryGet(mod) is SampleAnnotations ann)
				report.AnnotationSummary = $"{ann.Notes.Count} notes, {ann.Renames.Count} renames, {ann.Comments.Count} comments";

			// --- scan bodies: API calls, strings, call counts
			var apiCallers = new Dictionary<string, (string cat, HashSet<MethodDef> callers, int count)>();
			var callCounts = new Dictionary<MethodDef, HashSet<MethodDef>>();
			var callSites = new Dictionary<MethodDef, int>();
			var stringHits = new List<StringHit>();
			var seenStrings = new HashSet<string>();
			int bigSwitches = 0, ldstrCount = 0, invalidBodies = 0;
			foreach (var m in methods) {
				Token.ThrowIfCancellationRequested();
				if (!m.HasBody)
					continue;
				IList<Instruction> instrs;
				try {
					instrs = m.Body.Instructions;
				}
				catch {
					invalidBodies++;
					continue;
				}
				foreach (var ins in instrs) {
					switch (ins.Operand) {
					case IMethod im when im.IsMethod: {
						var key = $"{im.DeclaringType?.FullName}::{im.Name}";
						if (MatchApi(im) is string cat) {
							if (!apiCallers.TryGetValue(key, out var e))
								apiCallers[key] = e = (cat, new HashSet<MethodDef>(), 0);
							e.callers.Add(m);
							apiCallers[key] = (e.cat, e.callers, e.count + 1);
						}
						if (im is MethodDef md && md.Module == mod) {
							if (!callCounts.TryGetValue(md, out var set))
								callCounts[md] = set = new HashSet<MethodDef>();
							set.Add(m);
							callSites[md] = callSites.GetValueOrDefault(md) + 1;
						}
						break;
					}
					case string s:
						ldstrCount++;
						if (s.Length >= 4 && seenStrings.Add(s))
							ClassifyString(s, m, ins.Offset, stringHits);
						break;
					case Instruction[] targets when ins.OpCode.Code == Code.Switch && targets.Length >= 8:
						bigSwitches++;
						break;
					}
				}
			}
			report.Apis = apiCallers.Select(kv => new ApiHit(kv.Value.cat, kv.Key, kv.Value.callers.ToList(), kv.Value.count)).OrderBy(a => a.Category).ToList();
			report.Strings = stringHits;

			// --- P/Invokes
			var pinvokes = new List<ApiHit>();
			foreach (var m in methods.Where(m => m.IsPinvokeImpl && m.ImplMap is not null)) {
				var fn = m.ImplMap!.Name.String;
				var dll = m.ImplMap.Module?.Name.String ?? "?";
				var cat = PInvokeCategory(fn) ?? "Other";
				var callers = callCounts.TryGetValue(m, out var c) ? c.ToList() : new List<MethodDef>();
				pinvokes.Add(new ApiHit(cat, $"{dll}!{fn}", callers, callers.Count));
			}
			report.PInvokes = pinvokes;

			// --- obfuscation indicators
			var obf = new List<string>();
			var markerHits = types.Select(t => t.FullName).Concat(mod.Assembly?.CustomAttributes.Select(a => a.TypeFullName) ?? Enumerable.Empty<string>())
				.Concat(mod.CustomAttributes.Select(a => a.TypeFullName))
				.Where(n => obfuscatorMarkers.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase))).Distinct().Take(5).ToList();
			if (markerHits.Count > 0)
				obf.Add($"Obfuscator/protector marker types or attributes: {string.Join(", ", markerHits)}");
			var named = types.Where(t => !t.IsGlobalModuleType).Select(t => t.Name.String)
				.Concat(methods.Where(m => !m.IsConstructor && !m.IsSpecialName).Select(m => m.Name.String))
				.Where(n => !n.Contains('<')).ToList();
			int unreadable = named.Count(n => n.Any(ch => ch > 0x7E || char.IsControl(ch) || char.IsWhiteSpace(ch)));
			if (named.Count > 0 && unreadable > 0) {
				double pct = 100.0 * unreadable / named.Count;
				obf.Add($"{unreadable} of {named.Count} type/method names ({pct:0}%) contain non-ASCII or invisible characters" + (pct > 20 ? " -> renaming obfuscation" : ""));
			}
			int weird = named.Count(n => n.StartsWith("#=", StringComparison.Ordinal) || Regex.IsMatch(n, @"^[a-zA-Z]{1,2}$|^_?[0-9a-f]{16,}$|^[A-Za-z0-9]{25,}$"));
			if (named.Count > 20 && weird > named.Count / 4)
				obf.Add($"{weird} names look generated (very short, hex-like or #= prefixed) -> renaming obfuscation");
			if (bigSwitches > 10)
				obf.Add($"{bigSwitches} switch instructions with 8+ targets -> possible control-flow flattening");
			if (invalidBodies > 0)
				obf.Add($"{invalidBodies} method bodies dnlib can't parse -> invalid/encrypted IL (anti-decompilation)");
			if (noBody > 0)
				obf.Add($"{noBody} methods have no body -> bodies decrypted at runtime (method encryption / JIT hooking)");
			int countedMethods = methods.Count(m => m.HasBody);
			if (countedMethods > 50 && ldstrCount < countedMethods / 20)
				obf.Add($"Only {ldstrCount} string literals in {countedMethods} methods -> strings likely encrypted");
			if (cctor is { HasBody: true } && cctor.Body.Instructions.Any(i => i.Operand is IMethod))
				obf.Add("<Module>::.cctor calls methods: typical of protectors (anti-tamper, resource/string decryption setup)");
			if (mod.Resources.Count > 0 && mod.Resources.OfType<EmbeddedResource>().All(r => r.Length > 0 && IdentifyBytes(r.CreateReader().ToArray()).StartsWith("high entropy", StringComparison.Ordinal)))
				obf.Add("All embedded resources are high entropy -> encrypted resources");
			report.Obfuscation = obf;

			// --- string decryptor candidates
			report.DecryptorCandidates = callSites
				.Where(kv => kv.Value >= 5 && LooksLikeDecryptor(kv.Key))
				.Select(kv => new DecryptorCandidate(kv.Key, kv.Value))
				.OrderByDescending(d => d.CallSites).ToList();

			// --- blobs
			var blobs = new List<BlobHit>();
			try {
				foreach (var r in Resources(mod)) {
					if (r.Kind == "Embedded" && r.Entries.Count == 0 && (r.Size >= 256 && (r.Entropy > 7.0 || IsRecognized(r.Format))))
						blobs.Add(new BlobHit("resource", ResultFormatter.Quote(r.Name), (int)r.Size, r.Entropy, r.Format));
					foreach (var e in r.Entries) {
						if (e.Size >= 256 && (e.Entropy > 7.0 || IsRecognized(e.Format) || e.Format == "base64 text"))
							blobs.Add(new BlobHit("resource entry", $"{ResultFormatter.Quote(r.Name)}/{ResultFormatter.Quote(e.Name)}", e.Size, e.Entropy, e.Format));
					}
				}
				foreach (var a in InitializedArrays(mod, 16)) {
					bool byteArray = a.ElementType is "System.Byte" or "System.SByte";
					if (!(byteArray || a.Entropy > 6.5 || IsRecognized(a.Format) || a.Bytes.Length >= 1024))
						continue;
					var format = a.Format;
					if (byteArray && XorGuess(a.Bytes) is string guess)
						format += "; " + guess;
					var owner = a.Method is null ? "" : $" in {TriageReport.Short(a.Method)}";
					var target = AssignedField(a) is FieldDef f ? $" -> {AnnotationsReport.Printable(f.DeclaringType.Name)}::{AnnotationsReport.Printable(f.Name)}" : "";
					blobs.Add(new BlobHit($"{a.ElementType?.Replace("System.", "").ToLowerInvariant() ?? "?"}[] literal", $"{a.Field.Name} [0x{a.Field.MDToken.Raw:X8}]{owner}{target}", a.Bytes.Length, a.Entropy, format));
				}
			}
			catch (OperationCanceledException) {
				throw;
			}
			catch {
			}
			foreach (var s in stringHits.Where(s => s.Category is "Base64 blob" or "Hex blob" && s.Value.Length >= 344))
				blobs.Add(new BlobHit("string", $"in {TriageReport.Short(s.Method)}", s.Value.Length, 0, s.Decoded ?? ""));
			report.Blobs = blobs.OrderByDescending(b => b.Size).ToList();

			// --- starting points
			var start = new List<string>();
			if (mod.EntryPoint is MethodDef entry)
				start.Add($"Entry point: {entry.FullName}");
			if (cctor is { HasBody: true })
				start.Add($"Module initializer: {cctor.FullName}");
			foreach (var d in report.DecryptorCandidates.Take(3))
				start.Add($"String decryptor candidate: {d.Method.FullName} ({d.CallSites} call sites)");
			var keyCats = new[] { "Dynamic code loading", "Embedded resources", "Process injection / memory", "Reflection invoke", "Network", "Process execution", "Credential theft" };
			var keyMethods = report.Apis.Where(a => keyCats.Contains(a.Category)).Concat(report.PInvokes.Where(p => keyCats.Contains(p.Category)))
				.SelectMany(a => a.Callers.Select(c => (c, a.Category))).GroupBy(x => x.c)
				.OrderByDescending(g => g.Select(x => x.Category).Distinct().Count()).Take(6);
			foreach (var g in keyMethods)
				start.Add($"{g.Key.FullName}  uses {string.Join(", ", g.Select(x => x.Category).Distinct())}");
			report.StartingPoints = start;
			return report;
		}

		/// <summary>"single-byte XOR 0x5A -> "c2.exam..."" when one key turns the data into text or a known format.</summary>
		string? XorGuess(byte[] data) {
			var best = XorBruteforce(data).FirstOrDefault();
			return best is null ? null : $"single-byte XOR 0x{best.Key:X2} -> {best.Preview}";
		}

		/// <summary>
		/// Tries all 255 single-byte XOR keys and returns the ones that turn the data into mostly printable
		/// text or a recognizable format (best first). Cheap first guess for small encrypted blobs.
		/// </summary>
		public IReadOnlyList<XorCandidate> XorBruteforce(byte[] data) {
			var result = new List<XorCandidate>();
			if (data.Length < 4)
				return result;
			var sample = data.Length > 4096 ? data[..4096] : data;
			for (int k = 1; k < 256; k++) {
				var x = Xor(sample, (byte)k);
				var fmt = IdentifyBytes(x);
				if (IsRecognized(fmt)) {
					result.Add(new XorCandidate((byte)k, 1.0, fmt));
					continue;
				}
				int printable = x.Count(b => b is >= 0x20 and < 0x7F || b is 9 or 10 or 13);
				if ((double)printable / x.Length < 0.95)
					continue;
				var text = Encoding.ASCII.GetString(x);
				result.Add(new XorCandidate((byte)k, TextScore(text), ResultFormatter.Quote(text, 60)));
			}
			return result.OrderByDescending(c => c.Score).Take(5).ToList();
		}

		/// <summary>
		/// How much a string looks like real text/config (0..1): letters, digits, spaces and common
		/// separators score, rare symbols cost, and lowercase-heavy text gets a small bonus.
		/// </summary>
		static double TextScore(string s) {
			if (s.Length == 0)
				return 0;
			double score = 0;
			int lower = 0;
			foreach (var ch in s) {
				if (char.IsAsciiLetterLower(ch)) {
					score += 1;
					lower++;
				}
				else if (char.IsAsciiLetterUpper(ch) || char.IsAsciiDigit(ch) || ch == ' ')
					score += 1;
				else if (".:/\\-_=|,;'\"()\r\n\t".IndexOf(ch) >= 0)
					score += 0.6;
				else
					score -= 1;
			}
			return Math.Max(0, score / s.Length) * 0.9 + 0.1 * lower / s.Length;
		}

		/// <summary>The field an array literal is stored into (stsfld/stfld right after InitializeArray), if any.</summary>
		static FieldDef? AssignedField(ArrayInit a) {
			if (a.Method is null)
				return null;
			var instrs = a.Method.Body.Instructions;
			int i = instrs.TakeWhile(ins => ins.Offset != a.Offset).Count();
			for (int j = i + 1; j < instrs.Count && j <= i + 3; j++) {
				if (instrs[j].OpCode.Code is Code.Stsfld or Code.Stfld && instrs[j].Operand is IField f)
					return f.ResolveFieldDef();
			}
			return null;
		}

		static bool IsRecognized(string format) =>
			format.StartsWith("PE", StringComparison.Ordinal) || format is "ZIP" or "GZip" or "zlib" or "7z" or "RAR" or "CAB" or "BinaryFormatter serialized object";

		static string? MatchApi(IMethod im) {
			var typeName = im.DeclaringType?.FullName;
			if (typeName is null)
				return null;
			var name = im.Name.String;
			foreach (var (cat, pattern) in apiTable) {
				int sep = pattern.IndexOf("::", StringComparison.Ordinal);
				if (sep < 0) {
					// A type (any member) or a namespace (any type in it)
					if (typeName == pattern || typeName.StartsWith(pattern + ".", StringComparison.Ordinal) || typeName.StartsWith(pattern + "/", StringComparison.Ordinal))
						return cat;
					continue;
				}
				if (string.CompareOrdinal(typeName, 0, pattern, 0, sep) != 0 || typeName.Length != sep)
					continue;
				var mp = pattern.Substring(sep + 2);
				if (mp.EndsWith('*') ? name.StartsWith(mp.TrimEnd('*'), StringComparison.Ordinal) : name == mp)
					return cat;
			}
			return null;
		}

		static string? PInvokeCategory(string fn) {
			var baseName = fn.Length > 1 && (fn.EndsWith('A') || fn.EndsWith('W')) && char.IsLower(fn[^2]) ? fn[..^1] : fn;
			foreach (var (cat, names) in pinvokeTable) {
				if (names.Any(n => string.Equals(n, baseName, StringComparison.OrdinalIgnoreCase) || string.Equals(n, fn, StringComparison.OrdinalIgnoreCase)))
					return cat;
			}
			return null;
		}

		static readonly Regex base64Re = new Regex(@"^[A-Za-z0-9+/]{40,}={0,2}$");
		static readonly Regex hexRe = new Regex(@"^(?:[0-9A-Fa-f]{2}){32,}$");

		void ClassifyString(string s, MethodDef m, uint offset, List<StringHit> hits) {
			if (s.Length >= 40 && s.Length % 4 == 0 && base64Re.IsMatch(s)) {
				string? decoded = null;
				try {
					var bytes = Convert.FromBase64String(s);
					decoded = $"base64 -> {bytes.Length} bytes, {IdentifyBytes(bytes)}";
					if (IdentifyBytes(bytes) == "text")
						decoded += ": " + ResultFormatter.Quote(Encoding.UTF8.GetString(bytes), 60);
				}
				catch {
				}
				if (decoded is not null) {
					hits.Add(new StringHit("Base64 blob", s, m, offset, decoded));
					return;
				}
			}
			if (hexRe.IsMatch(s)) {
				var bytes = Convert.FromHexString(s);
				hits.Add(new StringHit("Hex blob", s, m, offset, $"hex -> {bytes.Length} bytes, {IdentifyBytes(bytes)}"));
				return;
			}
			foreach (var (cat, re) in stringTable) {
				if (!re.IsMatch(s))
					continue;
				if (cat == "IP address" && IsVersionLike(s))
					continue;
				hits.Add(new StringHit(cat, s, m, offset, null));
				return;
			}
		}

		static bool IsVersionLike(string s) {
			var m = Regex.Match(s, @"\b(\d+)\.(\d+)\.(\d+)\.(\d+)\b");
			if (!m.Success)
				return false;
			var parts = Enumerable.Range(1, 4).Select(i => int.Parse(m.Groups[i].Value)).ToArray();
			return parts[0] == 0 || parts.All(p => p < 10) || s.Contains("Version", StringComparison.OrdinalIgnoreCase);
		}

		static bool LooksLikeDecryptor(MethodDef m) {
			if (!m.IsStatic || m.ReturnType?.FullName is not ("System.String" or "System.Object"))
				return false;
			var ps = m.MethodSig?.Params;
			if (ps is null || ps.Count == 0 || ps.Count > 3)
				return false;
			return ps.All(p => p.ElementType is ElementType.I4 or ElementType.U4 or ElementType.I8 or ElementType.U8 or ElementType.I2 or ElementType.U2
				or ElementType.String or ElementType.I1 or ElementType.U1 or ElementType.Char);
		}

		static string DescribeImage(ModuleDef mod) {
			var parts = new List<string>();
			if (mod is ModuleDefMD md) {
				try {
					var pe = md.Metadata.PEImage;
					var fh = pe.ImageNTHeaders.FileHeader;
					var oh = pe.ImageNTHeaders.OptionalHeader;
					parts.Add($"PE: {(oh.Magic == 0x20B ? "PE32+" : "PE32")} {fh.Machine}");
					parts.Add((fh.Characteristics & Characteristics.Dll) != 0 ? "DLL" : "EXE");
					parts.Add(oh.Subsystem.ToString());
					parts.Add(md.IsILOnly ? "IL-only" : "MIXED-MODE (contains native code)");
					if (md.Is32BitRequired)
						parts.Add("32-bit required");
					else if (md.Is32BitPreferred)
						parts.Add("32-bit preferred");
					var ts = fh.TimeDateStamp;
					parts.Add(ts > 0x5F000000 && ts < 0x80000000 ? $"timestamp {DateTimeOffset.FromUnixTimeSeconds(ts):yyyy-MM-dd HH:mm} UTC" : $"timestamp 0x{ts:X8} (reproducible build or forged)");
					if (oh.DataDirectories.Length > 4 && oh.DataDirectories[4].VirtualAddress != 0)
						parts.Add("Authenticode signature present (not verified)");
					parts.Add($"{pe.ImageSectionHeaders.Count} sections: {string.Join(" ", pe.ImageSectionHeaders.Select(s => s.DisplayName))}");
				}
				catch {
				}
			}
			return string.Join(", ", parts);
		}

		static string AssemblyAttributes(ModuleDef mod) {
			if (mod.Assembly is null)
				return "";
			var names = new[] { "AssemblyTitleAttribute", "AssemblyDescriptionAttribute", "AssemblyCompanyAttribute", "AssemblyProductAttribute", "AssemblyCopyrightAttribute", "GuidAttribute" };
			var parts = new List<string>();
			foreach (var ca in mod.Assembly.CustomAttributes) {
				var n = ca.AttributeType?.Name.String;
				if (n is null || !names.Contains(n) || ca.ConstructorArguments.Count == 0)
					continue;
				var v = ca.ConstructorArguments[0].Value?.ToString();
				if (!string.IsNullOrWhiteSpace(v))
					parts.Add($"{n.Replace("Assembly", "").Replace("Attribute", "")}={ResultFormatter.Quote(v, 60)}");
			}
			return string.Join(", ", parts);
		}
	}
}
