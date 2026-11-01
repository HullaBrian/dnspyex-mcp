# Cross references and search

XrefsTo(target, module = null) -> IReadOnlyList<XRef>
  target: MethodDef/FieldDef/TypeDef/PropertyDef/EventDef, a dnlib ref, or a spec string ("Ns.Type::Member").
  Method: call/callvirt/newobj/ldftn/ldvirtftn/ldtoken.  Field: ldfld/stfld/ldsfld/stsfld/ldflda/ldtoken.
  Type: instructions whose operand is the type, or a member of it.  Property/Event: uses of its accessors.
  XRef { Method, Instruction, Offset, OpCode, Operand }; ToString() = "caller @IL_0012: call target".
Callers(target)      distinct calling methods
XrefsFrom(method)    every instruction in method that references a member/type/string
Callees(method)      distinct called methods (IMethod; Def(x) resolves them)
Strings(pattern)     ldstr literals in all method bodies -> StringRef { Value, Method, Offset }
DerivedTypes(type)   subclasses/implementers (transitive)
Overrides(method)    overriding / implementing methods

Searches cover every module loaded in dnSpy (or the `module` argument). Load dependencies with Load(path)
to get xrefs across assemblies.

## Recipes
// who writes this field?
XrefsTo(Field("App.Config::apiKey")).Where(x => x.OpCode.Code is Code.Stfld or Code.Stsfld)

// call tree (depth 2) below a method
void Tree(IMethod m, int depth, string indent = "") {
    print(indent + m.FullName);
    if (depth == 0 || m.ResolveMethodDef() is not MethodDef md) return;
    foreach (var c in Callees(md)) Tree(c, depth - 1, indent + "  ");
}
Tree(Method("App.Program::Main"), 2);

// methods that use a string, and decompile the first one
var hit = Strings("/https?://").First();
Decompile(hit.Method)

// find uses of a framework API (works even when System.* isn't loaded: compare names)
Methods().Where(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is IMethod im && im.FullName.Contains("System.Reflection.Assembly::Load")))
