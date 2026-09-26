using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HotReload.Tests")]

namespace HotReloadCheck;

/// <summary>
/// Scans compiled MelonLoader mods (no source needed) and reports whether they can be hot-reloaded by HotReload (the version this tool shipped with),
/// and what the author would have to add. It reads metadata only: which APIs a mod references, which types it
/// defines and which callbacks it overrides. It does not execute or load anything.
///
/// Usage: HotReloadCheck &lt;dll-or-folder&gt;... [--md report.md] [--csv report.csv] [--libs]
/// </summary>
internal static class Program
{
    /// <summary>The HotReload version these rules describe (the checker is versioned with HotReload).</summary>
    public static readonly string HotReloadVersion = typeof(Program).Assembly.GetName().Version is { } v ? v.Major + "." + v.Minor + "." + v.Build : "?";

    private static int Main(string[] args)
    {
        var inputs = new List<string>();
        string? md = null, csv = null;
        bool libs = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--md": md = args[++i]; break;
                case "--csv": csv = args[++i]; break;
                case "--libs": libs = true; break;
                case "-h" or "--help": PrintUsage(); return 0;
                default: inputs.Add(args[i]); break;
            }
        }
        if (inputs.Count == 0) { PrintUsage(); return 1; }

        var files = inputs.SelectMany(p => Directory.Exists(p)
                ? Directory.EnumerateFiles(p, "*.dll", SearchOption.AllDirectories)
                : File.Exists(p) ? new[] { p } : Array.Empty<string>())
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            // build intermediates of a project folder: obj/ holds copies and reference assemblies
            .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("obj", StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        var reports = new List<Report>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            ModScan? scan;
            try
            {
                if (!seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))))) continue; // same file twice
                scan = ModScan.Read(f);
            }
            catch (Exception e) { Console.Error.WriteLine("skip " + f + ": " + e.Message); continue; }
            if (scan == null) continue;                        // native DLL, not an assembly, or a reference assembly
            if (scan.Kind == MelonKind.Library && !libs) continue;
            reports.Add(Rules.Evaluate(scan));
        }

        var text = Output.Markdown(reports);
        Console.WriteLine(text);
        if (md != null) File.WriteAllText(md, text);
        if (csv != null) File.WriteAllText(csv, Output.Csv(reports));
        return 0;
    }

    private static void PrintUsage() => Console.WriteLine(
        "HotReloadCheck <dll-or-folder>... [--md report.md] [--csv report.csv] [--libs]\n" +
        "Scans compiled MelonLoader mods and reports whether HotReload can reload them. Folders are searched recursively.\n" +
        "--libs also lists DLLs without [MelonInfo] (libraries).");
}

internal enum MelonKind { Mod, Plugin, UnknownMelon, Library }
internal enum Flavor { Il2CppInterop, Unhollower, Mono, Unknown }
internal enum Severity { Info, Cleanup, Blocker }

internal sealed record MemberRef(string Type, string Member, string Scope, bool GenericMethod)
{
    public string Display => Type + "::" + Member + (GenericMethod ? "<T>" : "");
}

internal sealed record TypeDef(string FullName, string? BaseType, string? BaseScope);

/// <summary>Everything the rules need, read from the assembly's metadata tables.</summary>
internal sealed class ModScan
{
    public string Path = "", AssemblyName = "", AssemblyVersion = "", TargetFramework = "";
    public string MelonName = "", MelonVersion = "", MelonAuthor = "", MelonType = "";
    public MelonKind Kind = MelonKind.Library;
    public Flavor Flavor = Flavor.Unknown;
    public List<string> Games = new();
    public HashSet<string> AssemblyRefs = new(StringComparer.OrdinalIgnoreCase);
    public List<MemberRef> MemberRefs = new();
    public HashSet<string> AttributeTypes = new(StringComparer.Ordinal);
    public List<TypeDef> TypeDefs = new();
    public HashSet<string> DefinedMethods = new(StringComparer.Ordinal); // method names defined anywhere in the assembly
    public bool LooksObfuscated;

