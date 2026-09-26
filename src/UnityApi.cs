using System;
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
}
