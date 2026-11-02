using System;
using System.Windows.Threading;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Output;
using dnSpy.Contracts.Scripting;
using dnSpy.Contracts.Text;

namespace dnSpy.Mcp.Api {
	/// <summary>
	/// Owns the connection to the running dnSpy instance: MEF services and the UI dispatcher.
	/// Shared by all script sessions.
	/// </summary>
	public sealed class DnSpyHost {
		static readonly Guid OutputPaneGuid = new Guid("6C3A6F2E-2B7A-4F4B-9D7E-3E6B1C8A0D51");

		readonly IServiceLocator serviceLocator;
		readonly Lazy<IDsDocumentService> documentService;
		readonly Lazy<IDocumentTabService> documentTabService;
		readonly Lazy<IDecompilerService> decompilerService;
		readonly Lazy<IOutputTextPane?> outputPane;

		public Dispatcher Dispatcher { get; }

		public DnSpyHost(IServiceLocator serviceLocator, Dispatcher dispatcher) {
			this.serviceLocator = serviceLocator ?? throw new ArgumentNullException(nameof(serviceLocator));
			Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
			documentService = new Lazy<IDsDocumentService>(() => Resolve<IDsDocumentService>());
			documentTabService = new Lazy<IDocumentTabService>(() => Resolve<IDocumentTabService>());
			decompilerService = new Lazy<IDecompilerService>(() => Resolve<IDecompilerService>());
			outputPane = new Lazy<IOutputTextPane?>(() => {
				var svc = TryResolve<IOutputService>();
				return svc is null ? null : UI(() => svc.Create(OutputPaneGuid, "MCP"));
			});
		}

		public IDsDocumentService DocumentService => documentService.Value;
		public IDocumentTabService DocumentTabService => documentTabService.Value;
		public IDocumentTreeView DocumentTreeView => DocumentTabService.DocumentTreeView;
		public IDecompilerService DecompilerService => decompilerService.Value;

		/// <summary>Persisted renames/comments/notes, shared by all sessions.</summary>
		public AnnotationStore Annotations { get; } = new AnnotationStore();

		public T Resolve<T>() where T : class => serviceLocator.Resolve<T>();
		public T? TryResolve<T>() where T : class => serviceLocator.TryResolve<T>();

		public void UI(Action action) {
			if (Dispatcher.CheckAccess())
				action();
			else
				Dispatcher.Invoke(action, DispatcherPriority.Send);
		}

		public T UI<T>(Func<T> func) {
			if (Dispatcher.CheckAccess())
				return func();
			return Dispatcher.Invoke(func, DispatcherPriority.Send);
		}

		/// <summary>Writes a line to dnSpy's Output window ("MCP" pane).</summary>
		public void Log(string message) {
			try {
				var pane = outputPane.Value;
				if (pane is null)
					return;
				var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
				Dispatcher.BeginInvoke(() => pane.WriteLine(BoxedTextColor.Text, line));
			}
			catch {
				// Logging must never break a tool call
			}
		}
	}
}