    public static ModScan? Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) return null;
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly) return null;

        var s = new ModScan { Path = path };
        var def = md.GetAssemblyDefinition();
        s.AssemblyName = md.GetString(def.Name);
        s.AssemblyVersion = def.Version.ToString();

        foreach (var h in md.AssemblyReferences) s.AssemblyRefs.Add(md.GetString(md.GetAssemblyReference(h).Name));

        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            var (type, scope) = Names.Parent(md, mr.Parent);
            bool generic = mr.GetKind() == MemberReferenceKind.Method && md.GetBlobReader(mr.Signature).ReadSignatureHeader().IsGeneric;
            s.MemberRefs.Add(new MemberRef(type, md.GetString(mr.Name), scope, generic));
        }

        foreach (var h in md.CustomAttributes)
        {
            var ca = md.GetCustomAttribute(h);
            var attrType = Names.AttributeType(md, ca);
            if (attrType != null) s.AttributeTypes.Add(attrType);
        }
        if (s.AttributeTypes.Contains("System.Runtime.CompilerServices.ReferenceAssemblyAttribute")) return null; // metadata-only stub

        int weird = 0;
        var typeBases = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);
            var name = Names.Of(md, h);
            if (name == "<Module>") continue;
            string? baseName = null, baseScope = null;
            if (!td.BaseType.IsNil) (baseName, baseScope) = Names.Parent(md, td.BaseType);
            s.TypeDefs.Add(new TypeDef(name, baseName, baseScope));
            typeBases[name] = baseName;
            if (md.GetString(td.Name).Any(c => c > 0x7e || char.IsControl(c))) weird++;
            foreach (var mh in td.GetMethods()) s.DefinedMethods.Add(md.GetString(md.GetMethodDefinition(mh).Name));
        }
        s.LooksObfuscated = s.TypeDefs.Count > 5 && weird * 3 > s.TypeDefs.Count;

        // Assembly-level attributes: MelonInfo, MelonGame, TargetFramework.
        foreach (var h in def.GetCustomAttributes())
        {
            var ca = md.GetCustomAttribute(h);
            var t = Names.AttributeType(md, ca);
            if (t == null) continue;
            ImmutableArray<CustomAttributeTypedArgument<string>> a;
            try { a = ca.DecodeValue(new AttrTypeProvider()).FixedArguments; }
            catch { continue; }
            string Arg(int i) => i < a.Length ? a[i].Value?.ToString() ?? "" : "";
            switch (t)
            {
                case "MelonLoader.MelonInfoAttribute":
                    s.MelonType = Arg(0); s.MelonName = Arg(1); s.MelonVersion = Arg(2); s.MelonAuthor = Arg(3);
                    break;
                case "MelonLoader.MelonGameAttribute":
                    var g = (Arg(0) + " / " + Arg(1)).Trim(' ', '/');
                    s.Games.Add(g.Length > 0 ? g : "any game");
                    break;
                case "System.Runtime.Versioning.TargetFrameworkAttribute":
                    s.TargetFramework = Arg(0);
                    break;
            }
        }

        // Kind: walk the melon type's base chain inside this assembly.
        if (s.MelonType.Length > 0)
        {
            s.Kind = MelonKind.UnknownMelon;
            var cur = s.MelonType.Split(',')[0].Trim().Replace('+', '/');
            for (int guard = 0; guard < 20 && cur != null; guard++)
            {
                if (cur == "MelonLoader.MelonMod") { s.Kind = MelonKind.Mod; break; }
                if (cur == "MelonLoader.MelonPlugin") { s.Kind = MelonKind.Plugin; break; }
                cur = typeBases.TryGetValue(cur, out var b) ? b : null;
            }
        }
        else if (s.AttributeTypes.Contains("MelonLoader.MelonInfoAttribute")) s.Kind = MelonKind.UnknownMelon;

        // MelonLoader 0.6+ uses .NET 6 only for IL2CPP games; Mono games run net35/net472/netstandard mods.
        bool unhollower = s.AssemblyRefs.Contains("UnhollowerBaseLib") || s.AssemblyRefs.Contains("UnhollowerRuntimeLib");
        bool il2cppRefs = s.AssemblyRefs.Contains("Il2CppInterop.Runtime") || s.AssemblyRefs.Any(r => r.StartsWith("Il2Cpp", StringComparison.OrdinalIgnoreCase));
        bool netCore = s.TargetFramework.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase) || s.AssemblyRefs.Contains("System.Runtime");
        s.Flavor = unhollower ? Flavor.Unhollower
            : il2cppRefs || netCore ? Flavor.Il2CppInterop
            : s.AssemblyRefs.Contains("mscorlib") || s.AssemblyRefs.Contains("netstandard") ? Flavor.Mono
            : Flavor.Unknown;
        return s;
    }
}

