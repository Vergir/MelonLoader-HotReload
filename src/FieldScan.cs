using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Finds objects an old build holds in its fields: static fields of its types and instance fields of its melons, and
/// the items of arrays / collections / dictionaries in them (two levels). Mods keep what they must clean up (asset
/// bundles, hooks) in exactly such fields. Only fields whose declared type can hold a wanted object are read, so the
/// static constructors of unrelated types of the old build do not run.
/// </summary>
internal static class FieldScan
{
    private const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <param name="wantedType">Whether a declared type is (or derives from / implements) a wanted type.</param>
    /// <param name="isWanted">Whether a value found at runtime is wanted.</param>
    public static List<object> Held(IEnumerable<Assembly> assemblies, IEnumerable<MelonBase> melons, Func<Type, bool> wantedType, Func<object, bool> isWanted)
    {
        var found = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        bool CanHold(Type t) => CanHoldWanted(t, wantedType, 0);

        foreach (var asm in assemblies)
        {
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            foreach (var type in types)
            {
                if (type == null || type.ContainsGenericParameters) continue;
                FieldInfo[] fields;
                try { fields = type.GetFields(Statics); } catch { continue; }
                foreach (var f in fields)
                {
                    if (f.IsLiteral || !CanHold(f.FieldType)) continue;
                    object? v = null;
                    try { v = f.GetValue(null); } catch { /* type initializer failed */ }
                    Collect(v, isWanted, found, seen, 0);
                }
            }
        }
        foreach (var melon in melons)
        {
            for (var t = melon.GetType(); t != null && t != typeof(MelonMod) && t != typeof(MelonPlugin) && t != typeof(MelonBase); t = t.BaseType)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(Instance); } catch { continue; }
                foreach (var f in fields)
                {
                    if (!CanHold(f.FieldType)) continue;
                    object? v = null;
                    try { v = f.GetValue(melon); } catch { }
                    Collect(v, isWanted, found, seen, 0);
                }
            }
        }
        return found;
    }

    private static bool CanHoldWanted(Type t, Func<Type, bool> wantedType, int depth)
    {
        if (wantedType(t)) return true;
        if (depth > 2) return false;
        if (t.IsArray) return CanHoldWanted(t.GetElementType()!, wantedType, depth + 1);
        if (t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t))
            return t.GetGenericArguments().Any(a => CanHoldWanted(a, wantedType, depth + 1));
        return false;
    }

    private static void Collect(object? value, Func<object, bool> isWanted, List<object> found, HashSet<object> seen, int depth)
    {
        if (value == null || value is string || !seen.Add(value)) return;
        bool wanted;
        try { wanted = isWanted(value); } catch { wanted = false; }
        if (wanted) { found.Add(value); return; }
        if (depth >= 2) return;
        try
        {
            if (value is IDictionary d) { foreach (var v in d.Values.Cast<object?>().ToList()) Collect(v, isWanted, found, seen, depth + 1); }
            else if (value is IEnumerable list) { foreach (var v in list.Cast<object?>().ToList()) Collect(v, isWanted, found, seen, depth + 1); }
        }
        catch { /* a collection that cannot be enumerated right now */ }
    }

    /// <summary>Reference equality for the seen-set (Il2Cpp wrappers and Unity objects override Equals).</summary>
    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
