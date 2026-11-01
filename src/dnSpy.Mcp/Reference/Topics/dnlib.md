# dnlib quick reference (dnlib.DotNet, dnlib.DotNet.Emit)

ModuleDef            Name, Location, Assembly, Types (top-level), GetTypes() (all, incl. nested), EntryPoint,
                     ResolveToken(uint), Resources, CorLibTypes, Import(...), IsILOnly, RuntimeVersion, GetAssemblyRefs()
AssemblyDef          Name, Version, FullName, ManifestModule, Modules, CustomAttributes
TypeDef              Namespace, Name, FullName, BaseType, Interfaces, NestedTypes, DeclaringType, Methods, Fields,
                     Properties, Events, GenericParameters, CustomAttributes, IsInterface/IsEnum/IsValueType/IsAbstract,
                     FindMethod(name), FindField(name), FindProperty(name), FindDefaultConstructor(), FindStaticConstructor()
MethodDef            Name, FullName, DeclaringType, MethodSig, ReturnType, Parameters, ParamDefs, IsStatic/IsVirtual/
                     IsConstructor, HasBody, Body (CilBody), Overrides, ImplMap (P/Invoke), RVA, MDToken
CilBody              Instructions, Variables (locals), ExceptionHandlers, MaxStack, InitLocals,
                     SimplifyMacros(parameters) / OptimizeMacros() / OptimizeBranches() / UpdateInstructionOffsets()
Instruction          OpCode (OpCodes.Call, ...; .Code is the Code enum), Operand, Offset,
                     GetLdcI4Value(), IsLdcI4(), IsBr(), IsBrtrue(), IsLdloc(), IsStloc(), GetLocal(locals)
                     Instruction.Create(OpCodes.Ldstr, "x"), Instruction.Create(OpCodes.Call, method)
Operand types        IMethod (MethodDef | MemberRef | MethodSpec), IField (FieldDef | MemberRef), ITypeDefOrRef
                     (TypeDef | TypeRef | TypeSpec), string (ldstr), Instruction (branch), Instruction[] (switch), Local, Parameter
Resolving refs       memberRef.ResolveMethodDef(), .ResolveFieldDef(), typeRef.ResolveTypeDef() (null if unresolvable)
                     or the ...Throw() variants. Def(x) does this for you.
FieldDef             FieldType, InitialValue (byte[] for RVA fields), HasFieldRVA, Constant, IsStatic, IsLiteral
PropertyDef          GetMethod, SetMethod, PropertySig;  EventDef: AddMethod, RemoveMethod, EventType
CustomAttribute      AttributeType, TypeFullName, Constructor, ConstructorArguments (CAArgument.Value), NamedArguments
Resources            module.Resources -> EmbeddedResource (CreateReader().ToArray()), LinkedResource, AssemblyLinkedResource
Comparers            new SigComparer().Equals(a, b), MethodEqualityComparer.CompareDeclaringTypes, TypeEqualityComparer.Instance
Names                UTF8String converts implicitly to/from string; compare with == "Name"; use .String for string methods.

## Examples
// methods with a big switch (state machines / control-flow flattening)
Methods().Where(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode.Code == Code.Switch && ((Instruction[])i.Operand).Length > 20))

// all P/Invokes
Methods().Where(m => m.IsPinvokeImpl).Select(m => $"{m.FullName} -> {m.ImplMap.Module.Name}!{m.ImplMap.Name}")

// embedded resources
Modules.SelectMany(m => m.Resources.OfType<EmbeddedResource>()).Select(r => $"{r.Name} {r.CreateReader().Length} bytes")

// static byte arrays initialised from RVA fields
Fields().Where(f => f.HasFieldRVA && f.InitialValue?.Length > 16).Select(f => $"{f.FullName} {f.InitialValue.Length}")