internal static class Names
{
    public static string Of(MetadataReader md, TypeDefinitionHandle h)
    {
        var td = md.GetTypeDefinition(h);
        var name = md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return Of(md, decl) + "/" + name;
        var ns = md.GetString(td.Namespace);
        return ns.Length > 0 ? ns + "." + name : name;
    }

    /// <summary>Full type name and the assembly it comes from ("" = this assembly).</summary>
    public static (string type, string scope) Of(MetadataReader md, TypeReferenceHandle h)
    {
        var tr = md.GetTypeReference(h);
        var name = md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var (outer, scope) = Of(md, (TypeReferenceHandle)tr.ResolutionScope);
            return (outer + "/" + name, scope);
        }
        var ns = md.GetString(tr.Namespace);
        var full = ns.Length > 0 ? ns + "." + name : name;
        var sc = tr.ResolutionScope.Kind == HandleKind.AssemblyReference
            ? md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name)
            : "";
        return (full, sc);
    }

    public static (string type, string scope) Parent(MetadataReader md, EntityHandle h)
    {
        switch (h.Kind)
        {
            case HandleKind.TypeReference: return Of(md, (TypeReferenceHandle)h);
            case HandleKind.TypeDefinition: return (Of(md, (TypeDefinitionHandle)h), "");
            case HandleKind.TypeSpecification:
                try
                {
                    var ts = md.GetTypeSpecification((TypeSpecificationHandle)h);
                    var decoded = ts.DecodeSignature(new SigTypeProvider(md), null);
                    return (decoded.type, decoded.scope);
                }
                catch { return ("?", ""); }
            case HandleKind.MethodDefinition:
                return (Of(md, md.GetMethodDefinition((MethodDefinitionHandle)h).GetDeclaringType()), "");
            default: return ("?", "");
        }
    }

    public static string? AttributeType(MetadataReader md, CustomAttribute ca)
    {
        if (ca.Constructor.Kind == HandleKind.MemberReference)
            return Parent(md, md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent).type;
        if (ca.Constructor.Kind == HandleKind.MethodDefinition)
            return Of(md, md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType());
        return null;
    }
}

