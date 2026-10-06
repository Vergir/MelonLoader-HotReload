using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HotReload;

/// <summary>
/// For mods that want more than "it just works": load, reload or unload DLLs on demand and hear about reloads.
/// Nothing here is needed for a mod to reload. Reach it by reflection so the mod also runs without HotReload:
/// <code>
///   var api = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HotReload")?.GetType("HotReload.Api");
///   api?.GetMethod("Queue", new[] { typeof(string[]) })?.Invoke(null, new object[] { paths });
/// </code>
/// Event handlers from a build that is reloaded or unloaded are removed by HotReload.
/// </summary>
public static class Api
{
    private static HotReloadPlugin? _plugin;

    internal static void Attach(HotReloadPlugin plugin) => _plugin = plugin;

    /// <summary>HotReload's version, e.g. "1.2.0".</summary>
    public static string Version => VersionInfo.Version;

    /// <summary>True once HotReload has started (from the first OnInitializeMelon on).</summary>
    public static bool IsRunning => _plugin != null;

    /// <summary>Whether DLL changes are picked up on their own (setting AutoReload). When false, only the reload key and this API act.</summary>
    public static bool AutoReload => _plugin?.AutoReloadOn ?? false;

    /// <summary>True while files of a batch are still waiting (see <see cref="Queue(string[])"/>).</summary>
    public static bool Busy => _plugin?.Busy ?? false;

    /// <summary>
    /// Loads, reloads or unloads the DLL at <paramref name="path"/> now, on the calling (main) thread. A path whose file
    /// is gone unloads the mod loaded from it. Returns "Reloaded" (also for a first load), "Unloaded", "Unchanged",
    /// "Skipped", "NotReady" (unreadable for now) or "Failed".
    /// </summary>
    public static string Process(string path) =>
        _plugin == null ? "Skipped" : _plugin.ProcessNow(path).ToString();

    /// <summary>
    /// Adds files to the current batch: they are processed in the order MelonLoader would load them at startup
    /// (dependencies, [MelonPriority], name), deleted files first, one reload or unload per frame. <see cref="BatchFinished"/>
    /// fires when the batch is done. Faster than waiting for the file watcher's debounce.
    /// </summary>
    public static void Queue(string[] paths) => _plugin?.Enqueue(paths ?? Array.Empty<string>(), fromKey: false);

    /// <summary>A melon assembly or library was loaded into the running game (a reload or a new DLL): its assembly name.</summary>
    public static event Action<string>? Reloaded;

    /// <summary>A mod was unloaded because its DLL went away: its assembly name.</summary>
    public static event Action<string>? Unloaded;

    /// <summary>The current batch is done: every file that was due has been processed.</summary>
    public static event Action? BatchFinished;

    internal static void RaiseReloaded(string name) => Raise(Reloaded, name);
    internal static void RaiseUnloaded(string name) => Raise(Unloaded, name);
    internal static void RaiseBatchFinished()
    {
        foreach (var d in BatchFinished?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action)d)(); }
            catch (Exception e) { HotReloadPlugin.Log?.Warning("A BatchFinished handler failed: " + e.GetBaseException().Message); }
        }
    }

    private static void Raise(Action<string>? handlers, string name)
    {
        foreach (var d in handlers?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<string>)d)(name); }
            catch (Exception e) { HotReloadPlugin.Log?.Warning("A HotReload event handler failed: " + e.GetBaseException().Message); }
        }
    }

    /// <summary>Drops handlers declared in assemblies that are being reloaded or unloaded. Returns how many.</summary>
    internal static int RemoveHandlersFrom(ICollection<Assembly> assemblies)
    {
        int n = 0;
        Reloaded = Without(Reloaded, assemblies, ref n);
        Unloaded = Without(Unloaded, assemblies, ref n);
        BatchFinished = Without(BatchFinished, assemblies, ref n);
        return n;
    }

    private static T? Without<T>(T? handlers, ICollection<Assembly> assemblies, ref int removed) where T : Delegate
    {
        if (handlers == null) return null;
        var keep = handlers.GetInvocationList().Where(d => d.Method.DeclaringType == null || !assemblies.Contains(d.Method.DeclaringType.Assembly)).ToArray();
        removed += handlers.GetInvocationList().Length - keep.Length;
        return keep.Length == 0 ? null : (T?)Delegate.Combine(keep);
    }
}
