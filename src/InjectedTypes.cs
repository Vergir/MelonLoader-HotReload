using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;

namespace HotReload;

/// <summary>
/// IL2CPP: lets a reloaded build inject Il2Cpp classes (MonoBehaviours etc.) with the same names as the old build.
/// Il2CppInterop refuses that twice over: ClassInjector.InjectedTypes (a set of full names) throws "already injected",
/// and InjectorHelpers.s_ClassNameLookup (namespace, name, image) -> class, which backs il2cpp_class_from_name,
/// throws on a duplicate key. Both are plain collections, so after the old build is retired HotReload removes its
/// names from them. The old Il2Cpp classes stay alive for the old instances; those are destroyed (UnityApi) and their
/// methods retired, and name lookups resolve to the new build's classes.
/// Il2CppInterop is reached through reflection (HotReload is one DLL for Mono and IL2CPP); on Mono nothing is found.
/// </summary>
internal static class InjectedTypes
{
    private sealed class Api
    {
        public readonly Type ObjectBase;
        public readonly MethodInfo GetNativeClassPointer;
        public readonly FieldInfo? InjectedNames, ClassNameLookup;

        private Api(Type objectBase, MethodInfo getPtr, FieldInfo? names, FieldInfo? lookup)
        {
            ObjectBase = objectBase; GetNativeClassPointer = getPtr; InjectedNames = names; ClassNameLookup = lookup;
        }

        public static Api? Find()
        {
            if (Compat.IsMono) return null;
            const string asm = "Il2CppInterop.Runtime";
            var objectBase = UnityApi.FindType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase", asm);
            var store = UnityApi.FindType("Il2CppInterop.Runtime.Il2CppClassPointerStore", asm);
            var getPtr = store?.GetMethod("GetNativeClassPointer", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Type) }, null);
            if (objectBase == null || getPtr == null) return null;
            var injector = UnityApi.FindType("Il2CppInterop.Runtime.Injection.ClassInjector", asm);
            var helpers = UnityApi.FindType("Il2CppInterop.Runtime.Injection.InjectorHelpers", asm);
            return new Api(objectBase, getPtr,
                injector?.GetField("InjectedTypes", BindingFlags.NonPublic | BindingFlags.Static),
                helpers?.GetField("s_ClassNameLookup", BindingFlags.NonPublic | BindingFlags.Static));
        }

        public HashSet<string>? Names => InjectedNames?.GetValue(null) as HashSet<string>;
        public IDictionary? Lookup => ClassNameLookup?.GetValue(null) as IDictionary;

        public IntPtr ClassPointer(Type t)
        {
            try { return (IntPtr)GetNativeClassPointer.Invoke(null, new object[] { t })!; }
            catch { return IntPtr.Zero; }
        }
    }

    private static bool _looked;
    private static Api? _api;
    private static Api? A
    {
        get
        {
            if (!_looked) { _looked = true; _api = Api.Find(); }
            return _api;
        }
    }

    /// <summary>Whether this Il2CppInterop exposes both registries; if not, re-injection fails as before.</summary>
    public static bool Supported => A is { } a && a.Names != null && a.Lookup != null;

    /// <summary>Types of <paramref name="asm"/> that were injected into Il2Cpp. Empty on Mono.</summary>
    public static List<Type> InjectedIn(Assembly asm)
    {
        var result = new List<Type>();
        var a = A;
        if (a == null) return result;
        var names = a.Names;
        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        foreach (var t in types)
        {
            if (t == null || !a.ObjectBase.IsAssignableFrom(t) || t.ContainsGenericParameters) continue;
            if (a.ClassPointer(t) == IntPtr.Zero) continue;
            bool injected;
            if (names != null) lock (names) injected = t.FullName != null && names.Contains(t.FullName);
            else injected = true; // cannot tell; a non-injected subclass of an interop type is rare in mods
            if (injected) result.Add(t);
        }
        return result;
    }

    /// <summary>Removes the old types' names from Il2CppInterop's registries. Returns how many types were released.</summary>
    public static int Release(IEnumerable<Type> oldTypes, MelonLogger.Instance log)
    {
        var a = A;
        var names = a?.Names;
        var lookup = a?.Lookup;
        if (a == null || names == null || lookup == null)
        {
            log.Warning("This Il2CppInterop has no ClassInjector.InjectedTypes / InjectorHelpers.s_ClassNameLookup; the new build cannot re-inject its classes.");
            return 0;
        }
        int n = 0;
        foreach (var t in oldTypes)
        {
            var ptr = a.ClassPointer(t);
            lock (names) { if (t.FullName != null && names.Remove(t.FullName)) n++; }
            lock (lookup)
            {
                var keys = new List<object>();
                foreach (DictionaryEntry e in lookup)
                    if (e.Value is IntPtr p && p == ptr) keys.Add(e.Key);
                foreach (var k in keys) lookup.Remove(k);
            }
        }
        return n;
    }
}
