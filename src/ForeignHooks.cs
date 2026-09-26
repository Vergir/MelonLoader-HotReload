using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Hooks made outside Harmony: MonoMod.RuntimeDetour (Hook, Detour, ILHook, NativeDetour) and MelonLoader's
/// NativeHook&lt;T&gt;. HotReload cannot enumerate them, so it disposes the ones the old build keeps in its fields
/// (FieldScan), and Retirer leaves the methods handed to a hook constructor alone: a hook HotReload could not find keeps
/// calling working old code instead of a retired handler that returns defaults.
/// </summary>
internal static class ForeignHooks
{
    /// <summary>A MonoMod.RuntimeDetour type (Hook, Detour, ILHook, NativeDetour, IDetour) or MelonLoader's NativeHook&lt;T&gt;.</summary>
    internal static bool IsHookType(Type t)
    {
        for (var b = t; b != null && b != typeof(object); b = b.BaseType)
        {
            if (b.Namespace == "MonoMod.RuntimeDetour") return true;
            if (b.IsGenericType && b.GetGenericTypeDefinition().FullName == "MelonLoader.NativeUtils.NativeHook`1") return true;
        }
        foreach (var i in SafeInterfaces(t))
            if (i.Namespace == "MonoMod.RuntimeDetour") return true;
        return false;
    }

    private static Type[] SafeInterfaces(Type t)
    {
        try { return t.GetInterfaces(); } catch { return Type.EmptyTypes; }
    }

    /// <summary>Disposes (MonoMod) or detaches (NativeHook) the hooks the old build keeps in its fields. Returns how many.</summary>
    public static int DisposeHeld(string name, ICollection<Assembly> oldAssemblies, ICollection<MelonBase> oldMelons, MelonLogger.Instance log)
    {
        int n = 0;
        foreach (var hook in FieldScan.Held(oldAssemblies, oldMelons, IsHookType, o => IsHookType(o.GetType())))
        {
            try
            {
                var detach = hook.GetType().GetMethod("Detach", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (detach != null) detach.Invoke(hook, null);          // NativeHook<T>
                else if (hook is IDisposable d) d.Dispose();           // MonoMod: undoes the detour and frees it
                else continue;
                n++;
            }
            catch (Exception e) { log.Warning(name + ": could not dispose a " + hook.GetType().Name + ": " + (e.InnerException ?? e).Message); }
        }
        return n;
    }
}
