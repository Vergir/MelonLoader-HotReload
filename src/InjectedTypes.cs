#if !MONO
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Lets a reloaded build inject Il2Cpp classes (MonoBehaviours etc.) with the same names as the old build.
/// Il2CppInterop refuses that twice over: ClassInjector.InjectedTypes (a set of full names) throws "already injected",
/// and InjectorHelpers.s_ClassNameLookup (namespace, name, image) -> class, which backs il2cpp_class_from_name,
/// throws on a duplicate key. Both are plain collections, so after the old build is retired HotReload removes its
/// names from them. The old Il2Cpp classes stay alive for the old instances; those are destroyed (UnityApi) and their
/// methods retired, and name lookups resolve to the new build's classes.
/// </summary>
internal static class InjectedTypes
{
    private static readonly FieldInfo? InjectedNamesField =
        typeof(ClassInjector).GetField("InjectedTypes", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo? ClassNameLookupField =
        typeof(ClassInjector).Assembly.GetType("Il2CppInterop.Runtime.Injection.InjectorHelpers")
            ?.GetField("s_ClassNameLookup", BindingFlags.NonPublic | BindingFlags.Static);

    /// <summary>Whether this Il2CppInterop exposes both registries; if not, re-injection fails as before.</summary>
    public static bool Supported => InjectedNamesField?.GetValue(null) is HashSet<string> && ClassNameLookupField?.GetValue(null) is System.Collections.IDictionary;

    /// <summary>Types of <paramref name="asm"/> that were injected into Il2Cpp.</summary>
    public static List<Type> InjectedIn(Assembly asm)
    {
        var names = InjectedNamesField?.GetValue(null) as HashSet<string>;
        var result = new List<Type>();
        Type?[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        foreach (var t in types)
        {
            if (t == null || !typeof(Il2CppObjectBase).IsAssignableFrom(t) || t.ContainsGenericParameters) continue;
            IntPtr ptr;
            try { ptr = Il2CppClassPointerStore.GetNativeClassPointer(t); } catch { continue; }
            if (ptr == IntPtr.Zero) continue;
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
        var names = InjectedNamesField?.GetValue(null) as HashSet<string>;
        var lookup = ClassNameLookupField?.GetValue(null) as System.Collections.IDictionary;
        if (names == null || lookup == null)
        {
            log.Warning("This Il2CppInterop has no ClassInjector.InjectedTypes / InjectorHelpers.s_ClassNameLookup; the new build cannot re-inject its classes.");
            return 0;
        }
        int n = 0;
        foreach (var t in oldTypes)
        {
            var ptr = Il2CppClassPointerStore.GetNativeClassPointer(t);
            lock (names) { if (t.FullName != null && names.Remove(t.FullName)) n++; }
            lock (lookup)
            {
                var keys = new List<object>();
                foreach (System.Collections.DictionaryEntry e in lookup)
                    if (e.Value is IntPtr p && p == ptr) keys.Add(e.Key);
                foreach (var k in keys) lookup.Remove(k);
            }
        }
        return n;
    }
}
#endif
