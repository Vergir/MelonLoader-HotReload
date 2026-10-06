using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Unity objects a mod creates in code and that no scene owns: textures, render textures, materials, meshes, sprites and
/// ScriptableObjects (setting DestroyOldAssets), and optionally GameObjects (DestroyOldGameObjects). They outlive a
/// reload: the old build's copies stay in memory, and a mod that creates them on every reload leaks a little each time.
/// Postfixes on their constructors and factory methods record the creating mod (from the managed stack, like the
/// DontDestroyOnLoad tracking); on reload the old build's live ones are destroyed.
///
/// IL2CPP: only the constructors that create a native object are hooked. The (IntPtr) constructor only wraps an object
/// that already exists (Il2CppInterop calls it for every object a game method returns), so it is left alone.
/// Generic methods (ScriptableObject.CreateInstance&lt;T&gt;) cannot be hooked; on Mono they call the non-generic one.
/// </summary>
internal static class CreatedObjects
{
    private static readonly string[] AssetTypes =
        { "UnityEngine.Texture2D", "UnityEngine.RenderTexture", "UnityEngine.Material", "UnityEngine.Mesh", "UnityEngine.Cubemap", "UnityEngine.Texture3D", "UnityEngine.Texture2DArray" };
    private static readonly string[] Modules = { "UnityEngine.CoreModule", "UnityEngine" };

    private sealed class Owned
    {
        public readonly List<object> Assets = new List<object>();
        public readonly List<object> GameObjects = new List<object>();
        public int PruneAt = 256;
    }

    private static readonly Dictionary<string, Owned> ByOwner = new Dictionary<string, Owned>(StringComparer.OrdinalIgnoreCase);
    private static Func<bool> _trackGameObjects = () => false;

