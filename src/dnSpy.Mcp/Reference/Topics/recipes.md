# Recipes

## Triage a new binary
var m = Load(@"C:\samples\app.exe");
new { Asm = m.Assembly?.FullName, m.RuntimeVersion, m.Kind, Types = m.GetTypes().Count(), Entry = m.EntryPoint?.FullName,
      Refs = string.Join(", ", m.GetAssemblyRefs().Select(r => r.Name)) }

## Namespaces and their sizes
Types(module: Module("app")).GroupBy(t => t.Namespace.String).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}: {g.Count()}")

## Find obfuscation hints
Types().Count(t => t.Name.String.Any(c => c > 0x7f || char.IsControl(c)))   // unreadable names
Types().Where(t => t.CustomAttributes.Any(a => a.TypeFullName.Contains("Obfuscat"))).Select(t => t.FullName)

## Crypto / network / process APIs used
var apis = new[] { "System.Security.Cryptography", "System.Net.", "System.Diagnostics.Process::Start", "Assembly::Load" };
Methods().Where(m => m.HasBody).SelectMany(m => XrefsFrom(m))
  .Where(x => x.Operand is IMethod im && apis.Any(a => im.FullName.Contains(a)))
  .GroupBy(x => ((IMethod)x.Operand).FullName).Select(g => $"{g.Count(),4}  {g.Key}")

## Decompile everything that touches a string
foreach (var s in Strings("license").DistinctBy(s => s.Method).Take(5)) print(Decompile(s.Method));

## Export a whole module as C# to disk
var sb = new StringBuilder();
foreach (var t in Module("app").Types) { if (Token.IsCancellationRequested) break; sb.AppendLine(Decompile(t)); }
File.WriteAllText(@"C:\out\app.cs", sb.ToString());

## Helper functions persist across calls
string D(string spec) => Decompile(Def(spec));
// next call:  D("App.Crypto::Decrypt")

## Show the result to the user in dnSpy
GoTo(Method("App.Crypto::Decrypt"))
