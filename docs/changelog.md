# Changelog

## 1.2.0

Fixes:

* **Startup crash in IL2CPP games** (`coreclr.dll`, right after `Loading Mods`, mostly on the first start of the day).
  The `Assembly.Location` patch was installed before MelonLoader loaded the mods, and .NET's tiered compiler crashed while
  recompiling the many callers of `Location` during loading. It is now installed once the mods are loaded. A mod that
  reads its own `Location` in `OnEarlyInitializeMelon` sees the shadow copy's path. New setting `PatchAssemblyLocation`.
* Several DLLs changing at once (a solution build, a tool switching mods) loaded in the same frame, freezing the game
  for seconds, in no particular order. They now load in MelonLoader's startup order (dependencies, `[MelonPriority]`,
  name), one per frame, removed DLLs first.
* The reload key did nothing while another mod blocked `UnityEngine.Input` (for example while its window was open).
  HotReload now reads the key around such patches.
* A mod unloaded (DLL removed) and loaded again counted as a new mod and lost its handed-over state. It now counts as a
  reload: `LoadingLate = "reload"`, state handed over.
* Unity error lines named only the latest reload; they now also name the other reloads and unloads of the last 30 s.

New:

* Textures, materials, meshes, sprites and ScriptableObjects the old build created are destroyed (`DestroyOldAssets`),
  and optionally its GameObjects (`DestroyOldGameObjects`, off by default).
* MonoMod hooks (`Hook`, `Detour`, `ILHook`) are disposed even when the mod did not keep them in a field.
* A mod with `OnApplicationQuit` but no `OnDeinitializeMelon` gets its `OnApplicationQuit` called before a reload
  (`CallQuitOnUnload`), so mods that save only on quit lose nothing.
* Force-reload key (`ForceReloadKey`, `LeftShift+F8`): reloads the mods of the last reload with the same bytes.
* A DLL renamed into place loads after 100 ms instead of waiting for `DebounceMs`.
* `ReplayActiveSceneOnly`: replay scene callbacks for the active scene only.
* `HotReload.Unloading` AppDomain flag (`"reload"` / `"unload"`, unset at game quit) for `OnDeinitializeMelon`.
* `HotReload.Api`: load, reload, unload and batch DLLs on demand, and events for reloads, unloads and finished batches.
* HotReloadCheck: new findings for quit-only cleanup, patches on `UnityEngine.Input`, created assets and GameObjects,
  `Assembly.Location`, UI Toolkit and global Unity state; reworded timers finding.

## 1.1.1

* HotReloadCheck ships without the `HotReloadCheck.exe` launcher; run it with `dotnet HotReloadCheck.dll`. The
  unsigned launcher stub from the .NET SDK was flagged by antivirus scanners.
* Reproducible builds: a Release build of a tag with the same .NET SDK gives byte-identical DLLs. The release notes list
  the SDK, the commit and the SHA256 of every file in the zips ([how to check](building.md#reproducing-a-release)).
* HotReload itself is unchanged apart from the build settings.

## 1.1.0

* Unity errors and exceptions that appear after a reload are copied into the MelonLoader log, once per reload with a
  repeat count and the reload they followed. Before, exceptions thrown inside game code reached only `Player.log`.
  Setting `EchoUnityErrors` (`AfterReload`, `Always`, `Off`); left to MelonLoader when its `capture_player_logs` is on.
* `HotReload.LoadingLate` AppDomain flag: `"reload"` or `"new"` while HotReload loads a melon into a running game, so a
  mod can skip once-per-launch work without referencing HotReload.
* Guide: a plain copy into `Mods/` is enough; moving the old DLL aside is only needed for plugins and libraries.

## 1.0.0

Initial release.
