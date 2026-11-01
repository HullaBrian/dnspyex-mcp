using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Windows.Threading;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Scripting;
using dnSpy.Mcp.Api;
using dnSpy.Mcp.Server;

namespace dnSpy.Mcp {
	[ExportExtension]
	sealed class TheExtension : IExtension {
		readonly IServiceLocator serviceLocator;
		McpHttpServer? server;
		InstanceRegistry? registry;

		[ImportingConstructor]
		TheExtension(IServiceLocator serviceLocator) => this.serviceLocator = serviceLocator;

		public IEnumerable<string> MergedResourceDictionaries {
			get { yield break; }
		}

		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "MCP server: lets AI agents drive dnSpy by executing C# scripts",
		};

		public void OnEvent(ExtensionEvent @event, object? obj) {
			switch (@event) {
			case ExtensionEvent.AppLoaded:
				Start();
				break;
			case ExtensionEvent.AppExit:
				Stop();
				break;
			}
		}

		void Start() {
			if (Environment.GetEnvironmentVariable("DNSPY_MCP_DISABLE") == "1")
				return;
			DnSpyHost? host = null;
			try {
				host = new DnSpyHost(serviceLocator, Dispatcher.CurrentDispatcher);
				var config = ServerConfig.Load();
				var tools = new McpTools(host);
				server = McpHttpServer.StartOnFreePort(tools, config);
				registry = new InstanceRegistry(server.Port, server.RequiresToken, server.Endpoint);
				registry.Register();
				if (config.SettingsFile is not null)
					host.Log($"MCP settings: {config.SettingsFile}");
				host.Log($"MCP server listening on {server.Endpoint}{(server.RequiresToken ? " (bearer token required)" : "")}");
				if (config.IsRemote) {
					host.Log($"Remote access ENABLED (bound to {config.BindAddress}, {(server.RequiresToken ? "token required" : "no token: anyone who can reach this port can run code")}). Reachable at:");
					foreach (var url in server.RemoteEndpoints)
						host.Log("  " + url);
					host.Log("If the client can't connect, allow the port in Windows Firewall: " +
						$"netsh advfirewall firewall add rule name=\"dnSpy MCP\" dir=in action=allow protocol=TCP localport={server.Port}");
				}
			}
			catch (Exception ex) {
				Debug.WriteLine($"dnSpy.Mcp: failed to start: {ex}");
				try {
					host?.Log($"MCP server failed to start: {ex.Message}");
				}
				catch {
				}
			}
		}

		void Stop() {
			registry?.Unregister();
			server?.Dispose();
			server = null;
		}
	}
}
