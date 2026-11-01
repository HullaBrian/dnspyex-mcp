using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Writer;
using dnlib.PE;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.DotNet.Breakpoints.Code;
using dnSpy.Contracts.Metadata;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Mcp.Api {
	public sealed partial class ScriptGlobals {
		// ------------------------------------------------------------------ UI state

		/// <summary>The definition behind the first selected node in the assembly explorer (or null).</summary>
		public object? Selected => host.UI(() =>
			host.DocumentTreeView.TreeView.SelectedItems.OfType<IMDTokenNode>().Select(n => (object?)n.Reference).FirstOrDefault());

		/// <summary>The definitions behind all selected nodes in the assembly explorer.</summary>
		public object[] SelectedAll => host.UI(() =>
			host.DocumentTreeView.TreeView.SelectedItems.OfType<IMDTokenNode>().Select(n => n.Reference).OfType<object>().ToArray());

		/// <summary>The node(s) shown in the active tab, e.g. the type or method the user is looking at.</summary>
		public object? Current => host.UI(() =>
			host.DocumentTabService.ActiveTab?.Content.Nodes.OfType<IMDTokenNode>().Select(n => (object?)n.Reference).FirstOrDefault());

		/// <summary>The reference (member, local, ...) under the caret in the active decompiler tab, or null.</summary>
		public object? CaretReference => host.UI(() => {
			var viewer = host.DocumentTabService.ActiveTab.TryGetDocumentViewer();
			if (viewer is null)
				return null;
			var pos = viewer.Caret.Position.BufferPosition.Position;
			return viewer.Content.ReferenceCollection.Find(pos)?.Data.Reference;
		});

		/// <summary>The text currently displayed in the active tab (decompiled code as the user sees it), or null.</summary>
		public string? CurrentText => host.UI(() => host.DocumentTabService.ActiveTab.TryGetDocumentViewer()?.Content.Text);

		/// <summary>Shows a definition in dnSpy (like clicking a reference). Accepts anything <see cref="Def"/> accepts.</summary>
		public void GoTo(object target, bool newTab = false) {
			object reference = target is ModuleDef or AssemblyDef or IDsDocument ? target : Def(target);
			host.UI(() => host.DocumentTabService.FollowReference(reference, newTab, setFocus: true));
		}

		/// <summary>
		/// Refreshes dnSpy's views after editing metadata/IL with dnlib (tree node names and
		/// decompiled tabs). Pass a module to refresh only its tabs.
		/// </summary>
		public void Refresh(ModuleDef? module = null) {
			host.UI(() => {
				host.DocumentTreeView.TreeView.RefreshAllNodes();
				if (module is not null && DocumentOf(module) is IDsDocument doc)
					host.DocumentTabService.RefreshModifiedDocument(doc);
				else
					host.DocumentTabService.Refresh(host.DocumentTabService.VisibleFirstTabs.ToArray());
			});
		}

		// ------------------------------------------------------------------ saving

		/// <summary>
		/// Writes a module to disk (defaults to overwriting its own file). Mixed-mode modules are written
		/// with dnlib's native writer so native code/resources are kept. Returns the path written.
		/// Note: edits made through scripts are not on dnSpy's undo stack.
		/// </summary>
		public string Save(ModuleDef module, string? path = null, bool preserveTokens = false) {
			var target = Path.GetFullPath(path ?? module.Location);
			if (string.IsNullOrEmpty(target))
				throw new ArgumentException("Module has no file location; pass a path.");
			var ms = new MemoryStream();
			if (module is ModuleDefMD md && !md.IsILOnly) {
				var opts = new NativeModuleWriterOptions(md, optimizeImageSize: true);
				if (preserveTokens)
					opts.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
				opts.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
				md.NativeWrite(ms, opts);
			}
			else {
				var opts = new ModuleWriterOptions(module);
				if (preserveTokens)
					opts.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
				opts.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
				module.Write(ms, opts);
			}
			// The file may be memory-mapped by any loaded document; release the mapping first
			foreach (var doc in AllDocuments()) {
				if (doc.PEImage is IPEImage pe && string.Equals(Path.GetFullPath(pe.Filename ?? ""), target, StringComparison.OrdinalIgnoreCase))
					(pe as IInternalPEImage)?.UnsafeDisableMemoryMappedIO();
			}
			File.WriteAllBytes(target, ms.ToArray());
			return target;
		}

		// ------------------------------------------------------------------ debugger

		/// <summary>
		/// dnSpy's debugger (null if the debugger extension isn't loaded). Use it to start/attach,
		/// inspect processes/threads/frames, and manage breakpoints. See reference("debugger").
		/// </summary>
		public DbgManager? Debugger => host.TryResolve<DbgManager>();

		/// <summary>
		/// Sets (or returns the existing) breakpoint at an IL offset of a method; with
		/// <paramref name="tracepointMessage"/> it's a tracepoint that logs instead of breaking.
		/// Works before debugging starts. Remove with Resolve&lt;DbgCodeBreakpointsService&gt;().Remove(...).
		/// </summary>
		public DbgCodeBreakpoint SetBreakpoint(object method, uint ilOffset = 0, string? tracepointMessage = null) {
			var m = Def(method) as MethodDef ?? throw new ArgumentException("SetBreakpoint() needs a method");
			var factory = host.Resolve<DbgDotNetBreakpointFactory>();
			var moduleId = ModuleId.CreateFromFile(m.Module);
			var bp = tracepointMessage is null
				? factory.Create(moduleId, m.MDToken.Raw, ilOffset)
				: factory.CreateTracepoint(moduleId, m.MDToken.Raw, ilOffset, tracepointMessage);
			return bp ?? factory.TryGetBreakpoint(moduleId, m.MDToken.Raw, ilOffset)
				?? throw new InvalidOperationException("Could not create the breakpoint");
		}
	}
}
