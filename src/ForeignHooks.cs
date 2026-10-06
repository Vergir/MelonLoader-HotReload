using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Hooks made outside Harmony: MonoMod.RuntimeDetour (Hook, Detour, ILHook, NativeDetour) and MelonLoader's
/// NativeHook&lt;T&gt;. HotReload disposes the ones the old build keeps in its fields (FieldScan), then the MonoMod
/// detours and IL hooks whose handler lives in the old build, found in MonoMod's own registry. Retirer leaves the methods
/// handed to a hook constructor alone: a hook HotReload could not find (a NativeDetour or NativeHook not kept in a
/// field) keeps calling working old code instead of a retired handler that returns defaults.
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

    // ---- MonoMod hooks recorded as they are made ----------------------------------------------------------------
    // MonoMod raises Hook.OnDetour / Detour.OnDetour / ILHook.OnDetour for every hook it applies, with the handler method.
    // A Hook wraps its handler in a generated method, so the registry below cannot tell whose it is; recorded here, it can.

    private static readonly List<(object hook, Assembly owner)> Made = new List<(object, Assembly)>();

    /// <summary>Subscribes to MonoMod's OnDetour callbacks. Call before mods load. Returns false when MonoMod is not there.</summary>
    public static bool InstallTracking(MelonLogger.Instance log)
    {
        int n = 0;
        n += Subscribe("MonoMod.RuntimeDetour.Hook", nameof(OnHook));
        n += Subscribe("MonoMod.RuntimeDetour.Detour", nameof(OnDetour));
        n += Subscribe("MonoMod.RuntimeDetour.ILHook", nameof(OnILHook));
        return n > 0;

        int Subscribe(string typeName, string handler)
        {
            try
            {
                var t = Type.GetType(typeName + ", MonoMod.RuntimeDetour", false) ?? FindType(typeName);
                var field = t?.GetField("OnDetour", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null) return 0;
                var mine = Delegate.CreateDelegate(field.FieldType, typeof(ForeignHooks).GetMethod(handler, BindingFlags.Static | BindingFlags.NonPublic)!);
                field.SetValue(null, Delegate.Combine((Delegate?)field.GetValue(null), mine));
                return 1;
            }
            catch (Exception e) { log.Msg("Not tracking " + typeName + ": " + e.GetBaseException().Message); return 0; }
        }
    }

    // Parameter types are base types of MonoMod's (Hook, Detour, ILHook, ILContext.Manipulator): delegate contravariance.
    private static bool OnHook(object hook, MethodBase from, MethodBase to, object target) { Record(hook, to); return true; }
    private static bool OnDetour(object detour, MethodBase from, MethodBase to) { Record(detour, to); return true; }
    private static bool OnILHook(object hook, MethodBase from, Delegate manipulator) { Record(hook, manipulator?.Method); return true; }

    private static void Record(object hook, MethodBase? handler)
    {
        var owner = handler?.DeclaringType?.Assembly;
        if (owner == null || owner.IsDynamic) return; // a generated wrapper: its Hook is recorded with the real handler
        lock (Made) Made.Add((hook, owner));
    }

    /// <summary>Disposes the hooks MonoMod made with a handler in the old build. Returns how many.</summary>
    public static int DisposeMade(string name, ICollection<Assembly> oldAssemblies, MelonLogger.Instance log)
    {
        List<object> mine;
        lock (Made)
        {
            mine = Made.Where(m => oldAssemblies.Contains(m.owner)).Select(m => m.hook).ToList();
            Made.RemoveAll(m => oldAssemblies.Contains(m.owner));
        }
        int n = 0;
        foreach (var hook in mine)
        {
            try
            {
                if (hook.GetType().GetProperty("IsApplied")?.GetValue(hook, null) is false) continue; // disposed already (kept in a field)
                ((IDisposable)hook).Dispose();
                n++;
            }
            catch (Exception e) { log.Warning(name + ": could not dispose a " + hook.GetType().Name + ": " + (e.InnerException ?? e).Message); }
        }
        return n;
    }

    /// <summary>
    /// MonoMod.RuntimeDetour (22.x, as MelonLoader ships it) keeps every applied Detour in <c>Detour._DetourMap</c> and
    /// every ILHook in <c>ILHook._Map</c>. A Hook applies a Detour whose Target is the handler; an ILHook has a
    /// manipulator delegate. Those that point into the old build are disposed. Returns how many.
    /// </summary>
    public static int DisposeRegistered(string name, ICollection<Assembly> oldAssemblies, MelonLogger.Instance log)
    {
        int n = 0;
        var detour = FindType("MonoMod.RuntimeDetour.Detour");
        var ilHook = FindType("MonoMod.RuntimeDetour.ILHook");
        if (detour != null)
        {
            var target = detour.GetField("Target", BindingFlags.Public | BindingFlags.Instance);
            foreach (var d in Registered(detour, "_DetourMap"))
                if (target?.GetValue(d) is MethodBase m && Owned(m)) n += Dispose(d);
        }
        if (ilHook != null)
        {
            var manipulator = ilHook.GetField("Manipulator", BindingFlags.Public | BindingFlags.Instance);
            foreach (var h in Registered(ilHook, "_Map"))
                if (manipulator?.GetValue(h) is Delegate del && Owned(del.Method)) n += Dispose(h);
        }
        return n;

        bool Owned(MethodBase m) => m.DeclaringType != null && oldAssemblies.Contains(m.DeclaringType.Assembly);

        int Dispose(object hook)
        {
            try { ((IDisposable)hook).Dispose(); return 1; }
            catch (Exception e) { log.Warning(name + ": could not dispose a registered " + hook.GetType().Name + ": " + (e.InnerException ?? e).Message); return 0; }
        }
    }

    /// <summary>The detours in a static registry dictionary: values are List&lt;Detour&gt;, or ILHook contexts with a Chain list.</summary>
    private static List<object> Registered(Type owner, string mapField)
    {
        var found = new List<object>();
        try
        {
            if (owner.GetField(mapField, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is not System.Collections.IDictionary map) return found;
            lock (map)
            {
                foreach (var value in map.Values)
                {
                    var list = value as System.Collections.IEnumerable
                               ?? value?.GetType().GetField("Chain", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(value) as System.Collections.IEnumerable;
                    if (list == null) continue;
                    foreach (var d in list) if (d != null && owner.IsInstanceOfType(d)) found.Add(d);
                }
            }
        }
        catch { /* another MonoMod version: only field-held hooks are disposed */ }
        return found;
    }

    private static Type? FindType(string fullName)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType(fullName, false);
            if (t != null) return t;
        }
        return null;
    }
}
