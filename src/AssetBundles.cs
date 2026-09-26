using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Unity refuses to load an AssetBundle while another copy of it is loaded, so a reloaded mod that loads its bundle
/// again gets null ("another AssetBundle with the same files is already loaded"). On reload HotReload unloads the old
/// build's bundles with Unload(false): the bundle's files are released, objects already created from it stay intact.
/// The old build's bundles are found two ways:
///  1. the bundles it keeps in fields (FieldScan) - covers wrappers like UniverseLib's own AssetBundle class too;
///  2. postfixes on UnityEngine.AssetBundle.LoadFrom* (and their Async variants), owner from the managed call stack -
///     covers bundles a mod loads and drops. Best effort on IL2CPP: stripped methods cannot be hooked and are skipped.
/// </summary>
internal static class AssetBundles
{
    private static readonly Dictionary<string, List<object>> Loaded = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hooks the AssetBundle load methods. Returns how many were hooked.</summary>
    public static int InstallTracking(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        var bundle = UnityApi.FindType("UnityEngine.AssetBundle", "UnityEngine.AssetBundleModule", "UnityEngine");
        if (bundle == null) return 0;
        var postfix = new HarmonyMethod(typeof(AssetBundles).GetMethod(nameof(LoadPostfix), BindingFlags.Static | BindingFlags.NonPublic));
        int hooked = 0;
        foreach (var m in bundle.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (!m.Name.StartsWith("LoadFrom", StringComparison.Ordinal) || m.IsGenericMethod) continue;
            if (m.ReturnType != bundle && m.ReturnType.Name != "AssetBundleCreateRequest") continue;
            try { harmony.Patch(m, postfix: postfix); hooked++; }
            catch { /* stripped on IL2CPP, or not patchable: bundles loaded through it are only found through fields */ }
        }
        return hooked;
    }

    private static void LoadPostfix(object __result)
    {
        if (__result == null) return;
        var owner = Callers.FindModAssembly();
        if (owner == null) return;
        if (!Loaded.TryGetValue(owner, out var list)) Loaded[owner] = list = new List<object>();
        list.Add(__result); // an AssetBundle, or an AssetBundleCreateRequest whose bundle arrives later
    }

    /// <summary>UnityEngine.AssetBundle, or a mod's own wrapper named like it with an Unload(bool) method (UniverseLib).</summary>
    private static bool IsBundleType(Type t) =>
        t.Name == "AssetBundleCreateRequest" || (t.Name.EndsWith("AssetBundle", StringComparison.Ordinal) && UnloadMethod(t) != null);

    private static MethodInfo? UnloadMethod(Type t) =>
        t.GetMethod("Unload", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);

    /// <summary>Unloads the bundles of the old build <paramref name="name"/>. Returns how many were loaded.</summary>
    public static int UnloadOld(string name, ICollection<Assembly> oldAssemblies, ICollection<MelonBase> oldMelons, MelonLogger.Instance log)
    {
        var candidates = new List<object>();
        if (Loaded.TryGetValue(name, out var tracked)) { candidates.AddRange(tracked); Loaded.Remove(name); }
        candidates.AddRange(FieldScan.Held(oldAssemblies, oldMelons, IsBundleType, o => IsBundleType(o.GetType())));

        int n = 0;
        var done = new HashSet<IntPtr>();
        var doneManaged = new List<object>();
        foreach (var candidate in candidates)
        {
            try
            {
                var bundle = candidate.GetType().Name == "AssetBundleCreateRequest"
                    ? candidate.GetType().GetProperty("assetBundle")?.GetValue(candidate, null)
                    : candidate;
                if (bundle == null || !UnityApi.IsAliveOrNotUnity(bundle)) continue;
                // The same bundle can come from a field and from tracking (as different wrappers on IL2CPP).
                var ptr = UnityApi.NativePointer(bundle);
                if (ptr != IntPtr.Zero ? !done.Add(ptr) : doneManaged.Any(o => ReferenceEquals(o, bundle))) continue;
                doneManaged.Add(bundle);
                var unload = UnloadMethod(bundle.GetType());
                if (unload == null) continue;
                unload.Invoke(bundle, new object[] { false });
                n++;
            }
            catch (Exception e) { log.Warning(name + ": could not unload an AssetBundle: " + (e.InnerException ?? e).Message); }
        }
        return n;
    }
}
