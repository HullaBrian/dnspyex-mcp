using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace dnSpy.Mcp.Server {
	sealed class ServerConfig {
		public const int DefaultPort = 13380;
		public const string SettingsFileName = "dnSpy.Mcp.json";

		/// <summary>Address to listen on. Loopback by default; 0.0.0.0 / :: (or a specific NIC address) for remote access (token optional).</summary>
		public IPAddress BindAddress { get; init; } = IPAddress.Loopback;
		public int Port { get; init; } = DefaultPort;
		/// <summary>When set, try Port, Port+1, ... until one is free.</summary>
		public bool ScanForFreePort { get; init; } = true;
		public string? Token { get; init; }
		/// <summary>Where settings came from, for the log.</summary>
		public string? SettingsFile { get; init; }

		public bool IsRemote => !IPAddress.IsLoopback(BindAddress);

		public static string SettingsPath =>
			Path.Combine(Path.GetDirectoryName(typeof(ServerConfig).Assembly.Location) ?? ".", SettingsFileName);

		/// <summary>
		/// Reads <see cref="SettingsFileName"/> next to the extension DLL, then lets environment variables
		/// (DNSPY_MCP_BIND, DNSPY_MCP_PORT, DNSPY_MCP_TOKEN) override it. Throws on invalid settings.
		/// </summary>
		public static ServerConfig Load() {
			string? bind = null, token = null, file = null;
			int? port = null;

			var path = SettingsPath;
			if (File.Exists(path)) {
				file = path;
				JsonNode? json;
				try {
					json = JsonNode.Parse(File.ReadAllText(path), documentOptions: new System.Text.Json.JsonDocumentOptions {
						CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
						AllowTrailingCommas = true,
					});
				}
				catch (Exception ex) {
					throw new InvalidOperationException($"Invalid {path}: {ex.Message}");
				}
				if (json is JsonObject obj) {
					bind = obj["bind"]?.ToString();
					token = obj["token"]?.ToString();
					var p = obj["port"];
					if (p is not null) {
						if (!int.TryParse(p.ToString(), out var pv))
							throw new InvalidOperationException($"Invalid port in {path}: {p}");
						port = pv;
					}
				}
			}

			bind = NonEmpty(Environment.GetEnvironmentVariable("DNSPY_MCP_BIND")) ?? NonEmpty(bind);
			token = NonEmpty(Environment.GetEnvironmentVariable("DNSPY_MCP_TOKEN")) ?? NonEmpty(token);
			var portEnv = NonEmpty(Environment.GetEnvironmentVariable("DNSPY_MCP_PORT"));
			if (portEnv is not null) {
				if (!int.TryParse(portEnv, out var pv))
					throw new InvalidOperationException($"Invalid DNSPY_MCP_PORT: {portEnv}");
				port = pv;
			}
			if (port is not null && (port <= 0 || port >= 65536))
				throw new InvalidOperationException($"Port out of range: {port}");

			var config = new ServerConfig {
				BindAddress = ParseBind(bind),
				Port = port ?? DefaultPort,
				ScanForFreePort = port is null,
				Token = token,
				SettingsFile = file,
			};
			return config;
		}

		static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

		static IPAddress ParseBind(string? bind) {
			switch (bind?.ToLowerInvariant()) {
			case null:
			case "localhost":
			case "loopback":
				return IPAddress.Loopback;
			case "*":
			case "any":
			case "0.0.0.0":
				return IPAddress.Any;
			case "::":
			case "[::]":
				return IPAddress.IPv6Any;
			}
			if (IPAddress.TryParse(bind.Trim('[', ']'), out var addr))
				return addr;
			throw new InvalidOperationException($"Invalid bind address '{bind}': use an IP address, 0.0.0.0 (all interfaces) or 127.0.0.1");
		}
	}

	/// <summary>
	/// MCP "Streamable HTTP" transport (JSON responses only, no SSE) on top of a tiny HTTP/1.1
	/// server bound to the loopback interface.
	/// </summary>
	sealed class McpHttpServer : IDisposable {
		const string McpPath = "/mcp";
		const int MaxBodySize = 64 * 1024 * 1024;
		static readonly string[] SupportedProtocolVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

		readonly McpTools tools;
		readonly ServerConfig config;
		readonly TcpListener listener;
		readonly CancellationTokenSource cts = new CancellationTokenSource();
		readonly ConcurrentDictionary<string, McpSession> sessions = new ConcurrentDictionary<string, McpSession>();

		public int Port { get; }
		/// <summary>URL for local clients (loopback works for both loopback and wildcard binds).</summary>
		public string Endpoint => config.BindAddress.Equals(IPAddress.Any) || config.BindAddress.Equals(IPAddress.IPv6Any) || !config.IsRemote
			? $"http://127.0.0.1:{Port}{McpPath}"
			: UrlFor(config.BindAddress);
		public bool RequiresToken => config.Token is not null;
		public ServerConfig Config => config;

		string UrlFor(IPAddress addr) =>
			addr.AddressFamily == AddressFamily.InterNetworkV6 ? $"http://[{addr}]:{Port}{McpPath}" : $"http://{addr}:{Port}{McpPath}";

		/// <summary>URLs other machines can use to reach this server (empty when bound to loopback).</summary>
		public IReadOnlyList<string> RemoteEndpoints {
			get {
				var list = new List<string>();
				if (!config.IsRemote)
					return list;
				bool any4 = config.BindAddress.Equals(IPAddress.Any), any6 = config.BindAddress.Equals(IPAddress.IPv6Any);
				if (!any4 && !any6) {
					list.Add(UrlFor(config.BindAddress));
					return list;
				}
				try {
					foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) {
						if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up ||
							nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
							continue;
						foreach (var ua in nic.GetIPProperties().UnicastAddresses) {
							var a = ua.Address;
							if (a.AddressFamily == AddressFamily.InterNetwork || (any6 && a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
								list.Add(UrlFor(a));
						}
					}
				}
				catch {
				}
				return list;
			}
		}

		McpHttpServer(McpTools tools, ServerConfig config, TcpListener listener) {
			this.tools = tools;
			this.config = config;
			this.listener = listener;
			Port = ((IPEndPoint)listener.LocalEndpoint).Port;
			_ = AcceptLoopAsync();
		}

		public static McpHttpServer StartOnFreePort(McpTools tools, ServerConfig config) {
			int attempts = config.ScanForFreePort ? 32 : 1;
			Exception? last = null;
			for (int i = 0; i < attempts; i++) {
				var listener = new TcpListener(config.BindAddress, config.Port + i);
				try {
					listener.ExclusiveAddressUse = true;
					if (config.BindAddress.Equals(IPAddress.IPv6Any))
						listener.Server.DualMode = true;
					listener.Start();
					return new McpHttpServer(tools, config, listener);
				}
				catch (SocketException ex) {
					last = ex;
					listener.Stop();
				}
			}
			throw new InvalidOperationException($"Could not bind a port in {config.Port}..{config.Port + attempts - 1}", last);
		}

		async Task AcceptLoopAsync() {
			while (!cts.IsCancellationRequested) {
				TcpClient client;
				try {
					client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) {
					break;
				}
				catch (ObjectDisposedException) {
					break;
				}
				catch (SocketException) {
					continue;
				}
				_ = Task.Run(() => HandleConnectionAsync(client));
			}
		}

		async Task HandleConnectionAsync(TcpClient client) {
			using (client) {
				client.NoDelay = true;
				var stream = client.GetStream();
				var reader = new LineReader(new BufferedStream(stream, 16 * 1024));
				try {
					while (!cts.IsCancellationRequested) {
						var request = await HttpRequest.ReadAsync(reader, MaxBodySize, cts.Token).ConfigureAwait(false);
						if (request is null)
							break;
						var response = await HandleRequestAsync(request).ConfigureAwait(false);
						bool keepAlive = request.KeepAlive;
						await response.WriteAsync(stream, keepAlive, cts.Token).ConfigureAwait(false);
						if (!keepAlive)
							break;
					}
				}
				catch (HttpParseException ex) {
					try {
						await HttpResponse.Text(400, ex.Message).WriteAsync(stream, false, CancellationToken.None).ConfigureAwait(false);
					}
					catch {
					}
				}
				catch (Exception ex) when (ex is IOException || ex is SocketException || ex is OperationCanceledException || ex is ObjectDisposedException) {
				}
				catch (Exception ex) {
					Debug.WriteLine($"dnSpy.Mcp: connection error: {ex}");
				}
			}
		}

		async Task<HttpResponse> HandleRequestAsync(HttpRequest request) {
			// DNS rebinding protection: browsers always send Origin on cross-origin requests.
			var origin = request.GetHeader("Origin");
			if (origin is not null && !IsLoopbackOrigin(origin))
				return HttpResponse.Text(403, "Forbidden origin");

			if (config.Token is not null && !TokenMatches(request.GetHeader("Authorization")))
				return HttpResponse.Text(401, "Missing or invalid bearer token");

			var path = request.Path;
			int q = path.IndexOf('?');
			if (q >= 0)
				path = path.Substring(0, q);

			if (path == "/" || path == "/health") {
				if (request.Method != "GET")
					return HttpResponse.Text(405, "Method not allowed");
				var info = new JsonObject {
					["name"] = "dnSpy.Mcp",
					["version"] = typeof(McpHttpServer).Assembly.GetName().Version?.ToString(),
					["pid"] = Environment.ProcessId,
					["endpoint"] = Endpoint,
					["sessions"] = sessions.Count,
				};
				return HttpResponse.Json(200, info.ToJsonString());
			}

			if (path != McpPath)
				return HttpResponse.Text(404, "Not found");

			switch (request.Method) {
			case "POST":
				return await HandlePostAsync(request).ConfigureAwait(false);
			case "DELETE": {
				var id = request.GetHeader("Mcp-Session-Id");
				if (id is not null && sessions.TryRemove(id, out var session))
					session.Dispose();
				return HttpResponse.Empty(200);
			}
			case "GET":
				// We never initiate server->client messages, so no SSE stream is offered.
				return HttpResponse.Text(405, "SSE stream not supported");
			default:
				return HttpResponse.Text(405, "Method not allowed");
			}
		}

		bool TokenMatches(string? authorization) {
			const string prefix = "Bearer ";
			if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
				return false;
			var given = Encoding.UTF8.GetBytes(authorization.Substring(prefix.Length).Trim());
			var expected = Encoding.UTF8.GetBytes(config.Token!);
			return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(given, expected);
		}

		static bool IsLoopbackOrigin(string origin) {
			if (origin == "null")
				return false;
			if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
				return false;
			return uri.IsLoopback;
		}

		async Task<HttpResponse> HandlePostAsync(HttpRequest request) {
			JsonNode? body;
			try {
				body = JsonNode.Parse(request.Body);
			}
			catch (Exception ex) {
				return HttpResponse.Json(400, JsonRpc.Error(null, JsonRpc.ParseError, "Parse error: " + ex.Message).ToJsonString());
			}

			var sessionId = request.GetHeader("Mcp-Session-Id");
			McpSession? session = null;
			if (sessionId is not null && !sessions.TryGetValue(sessionId, out session))
				return HttpResponse.Text(404, "Unknown session");

			var messages = body is JsonArray arr ? new List<JsonNode?>(arr) : new List<JsonNode?> { body };
			string? newSessionId = null;
			var responses = new JsonArray();
			foreach (var msg in messages) {
				if (msg is not JsonObject obj) {
					responses.Add(JsonRpc.Error(null, JsonRpc.InvalidRequest, "Invalid request"));
					continue;
				}
				var method = obj["method"]?.GetValue<string>();
				var id = obj["id"]?.DeepClone();
				if (method is null)
					continue; // A response from the client, we never send requests so ignore it
				if (id is null)
					continue; // Notification (initialized, cancelled, ...)

				if (method == "initialize") {
					session ??= CreateSession(out newSessionId);
					responses.Add(JsonRpc.Result(id, BuildInitializeResult(obj["params"] as JsonObject)));
					continue;
				}
				// Clients that skip session headers (or old clients) share an implicit default session
				session ??= GetDefaultSession();
				responses.Add(await DispatchAsync(session, id, method, obj["params"] as JsonObject).ConfigureAwait(false));
			}

			if (responses.Count == 0)
				return HttpResponse.Empty(202);
			var json = body is JsonArray ? responses.ToJsonString() : responses[0]!.ToJsonString();
			var response = HttpResponse.Json(200, json);
			if (newSessionId is not null)
				response.Headers["Mcp-Session-Id"] = newSessionId;
			return response;
		}

		McpSession CreateSession(out string id) {
			id = Guid.NewGuid().ToString("N");
			var session = new McpSession(id);
			sessions[id] = session;
			return session;
		}

		McpSession GetDefaultSession() => sessions.GetOrAdd("default", id => new McpSession(id));

		JsonObject BuildInitializeResult(JsonObject? p) {
			var requested = p?["protocolVersion"]?.GetValue<string>();
			var version = Array.IndexOf(SupportedProtocolVersions, requested) >= 0 ? requested! : SupportedProtocolVersions[0];
			return new JsonObject {
				["protocolVersion"] = version,
				["capabilities"] = new JsonObject {
					["tools"] = new JsonObject { ["listChanged"] = false },
				},
				["serverInfo"] = new JsonObject {
					["name"] = "dnspy",
					["title"] = "dnSpyEx",
					["version"] = typeof(McpHttpServer).Assembly.GetName().Version?.ToString(3),
				},
				["instructions"] = McpTools.ServerInstructions,
			};
		}

		async Task<JsonNode> DispatchAsync(McpSession session, JsonNode id, string method, JsonObject? p) {
			try {
				switch (method) {
				case "ping":
					return JsonRpc.Result(id, new JsonObject());
				case "tools/list":
					return JsonRpc.Result(id, new JsonObject { ["tools"] = tools.ListTools() });
				case "tools/call": {
					var name = p?["name"]?.GetValue<string>() ?? string.Empty;
					var args = p?["arguments"] as JsonObject ?? new JsonObject();
					var result = await tools.CallAsync(session, name, args).ConfigureAwait(false);
					return JsonRpc.Result(id, result);
				}
				case "resources/list":
					return JsonRpc.Result(id, new JsonObject { ["resources"] = new JsonArray() });
				case "prompts/list":
					return JsonRpc.Result(id, new JsonObject { ["prompts"] = new JsonArray() });
				default:
					return JsonRpc.Error(id, JsonRpc.MethodNotFound, $"Method not found: {method}");
				}
			}
			catch (McpException ex) {
				return JsonRpc.Error(id, ex.Code, ex.Message);
			}
			catch (Exception ex) {
				return JsonRpc.Error(id, JsonRpc.InternalError, ex.ToString());
			}
		}

		public void Dispose() {
			cts.Cancel();
			try {
				listener.Stop();
			}
			catch {
			}
			foreach (var s in sessions.Values)
				s.Dispose();
			sessions.Clear();
		}
	}

	sealed class McpException : Exception {
		public int Code { get; }
		public McpException(int code, string message) : base(message) => Code = code;
	}

	static class JsonRpc {
		public const int ParseError = -32700;
		public const int InvalidRequest = -32600;
		public const int MethodNotFound = -32601;
		public const int InvalidParams = -32602;
		public const int InternalError = -32603;

		public static JsonObject Result(JsonNode? id, JsonNode result) =>
			new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

		public static JsonObject Error(JsonNode? id, int code, string message) =>
			new JsonObject {
				["jsonrpc"] = "2.0",
				["id"] = id,
				["error"] = new JsonObject { ["code"] = code, ["message"] = message },
			};
	}

	sealed class HttpParseException : Exception {
		public HttpParseException(string message) : base(message) { }
	}

	sealed class HttpRequest {
		public string Method { get; private set; } = string.Empty;
		public string Path { get; private set; } = string.Empty;
		public string Version { get; private set; } = string.Empty;
		public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		public string Body { get; private set; } = string.Empty;

		public string? GetHeader(string name) => Headers.TryGetValue(name, out var v) ? v : null;

		public bool KeepAlive {
			get {
				var conn = GetHeader("Connection");
				if (Version == "HTTP/1.0")
					return string.Equals(conn, "keep-alive", StringComparison.OrdinalIgnoreCase);
				return !string.Equals(conn, "close", StringComparison.OrdinalIgnoreCase);
			}
		}

		/// <summary>Returns null if the connection was closed before a new request started.</summary>
		public static async Task<HttpRequest?> ReadAsync(LineReader reader, int maxBodySize, CancellationToken token) {
			var requestLine = await reader.ReadLineAsync(token).ConfigureAwait(false);
			if (requestLine is null)
				return null;
			var parts = requestLine.Split(' ');
			if (parts.Length != 3)
				throw new HttpParseException("Bad request line");
			var req = new HttpRequest { Method = parts[0].ToUpperInvariant(), Path = parts[1], Version = parts[2] };
			while (true) {
				var line = await reader.ReadLineAsync(token).ConfigureAwait(false) ?? throw new HttpParseException("Unexpected EOF in headers");
				if (line.Length == 0)
					break;
				int colon = line.IndexOf(':');
				if (colon <= 0)
					throw new HttpParseException("Bad header");
				req.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
				if (req.Headers.Count > 100)
					throw new HttpParseException("Too many headers");
			}

			byte[] body;
			if (string.Equals(req.GetHeader("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase))
				body = await reader.ReadChunkedAsync(maxBodySize, token).ConfigureAwait(false);
			else if (int.TryParse(req.GetHeader("Content-Length"), out var len) && len > 0) {
				if (len > maxBodySize)
					throw new HttpParseException("Body too large");
				body = await reader.ReadExactAsync(len, token).ConfigureAwait(false);
			}
			else
				body = Array.Empty<byte>();
			req.Body = Encoding.UTF8.GetString(body);
			return req;
		}
	}

	/// <summary>Reads HTTP requests from one connection (wrap the socket stream in a BufferedStream).</summary>
	sealed class LineReader {
		readonly Stream stream;
		readonly byte[] buffer = new byte[1];

		public LineReader(Stream stream) => this.stream = stream;

		async Task<int> ReadByteAsync(CancellationToken token) {
			int n = await stream.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false);
			return n == 0 ? -1 : buffer[0];
		}

		public async Task<string?> ReadLineAsync(CancellationToken token) {
			var bytes = new List<byte>();
			while (true) {
				int b = await ReadByteAsync(token).ConfigureAwait(false);
				if (b < 0)
					return bytes.Count == 0 ? null : throw new HttpParseException("Unexpected EOF");
				if (b == '\n')
					break;
				if (b != '\r')
					bytes.Add((byte)b);
				if (bytes.Count > 16 * 1024)
					throw new HttpParseException("Line too long");
			}
			return Encoding.ASCII.GetString(bytes.ToArray());
		}

		public async Task<byte[]> ReadExactAsync(int count, CancellationToken token) {
			var data = new byte[count];
			int pos = 0;
			while (pos < count) {
				int n = await stream.ReadAsync(data.AsMemory(pos, count - pos), token).ConfigureAwait(false);
				if (n == 0)
					throw new HttpParseException("Unexpected EOF in body");
				pos += n;
			}
			return data;
		}

		public async Task<byte[]> ReadChunkedAsync(int maxBodySize, CancellationToken token) {
			var ms = new MemoryStream();
			while (true) {
				var sizeLine = await ReadLineAsync(token).ConfigureAwait(false) ?? throw new HttpParseException("Unexpected EOF in chunk");
				int semi = sizeLine.IndexOf(';');
				if (semi >= 0)
					sizeLine = sizeLine.Substring(0, semi);
				if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out var size) || size < 0)
					throw new HttpParseException("Bad chunk size");
				if (size == 0) {
					// Trailers
					while (!string.IsNullOrEmpty(await ReadLineAsync(token).ConfigureAwait(false))) { }
					return ms.ToArray();
				}
				if (ms.Length + size > maxBodySize)
					throw new HttpParseException("Body too large");
				var chunk = await ReadExactAsync(size, token).ConfigureAwait(false);
				ms.Write(chunk, 0, chunk.Length);
				await ReadLineAsync(token).ConfigureAwait(false);
			}
		}
	}

	sealed class HttpResponse {
		public int Status { get; }
		public string ContentType { get; }
		public byte[] Body { get; }
		public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>();

		HttpResponse(int status, string contentType, byte[] body) {
			Status = status;
			ContentType = contentType;
			Body = body;
		}

		public static HttpResponse Json(int status, string json) => new HttpResponse(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
		public static HttpResponse Text(int status, string text) => new HttpResponse(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
		public static HttpResponse Empty(int status) => new HttpResponse(status, "text/plain", Array.Empty<byte>());

		static string Reason(int status) => status switch {
			200 => "OK",
			202 => "Accepted",
			400 => "Bad Request",
			401 => "Unauthorized",
			403 => "Forbidden",
			404 => "Not Found",
			405 => "Method Not Allowed",
			_ => "Error",
		};

		public async Task WriteAsync(Stream stream, bool keepAlive, CancellationToken token) {
			var sb = new StringBuilder();
			sb.Append($"HTTP/1.1 {Status} {Reason(Status)}\r\n");
			sb.Append($"Content-Type: {ContentType}\r\n");
			sb.Append($"Content-Length: {Body.Length}\r\n");
			sb.Append(keepAlive ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
			foreach (var kv in Headers)
				sb.Append($"{kv.Key}: {kv.Value}\r\n");
			sb.Append("\r\n");
			var head = Encoding.ASCII.GetBytes(sb.ToString());
			await stream.WriteAsync(head, token).ConfigureAwait(false);
			if (Body.Length > 0)
				await stream.WriteAsync(Body, token).ConfigureAwait(false);
			await stream.FlushAsync(token).ConfigureAwait(false);
		}
	}
}
