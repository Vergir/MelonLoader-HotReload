using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HotReload;
using MelonLoader;
using MelonLoader.Utils;

[assembly: MelonInfo(typeof(HotReloadPlugin), "HotReload", VersionInfo.Version, "vergir", VersionInfo.DownloadLink)]
// One DLL for every Unity game, Mono or IL2CPP: net472, compiled against MelonLoader's net35 assemblies, with the runtime
// differences decided at runtime (Compat.IsMono). Every API it binds to exists since MelonLoader 0.6.0; shadow-copying
// Mods/ needs 0.7.1+.
[assembly: MelonGame(null, null)]
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.UNIVERSAL)]
[assembly: VerifyLoaderVersion(0, 6, 0, true)]
// A plugin (Plugins/ folder) registers before any mod is loaded, which the shadow copy of Mods/ needs.
[assembly: MelonPriority(-10000)]
// No [HarmonyPatch] classes; hooks are applied by hand.
[assembly: HarmonyDontPatchAll]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HotReload.Tests")]

namespace HotReload;

/// <summary>
/// Dev tool in the spirit of BepInEx AutoReload: watches Mods/ (plus optional extra paths) and hot-reloads a mod
/// when its DLL changes. The reload key (F8 by default) reloads every changed DLL on demand.
/// Settings live in UserData/HotReload.toml and are re-read when that file is saved.
/// Unity is reached only through UnityApi (reflection), from OnInitializeMelon on.
/// </summary>
public class HotReloadPlugin : MelonPlugin
{
    private const string ConfigFileName = "HotReload.toml";
    private const string OldFileSuffix = ".hotreload-old";

    private MelonPreferences_Category _cat = null!;
    private MelonPreferences_Entry<bool> _autoReload = null!;
    private MelonPreferences_Entry<string> _reloadKey = null!;
    private MelonPreferences_Entry<string[]> _extraWatchPaths = null!;
    private MelonPreferences_Entry<string[]> _ignore = null!;
    private MelonPreferences_Entry<int> _debounceMs = null!;
    private MelonPreferences_Entry<bool> _shadowCopy = null!;
    private MelonPreferences_Entry<bool> _replayScenes = null!;
    private MelonPreferences_Entry<bool> _reloadDependents = null!;
    private MelonPreferences_Entry<bool> _retireOldBuild = null!;
    private MelonPreferences_Entry<bool> _destroyPersistent = null!;
    private MelonPreferences_Entry<bool> _freshLibraries = null!;
    private MelonPreferences_Entry<bool> _patchLocation = null!;
    private MelonPreferences_Entry<bool> _replayActiveSceneOnly = null!;
    private MelonPreferences_Entry<bool> _callQuitOnUnload = null!;
    private MelonPreferences_Entry<bool> _destroyAssets = null!;
    private MelonPreferences_Entry<bool> _destroyGameObjects = null!;

    private Reloader _reloader = null!;
    private KeyInput _keys = null!;
    private KeyInput _forceKeys = null!;
    private MelonPreferences_Entry<string> _forceReloadKey = null!;
    private MelonPreferences_Entry<string> _inputBackend = null!;
    private MelonPreferences_Entry<string> _echoUnityErrors = null!;
    private ErrorEcho _errorEcho = null!;
    private string _unityErrorsStatus = "off";
    private bool _locationPatched;
    private bool _hookTracking;
    private string _shadowStatus = "off";
    private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
    // Written by watcher threads, drained on the main thread: full path -> when it is due (UTC).
    private readonly ConcurrentDictionary<string, DateTime> _pending = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _retries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private volatile int _debounce = 500;
    // Files due for processing, in load order: one reload or unload per frame, so a batch does not freeze the game.
    private readonly List<string> _work = new List<string>();
    private bool _batchOpen, _batchFromKey;
    private int _batchActions;
    private volatile bool _configDirty;
    private FileSystemWatcher? _configWatcher;
    private long _configChangedTicks; // UTC ticks of the last change to HotReload.toml, 0 = none pending
    private volatile bool _watcherFailed;

