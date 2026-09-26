using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
// apicompat <plugin.dll> <libdir>... : checks every member the plugin references in MelonLoader / 0Harmony / Mono.Cecil /
// Il2CppInterop.Runtime (by type, name, parameter count) against each lib dir, plus the members HotReload reaches by
// reflection. A library a dir does not ship (Il2CppInterop in net35) is skipped for that dir.
var plugin = args[0];
string[] libs = { "MelonLoader", "0Harmony", "Mono.Cecil", "Il2CppInterop.Runtime" };
var used = new List<(string lib, string type, string member, int pars)>();
using (var pe = new PEReader(File.OpenRead(plugin)))
{
    var md = pe.GetMetadataReader();
    foreach (var h in md.MemberReferences)
    {
        var mr = md.GetMemberReference(h);
        var (type, scope) = Parent(md, mr.Parent);
        if (!libs.Contains(scope)) continue;
        var br = md.GetBlobReader(mr.Signature);
        var hdr = br.ReadSignatureHeader();
        int pars = -1;
        if (hdr.Kind == SignatureKind.Method) { if (hdr.IsGeneric) br.ReadCompressedInteger(); pars = br.ReadCompressedInteger(); }
        used.Add((scope, type, md.GetString(mr.Name), pars));
    }
    foreach (var h in md.TypeReferences)
    {
        var (type, scope) = Name(md, h);
        if (libs.Contains(scope)) used.Add((scope, type, "", -2));
    }
}
var reflective = new[] {
    ("MelonLoader", "MelonLoader.MelonAssembly", "loadedAssemblies"),
    ("MelonLoader", "MelonLoader.Melons.MelonFolderHandler", "_modDirs"),
    ("MelonLoader", "MelonLoader.Preferences.MelonPreferences_ReflectiveCategory", "SystemType"),
    ("MelonLoader", "MelonLoader.MelonPreferences_ReflectiveCategory", "SystemType"),
    ("MelonLoader", "MelonLoader.MelonAssembly", "set_Location"),
    ("MelonLoader", "MelonLoader.MelonEventBase`1", "GetSubscribers"),
    ("MelonLoader", "MelonLoader.MelonEventBase`1", "Unsubscribe"),
    ("MelonLoader", "MelonLoader.MelonEventBase`1/MelonEventSubscriber", "del"),
    // Il2CppInterop, reflection only since the universal DLL (IL2CPP games; absent in net35 dirs)
    ("Il2CppInterop.Runtime", "Il2CppInterop.Runtime.Injection.ClassInjector", "InjectedTypes"),
    ("Il2CppInterop.Runtime", "Il2CppInterop.Runtime.Injection.InjectorHelpers", "s_ClassNameLookup"),
    ("Il2CppInterop.Runtime", "Il2CppInterop.Runtime.Il2CppClassPointerStore", "GetNativeClassPointer"),
    ("Il2CppInterop.Runtime", "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", "get_WasCollected"),
    ("Il2CppInterop.Runtime", "Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", "TryCast"),
    ("Il2CppInterop.Runtime", "Il2CppInterop.Runtime.Il2CppType", "From"),
};
foreach (var dir in args.Skip(1))
{
    var defs = new Dictionary<string, HashSet<string>>();
    var present = new HashSet<string>();
    foreach (var lib in libs)
    {
        var p = Path.Combine(dir, lib + ".dll");
        if (!File.Exists(p)) continue;
        present.Add(lib);
        using var pe = new PEReader(File.OpenRead(p));
        var md = pe.GetMetadataReader();
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            var key = lib + "|" + TypeName(md, th);
            var set = defs[key] = new HashSet<string> { "" };
            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var br = md.GetBlobReader(m.Signature); var hdr = br.ReadSignatureHeader(); if (hdr.IsGeneric) br.ReadCompressedInteger();
                set.Add(md.GetString(m.Name) + "/" + br.ReadCompressedInteger());
                set.Add(md.GetString(m.Name));
            }
            foreach (var fh in td.GetFields()) { set.Add(md.GetString(md.GetFieldDefinition(fh).Name) + "/-1"); set.Add(md.GetString(md.GetFieldDefinition(fh).Name)); }
        }
    }
    var missing = used.Distinct().Where(u => present.Contains(u.lib)).Where(u => !(defs.TryGetValue(u.lib + "|" + u.type, out var s) && s.Contains(u.pars == -2 ? "" : u.member + "/" + u.pars))).ToList();
    var missingRefl = reflective.Where(r => defs.Any(d => d.Key == r.Item1 + "|" + r.Item2) && !defs[r.Item1 + "|" + r.Item2].Contains(r.Item3)).Select(r => r.Item2 + "." + r.Item3).ToList();
    var reflTypesMissing = reflective.Where(r => present.Contains(r.Item1)).GroupBy(r => r.Item3).Where(g => !g.Any(r => defs.ContainsKey(r.Item1 + "|" + r.Item2) && defs[r.Item1 + "|" + r.Item2].Contains(r.Item3))).Select(g => g.Key).ToList();
    var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var label = Path.GetFileName(Path.GetDirectoryName(full)) + "/" + Path.GetFileName(full) + " [" + string.Join(" ", present) + "]";
    Console.WriteLine($"{label}: {used.Distinct().Count(u => present.Contains(u.lib))} refs, {missing.Count} missing" +
                      (missing.Count > 0 ? ": " + string.Join(", ", missing.Select(m => m.type + (m.member.Length > 0 ? "::" + m.member + "/" + m.pars : ""))) : "") +
                      $"; reflection targets missing: {(reflTypesMissing.Count == 0 ? "none" : string.Join(", ", reflTypesMissing))}");
}

static (string, string) Name(MetadataReader md, TypeReferenceHandle h)
{
    var tr = md.GetTypeReference(h);
    if (tr.ResolutionScope.Kind == HandleKind.TypeReference) { var (o, s) = Name(md, (TypeReferenceHandle)tr.ResolutionScope); return (o + "/" + md.GetString(tr.Name), s); }
    var ns = md.GetString(tr.Namespace);
    var sc = tr.ResolutionScope.Kind == HandleKind.AssemblyReference ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name) : "";
    return ((ns.Length > 0 ? ns + "." : "") + md.GetString(tr.Name), sc);
}
static string TypeName(MetadataReader md, TypeDefinitionHandle h)
{
    var td = md.GetTypeDefinition(h);
    if (!td.GetDeclaringType().IsNil) return TypeName(md, td.GetDeclaringType()) + "/" + md.GetString(td.Name);
    var ns = md.GetString(td.Namespace);
    return (ns.Length > 0 ? ns + "." : "") + md.GetString(td.Name);
}
static (string, string) Parent(MetadataReader md, EntityHandle h)
{
    if (h.Kind == HandleKind.TypeReference) return Name(md, (TypeReferenceHandle)h);
    if (h.Kind == HandleKind.TypeSpecification)
    {
        // generic instance: first element after GENERICINST + CLASS/VALUETYPE is the type def-or-ref
        var br = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
        if (br.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance) { br.ReadCompressedInteger(); var t = br.ReadTypeHandle(); if (t.Kind == HandleKind.TypeReference) return Name(md, (TypeReferenceHandle)t); }
    }
    return ("?", "");
}
