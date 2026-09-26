using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HotReload;

/// <summary>
/// Everything that touches Unity / Il2Cpp interop types. HotReload is a plugin and registers before the interop
/// assemblies can be loaded, so no Unity type may appear in a field or signature of the plugin class itself;
/// these methods are only JIT-compiled once the game runs.
/// </summary>
internal static class UnityApi
{
    public const int NoKey = 0; // KeyCode.None

    /// <summary>Parses a KeyCode name. Returns false for unknown names.</summary>
    public static bool TryParseKey(string name, out int key)
    {
        key = NoKey;
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)) return true;
        if (!Enum.TryParse(name.Trim(), true, out KeyCode k)) return false;
        key = (int)k;
        return true;
    }

    public static string KeyName(int key) => ((KeyCode)key).ToString();

    public static bool KeyDown(int key) => key != NoKey && Input.GetKeyDown((KeyCode)key);

    /// <summary>
    /// A reloaded mod never saw the scenes that are already open. Replays OnSceneWasLoaded + OnSceneWasInitialized
    /// for each loaded scene, in load order, as MelonLoader would have.
    /// </summary>
    public static int ReplaySceneEvents(MelonMod mod)
    {
        int n = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            try { mod.OnSceneWasLoaded(scene.buildIndex, scene.name); }
            catch (Exception e) { mod.LoggerInstance.Error("OnSceneWasLoaded(" + scene.name + ") during hot reload: " + e); }
            try { mod.OnSceneWasInitialized(scene.buildIndex, scene.name); }
            catch (Exception e) { mod.LoggerInstance.Error("OnSceneWasInitialized(" + scene.name + ") during hot reload: " + e); }
            n++;
        }
        return n;
    }

    // ---- Objects kept across scene loads ------------------------------------------------------------------------
    // Scene objects a mod creates go away with the scene. Objects passed to DontDestroyOnLoad live until destroyed,
    // so after a reload the old build's UI roots, canvases and EventSystems would stay next to the new build's.
    // DontDestroyOnLoad is only ever called from managed code by mods (the game calls it natively), so a postfix
    // that looks at the managed stack tells which mod asked.

    private static readonly Dictionary<string, List<UnityEngine.Object>> Persistent =
        new Dictionary<string, List<UnityEngine.Object>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Needs Il2Cpp support to be set up (OnApplicationStart), and must run before mods' OnInitializeMelon.</summary>
    public static bool InstallPersistentObjectTracking(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        try
        {
            var original = AccessTools.Method(typeof(UnityEngine.Object), nameof(UnityEngine.Object.DontDestroyOnLoad), new[] { typeof(UnityEngine.Object) });
            harmony.Patch(original, postfix: new HarmonyMethod(typeof(UnityApi).GetMethod(nameof(DontDestroyOnLoadPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            return true;
        }
        catch (Exception e)
        {
            log.Warning("Could not hook DontDestroyOnLoad; objects a reloaded mod kept across scenes will stay: " + e.Message);
            return false;
        }
    }

    private static void DontDestroyOnLoadPostfix(UnityEngine.Object target)
    {
        if (target == null) return;
        var owner = Callers.FindModAssembly();
        if (owner == null) return;
        if (!Persistent.TryGetValue(owner, out var list)) Persistent[owner] = list = new List<UnityEngine.Object>();
        list.Add(target);
    }

    /// <summary>Destroys the GameObjects <paramref name="assemblyName"/> passed to DontDestroyOnLoad. Returns how many were alive.</summary>
    public static int DestroyPersistentObjects(string assemblyName, MelonLogger.Instance log)
    {
        if (!Persistent.TryGetValue(assemblyName, out var list)) return 0;
        Persistent.Remove(assemblyName);
        int n = 0;
        foreach (var obj in list)
        {
            try
            {
                if (obj == null || obj.WasCollected) continue; // already destroyed
                var go = obj.TryCast<GameObject>() ?? obj.TryCast<Component>()?.gameObject;
                if (go == null) continue;
                UnityEngine.Object.Destroy(go);
                n++;
            }
            catch (Exception e) { log.Warning(assemblyName + ": could not destroy a persistent object: " + e.Message); }
        }
        return n;
    }
}
