using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace dnSpy.Mcp.Server {
	/// <summary>
	/// Advertises this dnSpy instance to the stdio bridge by writing
	/// %LOCALAPPDATA%\dnSpy.Mcp\instances\&lt;pid&gt;.json while the server runs.
	/// </summary>
	sealed class InstanceRegistry {
		readonly int port;
		readonly bool requiresToken;
		readonly string url;
		readonly string filename;

		public static string InstancesDirectory =>
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dnSpy.Mcp", "instances");

		public InstanceRegistry(int port, bool requiresToken, string url) {
			this.port = port;
			this.requiresToken = requiresToken;
			this.url = url;
			filename = Path.Combine(InstancesDirectory, $"{Environment.ProcessId}.json");
		}

		public void Register() {
			try {
				Directory.CreateDirectory(InstancesDirectory);
				var info = new {
					pid = Environment.ProcessId,
					port,
					url,
					requires_token = requiresToken,
					exe = Process.GetCurrentProcess().MainModule?.FileName,
					started = DateTime.UtcNow.ToString("o"),
					version = typeof(InstanceRegistry).Assembly.GetName().Version?.ToString(),
				};
				File.WriteAllText(filename, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
			}
			catch (Exception ex) {
				Debug.WriteLine($"dnSpy.Mcp: could not register instance: {ex.Message}");
			}
		}

		public void Unregister() {
			try {
				File.Delete(filename);
			}
			catch {
			}
		}
	}
}
