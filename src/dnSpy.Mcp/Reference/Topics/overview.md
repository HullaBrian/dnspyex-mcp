# dnSpy MCP: code mode

`execute_csharp` runs C# script code (Roslyn scripting) inside the live dnSpyEx process.
- State persists across calls: variables, methods, classes and `using`s stay defined. Use `reset=true` to start over.
- A trailing expression with no semicolon is the result. `print(x)` / `Console.WriteLine` write output.
- Top-level `await` works. In long loops, check `Token.IsCancellationRequested` (the call's timeout cancels it).
- Imported: System, System.IO, System.Linq, System.Text, System.Text.RegularExpressions, System.Collections.Generic,
  System.Threading(.Tasks), dnlib.DotNet, dnlib.DotNet.Emit, dnSpy.Contracts.Documents, dnSpy.Contracts.Decompiler, dnSpy.Mcp.Api.
- Output is capped, so narrow results with LINQ (`.Where(...).Take(50)`) instead of printing whole modules.

## Globals (full list with signatures: reference("globals"))
Documents
  Modules, Assemblies, Documents        everything loaded in dnSpy's Assembly Explorer
  Module("Foo" | "Foo.dll" | path)      one module
  Load(path) / Unload(x)                open/close files in dnSpy
Lookup ("pattern": case-insensitive substring, wildcards "*Login*", or "/regex/")
  Types(pattern, module?)               all types incl. nested, matched on FullName
  Methods / Fields / Properties / Events(pattern, module?)   matched on member FullName
  Type("Ns.Type" | "Ns.Outer/Inner" | "UniqueSimpleName")
  Method("Ns.Type::Name" | "Ns.Type.Name(String,Int32)" | "System.Void Ns.T::M()")
  Field / Property / Event("Ns.Type::Name")
  Def(x)                                spec string, "0x06000123" token, or a dnlib ref -> definition
Code
  Decompile(x, language = "C#")         x: type/member/module/assembly/spec string. Languages: C#, VB, IL
  IL(method)                            compact dnlib IL listing with offsets and handlers
Cross references (like dnSpy's Analyzer)
  XrefsTo(x) / Callers(x)               instructions/methods that use a method, field, type, property or event
  XrefsFrom(m) / Callees(m)             what a method references/calls
  Strings(pattern)                      ldstr literals -> StringRef { Value, Method, Offset }
  DerivedTypes(t), Overrides(m)
UI
  Current        member shown in the active tab      Selected / SelectedAll   Assembly Explorer selection
  CaretReference reference under the caret            CurrentText              text of the active tab
  GoTo(x, newTab=false)                Refresh(module?) after edits
Editing / saving
  Edit with dnlib directly (see reference("editing")), then Refresh() and Save(module, path?)
Malware analysis (details: reference("malware"))
  Triage(module?)                       one-call report; also the `triage` tool
  Resources(), ResourceBytes(), InitializedArrays(), FieldData(), Identify(), Entropy(), Hex()
  Xor, Rc4, AesDecrypt, DesDecrypt, Pbkdf2, Gunzip, Inflate, Zlib, FromBase64, FromHex, Hash
  Dump(bytes, name), DumpResource(name), LoadBytes(bytes, name)   LoadBytes opens a decrypted stage in dnSpy
  SampleModules                         loaded files that aren't framework/dnSpy assemblies (default module for these helpers)
Annotations (persist per file SHA-256, re-applied when the file is opened)
  Rename(x, name), RenameParameter(m, i, name), Unrename(x)
  Comment(x, text, ilOffset?)           shows up in Decompile()/IL() output
  Note(text), Notes(), DeleteNote(i), Annotations()
Escape hatches
  Resolve<T>() / TryResolve<T>()        any dnSpy MEF service (IDocumentTabService, IDecompilerService, ...)
  UI(() => ...)                          run on the UI thread (needed for WPF objects)
  Debugger                               DbgManager (reference("debugger"))

## Typical first calls
  Modules
  Types("License").Select(t => t.FullName)
  Decompile(Type("MyApp.LicenseManager"))
  Strings("password").Take(20)
  Callers(Method("MyApp.LicenseManager::Check"))
