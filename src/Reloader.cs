using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using MelonLoader;
using MelonLoader.Utils;

namespace HotReload;

/// <summary>
/// Unregisters the melons of an assembly and loads a new build of it into a fresh AssemblyLoadContext.
/// Old assemblies are never unloaded (MelonLoader, Harmony and Il2CppInterop keep references); each reload costs the DLL's size in memory.
/// </summary>
internal sealed class Reloader
{
    public enum Result { Unchanged, Reloaded, Unloaded, Skipped, NotReady, Failed }

    /// <summary>Newest loaded Assembly per simple name, so a reloaded mod that references another reloaded mod binds to its newest build.</summary>
    internal static readonly Dictionary<string, Assembly> Latest = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

    private static readonly FieldInfo? LoadedAssembliesField =
        typeof(MelonAssembly).GetField("loadedAssemblies", BindingFlags.NonPublic | BindingFlags.Static);

    private readonly MelonLogger.Instance _log;
    private readonly string _selfName;
    private readonly Func<string, bool> _isIgnored;
    private readonly Func<bool> _replayScenes;
    private readonly Func<bool> _reloadDependents;
    private readonly Func<bool> _retireOldBuild;
    private readonly Func<bool> _destroyPersistent;
    private readonly Dictionary<string, string> _hash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // assembly name -> SHA256 of the loaded bytes
    private readonly Dictionary<string, string> _source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // assembly name -> file it was loaded from
    private readonly HashSet<string> _warnedOnce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private int _generation;

    public Reloader(MelonLogger.Instance log, string selfName, Func<string, bool> isIgnored, Func<bool> replayScenes, Func<bool> reloadDependents,
        Func<bool> retireOldBuild, Func<bool> destroyPersistent)
    {
        _retireOldBuild = retireOldBuild;
        _destroyPersistent = destroyPersistent;
        _log = log;
        _selfName = selfName;
        _isIgnored = isIgnored;
        _replayScenes = replayScenes;
        _reloadDependents = reloadDependents;
        if (LoadedAssembliesField == null)
            _log.Error("MelonAssembly.loadedAssemblies not found; this MelonLoader version is not supported. Reloads will fail.");
    }

    /// <summary>Remember what is loaded right now, so only later changes trigger a reload.</summary>
    public void Snapshot(IEnumerable<string> watchedFiles)
    {
        foreach (var ma in MelonAssembly.LoadedAssemblies)
        {
            var name = ma.Assembly.GetName().Name;
            if (name == null || string.IsNullOrEmpty(ma.Location) || !File.Exists(ma.Location)) continue;
            if (TryRead(ma.Location, out var bytes)) _hash[name] = Sha256(bytes);
            _source[name] = Path.GetFullPath(ma.Location);
        }
        // Watched DLLs that are not loaded (e.g. an extra bin/Release folder of a mod that is not in Mods yet) are new mods on their next change.
        int watched = watchedFiles.Count();
        _log.Msg("Tracking " + _hash.Count + " loaded melon assemblies, " + watched + " watched DLL(s).");
    }

    /// <summary>Mod DLLs in Mods/ that were deleted while the game runs (their mod gets unloaded).</summary>
    public IEnumerable<string> MissingSources() =>
        _source.Values.Where(p => IsInMods(p) && !File.Exists(p)).ToList();

    public Result ProcessFile(string path)
    {
        path = Path.GetFullPath(path);

        if (!File.Exists(path))
        {
            // Only a deletion from Mods/ unloads; cleaning a bin folder must not.
            var gone = _source.FirstOrDefault(kv => PathEquals(kv.Value, path)).Key;
            if (gone == null || !IsInMods(path) || gone == _selfName) return Result.Skipped;
            return Unload(gone) ? Result.Unloaded : Result.Skipped;
        }

        if (!TryRead(path, out var bytes)) return Result.NotReady;
        if (!TryInspect(bytes, out var name, out bool isMelon)) return Result.NotReady; // partially written or not .NET

        if (name == _selfName)
        {
            if (_warnedOnce.Add(name)) _log.Msg("HotReload itself changed; restart the game to use the new build.");
            return Result.Skipped;
        }
        if (_isIgnored(name)) return Result.Skipped;

        var hash = Sha256(bytes);
        if (_hash.TryGetValue(name, out var known) && known == hash) return Result.Unchanged;
        if (!isMelon)
        {
            _hash[name] = hash;
            if (_warnedOnce.Add(name)) _log.Msg(Path.GetFileName(path) + " has no [MelonInfo]; not a mod, ignored.");
            return Result.Skipped;
        }
        return Reload(name, path, bytes, hash, new HashSet<string>(StringComparer.OrdinalIgnoreCase)) ? Result.Reloaded : Result.Failed;
    }

