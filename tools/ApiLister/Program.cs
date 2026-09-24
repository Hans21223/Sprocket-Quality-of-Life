// Lists classes and their members in Sprocket's BepInEx\interop assemblies by reading metadata only
// (no game code is loaded or run).
//   dotnet run --project tools\ApiLister -- <interop folder or .dll> ~Turret          find classes whose name contains "Turret"
//   dotnet run --project tools\ApiLister -- <interop folder or .dll> TurretRingEditor  show that class's fields and methods
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length < 2) { Console.WriteLine("usage: ApiLister <interop folder or dll> <~search | ClassName>..."); return; }
var files = Directory.Exists(args[0]) ? Directory.GetFiles(args[0], "*.dll").OrderBy(f => f).ToArray() : new[] { args[0] };
foreach (var file in files)
{
    using var pe = new PEReader(File.OpenRead(file));
    if (!pe.HasMetadata) continue;
    var md = pe.GetMetadataReader();
    var names = new Names(md);
    foreach (var h in md.TypeDefinitions)
    {
        var t = md.GetTypeDefinition(h);
        var full = FullName(md, h);
        var search = args.Skip(1).Where(a => a.StartsWith("~")).Any(a => full.Contains(a[1..], StringComparison.OrdinalIgnoreCase));
        var exact = args.Skip(1).Any(a => !a.StartsWith("~") && (full == a || full.EndsWith("." + a) || full.EndsWith("+" + a)));
        if (search && !exact) Console.WriteLine($"{Path.GetFileName(file)}: {full}");
        if (!exact) continue;
        var bases = t.BaseType.IsNil ? "" : " : " + names.Of(t.BaseType);
        var ifaces = t.GetInterfaceImplementations().Select(i => names.Of(md.GetInterfaceImplementation(i).Interface)).ToList();
        Console.WriteLine($"\n== {full}{bases}{(ifaces.Count > 0 ? " | " + string.Join(", ", ifaces) : "")}   [{Path.GetFileName(file)}]");
        foreach (var f in t.GetFields())
        {
            var fd = md.GetFieldDefinition(f); var n = md.GetString(fd.Name);
            if (!n.StartsWith("NativeFieldInfoPtr") && !n.StartsWith("NativeMethodInfoPtr")) Console.WriteLine($"   field {fd.DecodeSignature(names, null)} {n}");
        }
        foreach (var m in t.GetMethods())
        {
            var def = md.GetMethodDefinition(m); var n = md.GetString(def.Name);
            if (n == ".cctor") continue;
            var sig = def.DecodeSignature(names, null);
            var pnames = def.GetParameters().Select(p => md.GetParameter(p)).Where(p => p.SequenceNumber > 0)
                .OrderBy(p => p.SequenceNumber).Select(p => md.GetString(p.Name)).ToList();
            var ps = sig.ParameterTypes.Select((p, i) => p + " " + (i < pnames.Count ? pnames[i] : ""));
            var mods = ((def.Attributes & MethodAttributes.Static) != 0 ? "static " : "") + ((def.Attributes & MethodAttributes.Virtual) != 0 ? "virtual " : "");
            Console.WriteLine($"   {mods}{sig.ReturnType} {n}({string.Join(", ", ps)})");
        }
    }
}

static string FullName(MetadataReader md, TypeDefinitionHandle h)
{
    var t = md.GetTypeDefinition(h);
    if (!t.GetDeclaringType().IsNil) return FullName(md, t.GetDeclaringType()) + "+" + md.GetString(t.Name);
    var ns = md.GetString(t.Namespace);
    return (ns.Length > 0 ? ns + "." : "") + md.GetString(t.Name);
}

sealed class Names : ISignatureTypeProvider<string, object?>
{
    readonly MetadataReader md; public Names(MetadataReader m) => md = m;
    public string Of(EntityHandle h) => h.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeFromDefinition(md, (TypeDefinitionHandle)h, 0),
        HandleKind.TypeReference => GetTypeFromReference(md, (TypeReferenceHandle)h, 0),
        HandleKind.TypeSpecification => GetTypeFromSpecification(md, null, (TypeSpecificationHandle)h, 0),
        _ => "?"
    };
    public string GetArrayType(string e, ArrayShape s) => e + "[,]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a) => g + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
}