/// <summary>Decodes type specs just far enough to name the generic type definition (UnityEvent`1, Il2CppSystem.Action`1, ...).</summary>
internal sealed class SigTypeProvider : ISignatureTypeProvider<(string type, string scope), object?>
{
    private readonly MetadataReader _md;
    public SigTypeProvider(MetadataReader md) => _md = md;
    private static (string, string) N(string s) => (s, "");
    public (string type, string scope) GetArrayType((string type, string scope) e, ArrayShape shape) => (e.type + "[]", e.scope);
    public (string type, string scope) GetByReferenceType((string type, string scope) e) => e;
    public (string type, string scope) GetFunctionPointerType(MethodSignature<(string type, string scope)> s) => N("fnptr");
    public (string type, string scope) GetGenericInstantiation((string type, string scope) g, ImmutableArray<(string type, string scope)> a) => g;
    public (string type, string scope) GetGenericMethodParameter(object? c, int i) => N("!!" + i);
    public (string type, string scope) GetGenericTypeParameter(object? c, int i) => N("!" + i);
    public (string type, string scope) GetModifiedType((string type, string scope) m, (string type, string scope) u, bool r) => u;
    public (string type, string scope) GetPinnedType((string type, string scope) e) => e;
    public (string type, string scope) GetPointerType((string type, string scope) e) => (e.type + "*", e.scope);
    public (string type, string scope) GetPrimitiveType(PrimitiveTypeCode c) => N(c.ToString());
    public (string type, string scope) GetSZArrayType((string type, string scope) e) => (e.type + "[]", e.scope);
    public (string type, string scope) GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => (Names.Of(r, h), "");
    public (string type, string scope) GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Names.Of(r, h);
    public (string type, string scope) GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) =>
        r.GetTypeSpecification(h).DecodeSignature(this, c);
}

internal sealed class AttrTypeProvider : ICustomAttributeTypeProvider<string>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSystemType() => "System.Type";
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Names.Of(r, h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Names.Of(r, h).type;
    public string GetTypeFromSerializedName(string name) => name;
    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    public bool IsSystemType(string type) => type == "System.Type";
}

internal sealed record Finding(string Id, Severity Severity, string Title, string Advice, List<string> Evidence);

internal sealed record Report(ModScan Scan, string Verdict, List<Finding> Findings, List<string> Dependencies)
{
    public int Rank => Verdict switch
    {
        "READY" => 0, "REVIEW" => 1, "NEEDS CLEANUP" => 2, "PLUGIN" => 3, "BLOCKED" => 4, _ => 5,
    };
}

internal static class Rules
{
    // Assemblies that are the runtime, the loader, the game or Unity, not other mods.
    private static readonly string[] KnownPrefixes =
    {
        "System", "Microsoft", "mscorlib", "netstandard", "MelonLoader", "0Harmony", "Harmony", "Il2Cpp", "Unity", "UnityEngine",
        "Assembly-CSharp", "Mono.", "MonoMod", "Newtonsoft", "Tomlet", "Iced", "AsmResolver", "Unhollower", "Semver", "Cpp2IL", "LibCpp2IL",
    };