    /// <summary>For code that has no logger handed to it.</summary>
    internal static MelonLogger.Instance? Log { get; private set; }

    public override void OnEarlyInitializeMelon()
    {
        Log = LoggerInstance;
        // Runs while plugins register, before MelonLoader loads any mod.
        CreatePreferences();
        // Each feature degrades on its own: a missing MelonLoader internal disables that feature with a warning.
        if (_shadowCopy.Value) _shadowStatus = StartupLoader.InstallShadowMods(LoggerInstance);
        _hookTracking = ForeignHooks.InstallTracking(LoggerInstance); // before mods load, which may make hooks
    }

    public override void OnInitializeMelon()
    {
        // Runs at application start, before any mod's OnInitializeMelon: every mod is loaded and Unity types are usable.
        // The Location patch waits until here: patched while MelonLoader loads mods (and calls Assembly.Location in a
        // burst), it crashed .NET's tiered JIT on cold starts. The shadow mapping is recorded from the start either way.
        if (_patchLocation.Value) _locationPatched = StartupLoader.InstallLocationPatch(HarmonyInstance, LoggerInstance);
        bool ddol = UnityApi.InstallPersistentObjectTracking(HarmonyInstance, LoggerInstance);
        int bundleHooks = AssetBundles.InstallTracking(HarmonyInstance, LoggerInstance);
        int createHooks = CreatedObjects.InstallTracking(HarmonyInstance, () => _destroyGameObjects.Value, LoggerInstance);
        _keys = new KeyInput(LoggerInstance);
        _forceKeys = new KeyInput(LoggerInstance, "Force-reload key");
        _errorEcho = new ErrorEcho(LoggerInstance.Warning);
        _unityErrorsStatus = UnityErrors.Install(_errorEcho);
        WatchConfigFile();
        _reloader = new Reloader(LoggerInstance, typeof(HotReloadPlugin).Assembly.GetName().Name!, IsIgnored, new ReloadOptions
        {
            ReplayScenes = () => _replayScenes.Value, ReplayActiveSceneOnly = () => _replayActiveSceneOnly.Value,
            ReloadDependents = () => _reloadDependents.Value, RetireOldBuild = () => _retireOldBuild.Value,
            DestroyOld = () => _destroyPersistent.Value, DestroyOldAssets = () => _destroyAssets.Value, DestroyOldGameObjects = () => _destroyGameObjects.Value,
            FreshLibraries = () => _freshLibraries.Value, CallQuitOnUnload = () => _callQuitOnUnload.Value,
        });
        _reloader.AfterReload = what => _errorEcho.Reloaded(what, DateTime.UtcNow);
        CleanupOldFiles();
        StartupLoader.FixAllLocations();
        ApplyConfig();
        _reloader.Snapshot(WatchedFiles());
        LoggerInstance.Msg("MelonLoader " + LoaderVersion() + ", " + Compat.RuntimeDescription() + (Compat.IsMono ? " (Mono game)" : " (IL2CPP game)") + ". Shadow copy: " + _shadowStatus
                           + "; Assembly.Location patch: " + (_locationPatched ? "on" : _patchLocation.Value ? "failed" : "off")
                           + "; DontDestroyOnLoad tracking: " + (ddol ? "on" : "off")
                           + "; AssetBundle tracking: " + (bundleHooks > 0 ? bundleHooks + " load method(s)" : "fields only")
                           + "; created-object tracking: " + createHooks + " method(s)"
                           + "; MonoMod hook tracking: " + (_hookTracking ? "on" : "off") + ReinjectionStatus() + "; Unity errors: " + UnityErrorsDescription() + "; reload key: " + _keys.Describe() + ".");
        if (_shadowCopy.Value) LoggerInstance.Msg(StartupLoader.Describe(_patchLocation.Value));
        Api.Attach(this);
    }

    internal bool AutoReloadOn => _autoReload.Value;
    internal bool Busy => _work.Count > 0 || !_pending.IsEmpty;
    internal Reloader.Result ProcessNow(string path) => _reloader.ProcessFile(path);

