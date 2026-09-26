using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace HotReload;

/// <summary>
/// MelonPreferences_Category.CreateEntry throws when the entry already exists, so a reloaded mod would crash in its
/// OnInitializeMelon. We record which assembly created each category (postfix on MelonPreferences.CreateCategory) and,
/// before a reload, save that mod's categories and empty their entry lists. The values stay in the preferences file,
/// so the new build's CreateEntry picks them up again.
/// </summary>
internal static class PrefOwnership
{
    // Category identifier -> simple name of the assembly that created it first. Another mod that merely looks the
    // category up via CreateCategory does not take it over, so reloading that mod leaves the category alone.
    private static readonly Dictionary<string, string> OwnerOf = new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly FieldInfo? ReflectiveTypeField =
        typeof(MelonLoader.Preferences.MelonPreferences_ReflectiveCategory).GetField("SystemType", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        // Every overload plus the constructor: MelonLoader calls CreateCategory for its own categories before any mod loads,
        // so the short overloads are already JIT-compiled with the long one inlined, and a patch on one overload is not enough.
        var targets = new List<MethodBase>();
        targets.AddRange(typeof(MelonPreferences).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(MelonPreferences.CreateCategory) && !m.IsGenericMethodDefinition && m.ReturnType == typeof(MelonPreferences_Category)));
        targets.AddRange(typeof(MelonPreferences_Category).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        int ok = 0;
        foreach (var t in targets)
        {
            try
            {
                var postfix = t is ConstructorInfo ? nameof(CtorPostfix) : nameof(Postfix);
                harmony.Patch(t, postfix: new HarmonyMethod(typeof(PrefOwnership).GetMethod(postfix, BindingFlags.Static | BindingFlags.NonPublic)));
                ok++;
            }
            catch (Exception e)
            {
                log.Warning("Could not hook " + t.DeclaringType?.Name + "." + t.Name + ": " + e.Message);
            }
        }
        if (ok == 0) log.Warning("No preference hooks; reloading a mod that creates preference entries falls back to matching category names.");
    }

    private static void CtorPostfix(MelonPreferences_Category __instance) => Postfix(__instance);

    private static void Postfix(MelonPreferences_Category __result)
    {
        if (__result == null) return;
        var owner = Callers.FindModAssembly();
        if (owner == null) return;
        if (__result.Identifier != null && !OwnerOf.ContainsKey(__result.Identifier)) OwnerOf[__result.Identifier] = owner;
    }


    /// <summary>
    /// Saves and empties the preference categories created by <paramref name="assemblyName"/>. Falls back to categories named
    /// like the assembly or one of its melons when no ownership was recorded.
    /// </summary>
    public static void ReleaseCategories(string assemblyName, IEnumerable<string> melonNames, MelonLogger.Instance log)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in OwnerOf)
            if (string.Equals(kv.Value, assemblyName, StringComparison.OrdinalIgnoreCase)) ids.Add(kv.Key);
        if (ids.Count == 0)
        {
            foreach (var cat in MelonPreferences.Categories)
                if (string.Equals(cat.Identifier, assemblyName, StringComparison.OrdinalIgnoreCase) || melonNames.Contains(cat.Identifier, StringComparer.OrdinalIgnoreCase))
                    ids.Add(cat.Identifier);
        }

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
        // Reflective categories (CreateCategory<T>) are never looked up again: MelonLoader constructs a new one on every
        // call, so the old one (typed with the old build's T) would fight the new one over the same file section.
        foreach (var rc in MelonPreferences.ReflectiveCategories.ToList())
        {
            var type = ReflectiveTypeField?.GetValue(rc) as Type;
            if (type == null || !string.Equals(type.Assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)) continue;
            try { rc.SaveToFile(false); }
            catch (Exception e) { log.Warning("Saving preferences of " + rc.Identifier + " failed: " + e.Message); }
            MelonPreferences.ReflectiveCategories.Remove(rc);
            released.Add(rc.Identifier + " (reflective)");
        }
        if (released.Count > 0) log.Msg("Released preference entries of " + string.Join(", ", released));
    }
}