    public static Report Evaluate(ModScan s)
    {
        var findings = new List<Finding>();
        void Add(string id, Severity sev, string title, string advice, IEnumerable<string> evidence)
        {
            var ev = evidence.Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (ev.Count > 0) findings.Add(new Finding(id, sev, title, advice, ev));
        }
        IEnumerable<string> Refs(Func<MemberRef, bool> pred) => s.MemberRefs.Where(pred).Select(m => m.Display);
        bool Il2CppScope(string scope) => scope.StartsWith("Il2Cpp", StringComparison.OrdinalIgnoreCase) || scope.StartsWith("Unity", StringComparison.OrdinalIgnoreCase);

        // ---- Blockers ------------------------------------------------------------------------------------------
        var injected = Refs(m => m.Type.EndsWith("ClassInjector") && m.Member.StartsWith("RegisterTypeInIl2Cpp"))
            .Concat(s.AttributeTypes.Where(a => a is "MelonLoader.RegisterTypeInIl2Cpp" or "MelonLoader.RegisterTypeInIl2CppWithInterfaces").Select(a => "[" + a + "]"));
        if (s.Flavor == Flavor.Il2CppInterop)
            injected = injected.Concat(s.TypeDefs
                .Where(t => t.BaseType != null && t.BaseScope != null && Il2CppScope(t.BaseScope) && !IsManagedOnlyBase(t.BaseType))
                .Select(t => t.FullName + " : " + t.BaseType));
        Add("injection", Severity.Info, "Il2Cpp class injection",
            "Handled: HotReload destroys live instances of the old build's injected classes, retires their methods and releases " +
            "their names in Il2CppInterop, so the new build can inject classes with the same names. Code that finds these " +
            "classes by name gets the new ones.",
            injected);

        // ---- Needs cleanup in OnDeinitializeMelon --------------------------------------------------------------
        Add("assetbundles", Severity.Cleanup, "Loads AssetBundles",
            "Loading the same bundle again fails while the old one is loaded. Call bundle.Unload(true/false) in OnDeinitializeMelon.",
            Refs(m => m.Type == "UnityEngine.AssetBundle" && m.Member.StartsWith("LoadFrom")));
        Add("nativehooks", Severity.Cleanup, "Native / MonoMod hooks outside Harmony",
            "HotReload removes only Harmony patches, and it retires the old build's delegate targets, so a hooked function would " +
            "return defaults instead of running. Dispose these hooks in OnDeinitializeMelon (or set RetireOldBuild = false).",
            Refs(m => (m.Type.StartsWith("MonoMod.RuntimeDetour.") && m.Member == ".ctor") ||
                      (m.Type == "MelonLoader.MelonUtils" && m.Member.StartsWith("NativeHook")) ||
                      (m.Type.StartsWith("MelonLoader.NativeUtils.NativeHook") && m.Member is ".ctor" or "Attach")));
        Add("threads", Severity.Cleanup, "Starts its own threads",
            "A loop already running on a thread keeps running after a reload; retiring only stops later calls. Signal it to stop in OnDeinitializeMelon.",
            Refs(m => m.Type == "System.Threading.Thread" && m.Member == ".ctor"));

        // ---- Handled by HotReload (informational) -------------------------------------------------------------
        Add("coroutines", Severity.Info, "Starts MelonCoroutines",
            "Handled: the old build's coroutine steps are retired, so its coroutines end on their next step.",
            Refs(m => m.Type == "MelonLoader.MelonCoroutines" && m.Member == "Start"));
        Add("persistent", Severity.Info, "Keeps objects across scenes (DontDestroyOnLoad)",
            "Handled: HotReload destroys the objects the old build passed to DontDestroyOnLoad.",
            Refs(m => m.Type == "UnityEngine.Object" && m.Member == "DontDestroyOnLoad"));
        Add("objects", Severity.Info, "Creates GameObjects / components",
            "Handled for the common case: objects in normal scenes go away with the scene. Components added to objects the game keeps stay until then.",
            Refs(m => (m.Type == "UnityEngine.GameObject" && (m.Member == ".ctor" || m.Member == "AddComponent")) ||
                      (m.Type == "UnityEngine.Object" && m.Member == "Instantiate")));
        Add("callbacks", Severity.Info, "Hands callbacks to the game",
            "Handled: the old build's delegate targets are retired, so leftover listeners and settings rows do nothing until the game rebuilds that UI.",
            Refs(m => (m.Member == "AddListener" && m.Type.StartsWith("UnityEngine.Events.UnityEvent")) ||
                      (m.Type.EndsWith("DelegateSupport") && m.Member == "ConvertDelegate") ||
                      (m.Member == "op_Implicit" && m.Type.StartsWith("Il2CppSystem.") && (m.Type.Contains("Action") || m.Type.Contains("Func") || m.Type.Contains("Predicate") || m.Type.Contains("Comparison"))) ||
                      (m.Member.StartsWith("add_") && Il2CppScope(m.Scope))));
        Add("timers", Severity.Info, "Timers, tasks, thread-pool work or file watchers",
            "Handled: their callbacks and async steps in the old build are retired.",
            Refs(m => (m.Type is "System.Threading.Timer" or "System.Timers.Timer" or "System.IO.FileSystemWatcher" && m.Member == ".ctor") ||
                      (m.Type == "System.Threading.Tasks.Task" && m.Member == "Run") ||
                      (m.Type == "System.Threading.Tasks.TaskFactory" && m.Member == "StartNew") ||
                      (m.Type == "System.Threading.ThreadPool" && m.Member.StartsWith("QueueUserWorkItem"))));
        Add("staticevents", Severity.Info, "Subscribes to process-wide .NET events",
            "Handled: the old build's handlers are retired (they stay subscribed but do nothing).",
            Refs(m => m.Member.StartsWith("add_") && m.Type is "System.AppDomain" or "System.Console"));

        Add("harmony", Severity.Info, "Own Harmony instance",
            "Handled: HotReload removes patches by the assembly of the patch method, not only the melon's HarmonyInstance.",
            Refs(m => m.Type == "HarmonyLib.Harmony" && (m.Member == ".ctor" || m.Member == "CreateAndPatchAll")));
        Add("reflectiveprefs", Severity.Info, "Reflective preference category (CreateCategory<T>)",
            "Handled: HotReload saves and drops the old category before the reload.",
            Refs(m => m.Type == "MelonLoader.MelonPreferences" && m.Member == "CreateCategory" && m.GenericMethod));
        Add("location", Severity.Info, "Reads Assembly.Location",
            "Handled: HotReload makes Assembly.Location return the DLL path for mods loaded from memory.",
            Refs(m => m.Type == "System.Reflection.Assembly" && m.Member == "get_Location"));
        Add("scenes", Severity.Info, "Scene callbacks",
            "Handled: HotReload replays OnSceneWasLoaded/OnSceneWasInitialized for scenes already open after a reload.",
            new[] { "OnSceneWasLoaded", "OnSceneWasInitialized" }.Where(s.DefinedMethods.Contains));
        if (s.LooksObfuscated)
            findings.Add(new Finding("obfuscated", Severity.Info, "Looks obfuscated", "Findings may be incomplete.", new List<string> { "many unreadable type names" }));

        var deps = s.AssemblyRefs.Where(r => !KnownPrefixes.Any(p => r.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();

        bool hasDeinit = s.DefinedMethods.Contains("OnDeinitializeMelon");
        string verdict =
            s.Kind == MelonKind.Library ? "LIBRARY" :
            s.Flavor == Flavor.Unhollower ? "UNSUPPORTED (MelonLoader 0.5 era)" :
            findings.Any(f => f.Severity == Severity.Blocker) ? "BLOCKED" :
            !findings.Any(f => f.Severity == Severity.Cleanup) ? "READY" :
            hasDeinit ? "REVIEW" : "NEEDS CLEANUP";
        return new Report(s, verdict, findings, deps);
    }

    /// <summary>Il2Cpp-mod types that derive from interop types but are not injected (attributes, exceptions are managed-only).</summary>
    private static bool IsManagedOnlyBase(string baseType) =>
        baseType.EndsWith("Attribute") || baseType.EndsWith("Exception") || baseType.StartsWith("System.");
}

internal static class Output
{
    private static readonly Dictionary<string, string> Legend = new()
    {
        ["READY"] = "nothing found that HotReload cannot clean up; should hot-reload as is",
        ["REVIEW"] = "uses something HotReload cannot clean up, but has OnDeinitializeMelon; check it undoes it",
        ["NEEDS CLEANUP"] = "uses something HotReload cannot clean up and has no OnDeinitializeMelon; author must add cleanup",

        ["BLOCKED"] = "uses something HotReload cannot reload",
        ["UNSUPPORTED (MelonLoader 0.5 era)"] = "built against Unhollower; does not load on MelonLoader 0.6+ anyway",
        ["LIBRARY"] = "no [MelonInfo]; not a mod",
    };

    public static string Markdown(List<Report> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# HotReload compatibility report");
        sb.AppendLine();
        sb.AppendLine("Static scan of compiled DLLs (metadata only), against HotReload " + Program.HotReloadVersion + ". \"Cleanup\" findings are things HotReload cannot undo itself; " +
                      "whether the mod's OnDeinitializeMelon undoes them needs a look at the code or a test.");
        sb.AppendLine();
        sb.AppendLine("| Mod | Version | Author | Kind | Verdict | Cleanup items | Handled | Depends on |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in reports.OrderBy(r => r.Rank).ThenBy(r => r.Scan.MelonName, StringComparer.OrdinalIgnoreCase))
        {
            var s = r.Scan;
            string Ids(Severity sev) => string.Join(", ", r.Findings.Where(f => f.Severity == sev).Select(f => f.Id));
            sb.AppendLine($"| {Esc(s.MelonName.Length > 0 ? s.MelonName : s.AssemblyName)} | {Esc(s.MelonVersion)} | {Esc(s.MelonAuthor)} | {s.Kind} | **{r.Verdict}** | " +
                          $"{Ids(Severity.Blocker)}{(Ids(Severity.Blocker).Length > 0 && Ids(Severity.Cleanup).Length > 0 ? ", " : "")}{Ids(Severity.Cleanup)} | {Ids(Severity.Info)} | {Esc(string.Join(", ", r.Dependencies))} |");
        }
        sb.AppendLine();
        sb.AppendLine("Verdicts:");
        foreach (var v in reports.Select(r => r.Verdict).Distinct().OrderBy(v => v))
            sb.AppendLine($"- **{v}**: {Legend.GetValueOrDefault(v, "")}");
        sb.AppendLine();

        foreach (var r in reports.OrderBy(r => r.Rank).ThenBy(r => r.Scan.MelonName, StringComparer.OrdinalIgnoreCase))
        {
            var s = r.Scan;
            sb.AppendLine($"## {Esc(s.MelonName.Length > 0 ? s.MelonName : s.AssemblyName)} {s.MelonVersion}: {r.Verdict}");
            sb.AppendLine();
            sb.AppendLine($"`{Path.GetFileName(s.Path)}`, {s.Kind} | {s.Flavor} | {(s.TargetFramework.Length > 0 ? s.TargetFramework : "framework unknown")}" +
                          (s.Games.Count > 0 ? " | game: " + string.Join("; ", s.Games) : " | any game") +
                          (s.DefinedMethods.Contains("OnDeinitializeMelon") ? " | has OnDeinitializeMelon" : " | no OnDeinitializeMelon"));
            sb.AppendLine();
            if (r.Findings.Count == 0) sb.AppendLine("No findings.");
            foreach (var f in r.Findings.OrderByDescending(f => f.Severity))
            {
                var tag = f.Severity switch { Severity.Blocker => "BLOCKER", Severity.Cleanup => "cleanup", _ => "handled" };
                var ev = string.Join(", ", f.Evidence.Take(6).Select(e => "`" + e + "`")) + (f.Evidence.Count > 6 ? $" (+{f.Evidence.Count - 6} more)" : "");
                sb.AppendLine($"- **{tag}: {f.Title}.** {f.Advice} Evidence: {ev}");
            }
            if (r.Dependencies.Count > 0)
                sb.AppendLine($"- **depends on:** {string.Join(", ", r.Dependencies)}. HotReload reloads this mod when one of these reloads, and reloads the stateful UserLibs libraries among them together with this mod.");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string Csv(List<Report> reports)
    {
        var sb = new StringBuilder("file,assembly,melon,version,author,kind,flavor,verdict,has_deinit,blockers,cleanup,handled,depends_on\n");
        foreach (var r in reports)
        {
            var s = r.Scan;
            string Ids(Severity sev) => string.Join(" ", r.Findings.Where(f => f.Severity == sev).Select(f => f.Id));
            sb.AppendLine(string.Join(",", new[]
            {
                Path.GetFileName(s.Path), s.AssemblyName, s.MelonName, s.MelonVersion, s.MelonAuthor, s.Kind.ToString(), s.Flavor.ToString(), r.Verdict,
                s.DefinedMethods.Contains("OnDeinitializeMelon") ? "yes" : "no", Ids(Severity.Blocker), Ids(Severity.Cleanup), Ids(Severity.Info), string.Join(" ", r.Dependencies),
            }.Select(CsvCell)));
        }
        return sb.ToString();
    }

    private static string Esc(string s) => s.Replace("|", "\\|");
    private static string CsvCell(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
