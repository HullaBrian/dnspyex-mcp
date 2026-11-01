# Working with dnSpy's UI

What the user is looking at
  Current          definition shown in the active tab (TypeDef/MethodDef/...)
  CaretReference   reference under the caret in the active code view (member, local, ...)
  CurrentText      full text of the active tab (as displayed)
  Selected         first node selected in the Assembly Explorer; SelectedAll for all of them
Navigation
  GoTo(x, newTab = false)   open/select a definition (anything Def() accepts, or a ModuleDef/AssemblyDef)
  Load(path), Unload(x)     open/close files
  Refresh(module?)          re-render after edits
Services (all MEF exports, via Resolve<T>()):
  IDocumentTabService (DocumentTabService)   tabs: ActiveTab, FollowReference, Refresh, CloseAll
  IDocumentTreeView (DocumentTreeView)       FindNode(def), TreeView.SelectItems(nodes), TreeView.SelectedItems
  IDsDocumentService (DocumentService)       GetDocuments(), TryGetOrCreate, Remove, AssemblyResolver
  IDecompilerService (DecompilerService)     AllDecompilers, Decompiler (current language)
  dnSpy.Contracts.Output.IOutputService       output window panes
  dnSpy.Contracts.Bookmarks.BookmarksService  bookmarks
WPF objects must be touched on the UI thread: UI(() => DocumentTabService.ActiveTab?.Content.Title)
