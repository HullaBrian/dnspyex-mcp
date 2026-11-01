using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.App;
using dnSpy.Mcp.Api;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Scripting;

namespace dnSpy.Mcp.Scripting {
	public sealed class ExecutionResult {
		public bool Success { get; init; }
		public string Stdout { get; init; } = string.Empty;
		public long DroppedStdoutChars { get; init; }
		public bool HasValue { get; init; }
		public object? Value { get; init; }
		/// <summary>Compilation diagnostics or the runtime exception</summary>
		public string? Error { get; init; }
		public string? ErrorKind { get; init; }
		public TimeSpan Elapsed { get; init; }
		public bool TimedOut { get; init; }
		public bool SessionWasReset { get; init; }
	}

	/// <summary>
	/// A persistent C# script state: variables, functions, types and usings declared in one call
	/// are visible in the next. One per MCP session.
	/// </summary>
	public sealed class ScriptSession : IDisposable {
		static readonly CSharpParseOptions parseOptions = new CSharpParseOptions(LanguageVersion.Latest, kind: SourceCodeKind.Script);
		static readonly Lazy<ScriptOptions> scriptOptions = new Lazy<ScriptOptions>(CreateScriptOptions);

		readonly DnSpyHost host;
		readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
		ScriptState<object>? state;
		ScriptGlobals globals;
		bool poisoned;
		bool disposed;
		static int abandonedRunning;

		public ScriptSession(DnSpyHost host) {
			this.host = host;
			globals = new ScriptGlobals(host);
			OutputCapture.Install();
		}

		public void Reset() {
			state = null;
			globals = new ScriptGlobals(host);
			poisoned = false;
		}

		public async Task<ExecutionResult> ExecuteAsync(string code, TimeSpan timeout, int maxOutputChars, bool reset) {
			if (!await gate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)) {
				return new ExecutionResult {
					Success = false,
					ErrorKind = "busy",
					Error = "Another call is still executing in this session (calls run one at a time). Wait for it to return, then retry.",
				};
			}
			try {
				bool wasReset = false;
				if (reset || poisoned) {
					wasReset = poisoned && !reset;
					Reset();
				}

				var capture = new CapturedOutput(maxOutputChars);
				var cts = new CancellationTokenSource();
				globals.BeginExecution(cts.Token, capture);
				var sw = Stopwatch.StartNew();
				var prevState = state;
				var task = Task.Run(async () => {
					OutputCapture.Current = capture;
					try {
						if (prevState is null)
							return await CSharpScript.Create<object>(code, scriptOptions.Value, typeof(ScriptGlobals)).RunAsync(globals, CatchAll, cts.Token).ConfigureAwait(false);
						return await prevState.ContinueWithAsync<object>(code, scriptOptions.Value, CatchAll, cts.Token).ConfigureAwait(false);
					}
					finally {
						OutputCapture.Current = null;
					}
				});

				var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
				if (finished != task) {
					// Ask nicely (scripts that check Token will stop), then give up on this state.
					cts.Cancel();
					var grace = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
					if (grace != task) {
						// .NET can't abort the thread: abandon it (it keeps running) and give the session a fresh state
						poisoned = true;
						int running = Interlocked.Increment(ref abandonedRunning);
						_ = task.ContinueWith(_ => Interlocked.Decrement(ref abandonedRunning), TaskScheduler.Default);
						return new ExecutionResult {
							Success = false,
							TimedOut = true,
							ErrorKind = "timeout",
							Error = $"Script did not finish within {timeout.TotalSeconds:0.#}s and ignored cancellation, so it was abandoned (it keeps running in the background; {running} abandoned script(s) running). " +
								"The session state will be reset on the next call. In long loops check `Token.ThrowIfCancellationRequested()`.",
							Stdout = capture.ToString(),
							DroppedStdoutChars = capture.DroppedChars,
							Elapsed = sw.Elapsed,
						};
					}
				}
				sw.Stop();

				try {
					var newState = await task.ConfigureAwait(false);
					state = newState;
					if (newState.Exception is Exception ex) {
						return new ExecutionResult {
							Success = false,
							ErrorKind = ex is OperationCanceledException && cts.IsCancellationRequested ? "timeout" : "exception",
							TimedOut = cts.IsCancellationRequested,
							Error = FormatException(ex),
							Stdout = capture.ToString(),
							DroppedStdoutChars = capture.DroppedChars,
							Elapsed = sw.Elapsed,
							SessionWasReset = wasReset,
						};
					}
					return new ExecutionResult {
						Success = true,
						Stdout = capture.ToString(),
						DroppedStdoutChars = capture.DroppedChars,
						HasValue = EndsWithExpression(code, newState.ReturnValue),
						Value = newState.ReturnValue,
						Elapsed = sw.Elapsed,
						SessionWasReset = wasReset,
					};
				}
				catch (CompilationErrorException cee) {
					return new ExecutionResult {
						Success = false,
						ErrorKind = "compilation",
						Error = FormatDiagnostics(cee.Diagnostics),
						Elapsed = sw.Elapsed,
						SessionWasReset = wasReset,
					};
				}
				catch (Exception ex) {
					return new ExecutionResult {
						Success = false,
						ErrorKind = "exception",
						Error = FormatException(ex),
						Stdout = capture.ToString(),
						DroppedStdoutChars = capture.DroppedChars,
						Elapsed = sw.Elapsed,
						SessionWasReset = wasReset,
					};
				}
			}
			finally {
				gate.Release();
			}
		}

