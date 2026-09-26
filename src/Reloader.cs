using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using MelonLoader.Utils;

namespace HotReload;

/// <summary>
/// Reloads a group of assemblies: the one whose file changed, the loaded mods that reference it, and (optionally) the
/// stateful helper libraries those mods use, so every reloaded mod starts on fresh library state. The group is taken
/// down dependents-first (state handoff saved, melons unregistered, patches removed, old build retired, preference
/// categories and injected class names released) and brought up libraries-first: on IL2CPP each new build in its own
/// AssemblyLoadContext, on Mono under a unique assembly name. Old assemblies are never unloaded; each reload costs the DLLs' size in memory.
/// </summary>
internal sealed class Reloader
{
    public enum Result { Unchanged, Reloaded, Unloaded, Skipped, NotReady, Failed }

    /// <summary>Newest loaded Assembly per simple name, so a reloaded assembly binds to the newest build of the others.</summary>
    internal static readonly Dictionary<string, Assembly> Latest = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

    private static readonly FieldInfo? LoadedAssembliesField =
        typeof(MelonAssembly).GetField("loadedAssemblies", BindingFlags.NonPublic | BindingFlags.Static);

    /// <summary>One assembly of a reload group.</summary>
    private sealed class Item
    {
        public string Name = "", Path = "", Hash = "", Reason = "";
        public byte[] Bytes = Array.Empty<byte>();
        public bool IsLibrary;
        public List<MelonAssembly> OldLoaded = new();
        public HashSet<Assembly> OldAssemblies = new();
        public List<MelonBase> OldMelons = new();
        public string? OldVersion;
        public int PatchedBefore;
        public Dictionary<string, object?> SavedState = new();
    }

    private readonly MelonLogger.Instance _log;
    private readonly string _selfName;
    private readonly Func<string, bool> _isIgnored;
    private readonly Func<bool> _replayScenes, _reloadDependents, _retireOldBuild, _destroyOld, _freshLibraries;
    private readonly Dictionary<string, string> _hash = new(StringComparer.OrdinalIgnoreCase);   // assembly name -> SHA256 of the loaded bytes
    private readonly Dictionary<string, string> _source = new(StringComparer.OrdinalIgnoreCase); // assembly name -> file it was loaded from
    private readonly HashSet<string> _warnedOnce = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;

    public Reloader(MelonLogger.Instance log, string selfName, Func<string, bool> isIgnored, Func<bool> replayScenes, Func<bool> reloadDependents,
        Func<bool> retireOldBuild, Func<bool> destroyOld, Func<bool> freshLibraries)
    {
        _log = log;
        _selfName = selfName;
        _isIgnored = isIgnored;
        _replayScenes = replayScenes;
        _reloadDependents = reloadDependents;
        _retireOldBuild = retireOldBuild;
        _destroyOld = destroyOld;
        _freshLibraries = freshLibraries;
        if (LoadedAssembliesField == null)
            _log.Error("MelonAssembly.loadedAssemblies not found; this MelonLoader version is not supported. Reloads will fail.");
    }

    /// <summary>Remember what is loaded right now, so only later changes trigger a reload.</summary>
    public void Snapshot(IEnumerable<string> watchedFiles)
    {
        foreach (var ma in MelonAssembly.LoadedAssemblies)
        {
            var name = AsmNames.Of(ma.Assembly);
            if (name.Length == 0 || string.IsNullOrEmpty(ma.Location) || !File.Exists(ma.Location)) continue;
            if (TryRead(ma.Location, out var bytes)) _hash[name] = Compat.Sha256Hex(bytes);
            _source[name] = System.IO.Path.GetFullPath(ma.Location);
        }
        int watched = watchedFiles.Count();
        _log.Msg("Tracking " + _hash.Count + " loaded melon assemblies and libraries, " + watched + " watched DLL(s).");
    }

    /// <summary>Mod and plugin DLLs that were deleted while the game runs (their melons get unloaded).</summary>
    public IEnumerable<string> MissingSources() =>
        _source.Where(kv => LoaderFolders.CanUnload(kv.Value) && !File.Exists(kv.Value) && !IsLibrary(kv.Key)).Select(kv => kv.Value).ToList();

