using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using MelonLoader.Preferences;

namespace HotReload;

/// <summary>
/// MelonPreferences_Category.CreateEntry throws when the entry already exists, so a reloaded mod would crash in its
/// OnInitializeMelon. Before a reload HotReload finds the old build's preference categories, saves them and empties
/// their entry lists; the values stay in the preferences file, so the new build's CreateEntry picks them up again.
///
/// Finding them cannot use Harmony hooks on MelonPreferences: MelonLoader marks its own assembly with [PatchShield],
/// which silently skips every patch on its methods. Instead the categories are found through
///  1. the old build's own fields: static fields of its types and instance fields of its melons that hold a
///     category, an entry, a reflective category, or a collection of those (how mods keep their settings);
///  2. reflective categories (CreateCategory&lt;T&gt;) whose T lives in the old build;
///  3. categories named like the assembly or one of its melons (entries created and never stored).
/// </summary>
internal static class PrefOwnership
{
    private static readonly FieldInfo? ReflectiveTypeField =
        typeof(MelonPreferences_ReflectiveCategory).GetField("SystemType", BindingFlags.Instance | BindingFlags.NonPublic);

    private static int _warnedReflective;

    public static void ReleaseCategories(string assemblyName, ICollection<Assembly> oldAssemblies, ICollection<MelonBase> oldMelons, MelonLogger.Instance log)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var reflective = new HashSet<MelonPreferences_ReflectiveCategory>();

        // 1. What the old build holds in its fields.
        foreach (var value in HeldPreferenceObjects(oldAssemblies, oldMelons))
            Collect(value, ids, reflective, depth: 0);

        // 2. Reflective categories typed with the old build's classes.
        if (ReflectiveTypeField == null && MelonPreferences.ReflectiveCategories.Count > 0 && _warnedReflective++ == 0)
            log.Warning("MelonPreferences_ReflectiveCategory.SystemType not found in this MelonLoader; reflective categories are only found through fields.");
        foreach (var rc in MelonPreferences.ReflectiveCategories)
            if (ReflectiveTypeField?.GetValue(rc) is Type t && oldAssemblies.Contains(t.Assembly)) reflective.Add(rc);

        // 3. Categories named like the mod.
        var names = new HashSet<string>(oldMelons.Select(m => m.Info.Name).Append(assemblyName), StringComparer.OrdinalIgnoreCase);
        foreach (var cat in MelonPreferences.Categories)
            if (names.Contains(cat.Identifier)) ids.Add(cat.Identifier);

        var released = new List<string>();
        foreach (var id in ids)
        {
            var cat = MelonPreferences.GetCategory(id);
            if (cat == null || cat.Entries.Count == 0) continue;
            try { cat.SaveToFile(false); }
            catch (Exception e) { log.Warning("Saving preferences of " + id + " failed: " + e.Message); }
            released.Add(id + " (" + cat.Entries.Count + ")");
            cat.Entries.Clear();
        }
        // Reflective categories are never looked up again: MelonLoader constructs a new one on every CreateCategory<T>
        // call, so the old one (typed with the old build's T) would fight the new one over the same file section.
        foreach (var rc in reflective)
        {
            if (!MelonPreferences.ReflectiveCategories.Contains(rc)) continue;
            try { rc.SaveToFile(false); }
            catch (Exception e) { log.Warning("Saving preferences of " + rc.Identifier + " failed: " + e.Message); }
            MelonPreferences.ReflectiveCategories.Remove(rc);
            released.Add(rc.Identifier + " (reflective)");
        }
        if (released.Count > 0) log.Msg("Released preference entries of " + string.Join(", ", released));
    }

    private static bool IsPreferenceType(Type t)
    {
        if (typeof(MelonPreferences_Category).IsAssignableFrom(t) || typeof(MelonPreferences_Entry).IsAssignableFrom(t)
            || typeof(MelonPreferences_ReflectiveCategory).IsAssignableFrom(t)) return true;
        if (t.IsArray) return IsPreferenceType(t.GetElementType()!);
        if (t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t))
            return t.GetGenericArguments().Any(IsPreferenceType); // List<Entry>, Dictionary<string, Entry>, ...
        return false;
    }

    /// <summary>
    /// Values of the old build's fields that hold preference objects. Only fields whose declared type is a preference
    /// type are read, so the static constructors of unrelated old types are not triggered.
    /// </summary>
    private static IEnumerable<object> HeldPreferenceObjects(ICollection<Assembly> assemblies, ICollection<MelonBase> melons)
    {
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var asm in assemblies)
        {
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            foreach (var type in types)
            {
                if (type == null || type.ContainsGenericParameters) continue;
                FieldInfo[] fields;
                try { fields = type.GetFields(statics); } catch { continue; }
                foreach (var f in fields)
                {
                    if (f.IsLiteral || !IsPreferenceType(f.FieldType)) continue;
                    object? v = null;
                    try { v = f.GetValue(null); } catch { /* type initializer failed */ }
                    if (v != null) yield return v;
                }
            }
        }
        foreach (var melon in melons)
        {
            for (var t = melon.GetType(); t != null && t != typeof(MelonMod) && t != typeof(MelonPlugin) && t != typeof(MelonBase); t = t.BaseType)
            {
                foreach (var f in t.GetFields(instance))
                {
                    if (!IsPreferenceType(f.FieldType)) continue;
                    object? v = null;
                    try { v = f.GetValue(melon); } catch { }
                    if (v != null) yield return v;
                }
            }
        }
    }

    private static void Collect(object value, HashSet<string> ids, HashSet<MelonPreferences_ReflectiveCategory> reflective, int depth)
    {
        switch (value)
        {
            case MelonPreferences_Category c: if (c.Identifier != null) ids.Add(c.Identifier); break;
            case MelonPreferences_Entry e: if (e.Category?.Identifier != null) ids.Add(e.Category.Identifier); break;
            case MelonPreferences_ReflectiveCategory r: reflective.Add(r); break;
            case IDictionary d when depth < 2: foreach (var v in d.Values) if (v != null) Collect(v, ids, reflective, depth + 1); break;
            case IEnumerable list when depth < 2 && value is not string: foreach (var v in list) if (v != null) Collect(v, ids, reflective, depth + 1); break;
        }
    }
}