    private void CreatePreferences()
    {
        _cat = MelonPreferences.CreateCategory("HotReload", "Hot Reload");
        _cat.SetFilePath(Path.Combine(MelonEnvironment.UserDataDirectory, ConfigFileName), autoload: true, printmsg: false);
        _autoReload = _cat.CreateEntry("AutoReload", true,
            description: "Reload a mod as soon as its DLL changes (like BepInEx AutoReload). When false, only the reload key reloads.");
        _reloadKey = _cat.CreateEntry("ReloadKey", "F8",
            description: "Key that reloads every mod whose DLL changed since it was loaded. UnityEngine.KeyCode name (F8, F9, Insert, ...); \"None\" disables it.");
        _forceReloadKey = _cat.CreateEntry("ForceReloadKey", "LeftShift+F8",
            description: "Key that reloads the mods of the last reload even when their DLL is unchanged (every mod if nothing was reloaded yet), to test unload code without a new build. \"None\" disables it.");
        _extraWatchPaths = _cat.CreateEntry("ExtraWatchPaths", Array.Empty<string>(),
            description: "Extra folders (every *.dll inside) or single DLL files to watch besides Mods/, e.g. a project's bin/Release folder.");
        _ignore = _cat.CreateEntry("Ignore", Array.Empty<string>(),
            description: "Assembly names (file name without .dll) that are never reloaded.");
        _debounceMs = _cat.CreateEntry("DebounceMs", 500,
            description: "Wait this long after the last file change before reloading, so a build finishes writing first.");
        _shadowCopy = _cat.CreateEntry("ShadowCopyMods", true,
            description: "Load Mods/*.dll from copies in UserData/HotReload/Shadow so builds can overwrite the originals while the game runs. Restart to apply.");
        _patchLocation = _cat.CreateEntry("PatchAssemblyLocation", true,
            description: "Make Assembly.Location of a shadow-copied or reloaded mod report its path in Mods/ (a Harmony patch on the runtime, installed once mods are loaded). " +
                         "When false, mods that read their own Location see the copy's path. Restart to apply.");
        _replayScenes = _cat.CreateEntry("ReplaySceneEvents", true,
            description: "After a reload, call the mod's OnSceneWasLoaded/OnSceneWasInitialized for the scenes that are already open.");
        _replayActiveSceneOnly = _cat.CreateEntry("ReplayActiveSceneOnly", false,
            description: "Replay the scene callbacks for the active scene only, not for every open scene. For games that stream their world as many additive scenes.");
        _callQuitOnUnload = _cat.CreateEntry("CallQuitOnUnload", true,
            description: "Before taking a mod down, call its OnApplicationQuit if it has one but no OnDeinitializeMelon, so mods that save or clean up only on quit do so on a reload too.");
        _reloadDependents = _cat.CreateEntry("ReloadDependents", true,
            description: "When a mod reloads, also reload the loaded mods that reference it, so they call its new build.");
        _retireOldBuild = _cat.CreateEntry("RetireOldBuild", true,
            description: "After a reload, turn the old build's delegate targets and coroutine/async steps into no-ops, so callbacks, coroutines and timers it left behind stop instead of running old code.");
        _destroyPersistent = _cat.CreateEntry("DestroyOldObjects", true,
            description: "After a reload, destroy the GameObjects the old build passed to DontDestroyOnLoad (UI roots, canvases, EventSystems) and live instances of the old build's injected Il2Cpp classes.");
        _destroyAssets = _cat.CreateEntry("DestroyOldAssets", true,
            description: "With DestroyOldObjects: also destroy the textures, render textures, materials, meshes, sprites and ScriptableObjects the old build created in code. " +
                         "One it handed to the game (an icon registered once) stays blank until the new build sets it again.");
        _destroyGameObjects = _cat.CreateEntry("DestroyOldGameObjects", false,
            description: "With DestroyOldObjects: also destroy every GameObject the old build created with new GameObject(...) that still exists, wherever it was attached. " +
                         "Off by default: most mods remove their own objects, and some hand them to the game.");
        _freshLibraries = _cat.CreateEntry("FreshLibraries", true,
            description: "Reload the UserLibs libraries a mod uses together with it (only libraries that reference MelonLoader, Il2CppInterop or Unity), so state the old build registered with them is gone. Mods sharing such a library reload too.");
        _inputBackend = _cat.CreateEntry("InputBackend", "Auto",
            description: "How the reload key is read: Auto (legacy Input, then Input System, then Windows key state), Legacy, InputSystem or Windows.");
        _echoUnityErrors = _cat.CreateEntry("EchoUnityErrors", "AfterReload",
            description: "Copy Unity errors and exceptions (normally only in Player.log) into this log, deduplicated, with the reload they followed: AfterReload (errors that are new since a reload), Always, or Off.");
        _cat.SaveToFile(false); // writes the file with defaults and descriptions on first run
    }