    private bool Reload(string name, string path, byte[] bytes, string hash, HashSet<string> visited, string? becauseOf = null)
    {
        visited.Add(name);
        var sw = Stopwatch.StartNew();
        _hash[name] = hash; // even on failure: do not retry the same broken bytes on every event
        string? oldVersion = null;
        try
        {
            // 1. Unregister the old build: OnDeinitializeMelon, callbacks unsubscribed, melon Harmony patches removed.
            var oldLoaded = FindLoaded(name).ToList();
            var oldAssemblies = new HashSet<Assembly>(oldLoaded.Select(a => a.Assembly));
            var oldMelons = oldLoaded.SelectMany(a => a.LoadedMelons).ToList();
            int patchedBefore = CountMethodsPatchedFrom(oldAssemblies);
            foreach (var old in oldLoaded)
            {
                oldVersion ??= old.LoadedMelons.FirstOrDefault()?.Info.Version;
                old.UnregisterMelons("HotReload", silent: true);
                ForgetMelonAssembly(old);
            }
            // 1b. Patches the old build made through its own `new Harmony(...)` instances.
            RemoveRemainingPatches(name, oldAssemblies);
            // 1c. What the old build left running: objects kept across scenes, delegates, coroutines.
            RetireOldBuild(name, oldAssemblies, visited);
            // 2. Let the new build call CreateEntry / CreateCategory<T> again (values are kept in the preferences file).
            PrefOwnership.ReleaseCategories(name, oldAssemblies, oldMelons, _log);

            // 3. Load the new build from bytes into its own load context (no file lock; the default context refuses a second
            //    assembly with the same name).
            byte[]? pdb = null;
            var pdbPath = Path.ChangeExtension(path, ".pdb");
            if (File.Exists(pdbPath) && TryRead(pdbPath, out var pdbBytes)) pdb = pdbBytes;
            var ctx = new ModLoadContext(name + " #" + (++_generation));
            Assembly asm = ctx.LoadFromStream(new MemoryStream(bytes), pdb == null ? null : new MemoryStream(pdb));
            StartupLoader.RememberLocation(asm, path);
            Latest[name] = asm;

            // 4. Create and register its melons (OnEarlyInitializeMelon, Harmony auto-patch, OnInitializeMelon, OnLateInitializeMelon).
            var ma = MelonAssembly.LoadMelonAssembly(path, asm, loadMelons: true);
            if (ma == null) { _log.Error("Reload of " + name + " failed: MelonLoader could not load the assembly (see above)."); return false; }
            if (ma.RottenMelons.Count > 0) _log.Error(name + ": " + ma.RottenMelons.Count + " melon(s) failed to load (see above).");
            MelonBase.RegisterSorted(ma.LoadedMelons);
            _source[name] = path;

            // 5. The scenes that are already open.
            int scenes = 0;
            if (_replayScenes())
                foreach (var mod in ma.LoadedMelons.OfType<MelonMod>().Where(m => m.Registered))
                    scenes = UnityApi.ReplaySceneEvents(mod);

            int registered = ma.LoadedMelons.Count(m => m.Registered);
            var newVer = ma.LoadedMelons.FirstOrDefault()?.Info.Version ?? asm.GetName().Version?.ToString() ?? "?";
            var what = oldVersion == null ? "Loaded new mod " + name + " " + newVer : "Reloaded " + name + " " + oldVersion + " -> " + newVer;
            if (becauseOf != null) what += " (references " + becauseOf + ")";
            int patchedAfter = CountMethodsPatchedFrom(new HashSet<Assembly> { asm });
            what += " (Harmony: " + (oldVersion == null ? "" : patchedBefore + " method(s) unpatched, ") + patchedAfter + " patched"
                    + (scenes > 0 ? "; replayed " + scenes + " scene(s)" : "") + ")";
            if (registered == ma.LoadedMelons.Count && registered > 0)
                _log.Msg(what + " in " + sw.ElapsedMilliseconds + " ms (from " + ShortPath(path) + ")");
            else
                _log.Warning(what + ": only " + registered + " of " + ma.LoadedMelons.Count + " melon(s) registered (see above).");

            // 6. Mods that reference this one still call the old build: reload them too.
            if (registered > 0 && _reloadDependents()) ReloadDependents(name, visited);
            return registered > 0;
        }
        catch (Exception e)
        {
            _log.Error("Reload of " + name + " failed: " + e);
            return false;
        }
    }