		// Keep the state (and the variables defined before the throw) when user code throws
		static bool CatchAll(Exception ex) => true;

		/// <summary>
		/// True if the submission ends in an expression without a semicolon (REPL-style value).
		/// Void invocations produce no value, so a null result from an invocation is not shown.
		/// </summary>
		static bool EndsWithExpression(string code, object? value) {
			var root = SyntaxFactory.ParseSyntaxTree(code, parseOptions).GetRoot() as CompilationUnitSyntax;
			if (root?.Members.LastOrDefault() is not GlobalStatementSyntax gs)
				return false;
			if (gs.Statement is not ExpressionStatementSyntax es || !es.SemicolonToken.IsMissing)
				return false;
			if (value is null && es.Expression is InvocationExpressionSyntax or AwaitExpressionSyntax or AssignmentExpressionSyntax)
				return false;
			return true;
		}

		static string FormatDiagnostics(ImmutableArray<Diagnostic> diagnostics) {
			var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(20).Select(d => {
				var span = d.Location.GetLineSpan();
				var pos = span.IsValid ? $"({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1})" : string.Empty;
				return $"{pos}: {d.Id}: {d.GetMessage()}";
			});
			return string.Join(Environment.NewLine, errors);
		}

		internal static string FormatException(Exception ex) {
			while ((ex is AggregateException || ex is TargetInvocationException) && ex.InnerException is not null)
				ex = ex.InnerException;
			// Lookup errors from the globals API carry everything in the message
			if (ex.TargetSite?.DeclaringType?.Namespace == typeof(ScriptGlobals).Namespace &&
				ex is KeyNotFoundException or AmbiguousMatchException or ArgumentException or FileNotFoundException)
				return $"{ex.GetType().Name}: {ex.Message}";
			// Hide Roslyn/dnSpy.Mcp plumbing frames; the submission frames are the interesting ones
			var lines = ex.ToString().Replace("\r", "").Split('\n')
				.Where(l => !l.TrimStart().StartsWith("at ") ||
					!(l.Contains("Microsoft.CodeAnalysis.Scripting") || l.Contains("System.Runtime.CompilerServices") ||
					  l.Contains("System.Threading.Tasks") || l.Contains("System.Threading.ExecutionContext") || l.Contains(" dnSpy.Mcp.")))
				.Take(40);
			return string.Join("\n", lines).TrimEnd();
		}