    public override void OnPreferencesLoaded(string filepath)
    {
        // Fired (possibly off the main thread) when MelonLoader re-reads a preferences file, e.g. after the user edits HotReload.toml.
        if (_cat != null && filepath != null && filepath.EndsWith(ConfigFileName, StringComparison.OrdinalIgnoreCase))
            _configDirty = true;
    }

    public override void OnUpdate()
    {
        long changed = System.Threading.Interlocked.Read(ref _configChangedTicks);
        if (changed != 0 && DateTime.UtcNow.Ticks - changed > TimeSpan.TicksPerMillisecond * 300)
        {
            System.Threading.Interlocked.Exchange(ref _configChangedTicks, 0);
            try { _cat.LoadFromFile(false); _configDirty = true; }
            catch (Exception e) { LoggerInstance.Warning("Could not re-read " + ConfigFileName + ": " + e.Message); }
        }
        if (_configDirty) { _configDirty = false; ApplyConfig(); }
        if (_watcherFailed) { _watcherFailed = false; LoggerInstance.Warning("File watcher overflowed or failed; restarting it. Press " + _keys.KeyName + " if a change was missed."); ApplyConfig(force: true); }

        _errorEcho.Flush(DateTime.UtcNow);

        _debounce = Math.Max(50, _debounceMs.Value);

        KeyInput.NextFrame();
        bool force = _forceKeys.Pressed(), normal = _keys.Pressed(); // both read every frame; the longer chord wins
        if (force)
        {
            var targets = _reloader.ForceTargets();
            LoggerInstance.Msg(_forceKeys.KeyName + ": reloading " + (targets.Count == 0 ? "nothing (no mod loaded from a watched folder)" : string.Join(", ", targets.Select(Path.GetFileNameWithoutExtension).ToArray())));
            Enqueue(targets, fromKey: true);
        }
        else if (normal)
        {
            LoggerInstance.Msg(_keys.KeyName + ": checking watched DLLs");
            // A changed DLL, a retry of one that failed (its hash was forgotten), or a mod whose DLL was removed.
            Enqueue(WatchedFiles().Concat(_reloader.MissingSources()), fromKey: true);
        }

        if (!_pending.IsEmpty)
        {
            var now = DateTime.UtcNow;
            var due = new List<string>();
            foreach (var kv in _pending.ToArray())
            {
                if (now < kv.Value) continue;
                if (!_pending.TryRemove(kv.Key, out var stamp)) continue;
                if (stamp != kv.Value) { _pending[kv.Key] = stamp; continue; } // a newer event arrived meanwhile
                due.Add(kv.Key);
            }
            if (due.Count > 0) Enqueue(due, fromKey: false);
        }

        StepWork();
    }

    /// <summary>Adds files to the current batch and puts the whole batch in load order again.</summary>
    internal void Enqueue(IEnumerable<string> paths, bool fromKey)
    {
        var planned = _reloader.PlanBatch(_work.Concat(paths));
        _work.Clear();
        _work.AddRange(planned);
        _batchOpen |= fromKey || _work.Count > 0;
        _batchFromKey |= fromKey;
    }

