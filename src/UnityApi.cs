using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Everything that touches Unity, through reflection. HotReload compiles against no Unity assembly, so it builds without
/// a game and one DLL works with any Unity version: the types are looked up at runtime in the game's modules (Mono) or
/// the Il2Cpp interop assemblies (IL2CPP), which carry the same type and member names. Il2CppInterop helpers
/// (WasCollected, TryCast, Il2CppType.From) are reached the same way and are simply absent on Mono.
/// Nothing here runs before OnInitializeMelon: the interop assemblies do not exist earlier on IL2CPP.
/// </summary>
internal static class UnityApi
{
    public const int NoKey = 0; // KeyCode.None

    private static readonly string[] CoreModules = { "UnityEngine.CoreModule", "UnityEngine" };
    private static readonly string[] InputModules = { "UnityEngine.InputLegacyModule", "UnityEngine.CoreModule", "UnityEngine" };
    private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>A type by full name from the first of <paramref name="assemblyNames"/> that has it, loading the assembly if needed.</summary>
    public static Type? FindType(string fullName, params string[] assemblyNames)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? n;
            try { n = a.GetName().Name; } catch { continue; }
            if (n == null || !assemblyNames.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
            var t = a.GetType(fullName, false);
            if (t != null) return t;
        }
        foreach (var n in assemblyNames)
        {
            try
            {
                var t = Assembly.Load(n).GetType(fullName, false); // MelonLoader's resolver finds the interop assemblies
                if (t != null) return t;
            }
            catch { /* not in this game */ }
        }
        return null;
    }

    private static Type Need(string fullName, string[] modules) =>
        FindType(fullName, modules) ?? throw new InvalidOperationException(fullName + " not found in " + string.Join(" / ", modules));

    private static MethodInfo NeedMethod(Type t, string name, params Type[] args) =>
        t.GetMethod(name, PublicStatic | BindingFlags.Instance, null, args, null) ?? throw new MissingMethodException(t.FullName, name);

    // ---- Unity members, resolved on first use --------------------------------------------------------------------

    private sealed class Core
    {
        public readonly Type Object, GameObject, Component;
        public readonly Type? ScriptableObject;
        public readonly MethodInfo Destroy, Exists, FindObjectsOfTypeAll, DontDestroyOnLoad, SceneCount, GetSceneAt;
        public readonly MethodInfo? GetActiveScene;
        public readonly PropertyInfo ComponentGameObject, SceneIsLoaded, SceneBuildIndex, SceneName;

        public Core()
        {
            Object = Need("UnityEngine.Object", CoreModules);
            GameObject = Need("UnityEngine.GameObject", CoreModules);
            Component = Need("UnityEngine.Component", CoreModules);
            ScriptableObject = FindType("UnityEngine.ScriptableObject", CoreModules);
            Destroy = NeedMethod(Object, "Destroy", Object);
            DontDestroyOnLoad = NeedMethod(Object, "DontDestroyOnLoad", Object);
            // implicit operator bool(Object exists): false for destroyed objects, like "if (obj)" in a mod.
            Exists = Object.GetMethods(PublicStatic).FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == typeof(bool)
                         && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == Object)
                     ?? throw new MissingMethodException(Object.FullName, "op_Implicit");
            var resources = Need("UnityEngine.Resources", CoreModules);
            // The non-generic overload; its parameter is System.Type on Mono and Il2CppSystem.Type on IL2CPP.
            FindObjectsOfTypeAll = resources.GetMethods(PublicStatic).FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll" && !m.IsGenericMethod && m.GetParameters().Length == 1)
                                   ?? throw new MissingMethodException(resources.FullName, "FindObjectsOfTypeAll");
            ComponentGameObject = Component.GetProperty("gameObject", PublicInstance) ?? throw new MissingMemberException(Component.FullName, "gameObject");

            var sceneManager = Need("UnityEngine.SceneManagement.SceneManager", CoreModules);
            SceneCount = sceneManager.GetProperty("sceneCount", PublicStatic)?.GetGetMethod() ?? throw new MissingMemberException(sceneManager.FullName, "sceneCount");
            GetSceneAt = NeedMethod(sceneManager, "GetSceneAt", typeof(int));
            GetActiveScene = sceneManager.GetMethod("GetActiveScene", PublicStatic, null, Type.EmptyTypes, null);
            var scene = GetSceneAt.ReturnType;
            SceneIsLoaded = scene.GetProperty("isLoaded", PublicInstance) ?? throw new MissingMemberException(scene.FullName, "isLoaded");
            SceneBuildIndex = scene.GetProperty("buildIndex", PublicInstance) ?? throw new MissingMemberException(scene.FullName, "buildIndex");
            SceneName = scene.GetProperty("name", PublicInstance) ?? throw new MissingMemberException(scene.FullName, "name");
        }
    }

    private static Core? _core;
    private static Core U => _core ??= new Core();

    /// <summary>Il2CppInterop's object base and helpers; null on Mono.</summary>
    private sealed class Interop
    {
        public readonly Type ObjectBase;
        public readonly PropertyInfo WasCollected;
        public readonly PropertyInfo? Pointer;
        public readonly MethodInfo TryCast, TypeFrom;
        private readonly Dictionary<Type, MethodInfo> _casts = new Dictionary<Type, MethodInfo>();

        private Interop(Type objectBase, Type il2CppType)
        {
            ObjectBase = objectBase;
            WasCollected = objectBase.GetProperty("WasCollected", PublicInstance) ?? throw new MissingMemberException(objectBase.FullName, "WasCollected");
            Pointer = objectBase.GetProperty("Pointer", PublicInstance);
            TryCast = objectBase.GetMethod("TryCast", PublicInstance, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(objectBase.FullName, "TryCast");
            TypeFrom = il2CppType.GetMethod("From", PublicStatic, null, new[] { typeof(Type) }, null) ?? throw new MissingMethodException(il2CppType.FullName, "From");
        }

        public static Interop? Find()
        {
            var ob = FindType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", "Il2CppInterop.Runtime");
            var it = FindType("Il2CppInterop.Runtime.Il2CppType", "Il2CppInterop.Runtime");
            return ob != null && it != null ? new Interop(ob, it) : null;
        }

        /// <summary>Il2Cpp wrappers carry the declared type (a DontDestroyOnLoad argument is a UnityEngine.Object), so cast to the real one.</summary>
        public object? Cast(object obj, Type to)
        {
            if (!ObjectBase.IsInstanceOfType(obj)) return to.IsInstanceOfType(obj) ? obj : null;
            if (!_casts.TryGetValue(to, out var m)) _casts[to] = m = TryCast.MakeGenericMethod(to);
            return m.Invoke(obj, null);
        }
    }

    private static bool _interopLooked;
    private static Interop? _interop;
    private static Interop? Il2Cpp
    {
        get
        {
            if (!_interopLooked) { _interopLooked = true; _interop = Compat.IsMono ? null : Interop.Find(); }
            return _interop;
        }
    }

    /// <summary>Whether a Unity object is still alive: not collected on the Il2Cpp side and not destroyed.</summary>
    private static bool IsAlive(object? obj)
    {
        if (obj == null) return false;
        try
        {
            var il2cpp = Il2Cpp;
            if (il2cpp != null && il2cpp.ObjectBase.IsInstanceOfType(obj) && (bool)il2cpp.WasCollected.GetValue(obj, null)!) return false;
            return (bool)U.Exists.Invoke(null, new[] { obj })!;
        }
        catch { return false; }
    }

    /// <summary>Destroys a Unity object that is still alive. True when it was alive.</summary>
    public static bool DestroyIfAlive(object obj)
    {
        if (!IsAlive(obj)) return false;
        U.Destroy.Invoke(null, new[] { obj });
        return true;
    }

    /// <summary>
    /// For objects that may or may not be Unity objects (a mod's own wrapper around a native object): alive when it is a
    /// live Unity object, or not a Unity object and not a collected Il2Cpp object.
    /// </summary>
    public static bool IsAliveOrNotUnity(object obj)
    {
        try
        {
            if (U.Object.IsInstanceOfType(obj)) return IsAlive(obj);
            var il2cpp = Il2Cpp;
            return il2cpp == null || !il2cpp.ObjectBase.IsInstanceOfType(obj) || !(bool)il2cpp.WasCollected.GetValue(obj, null)!;
        }
        catch { return false; }
    }

    /// <summary>The native object behind an Il2Cpp wrapper (IntPtr.Zero on Mono or for managed objects).</summary>
    public static IntPtr NativePointer(object obj)
    {
        var il2cpp = Il2Cpp;
        if (il2cpp?.Pointer == null || !il2cpp.ObjectBase.IsInstanceOfType(obj)) return IntPtr.Zero;
        try { return (IntPtr)il2cpp.Pointer.GetValue(obj, null)!; } catch { return IntPtr.Zero; }
    }

    private static object? As(object obj, Type type)
    {
        var il2cpp = Il2Cpp;
        return il2cpp != null ? il2cpp.Cast(obj, type) : type.IsInstanceOfType(obj) ? obj : null;
    }

    // ---- Reload key (legacy UnityEngine.Input) --------------------------------------------------------------------

    private static Type? _keyCode;
    private static MethodInfo? _getKeyDown, _getKey;
    private static readonly Dictionary<int, object[]> KeyArgs = new Dictionary<int, object[]>();

    private static Type KeyCodeType => _keyCode ??= Need("UnityEngine.KeyCode", InputModules);

    /// <summary>Null when legacy input can be used, else why not.</summary>
    public static string? LegacyInputProblem()
    {
        try
        {
            var input = FindType("UnityEngine.Input", InputModules);
            if (input == null) return "the game has no UnityEngine.Input";
            _getKeyDown = input.GetMethod("GetKeyDown", PublicStatic, null, new[] { KeyCodeType }, null);
            _getKey = input.GetMethod("GetKey", PublicStatic, null, new[] { KeyCodeType }, null);
            return _getKeyDown == null || _getKey == null ? "Input.GetKeyDown / GetKey(KeyCode) not found" : null;
        }
        catch (Exception e) { return e.GetBaseException().Message; }
    }

    private static object[] Args(int key)
    {
        if (!KeyArgs.TryGetValue(key, out var args)) KeyArgs[key] = args = new[] { Enum.ToObject(KeyCodeType, key) };
        return args;
    }

    private static bool CallInput(MethodInfo? method, int key)
    {
        if (key == NoKey) return false;
        if ((_getKeyDown == null || _getKey == null) && LegacyInputProblem() is { } problem) throw new InvalidOperationException(problem);
        try { return (bool)Unpatched(method!).Invoke(null, Args(key))!; }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }

    // Another mod may patch Input.GetKey / GetKeyDown, e.g. to block game input while its window is open, which would
    // swallow the reload key. While someone has patched them:
    //  - Mono: the key is read through copies of the original methods (MonoMod's DynamicMethodDefinition, which Harmony
    //    itself uses; built from the method's IL, so without patches).
    //  - IL2CPP: Il2CppInterop patches the game's native method, which a managed copy still calls, so KeyInput reads the
    //    key another way (LegacyInputBlocked).
    private static readonly Dictionary<MethodInfo, MethodInfo?> UnpatchedCopies = new Dictionary<MethodInfo, MethodInfo?>();
    private static int _patchCheckIn;
    private static bool _inputPatched;

    private static bool InputPatched()
    {
        if (--_patchCheckIn <= 0)
        {
            _patchCheckIn = 30; // patches come and go (a mod's window opens); a look every 30 calls is enough
            _inputPatched = IsPatched(_getKeyDown) || IsPatched(_getKey);
        }
        return _inputPatched;
    }

    /// <summary>IL2CPP: another mod patched Input.GetKey / GetKeyDown, so legacy input cannot be trusted for the reload key right now.</summary>
    public static bool LegacyInputBlocked() => !Compat.IsMono && InputPatched();

    private static MethodInfo Unpatched(MethodInfo method)
    {
        if (!Compat.IsMono || !InputPatched()) return method;
        if (!UnpatchedCopies.TryGetValue(method, out var copy)) UnpatchedCopies[method] = copy = CopyOf(method);
        return copy ?? method;
    }

    private static bool IsPatched(MethodInfo? method)
    {
        try { return method != null && HarmonyLib.Harmony.GetPatchInfo(method) is { } info && info.Owners.Count > 0; }
        catch { return false; }
    }

    private static MethodInfo? CopyOf(MethodInfo method)
    {
        try
        {
            var dmdType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MonoMod.Utils.DynamicMethodDefinition", false)).FirstOrDefault(t => t != null)
                          ?? throw new TypeLoadException("MonoMod.Utils.DynamicMethodDefinition not loaded");
            var dmd = Activator.CreateInstance(dmdType, method)!;
            var copy = (MethodInfo)dmdType.GetMethod("Generate", Type.EmptyTypes)!.Invoke(dmd, null)!;
            HotReloadPlugin.Log?.Msg("Another mod patched Input." + method.Name + "; the reload key reads an unpatched copy.");
            return copy;
        }
        catch (Exception e)
        {
            HotReloadPlugin.Log?.Warning("Another mod patched Input." + method.Name + " and an unpatched copy could not be made (" + e.GetBaseException().Message +
                                         "); the reload key may not work while that mod blocks input. InputBackend = \"Windows\" avoids it.");
            return null;
        }
    }

    /// <summary>Parses a KeyCode name. Returns false for unknown names.</summary>
    public static bool TryParseKey(string name, out int key)
    {
        key = NoKey;
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)) return true;
        object value;
        try { value = Enum.Parse(KeyCodeType, name.Trim(), ignoreCase: true); }
        catch (ArgumentException) { return false; }
        if (!Enum.IsDefined(KeyCodeType, value)) return false; // a bare number that is no KeyCode
        key = Convert.ToInt32(value);
        return true;
    }

    public static string KeyName(int key) => Enum.ToObject(KeyCodeType, key).ToString() ?? key.ToString();

    /// <summary>True in the frame the key went down. Throws when the game disabled legacy input; KeyInput then moves on to the next backend.</summary>
    public static bool KeyDown(int key) => CallInput(_getKeyDown, key);

    /// <summary>True while the key is held.</summary>
    public static bool KeyHeld(int key) => CallInput(_getKey, key);

    // ---- Scenes ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A reloaded mod never saw the scenes that are already open. Replays OnSceneWasLoaded + OnSceneWasInitialized
    /// for each loaded scene, in load order, as MelonLoader would have.
    /// </summary>
    public static int ReplaySceneEvents(MelonMod mod, bool activeSceneOnly)
    {
        int n = 0;
        foreach (var (buildIndex, name) in activeSceneOnly ? ActiveScene() : LoadedScenes())
        {
            try { mod.OnSceneWasLoaded(buildIndex, name); }
            catch (Exception e) { mod.LoggerInstance.Error("OnSceneWasLoaded(" + name + ") during hot reload: " + e); }
            try { mod.OnSceneWasInitialized(buildIndex, name); }
            catch (Exception e) { mod.LoggerInstance.Error("OnSceneWasInitialized(" + name + ") during hot reload: " + e); }
            n++;
        }
        return n;
    }

    /// <summary>The active scene only (games that stream their world as many additive scenes); all loaded scenes if unknown.</summary>
    private static List<(int buildIndex, string name)> ActiveScene()
    {
        var u = U;
        if (u.GetActiveScene == null) return LoadedScenes();
        var scene = u.GetActiveScene.Invoke(null, null)!; // boxed struct
        if (!(bool)u.SceneIsLoaded.GetValue(scene, null)!) return new List<(int, string)>();
        return new List<(int, string)> { ((int)u.SceneBuildIndex.GetValue(scene, null)!, (string)u.SceneName.GetValue(scene, null)!) };
    }

    private static List<(int buildIndex, string name)> LoadedScenes()
    {
        var u = U;
        var result = new List<(int, string)>();
        int count = (int)u.SceneCount.Invoke(null, null)!;
        var arg = new object[1];
        for (int i = 0; i < count; i++)
        {
            arg[0] = i;
            var scene = u.GetSceneAt.Invoke(null, arg)!; // boxed struct
            if (!(bool)u.SceneIsLoaded.GetValue(scene, null)!) continue;
            result.Add(((int)u.SceneBuildIndex.GetValue(scene, null)!, (string)u.SceneName.GetValue(scene, null)!));
        }
        return result;
    }

    // ---- Objects kept across scene loads ------------------------------------------------------------------------
    // Scene objects a mod creates go away with the scene. Objects passed to DontDestroyOnLoad live until destroyed,
    // so after a reload the old build's UI roots, canvases and EventSystems would stay next to the new build's.
    // A postfix that looks at the managed stack tells which mod asked (the game's own calls have no mod on the stack).

    private static readonly Dictionary<string, List<object>> Persistent = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Needs Il2Cpp support to be set up (OnApplicationStart), and must run before mods' OnInitializeMelon.</summary>
    public static bool InstallPersistentObjectTracking(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        try
        {
            harmony.Patch(U.DontDestroyOnLoad, postfix: new HarmonyMethod(typeof(UnityApi).GetMethod(nameof(DontDestroyOnLoadPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            return true;
        }
        catch (Exception e)
        {
            log.Warning("Could not hook DontDestroyOnLoad; objects a reloaded mod kept across scenes will stay: " + e.Message);
            return false;
        }
    }

    private static void DontDestroyOnLoadPostfix(object __0)
    {
        if (__0 == null) return;
        var owner = Callers.FindModAssembly();
        if (owner == null) return;
        if (!Persistent.TryGetValue(owner, out var list)) Persistent[owner] = list = new List<object>();
        list.Add(__0);
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
                if (!IsAlive(obj)) continue; // already destroyed
                var go = As(obj, U.GameObject) ?? (As(obj, U.Component) is { } c ? U.ComponentGameObject.GetValue(c, null) : null);
                if (go == null) continue;
                U.Destroy.Invoke(null, new[] { go });
                n++;
            }
            catch (Exception e) { log.Warning(assemblyName + ": could not destroy a persistent object: " + (e.InnerException ?? e).Message); }
        }
        return n;
    }

    /// <summary>Mono: the old build's Component / ScriptableObject subclasses, whose instances Unity drives directly.</summary>
    public static IEnumerable<Type> ComponentTypesIn(Assembly asm)
    {
        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        var u = U;
        foreach (var t in types)
            if (t != null && !t.ContainsGenericParameters && (u.Component.IsAssignableFrom(t) || (u.ScriptableObject?.IsAssignableFrom(t) ?? false)))
                yield return t;
    }

    /// <summary>
    /// Destroys every live Unity object of the old build's classes: injected Il2Cpp classes, or on Mono its Component
    /// and ScriptableObject subclasses. Their GameObjects stay unless the mod also kept them across scenes.
    /// </summary>
    public static int DestroyInstancesOf(IEnumerable<Type> types, MelonLogger.Instance log)
    {
        int n = 0;
        var u = U;
        var il2cpp = Il2Cpp;
        foreach (var t in types)
        {
            if (!u.Object.IsAssignableFrom(t)) continue;
            try
            {
                var typeArg = il2cpp != null ? il2cpp.TypeFrom.Invoke(null, new object[] { t }) : t;
                if (u.FindObjectsOfTypeAll.Invoke(null, new[] { typeArg }) is not IEnumerable found) continue;
                foreach (var obj in found.Cast<object>().ToList())
                {
                    if (!IsAlive(obj)) continue;
                    u.Destroy.Invoke(null, new[] { obj });
                    n++;
                }
            }
            catch (Exception e) { log.Warning("Could not destroy instances of " + t.FullName + ": " + (e.InnerException ?? e).Message); }
        }
        return n;
    }
}
