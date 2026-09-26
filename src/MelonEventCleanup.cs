using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Removes MelonLoader event subscriptions (MelonEvents.OnUpdate, OnSceneWasLoaded, MelonPreferences.OnPreferencesLoaded,
/// ...) whose method belongs to an old build. Unregistering a melon only removes its own callbacks, and only if it was
/// registered: MelonLoader subscribes a melon's callbacks before OnEarlyInitializeMelon and leaves them in place when
/// that throws, so a build that failed to register keeps getting OnUpdate. Subscriptions a mod made by hand
/// (MelonEvents.OnUpdate.Subscribe(...)) are never removed by MelonLoader either.
/// Uses public API only (MelonEventBase&lt;T&gt;.GetSubscribers / Unsubscribe(MethodInfo, object)), reached by
/// reflection because the events have many generic shapes.
/// </summary>
internal static class MelonEventCleanup
{
    private static List<object>? _events;

    /// <summary>Every static MelonEventBase&lt;T&gt; field or property in MelonLoader.</summary>
    private static List<object> Events()
    {
        if (_events != null) return _events;
        _events = new List<object>();
        Type?[] types;
        try { types = typeof(MelonBase).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types; }
        const BindingFlags statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var t in types)
        {
            if (t == null || t.ContainsGenericParameters) continue;
            foreach (var f in t.GetFields(statics))
                if (IsEvent(f.FieldType) && Get(() => f.GetValue(null)) is { } v) _events.Add(v);
            foreach (var p in t.GetProperties(statics))
                if (p.GetIndexParameters().Length == 0 && IsEvent(p.PropertyType) && Get(() => p.GetValue(null, null)) is { } v) _events.Add(v);
        }
        _events = _events.Distinct().ToList();
        return _events;
    }

    private static object? Get(Func<object?> read)
    {
        try { return read(); } catch { return null; }
    }

    private static bool IsEvent(Type t)
    {
        for (var b = t; b != null && b != typeof(object); b = b.BaseType)
            if (b.IsGenericType && b.GetGenericTypeDefinition().FullName == "MelonLoader.MelonEventBase`1") return true;
        return false;
    }

    /// <summary>Unsubscribes every handler declared in <paramref name="oldAssemblies"/>. Returns how many were removed.</summary>
    public static int Remove(ICollection<Assembly> oldAssemblies, MelonLogger.Instance log) =>
        oldAssemblies.Count == 0 ? 0 : Remove(d => d.Method.DeclaringType?.Assembly is { } a && oldAssemblies.Contains(a), log);

    /// <summary>Unsubscribes the callbacks of melons that failed to register (MelonLoader leaves them subscribed).</summary>
    public static int RemoveCallbacksOf(ICollection<MelonBase> failedMelons, MelonLogger.Instance log) =>
        failedMelons.Count == 0 ? 0 : Remove(d => d.Target is MelonBase m && failedMelons.Contains(m), log);

    private static int Remove(Func<Delegate, bool> matches, MelonLogger.Instance log)
    {
        int removed = 0;
        foreach (var ev in Events())
        {
            try
            {
                var type = ev.GetType();
                var getSubscribers = type.GetMethod("GetSubscribers", Type.EmptyTypes);
                var unsubscribe = type.GetMethod("Unsubscribe", new[] { typeof(MethodInfo), typeof(object) });
                if (getSubscribers == null || unsubscribe == null) continue;
                if (getSubscribers.Invoke(ev, null) is not IEnumerable subscribers) continue;
                foreach (var sub in subscribers.Cast<object>().ToList())
                {
                    if (sub.GetType().GetField("del")?.GetValue(sub) is not Delegate del || !matches(del)) continue;
                    unsubscribe.Invoke(ev, new[] { del.Method, del.Target });
                    removed++;
                }
            }
            catch (Exception e) { log.Warning("Could not clean up a MelonLoader event: " + (e.InnerException ?? e).Message); }
        }
        return removed;
    }
}