    public Result ProcessFile(string path)
    {
        path = System.IO.Path.GetFullPath(path);

        if (!File.Exists(path))
        {
            // Only a deletion from a MelonLoader folder unloads; cleaning a bin folder must not.
            var gone = _source.FirstOrDefault(kv => PathEquals(kv.Value, path)).Key;
            if (gone == null || !LoaderFolders.CanUnload(path) || gone == _selfName || IsLibrary(gone)) return Result.Skipped;
            return Unload(gone) ? Result.Unloaded : Result.Skipped;
        }

        if (!TryRead(path, out var bytes)) return Result.NotReady;
        if (!AssemblyMeta.TryInspect(bytes, out var name, out bool isMelon)) return Result.NotReady; // partially written or not .NET

        if (_isIgnored(name) && name != _selfName) return Result.Skipped;

        var hash = Compat.Sha256Hex(bytes);
        if (_hash.TryGetValue(name, out var known) && known == hash) return Result.Unchanged;
        if (name == _selfName)
        {
            if (_warnedOnce.Add(name)) _log.Msg("HotReload itself changed; restart the game to use the new build.");
            return Result.Skipped;
        }
        if (_isIgnored(name)) return Result.Skipped;
        if (!isMelon && !IsLibrary(name))
        {
            _hash[name] = hash;
            if (_warnedOnce.Add(name)) _log.Msg(System.IO.Path.GetFileName(path) + " has no [MelonInfo] and is not a loaded library; ignored.");
            return Result.Skipped;
        }
        var root = new Item { Name = name, Path = path, Bytes = bytes, Hash = hash, IsLibrary = !isMelon, Reason = "changed" };
        return ReloadGroup(BuildGroup(root)) ? Result.Reloaded : Result.Failed;
    }

    // ---- Group -------------------------------------------------------------------------------------------------------