		static ScriptOptions CreateScriptOptions() {
			var binDir = AppDirectories.BinDirectory;
			var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
			var refs = new List<MetadataReference>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			void AddFile(string? path) {
				if (string.IsNullOrEmpty(path) || !File.Exists(path) || !seen.Add(Path.GetFileName(path)))
					return;
				refs.Add(MetadataReference.CreateFromFile(path));
			}

			void AddByName(string simpleName) {
				var loaded = AppDomain.CurrentDomain.GetAssemblies()
					.FirstOrDefault(a => !a.IsDynamic && string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.Location));
				if (loaded is not null) {
					AddFile(loaded.Location);
					return;
				}
				foreach (var dir in new[] { runtimeDir, binDir }) {
					var path = Path.Combine(dir, simpleName + ".dll");
					if (File.Exists(path)) {
						AddFile(path);
						return;
					}
				}
			}

			foreach (var name in DefaultReferences)
				AddByName(name);
			AddFile(typeof(ScriptGlobals).Assembly.Location);

			return ScriptOptions.Default
				.WithReferences(refs)
				.WithImports(DefaultImports)
				.WithMetadataResolver(ScriptMetadataResolver.Default.WithBaseDirectory(binDir).WithSearchPaths(binDir, runtimeDir))
				.WithSourceResolver(ScriptSourceResolver.Default.WithBaseDirectory(Environment.CurrentDirectory))
				.WithLanguageVersion(LanguageVersion.Latest)
				.WithAllowUnsafe(true)
				.WithOptimizationLevel(OptimizationLevel.Release)
				.WithEmitDebugInformation(false)
				.WithCheckOverflow(false);
		}

		internal static readonly string[] DefaultImports = {
			"System",
			"System.IO",
			"System.Linq",
			"System.Text",
			"System.Text.RegularExpressions",
			"System.Collections.Generic",
			"System.Threading",
			"System.Threading.Tasks",
			"dnlib.DotNet",
			"dnlib.DotNet.Emit",
			"dnSpy.Contracts.Documents",
			"dnSpy.Contracts.Decompiler",
			"dnSpy.Mcp.Api",
		};

		static readonly string[] DefaultReferences = {
			"System.Private.CoreLib",
			"System.Runtime",
			"netstandard",
			"mscorlib",
			"System.Core",
			"System.Collections",
			"System.Collections.Concurrent",
			"System.Collections.Immutable",
			"System.Collections.NonGeneric",
			"System.Collections.Specialized",
			"System.ComponentModel",
			"System.ComponentModel.Primitives",
			"System.ComponentModel.TypeConverter",
			"System.Console",
			"System.Diagnostics.Debug",
			"System.Diagnostics.Process",
			"System.Diagnostics.StackTrace",
			"System.IO.Compression",
			"System.IO.MemoryMappedFiles",
			"System.Linq",
			"System.Linq.Expressions",
			"System.Memory",
			"System.Net.Http",
			"System.Net.Primitives",
			"System.ObjectModel",
			"System.Reflection.Metadata",
			"System.Reflection.Primitives",
			"System.Runtime.Extensions",
			"System.Runtime.InteropServices",
			"System.Runtime.Numerics",
			"System.Security.Cryptography",
			"System.Text.Encoding.Extensions",
			"System.Text.Json",
			"System.Text.RegularExpressions",
			"System.Threading",
			"System.Threading.Tasks",
			"System.Threading.Thread",
			"System.Private.Xml",
			"System.Private.Xml.Linq",
			"System.Xml.ReaderWriter",
			"System.Xml.Linq",
			"Microsoft.CSharp",
			"WindowsBase",
			"PresentationCore",
			"PresentationFramework",
			"System.Xaml",
			"System.ComponentModel.Composition",
			"Microsoft.VisualStudio.CoreUtility",
			"Microsoft.VisualStudio.Text.Data",
			"Microsoft.VisualStudio.Text.Logic",
			"Microsoft.VisualStudio.Text.UI",
			"Microsoft.VisualStudio.Text.UI.Wpf",
			"dnlib",
			"Iced",
			"dnSpy.Contracts.Logic",
			"dnSpy.Contracts.DnSpy",
			"dnSpy.Contracts.Debugger",
			"dnSpy.Contracts.Debugger.DotNet",
			"dnSpy.Contracts.Debugger.DotNet.CorDebug",
			"dnSpy.Contracts.Debugger.DotNet.Mono",
		};

		public void Dispose() {
			if (disposed)
				return;
			disposed = true;
			state = null;
		}
	}
}
