# Editing assemblies and saving

Scripts edit the dnlib object model directly (the same objects dnSpy displays). After editing:
  Refresh(module)              update the tree and re-decompile open tabs
  Save(module, path = null)    write to disk (null = overwrite the original file; mixed-mode keeps native code)
Script edits are NOT on dnSpy's undo stack, so save to a new path if unsure.

## Rename
var t = Type("a.b"); t.Name = "LicenseChecker"; t.Namespace = "App"; Refresh(t.Module);
Method("App.LicenseChecker::a").Name = "Validate";

## Patch a method to `return true;`
var m = Method("App.LicenseChecker::Validate");
var body = new CilBody();
body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
body.Instructions.Add(Instruction.Create(OpCodes.Ret));
m.Body = body;
Refresh(m.Module);
Decompile(m)

## Insert a call at the start of a method
var target = Method("App.Program::Main");
var writeLine = target.Module.Import(typeof(Console).GetMethod("WriteLine", new[] { typeof(string) }));
var il = target.Body.Instructions;
il.Insert(0, Instruction.Create(OpCodes.Ldstr, "patched"));
il.Insert(1, Instruction.Create(OpCodes.Call, writeLine));
target.Body.UpdateInstructionOffsets();

## Editing an existing body safely
body.SimplifyMacros(m.Parameters);   // short branches -> long forms before inserting instructions
... modify body.Instructions ...
body.OptimizeMacros();               // back to short forms

## Decrypting strings
Prefer re-implementing the algorithm from Decompile(decryptor) in C#. Executing target code
(Assembly.LoadFile + Invoke) runs untrusted code inside dnSpy; only do it if the user asks and trusts the binary.

## Save
Save(Module("App"), @"C:\out\App.patched.exe")