    private List<Item> BuildGroup(Item root)
    {
        var items = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase) { [root.Name] = root };
        var queue = new Queue<Item>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var item = queue.Dequeue();
            if (_freshLibraries() && !item.IsLibrary)
                foreach (var lib in StatefulLibrariesUsedBy(item.Name))
                    TryAdd(lib, "library used by " + item.Name, isLibrary: true);
            if (_reloadDependents())
                foreach (var dep in DependentsOf(item.Name))
                    TryAdd(dep, (item.IsLibrary ? "uses " : "references ") + item.Name, isLibrary: IsLibrary(dep));
        }
        return TopologicalOrder(items.Values.ToList());

        void TryAdd(string name, string reason, bool isLibrary)
        {
            if (items.ContainsKey(name) || name == _selfName || _isIgnored(name)) return;
            if (!_source.TryGetValue(name, out var p) || !File.Exists(p) || !TryRead(p, out var bytes))
            {
                if (_warnedOnce.Add("unreadable:" + name)) _log.Warning(name + " (" + reason + ") cannot be read from disk; it is not reloaded with the group.");
                return;
            }
            var it = new Item { Name = name, Path = p, Bytes = bytes, Hash = Compat.Sha256Hex(bytes), IsLibrary = isLibrary, Reason = reason };
            items[name] = it;
            queue.Enqueue(it);
        }
    }

    /// <summary>Referenced assemblies first, so every assembly is loaded after the ones it binds to.</summary>
    private static List<Item> TopologicalOrder(List<Item> items)
    {
        var names = new HashSet<string>(items.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        var refs = items.ToDictionary(i => i.Name, i => AssemblyMeta.ReferencedNames(i.Bytes).Where(names.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var ordered = new List<Item>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (ordered.Count < items.Count)
        {
            var next = items.FirstOrDefault(i => !placed.Contains(i.Name) && refs[i.Name].All(r => placed.Contains(r) || r == i.Name))
                       ?? items.First(i => !placed.Contains(i.Name)); // cycle: take any
            ordered.Add(next);
            placed.Add(next.Name);
        }
        return ordered;
    }

    private bool ReloadGroup(List<Item> group)
    {
        var sw = Stopwatch.StartNew();
        foreach (var it in group) _hash[it.Name] = it.Hash; // even on failure: do not retry the same broken bytes on every event
        if (group.Count > 1)
            _log.Msg("Reloading together: " + string.Join(", ", group.Select(i => i.Name + " (" + i.Reason + ")")));
        try
        {
            // ---- Tear down, dependents first ----
            for (int k = group.Count - 1; k >= 0; k--)
            {
                var it = group[k];
                it.OldLoaded = FindLoaded(it.Name).ToList();
                it.OldAssemblies = new HashSet<Assembly>(it.OldLoaded.Select(a => a.Assembly));
                it.OldMelons = it.OldLoaded.SelectMany(a => a.LoadedMelons).ToList();
                it.OldVersion = it.OldMelons.FirstOrDefault()?.Info.Version ?? it.OldLoaded.FirstOrDefault()?.Assembly.GetName().Version?.ToString();
                it.PatchedBefore = CountMethodsPatchedFrom(it.OldAssemblies);
                it.SavedState = StateHandoff.Save(it.OldMelons, _log);
                foreach (var old in it.OldLoaded)
                {
                    old.UnregisterMelons("HotReload", silent: true);
                    ForgetMelonAssembly(old);
                }
            }
            var groupNames = new HashSet<string>(group.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var it in group) RemoveRemainingPatches(it.Name, it.OldAssemblies);
            foreach (var it in group) RetireOldBuild(it.Name, it.OldAssemblies, groupNames);
            foreach (var it in group) PrefOwnership.ReleaseCategories(it.Name, it.OldAssemblies, it.OldMelons, _log);

            // ---- Bring up, libraries (referenced assemblies) first ----
            bool allOk = true;
            foreach (var it in group) allOk &= BringUp(it);
            if (group.Count > 1) _log.Msg("Group reloaded in " + sw.ElapsedMilliseconds + " ms.");
            return allOk;
        }
        catch (Exception e)
        {
            _log.Error("Reload of " + string.Join(", ", group.Select(i => i.Name)) + " failed: " + e);
            return false;
        }
    }

    private bool BringUp(Item it)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            byte[]? pdb = null;
            var pdbPath = System.IO.Path.ChangeExtension(it.Path, ".pdb");
            if (File.Exists(pdbPath) && TryRead(pdbPath, out var pdbBytes)) pdb = pdbBytes;
            Assembly asm = LoadNewBuild(it, pdb);
            StartupLoader.RememberLocation(asm, it.Path);
            StartupLoader.EnsureLocationPatch(asm, it.Path);
            Latest[it.Name] = asm;
            _source[it.Name] = it.Path;

            // Libraries get a MelonAssembly too (as MelonLoader gives UserLibs one), without melons.
            var ma = MelonAssembly.LoadMelonAssembly(it.Path, asm, loadMelons: !it.IsLibrary);
            if (ma == null) { _log.Error("Reload of " + it.Name + " failed: MelonLoader could not load the assembly (see above)."); return false; }
            if (it.IsLibrary)
            {
                _log.Msg("Reloaded library " + it.Name + " (" + it.Reason + ") in " + sw.ElapsedMilliseconds + " ms.");
                return true;
            }

            if (ma.RottenMelons.Count > 0) _log.Error(it.Name + ": " + ma.RottenMelons.Count + " melon(s) failed to load (see above).");
            MelonBase.RegisterSorted(ma.LoadedMelons);
            var registeredMelons = ma.LoadedMelons.Where(m => m.Registered).ToList();
            foreach (var melon in registeredMelons) RunMissedStartCallbacks(melon);
            StateHandoff.Restore(registeredMelons, it.SavedState, _log);

            int scenes = 0;
            if (_replayScenes())
                foreach (var mod in registeredMelons.OfType<MelonMod>())
                    scenes = UnityApi.ReplaySceneEvents(mod);

            var newVer = ma.LoadedMelons.FirstOrDefault()?.Info.Version ?? asm.GetName().Version?.ToString() ?? "?";
            var kind = ma.LoadedMelons.FirstOrDefault() is MelonPlugin ? "plugin" : "mod";
            var what = it.OldVersion == null ? "Loaded new " + kind + " " + it.Name + " " + newVer : "Reloaded " + it.Name + " " + it.OldVersion + " -> " + newVer;
            if (it.Reason != "changed") what += " (" + it.Reason + ")";
            int patchedAfter = CountMethodsPatchedFrom(new HashSet<Assembly> { asm });
            what += " (Harmony: " + (it.OldVersion == null ? "" : it.PatchedBefore + " method(s) unpatched, ") + patchedAfter + " patched"
                    + (scenes > 0 ? "; replayed " + scenes + " scene(s)" : "")
                    + (it.SavedState.Count > 0 ? "; state handed over" : "") + ")";
            if (registeredMelons.Count == ma.LoadedMelons.Count && registeredMelons.Count > 0)
                _log.Msg(what + " in " + sw.ElapsedMilliseconds + " ms (from " + ShortPath(it.Path) + ")");
            else
                _log.Warning(what + ": only " + registeredMelons.Count + " of " + ma.LoadedMelons.Count + " melon(s) registered (see above).");
            return registeredMelons.Count > 0;
        }
        catch (Exception e)
        {
            _log.Error("Reload of " + it.Name + " failed: " + e);
            return false;
        }
    }

    /// <summary>
    /// Loads a new build from a copy in the shadow folder (a real Location; the original stays unlocked).
    /// IL2CPP (.NET 6): into its own AssemblyLoadContext, whose resolver prefers the newest reloaded builds.
    /// Mono: there are no load contexts and Mono binds a reference to the first loaded assembly of that name, so the
    /// build is renamed ("Name__hrN") and its references to reloaded assemblies are pointed at their current names.
    /// </summary>
    private Assembly LoadNewBuild(Item it, byte[]? pdb)
    {
        int gen = ++_generation;
        if (Compat.IsMono)
        {
            var current = Latest.ToDictionary(kv => kv.Key, kv => kv.Value.GetName().Name!, StringComparer.OrdinalIgnoreCase);
            var (dll, newPdb) = AssemblyMeta.Rename(it.Bytes, pdb, AsmNames.Unique(it.Name, gen), current);
            return Assembly.LoadFrom(StartupLoader.ShadowCopyForReload(it.Path, dll, newPdb, gen));
        }
        var ctx = LoadContexts.Create(it.Name + " #" + gen);
        try { return LoadContexts.LoadFromPath(ctx, StartupLoader.ShadowCopyForReload(it.Path, it.Bytes, pdb, gen)); }
        catch (IOException) { return LoadContexts.LoadFromStream(ctx, new MemoryStream(it.Bytes), pdb == null ? null : new MemoryStream(pdb)); }
    }

    /// <summary>
    /// MelonLoader calls OnInitializeMelon / OnLateInitializeMelon itself for a melon registered late, but other start
    /// callbacks are subscribed to one-time events that already fired, so a reloaded melon that uses them would never
    /// start: the obsolete OnApplicationStart / OnApplicationLateStart (UnityExplorer uses the first) and a plugin's
    /// OnApplicationStarted. Call the ones the melon overrides. A plugin's earlier hooks (OnPreInitialization,
    /// OnApplicationEarlyStart, OnPreModsLoaded) belong to game startup and are not re-run.
    /// </summary>
    private static void RunMissedStartCallbacks(MelonBase melon)
    {
        var callbacks = melon is MelonPlugin
            ? new (string, MelonEvent)[] { ("OnApplicationStarted", MelonEvents.OnApplicationStart), ("OnApplicationLateStart", MelonEvents.OnApplicationLateStart) }
            : new (string, MelonEvent)[] { ("OnApplicationStart", MelonEvents.OnApplicationStart), ("OnApplicationLateStart", MelonEvents.OnApplicationLateStart) };
        foreach (var (callback, fired) in callbacks)
        {
            if (!fired.Disposed) continue; // not fired yet: MelonLoader will call it
            var m = melon.GetType().GetMethod(callback, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (m == null || m.DeclaringType == typeof(MelonBase) || m.DeclaringType == typeof(MelonMod) || m.DeclaringType == typeof(MelonPlugin)) continue;
            try { m.Invoke(melon, null); }
            catch (TargetInvocationException e) { melon.LoggerInstance.Error(callback + " during hot reload: " + e.InnerException); }
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
        _log.Msg("Unloaded " + name + " (its DLL was removed).");
        return true;
    }

    private void RetireOldBuild(string name, HashSet<Assembly> oldAssemblies, HashSet<string> group)
    {
        var parts = new List<string>();
        // Classes whose instances the game drives directly: injected Il2Cpp classes, or MonoBehaviours on Mono.
        var injected = Compat.IsMono
            ? oldAssemblies.SelectMany(UnityApi.ComponentTypesIn).ToList()
            : oldAssemblies.SelectMany(InjectedTypes.InjectedIn).ToList();
        if (_destroyOld())
        {
            int destroyed = UnityApi.DestroyPersistentObjects(name, _log);
            if (destroyed > 0) parts.Add("destroyed " + destroyed + " object(s) kept across scenes");
            if (injected.Count > 0)
            {
                int instances = UnityApi.DestroyInstancesOf(injected, _log);
                if (instances > 0) parts.Add("destroyed " + instances + " instance(s) of old " + (Compat.IsMono ? "component" : "injected") + " classes");
            }
        }
        if (_retireOldBuild())
        {
            // An assembly that references this one and is not reloaded with it would call into the retired build.
            var stuck = DependentsOf(name).Where(n => !group.Contains(n)).ToList();
            if (stuck.Count > 0)
                parts.Add("old build NOT retired because " + string.Join(", ", stuck) + " still use it");
            else
                foreach (var asm in oldAssemblies)
                {
                    var (retired, failed, ms) = Retirer.Retire(asm, injected.Where(t => t.Assembly == asm), _log);
                    if (retired + failed > 0) parts.Add("retired " + retired + " old method(s)" + (failed > 0 ? " (" + failed + " failed)" : "") + " in " + ms + " ms");
                }
        }
        // IL2CPP: the new build registers classes with the same names; Il2CppInterop would refuse them otherwise.
        if (!Compat.IsMono && injected.Count > 0)
        {
            int released = InjectedTypes.Release(injected, _log);
            if (released > 0) parts.Add("released " + released + " injected class name(s)");
        }
        if (parts.Count > 0) _log.Msg(name + ": " + string.Join("; ", parts) + ".");
    }

    // ---- Dependency queries --------------------------------------------------------------------------------------------

    private static bool IsLibrary(string name)
    {
        var loaded = FindLoaded(name).ToList();
        return loaded.Count > 0 && loaded.All(a => a.LoadedMelons.Count == 0);
    }

    /// <summary>Loaded melon assemblies and libraries that reference <paramref name="name"/>.</summary>
    private IEnumerable<string> DependentsOf(string name) =>
        MelonAssembly.LoadedAssemblies
            .Where(a => a.Assembly.GetReferencedAssemblies().Any(r => string.Equals(AsmNames.Strip(r.Name), name, StringComparison.OrdinalIgnoreCase)))
            .Select(a => AsmNames.Of(a.Assembly))
            .Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase) && n != _selfName && !_isIgnored(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Loaded libraries (MelonAssemblies without melons, i.e. UserLibs) that <paramref name="name"/> references and that
    /// can hold mod state: they reference MelonLoader, Il2CppInterop or Unity. Plain libraries (JSON, math) are left alone.
    /// </summary>
    private static IEnumerable<string> StatefulLibrariesUsedBy(string name)
    {
        var asm = FindLoaded(name).FirstOrDefault()?.Assembly;
        if (asm == null) yield break;
        foreach (var r in asm.GetReferencedAssemblies())
        {
            var rn = AsmNames.Strip(r.Name);
            if (rn.Length == 0 || !IsLibrary(rn)) continue;
            var lib = FindLoaded(rn).First().Assembly;
            if (lib.GetReferencedAssemblies().Any(x => x.Name is "MelonLoader" or "Il2CppInterop.Runtime" or "UnityEngine.CoreModule" or "UnityEngine"))
                yield return rn;
        }
    }

    private static IEnumerable<MelonAssembly> FindLoaded(string name) =>
        MelonAssembly.LoadedAssemblies.Where(a => string.Equals(AsmNames.Of(a.Assembly), name, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>MelonAssembly.LoadMelonAssembly returns a cached entry with the same FullName, so the old one must go.</summary>
    private static void ForgetMelonAssembly(MelonAssembly ma)
    {
        if (LoadedAssembliesField?.GetValue(null) is List<MelonAssembly> list) list.Remove(ma);
    }

    // ---- Harmony ---------------------------------------------------------------------------------------------------------

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
        if (assemblies.Count == 0) return 0;
        try
        {
            return HarmonyLib.Harmony.GetAllPatchedMethods()
                .Count(m => HarmonyLib.Harmony.GetPatchInfo(m) is { } info && AllPatches(info).Any(p => FromAny(p, assemblies)));
        }
        catch { return 0; }
    }

    /// <summary>
    /// Unregistering only unpatches the melon's own HarmonyInstance. Anything the old build patched through another
    /// Harmony instance (or a library patched at all) is found by the assembly of the patch method and removed here.
    /// </summary>
    private void RemoveRemainingPatches(string name, HashSet<Assembly> oldAssemblies)
    {
        if (oldAssemblies.Count == 0) return;
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

    // ---- Files -----------------------------------------------------------------------------------------------------------

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

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string ShortPath(string path)
    {
        var root = MelonEnvironment.GameRootDirectory;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length).TrimStart('\\', '/') : path;
    }
}

/// <summary>The MelonLoader folders HotReload watches, and which of them may unload on delete.</summary>
internal static class LoaderFolders
{
    /// <summary>Folders MelonLoader loads melons from: Mods (and its manifest subfolders) and Plugins.</summary>
    public static IEnumerable<string> MelonFolders() =>
        StartupLoader.ModDirectories().Append(MelonEnvironment.PluginsDirectory).Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);

    public static string UserLibs => System.IO.Path.GetFullPath(MelonEnvironment.UserLibsDirectory);

    /// <summary>Deleting a melon DLL from a MelonLoader folder unloads it; deleting from a bin folder does not.</summary>
    public static bool CanUnload(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? "";
        return MelonFolders().Any(d => string.Equals(d.TrimEnd('\\', '/'), dir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }
}
