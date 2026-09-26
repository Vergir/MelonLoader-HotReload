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
/// Old assemblies are never unloaded (MelonLoader and Harmony keep references); each reload costs the DLL's size in memory.
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
    private readonly Dictionary<string, string> _hash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // assembly name -> SHA256 of the loaded bytes
    private readonly Dictionary<string, string> _source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // assembly name -> file it was loaded from
    private readonly HashSet<string> _warnedOnce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private int _generation;

    public Reloader(MelonLogger.Instance log, string selfName)
    {
        _log = log;
        _selfName = selfName;
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

    public Result ProcessFile(string path, Func<string, bool> isIgnored, out string? assemblyName)
    {
        assemblyName = null;
        path = Path.GetFullPath(path);

        if (!File.Exists(path))
        {
            // Only a deletion from Mods/ unloads; cleaning a bin folder must not.
            assemblyName = _source.FirstOrDefault(kv => PathEquals(kv.Value, path)).Key;
            if (assemblyName == null || !IsInMods(path) || assemblyName == _selfName) return Result.Skipped;
            return Unload(assemblyName) ? Result.Unloaded : Result.Skipped;
        }

        if (!TryRead(path, out var bytes)) return Result.NotReady;
        if (!TryInspect(bytes, out var name, out var version, out bool isMelon)) return Result.NotReady; // partially written or not .NET
        assemblyName = name;

        if (name == _selfName)
        {
            if (_warnedOnce.Add(name)) _log.Msg("HotReload itself changed; restart the game to use the new build.");
            return Result.Skipped;
        }
        if (isIgnored(name)) return Result.Skipped;

        var hash = Sha256(bytes);
        if (_hash.TryGetValue(name, out var known) && known == hash) return Result.Unchanged;
        if (!isMelon)
        {
            _hash[name] = hash;
            if (_warnedOnce.Add(name)) _log.Msg(Path.GetFileName(path) + " has no [MelonInfo]; not a mod, ignored.");
            return Result.Skipped;
        }
        return Reload(name, version, path, bytes, hash) ? Result.Reloaded : Result.Failed;
    }

    private bool Reload(string name, Version newVersion, string path, byte[] bytes, string hash)
    {
        var sw = Stopwatch.StartNew();
        _hash[name] = hash; // even on failure: do not retry the same broken bytes on every event
        string? oldVersion = null;
        try
        {
            // 1. Unregister the old build: OnDeinitializeMelon, callbacks unsubscribed, Harmony patches removed.
            var melonNames = new List<string>();
            var oldHarmonyIds = FindLoaded(name).SelectMany(a => a.LoadedMelons).Select(m => m.HarmonyInstance?.Id).OfType<string>().ToList();
            int patchedBefore = CountPatchedBy(oldHarmonyIds);
            foreach (var old in FindLoaded(name))
            {
                oldVersion ??= old.LoadedMelons.FirstOrDefault()?.Info.Version;
                melonNames.AddRange(old.LoadedMelons.Select(m => m.Info.Name));
                old.UnregisterMelons("HotReload", silent: true);
                ForgetMelonAssembly(old);
            }
            // 2. Let the new build call CreateEntry again (values are kept in the preferences file).
            WarnLeftoverPatches(name, oldHarmonyIds);
            PrefOwnership.ReleaseCategories(name, melonNames, _log);

            // 3. Load the new build from bytes into its own load context (no file lock; the default context refuses a second
            //    assembly with the same name).
            byte[]? pdb = null;
            var pdbPath = Path.ChangeExtension(path, ".pdb");
            if (File.Exists(pdbPath) && TryRead(pdbPath, out var pdbBytes)) pdb = pdbBytes;
            var ctx = new ModLoadContext(name + " #" + (++_generation));
            Assembly asm = ctx.LoadFromStream(new MemoryStream(bytes), pdb == null ? null : new MemoryStream(pdb));
            Latest[name] = asm;

            // 4. Create and register its melons (OnEarlyInitializeMelon, Harmony auto-patch, OnInitializeMelon, OnLateInitializeMelon).
            var ma = MelonAssembly.LoadMelonAssembly(path, asm, loadMelons: true);
            if (ma == null) { _log.Error("Reload of " + name + " failed: MelonLoader could not load the assembly (see above)."); return false; }
            if (ma.RottenMelons.Count > 0) _log.Error(name + ": " + ma.RottenMelons.Count + " melon(s) failed to load (see above).");
            MelonBase.RegisterSorted(ma.LoadedMelons);
            int registered = ma.LoadedMelons.Count(m => m.Registered);
            _source[name] = path;

            var newVer = ma.LoadedMelons.FirstOrDefault()?.Info.Version ?? newVersion.ToString();
            var what = oldVersion == null ? "Loaded new mod " + name + " " + newVer : "Reloaded " + name + " " + oldVersion + " -> " + newVer;
            int patchedAfter = CountPatchedBy(ma.LoadedMelons.Select(m => m.HarmonyInstance?.Id).OfType<string>().ToList());
            what += " (Harmony: " + (oldVersion == null ? "" : patchedBefore + " method(s) unpatched, ") + patchedAfter + " patched)";
            if (registered == ma.LoadedMelons.Count && registered > 0)
                _log.Msg(what + " in " + sw.ElapsedMilliseconds + " ms (from " + ShortPath(path) + ")");
            else
                _log.Warning(what + ": only " + registered + " of " + ma.LoadedMelons.Count + " melon(s) registered (see above).");
            return registered > 0;
        }
        catch (Exception e)
        {
            _log.Error("Reload of " + name + " failed: " + e);
            return false;
        }
    }

    private bool Unload(string name)
    {
        var loaded = FindLoaded(name).ToList();
        if (loaded.Count == 0) return false;
        var melonNames = loaded.SelectMany(a => a.LoadedMelons).Select(m => m.Info.Name).ToList();
        var oldHarmonyIds = loaded.SelectMany(a => a.LoadedMelons).Select(m => m.HarmonyInstance?.Id).OfType<string>().ToList();
        foreach (var old in loaded)
        {
            old.UnregisterMelons("HotReload: DLL deleted", silent: true);
            ForgetMelonAssembly(old);
        }
        WarnLeftoverPatches(name, oldHarmonyIds);
        PrefOwnership.ReleaseCategories(name, melonNames, _log);
        _hash.Remove(name);
        _source.Remove(name);
        _log.Msg("Unloaded " + name + " (its DLL was removed from Mods).");
        return true;
    }

    /// <summary>Number of methods that still carry a patch from one of these Harmony ids.</summary>
    private static int CountPatchedBy(List<string> harmonyIds)
    {
        if (harmonyIds.Count == 0) return 0;
        try
        {
            return HarmonyLib.Harmony.GetAllPatchedMethods()
                .Count(m => HarmonyLib.Harmony.GetPatchInfo(m)?.Owners.Any(harmonyIds.Contains) == true);
        }
        catch { return 0; }
    }

    private void WarnLeftoverPatches(string name, List<string> oldHarmonyIds)
    {
        int left = CountPatchedBy(oldHarmonyIds);
        if (left > 0)
            _log.Warning(name + ": " + left + " method(s) still patched by the old build (patches made with a Harmony instance other than the melon's HarmonyInstance are not removed; undo them in OnDeinitializeMelon).");
    }

    private static IEnumerable<MelonAssembly> FindLoaded(string name) =>
        MelonAssembly.LoadedAssemblies.Where(a => string.Equals(a.Assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>MelonAssembly.LoadMelonAssembly returns a cached entry with the same FullName, so the old one must go.</summary>
    private static void ForgetMelonAssembly(MelonAssembly ma)
    {
        if (LoadedAssembliesField?.GetValue(null) is List<MelonAssembly> list) list.Remove(ma);
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
    private static bool TryInspect(byte[] bytes, out string name, out Version version, out bool isMelon)
    {
        name = ""; version = new Version(0, 0); isMelon = false;
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes), PEStreamOptions.PrefetchEntireImage);
            if (!pe.HasMetadata) return false;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return false;
            var def = md.GetAssemblyDefinition();
            name = md.GetString(def.Name);
            version = def.Version;
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