    /// <summary>Works through the batch: files that need nothing are passed over, and at most one reload or unload runs per frame.</summary>
    private void StepWork()
    {
        while (_work.Count > 0)
        {
            var path = _work[0];
            _work.RemoveAt(0);
            var result = _reloader.ProcessFile(path);
            if (result == Reloader.Result.NotReady)
            {
                // Still being written or unreadable: retry for a few seconds, then give up until the next change.
                _retries.TryGetValue(path, out int r);
                if (r < 10) { _retries[path] = r + 1; _pending[path] = DateTime.UtcNow.AddMilliseconds(_debounce); }
                else { _retries.Remove(path); LoggerInstance.Warning("Gave up on " + path + " (unreadable or not a .NET assembly)."); }
                continue;
            }
            _retries.Remove(path);
            if (result is Reloader.Result.Reloaded or Reloader.Result.Failed or Reloader.Result.Unloaded) // a retry that failed was still an attempt
            {
                _batchActions++;
                break;
            }
        }
        if (_work.Count > 0 || !_batchOpen) return;
        if (_batchFromKey && _batchActions == 0) LoggerInstance.Msg("Nothing changed.");
        _batchOpen = _batchFromKey = false;
        _batchActions = 0;
        Api.RaiseBatchFinished();
    }

    /// <summary>
    /// MelonLoader's own preferences watcher misses editors that save by replacing the file (a rename) and skips the first
    /// change after any save, so HotReload watches its config file itself.
    /// </summary>
    private void WatchConfigFile()
    {
        try
        {
            _configWatcher = new FileSystemWatcher(MelonEnvironment.UserDataDirectory, ConfigFileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler onChange = (_, __) => System.Threading.Interlocked.Exchange(ref _configChangedTicks, DateTime.UtcNow.Ticks);
            _configWatcher.Changed += onChange;
            _configWatcher.Created += onChange;
            _configWatcher.Renamed += (_, __) => System.Threading.Interlocked.Exchange(ref _configChangedTicks, DateTime.UtcNow.Ticks);
            _configWatcher.EnableRaisingEvents = true;
        }
        catch (Exception e)
        {
            LoggerInstance.Warning("Cannot watch " + ConfigFileName + " (edits apply after a restart): " + e.Message);
        }
    }

    private bool IsIgnored(string assemblyName) =>
        _ignore.Value != null && _ignore.Value.Any(i => string.Equals(i?.Trim(), assemblyName, StringComparison.OrdinalIgnoreCase));

    private string _appliedConfig = "";

    private void ApplyConfig(bool force = false)
    {
        // MelonLoader re-reads the file after every save (ours included); only act on real changes.
        var signature = string.Join("|", _autoReload.Value, _reloadKey.Value, _forceReloadKey.Value, _inputBackend.Value, _echoUnityErrors.Value, string.Join(";", _extraWatchPaths.Value ?? Array.Empty<string>()));
        if (!force && signature == _appliedConfig) return;
        _appliedConfig = signature;

        if (!ErrorEcho.TryParse(_echoUnityErrors.Value, out var echoMode))
            LoggerInstance.Warning("EchoUnityErrors = \"" + _echoUnityErrors.Value + "\" is not AfterReload, Always or Off; using AfterReload.");
        _errorEcho.Current = echoMode;

        _keys.Configure(_reloadKey.Value ?? "F8", _inputBackend.Value ?? "Auto");
        _forceKeys.Configure(_forceReloadKey.Value ?? "None", _inputBackend.Value ?? "Auto");

        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        _pending.Clear();
        if (_autoReload.Value)
        {
            foreach (var (dir, filter) in WatchTargets())
            {
                try
                {
                    var w = new FileSystemWatcher(dir, filter)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                        InternalBufferSize = 64 * 1024,
                    };
                    w.Changed += (_, e) => Queue(e.FullPath);
                    w.Created += (_, e) => Queue(e.FullPath);
                    w.Deleted += (_, e) => Queue(e.FullPath);
                    w.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath, renamedIn: true); };
                    w.Error += (_, __) => _watcherFailed = true;
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception e)
                {
                    LoggerInstance.Warning("Cannot watch " + Path.Combine(dir, filter) + ": " + e.Message);
                }
            }
        }

        LoggerInstance.Msg("Auto reload " + (_autoReload.Value ? "on (" + string.Join(", ", WatchTargets().Select(t => Path.Combine(t.dir, t.filter))) + ")" : "off")
                           + ", reload key " + _keys.Describe() + ". Config: UserData/" + ConfigFileName);
    }

    /// <summary>
    /// Marks a file as changed. It is due once no event has come for the debounce time, so a build has finished writing.
    /// A file renamed into place is complete at once and is due almost immediately; the short wait lets a pdb follow.
    /// A removed file keeps the full debounce, so "move the old DLL aside, copy the new one" stays one reload.
    /// </summary>
    private void Queue(string fullPath, bool renamedIn = false)
    {
        if (!fullPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return;
        int wait = renamedIn ? Math.Min(100, _debounce) : _debounce;
        _pending[fullPath] = DateTime.UtcNow.AddMilliseconds(wait);
    }

    /// <summary>Directories + file filters to watch: Mods/*.dll and each extra path.</summary>
    private IEnumerable<(string dir, string filter)> WatchTargets()
    {
        // Mods/ and its manifest subfolders, Plugins/ (other plugins), UserLibs/ (libraries).
        foreach (var dir in LoaderFolders.MelonFolders().Append(LoaderFolders.UserLibs).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(dir)) yield return (dir, "*.dll");
        foreach (var raw in _extraWatchPaths.Value ?? Array.Empty<string>())
        {
            var p = (raw ?? "").Trim().Trim('"');
            if (p.Length == 0) continue;
            p = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(MelonEnvironment.GameRootDirectory, p));
            if (Directory.Exists(p)) yield return (p, "*.dll");
            else if (p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.GetDirectoryName(p)))
                yield return (Path.GetDirectoryName(p)!, Path.GetFileName(p));
            else LoggerInstance.Warning("ExtraWatchPaths entry not found: " + p);
        }
    }

    private IEnumerable<string> WatchedFiles()
    {
        foreach (var (dir, filter) in WatchTargets())
            foreach (var f in Directory.EnumerateFiles(dir, filter, SearchOption.TopDirectoryOnly))
                yield return f;
    }

    /// <summary>The deploy step renames a locked DLL to *.hotreload-old; nothing holds those after a restart.</summary>
    private void CleanupOldFiles()
    {
        try
        {
            foreach (var dir in LoaderFolders.MelonFolders().Append(LoaderFolders.UserLibs).Where(Directory.Exists))
                foreach (var f in Directory.EnumerateFiles(dir, "*" + OldFileSuffix))
                {
                    try { File.Delete(f); } catch { /* still locked by something else; next launch */ }
                }
        }
        catch (Exception e) { LoggerInstance.Warning("Cleanup of *" + OldFileSuffix + " failed: " + e.Message); }
    }

    /// <summary>MelonLoader's version: a const field in 0.6, a static property in 0.7.</summary>
    private static string LoaderVersion()
    {
        var info = typeof(MelonAssembly).Assembly.GetType("MelonLoader.Properties.BuildInfo");
        object? v = info?.GetProperty("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    ?? info?.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return v?.ToString() ?? typeof(MelonAssembly).Assembly.GetName().Version?.ToString() ?? "?";
    }

    private string UnityErrorsDescription() =>
        _unityErrorsStatus != "on" ? _unityErrorsStatus
        : _errorEcho.Current == ErrorEcho.Mode.AfterReload ? "echoed after a reload"
        : _errorEcho.Current == ErrorEcho.Mode.Always ? "echoed" : "not echoed (EchoUnityErrors = Off)";

    private static string ReinjectionStatus() =>
        Compat.IsMono ? "" : "; class re-injection: " + (InjectedTypes.Supported ? "on" : "off")
                             + "; load contexts: " + (LoadContexts.Problem == null ? "on" : "off (" + LoadContexts.Problem + ")");
}