    private void ReloadDependents(string name, HashSet<string> visited)
    {
        var dependents = MelonAssembly.LoadedAssemblies
            .Select(a => a.Assembly)
            .Where(a => a.GetReferencedAssemblies().Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.GetName().Name!)
            .Where(n => !visited.Contains(n) && n != _selfName && !_isIgnored(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var dep in dependents)
        {
            if (visited.Contains(dep)) continue; // reloaded as a dependent of an earlier dependent
            if (!_source.TryGetValue(dep, out var depPath) || !File.Exists(depPath) || !TryRead(depPath, out var depBytes))
            {
                _log.Warning(dep + " references " + name + " but its DLL cannot be read; it keeps calling the old " + name + ".");
                continue;
            }
            Reload(dep, depPath, depBytes, Sha256(depBytes), visited, becauseOf: name);
        }
    }

    private bool Unload(string name)
    {
        var loaded = FindLoaded(name).ToList();
        if (loaded.Count == 0) return false;
        var oldMelons = loaded.SelectMany(a => a.LoadedMelons).ToList();
        var oldAssemblies = new HashSet<Assembly>(loaded.Select(a => a.Assembly));
        foreach (var old in loaded)
        {
            old.UnregisterMelons("HotReload: DLL deleted", silent: true);
            ForgetMelonAssembly(old);
        }
        RemoveRemainingPatches(name, oldAssemblies);
        RetireOldBuild(name, oldAssemblies, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name });
        PrefOwnership.ReleaseCategories(name, oldAssemblies, oldMelons, _log);
        _hash.Remove(name);
        _source.Remove(name);
        Latest.Remove(name);
        _log.Msg("Unloaded " + name + " (its DLL was removed from Mods).");
        return true;
    }

    private void RetireOldBuild(string name, HashSet<Assembly> oldAssemblies, HashSet<string> visited)
    {
        var parts = new List<string>();
        if (_destroyPersistent())
        {
            int destroyed = UnityApi.DestroyPersistentObjects(name, _log);
            if (destroyed > 0) parts.Add("destroyed " + destroyed + " object(s) kept across scenes");
        }
        if (_retireOldBuild())
        {
            // A mod that references this one and is not reloaded with it would call into the retired build.
            var stuck = MelonAssembly.LoadedAssemblies
                .Where(a => !oldAssemblies.Contains(a.Assembly) && a.Assembly.GetReferencedAssemblies().Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.Assembly.GetName().Name!)
                .Where(n => !visited.Contains(n) && (!_reloadDependents() || _isIgnored(n) || n == _selfName))
                .ToList();
            if (stuck.Count > 0)
                parts.Add("old build NOT retired because " + string.Join(", ", stuck) + " still use it");
            else
                foreach (var asm in oldAssemblies)
                {
                    var (retired, failed, ms) = Retirer.Retire(asm, _log);
                    if (retired + failed > 0) parts.Add("retired " + retired + " old method(s)" + (failed > 0 ? " (" + failed + " failed)" : "") + " in " + ms + " ms");
                }
        }
        if (parts.Count > 0) _log.Msg(name + ": " + string.Join("; ", parts) + ".");
    }

