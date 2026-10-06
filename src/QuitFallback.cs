using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Many mods save or clean up only in OnApplicationQuit, which a reload never reaches: MelonLoader calls only
/// OnDeinitializeMelon when a melon is unregistered. For a melon that overrides OnApplicationQuit but not
/// OnDeinitializeMelon, HotReload calls OnApplicationQuit before taking it down (setting CallQuitOnUnload).
/// </summary>
internal static class QuitFallback
{
    public static void Run(IEnumerable<MelonBase> melons, MelonLogger.Instance log)
    {
        foreach (var melon in melons)
        {
            if (!Wants(melon.GetType())) continue;
            try
            {
                melon.OnApplicationQuit();
                log.Msg(melon.Info.Name + ": called its OnApplicationQuit (it has no OnDeinitializeMelon) before unloading it.");
            }
            catch (Exception e) { log.Warning(melon.Info.Name + ": OnApplicationQuit during hot reload failed: " + e); }
        }
    }

    /// <summary>True when the melon type overrides OnApplicationQuit but not OnDeinitializeMelon.</summary>
    internal static bool Wants(Type melonType) =>
        Overrides(melonType, nameof(MelonBase.OnApplicationQuit)) && !Overrides(melonType, nameof(MelonBase.OnDeinitializeMelon));

    private static bool Overrides(Type type, string method)
    {
        var m = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
        return m?.DeclaringType != null && m.DeclaringType.Assembly != typeof(MelonBase).Assembly;
    }
}