    /// <summary>Hooks the creating methods. Returns how many were hooked.</summary>
    public static int InstallTracking(HarmonyLib.Harmony harmony, Func<bool> trackGameObjects, MelonLogger.Instance log)
    {
        _trackGameObjects = trackGameObjects;
        var asset = new HarmonyMethod(typeof(CreatedObjects).GetMethod(nameof(AssetCtorPostfix), BindingFlags.Static | BindingFlags.NonPublic));
        var assetResult = new HarmonyMethod(typeof(CreatedObjects).GetMethod(nameof(AssetResultPostfix), BindingFlags.Static | BindingFlags.NonPublic));
        var gameObject = new HarmonyMethod(typeof(CreatedObjects).GetMethod(nameof(GameObjectCtorPostfix), BindingFlags.Static | BindingFlags.NonPublic));
        int hooked = 0;
        using var quiet = QuietInterop.Begin();

        foreach (var name in AssetTypes)
            if (UnityApi.FindType(name, Modules) is { } t)
                foreach (var c in CreatingCtors(t)) hooked += TryPatch(harmony, c, asset);
        if (UnityApi.FindType("UnityEngine.Sprite", Modules) is { } sprite)
            foreach (var m in sprite.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "Create" && !m.IsGenericMethod))
                hooked += TryPatch(harmony, m, assetResult);
        if (UnityApi.FindType("UnityEngine.ScriptableObject", Modules) is { } so)
            foreach (var m in so.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == "CreateInstance" && !m.IsGenericMethod))
                hooked += TryPatch(harmony, m, assetResult);
        if (UnityApi.FindType("UnityEngine.GameObject", Modules) is { } go)
            foreach (var c in CreatingCtors(go)) hooked += TryPatch(harmony, c, gameObject);
        return hooked;

        int TryPatch(HarmonyLib.Harmony h, MethodBase m, HarmonyMethod postfix)
        {
            try { h.Patch(m, postfix: postfix); return 1; }
            catch (Exception e) { log.Msg("Not tracking " + m.DeclaringType?.Name + "." + m.Name + ": " + e.GetBaseException().Message); return 0; }
        }
    }

    /// <summary>
    /// Il2CppInterop first tries to patch an interop method natively, fails for constructors with a warning, then falls
    /// back to a normal managed patch that works. Its logger is muted while these patches go in (one warning per
    /// constructor, about 80 per start).
    /// </summary>
    private sealed class QuietInterop : IDisposable
    {
        private readonly PropertyInfo? _instance;
        private readonly object? _saved;

        private QuietInterop(PropertyInfo instance, object? saved, object quiet)
        {
            _instance = instance;
            _saved = saved;
            instance.SetValue(null, quiet, null);
        }

        public static IDisposable? Begin()
        {
            if (Compat.IsMono) return null;
            try
            {
                var types = AppDomain.CurrentDomain.GetAssemblies();
                var logger = types.Select(a => a.GetType("Il2CppInterop.Common.Logger", false)).FirstOrDefault(t => t != null);
                var nullLogger = types.Select(a => a.GetType("Microsoft.Extensions.Logging.Abstractions.NullLogger", false)).FirstOrDefault(t => t != null);
                var instance = logger?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var quiet = nullLogger?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public)?.GetValue(null, null);
                if (instance == null || quiet == null || !instance.PropertyType.IsInstanceOfType(quiet)) return null;
                return new QuietInterop(instance, instance.GetValue(null, null), quiet);
            }
            catch { return null; }
        }

        public void Dispose()
        {
            try { _instance?.SetValue(null, _saved, null); } catch { /* keep going without Il2CppInterop's log */ }
        }
    }

    /// <summary>Public constructors that make a new object; not the Il2CppInterop (IntPtr) wrapper constructor.</summary>
    private static IEnumerable<ConstructorInfo> CreatingCtors(Type t) =>
        t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => !(c.GetParameters() is { Length: 1 } p && p[0].ParameterType == typeof(IntPtr)));

    private static void AssetCtorPostfix(object __instance) => Record(__instance, gameObject: false);
    private static void AssetResultPostfix(object __result) => Record(__result, gameObject: false);

    private static void GameObjectCtorPostfix(object __instance)
    {
        if (_trackGameObjects()) Record(__instance, gameObject: true);
    }

    private static void Record(object? obj, bool gameObject)
    {
        if (obj == null) return;
        var owner = Callers.FindModAssembly(skipFrames: 2);
        if (owner == null) return;
        if (!ByOwner.TryGetValue(owner, out var owned)) ByOwner[owner] = owned = new Owned();
        var list = gameObject ? owned.GameObjects : owned.Assets;
        list.Add(obj);
        // A mod that makes and destroys objects all the time would grow the list forever: drop the dead ones now and then.
        if (owned.Assets.Count + owned.GameObjects.Count >= owned.PruneAt)
        {
            owned.Assets.RemoveAll(o => !UnityApi.IsAliveOrNotUnity(o));
            owned.GameObjects.RemoveAll(o => !UnityApi.IsAliveOrNotUnity(o));
            owned.PruneAt = Math.Max(256, 2 * (owned.Assets.Count + owned.GameObjects.Count));
        }
    }

    /// <summary>Destroys what the old build of <paramref name="name"/> created and is still alive. Returns (assets, GameObjects).</summary>
    public static (int assets, int gameObjects) DestroyOld(string name, bool assets, bool gameObjects, MelonLogger.Instance log)
    {
        if (!ByOwner.TryGetValue(name, out var owned)) return (0, 0);
        ByOwner.Remove(name);
        return (assets ? Destroy(owned.Assets) : 0, gameObjects ? Destroy(owned.GameObjects) : 0);

        int Destroy(List<object> objects)
        {
            int n = 0;
            foreach (var o in objects)
            {
                try { if (UnityApi.DestroyIfAlive(o)) n++; }
                catch (Exception e) { log.Warning(name + ": could not destroy a " + o.GetType().Name + " of the old build: " + (e.InnerException ?? e).Message); }
            }
            return n;
        }
    }
}