    private static IEnumerable<MelonAssembly> FindLoaded(string name) =>
        MelonAssembly.LoadedAssemblies.Where(a => string.Equals(a.Assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>MelonAssembly.LoadMelonAssembly returns a cached entry with the same FullName, so the old one must go.</summary>
    private static void ForgetMelonAssembly(MelonAssembly ma)
    {
        if (LoadedAssembliesField?.GetValue(null) is List<MelonAssembly> list) list.Remove(ma);
    }

    private static IEnumerable<HarmonyLib.Patch> AllPatches(HarmonyLib.Patches info) =>
        info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers).Concat(info.ILManipulators);

    private static bool FromAny(HarmonyLib.Patch p, HashSet<Assembly> assemblies)
    {
        var asm = p.PatchMethod?.DeclaringType?.Assembly;
        return asm != null && assemblies.Contains(asm);
    }

    /// <summary>Number of methods carrying at least one patch whose patch method lives in one of these assemblies.</summary>
    private static int CountMethodsPatchedFrom(HashSet<Assembly> assemblies)
    {
        try
        {
            return HarmonyLib.Harmony.GetAllPatchedMethods()
                .Count(m => HarmonyLib.Harmony.GetPatchInfo(m) is { } info && AllPatches(info).Any(p => FromAny(p, assemblies)));
        }
        catch { return 0; }
    }

    /// <summary>
    /// Unregistering only unpatches the melon's own HarmonyInstance. Anything the old build patched through another
    /// Harmony instance is found by the assembly of the patch method and removed here.
    /// </summary>
    private void RemoveRemainingPatches(string name, HashSet<Assembly> oldAssemblies)
    {
        int removed = 0, failed = 0;
        foreach (var original in HarmonyLib.Harmony.GetAllPatchedMethods().ToList())
        {
            var info = HarmonyLib.Harmony.GetPatchInfo(original);
            if (info == null) continue;
            foreach (var p in AllPatches(info).Where(p => FromAny(p, oldAssemblies)).ToList())
            {
                try { new HarmonyLib.Harmony(p.owner).Unpatch(original, p.PatchMethod); removed++; }
                catch (Exception e) { failed++; _log.Warning(name + ": could not unpatch " + original.DeclaringType?.Name + "." + original.Name + ": " + e.Message); }
            }
        }
        if (removed > 0) _log.Msg(name + ": removed " + removed + " patch(es) made outside the melon's HarmonyInstance.");
        if (failed > 0) _log.Warning(name + ": " + failed + " patch(es) of the old build are still active.");
    }

    private static bool TryRead(string path, out byte[] bytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[fs.Length];
            int off = 0;
            while (off < bytes.Length)
            {
                int n = fs.Read(bytes, off, bytes.Length - off);
                if (n <= 0) break;
                off += n;
            }
            if (off != bytes.Length) { bytes = Array.Empty<byte>(); return false; }
            return bytes.Length > 0;
        }
        catch (IOException) { bytes = Array.Empty<byte>(); return false; }
        catch (UnauthorizedAccessException) { bytes = Array.Empty<byte>(); return false; }
    }

    /// <summary>Reads the assembly name and checks for [MelonInfo] without loading anything. False for partial/non-.NET files.</summary>
    private static bool TryInspect(byte[] bytes, out string name, out bool isMelon)
    {
        name = ""; isMelon = false;
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes), PEStreamOptions.PrefetchEntireImage);
            if (!pe.HasMetadata) return false;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return false;
            var def = md.GetAssemblyDefinition();
            name = md.GetString(def.Name);
            foreach (var h in def.GetCustomAttributes())
            {
                if (AttributeTypeName(md, md.GetCustomAttribute(h)) == "MelonInfoAttribute") { isMelon = true; break; }
            }
            return name.Length > 0;
        }
        catch (BadImageFormatException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attr)
    {
        if (attr.Constructor.Kind == HandleKind.MemberReference)
        {
            var parent = md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent;
            if (parent.Kind == HandleKind.TypeReference) return md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name);
        }
        else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
        {
            var type = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType();
            return md.GetString(md.GetTypeDefinition(type).Name);
        }
        return null;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static bool IsInMods(string path) =>
        PathEquals(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "", Path.GetFullPath(MelonEnvironment.ModsDirectory));

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string ShortPath(string path)
    {
        var root = MelonEnvironment.GameRootDirectory;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length).TrimStart('\\', '/') : path;
    }
}

/// <summary>One per reload. Dependencies resolve to the newest reloaded build of a mod, else to whatever the default context has.</summary>
internal sealed class ModLoadContext : AssemblyLoadContext
{
    public ModLoadContext(string name) : base("HotReload: " + name, isCollectible: false) { }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var n = assemblyName.Name;
        if (n == null) return null;
        if (Reloader.Latest.TryGetValue(n, out var reloaded)) return reloaded;
        foreach (var a in Default.Assemblies)
            if (string.Equals(a.GetName().Name, n, StringComparison.OrdinalIgnoreCase)) return a;
        try { return Default.LoadFromAssemblyName(assemblyName); } // runs MelonLoader's resolvers (Il2Cpp interop, UserLibs)
        catch { return null; }
    }
}
