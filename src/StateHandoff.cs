using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Opt-in runtime state handoff across a reload, by convention (no reference to HotReload needed):
/// <code>
///   // in the melon class, public or private:
///   object OnHotReloadSaveState()             // old build, called before it is unregistered
///   void   OnHotReloadRestoreState(object s)  // new build, called after its OnInitializeMelon
/// </code>
/// Melons are matched by MelonInfo name. The old and new builds are different assemblies, so the state must consist of
/// types both know: primitives, string, arrays, List/Dictionary of those, byte[] (or a JSON string). An object of the
/// old build's own classes would not cast to the new build's classes.
/// </summary>
internal static class StateHandoff
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static Dictionary<string, object?> Save(IEnumerable<MelonBase> oldMelons, MelonLogger.Instance log)
    {
        var saved = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var melon in oldMelons)
        {
            var m = melon.GetType().GetMethod("OnHotReloadSaveState", Any, null, Type.EmptyTypes, null);
            if (m == null) continue;
            try
            {
                var state = m.Invoke(melon, null);
                if (state != null && state.GetType().Assembly == melon.GetType().Assembly)
                    log.Warning(melon.Info.Name + ": OnHotReloadSaveState returned a " + state.GetType().FullName +
                                ", a type of the old build; the new build cannot cast it. Use framework types (Dictionary, string, ...).");
                saved[melon.Info.Name] = state;
            }
            catch (TargetInvocationException e) { log.Warning(melon.Info.Name + ": OnHotReloadSaveState failed: " + e.InnerException?.Message); }
        }
        return saved;
    }

    public static void Restore(IEnumerable<MelonBase> newMelons, Dictionary<string, object?> saved, MelonLogger.Instance log)
    {
        if (saved.Count == 0) return;
        foreach (var melon in newMelons)
        {
            if (!saved.TryGetValue(melon.Info.Name, out var state)) continue;
            var m = melon.GetType().GetMethod("OnHotReloadRestoreState", Any, null, new[] { typeof(object) }, null);
            if (m == null)
            {
                log.Warning(melon.Info.Name + ": the old build saved state but the new build has no OnHotReloadRestoreState(object); state dropped.");
                continue;
            }
            try { m.Invoke(melon, new[] { state }); }
            catch (TargetInvocationException e) { log.Warning(melon.Info.Name + ": OnHotReloadRestoreState failed: " + e.InnerException?.Message); }
        }
    }
}
