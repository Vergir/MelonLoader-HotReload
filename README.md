# HotReload

MelonLoader plugin for mod authors: rebuild a mod and the running game picks it up without a relaunch, like
[AutoReload](https://github.com/Hamunii/AutoReload) does for BepInEx 5. Works in any IL2CPP Unity game.

| | |
|---|---|
| Games | IL2CPP only. MelonLoader refuses to load it in Mono games, where it would not work. |
| MelonLoader | 0.6.0 or newer. Every API it uses exists in the 0.6.0-0.7.3 release binaries. Shadow-copying `Mods/` needs 0.7.1+; on older versions mod DLLs stay locked and builds need the rename-then-copy deploy step below. |
| Tested in-game | MelonLoader 0.7.3 in No Rest for the Wicked (Unity 6000.1, .NET 6.0.16), 2026-09-26. |

At startup it logs what it could enable, e.g.
`MelonLoader 0.7.3, .NET 6.0.16. Shadow copy: on; Assembly.Location patch: on; DontDestroyOnLoad tracking: on; reload key: F8 (legacy Input).`
Each feature degrades on its own: if a MelonLoader internal is missing, that feature is switched off with a warning.

## Use

1. Put `HotReload.dll` in `<game>/Plugins` (not `Mods`: MelonLoader skips a plugin there with a "wrong folder" warning).
2. Start the game, then build a mod and copy its DLL (and `.pdb`) into `Mods/`. A plain copy works while the game runs.
   HotReload sees the change and reloads it:
   `[HotReload] Reloaded X 1.0.0 -> 1.0.1 (Harmony: 6 method(s) unpatched, 6 patched; replayed 4 scene(s)) in 110 ms`.
3. **F8** reloads every mod whose DLL changed, for when auto reload is off or a file event was missed.

Also handled: deleting a mod DLL from `Mods/` unloads that mod, a new mod DLL dropped into `Mods/` is loaded, and mods that
reference a reloaded mod are reloaded after it.

## Config: `<game>/UserData/HotReload.toml`

Created on first launch. Edits apply immediately, except `ShadowCopyMods`.

| Key | Default | Meaning |
|---|---|---|
| `AutoReload` | `true` | Reload as soon as a DLL changes. `false` = only the reload key. |
| `ReloadKey` | `"F8"` | Any [`UnityEngine.KeyCode`](https://docs.unity3d.com/ScriptReference/KeyCode.html) name; `"None"` disables it. |
| `ExtraWatchPaths` | `[]` | More folders (every `*.dll`) or DLL files to watch, e.g. a project's `bin/Release`. Relative to the game folder. |
| `Ignore` | `["UnityExplorer.ML.IL2CPP.CoreCLR"]` | Assembly names never reloaded. |
| `DebounceMs` | `500` | Quiet time after the last file event before reloading. |
| `ShadowCopyMods` | `true` | MelonLoader loads `Mods/*.dll` from copies in `UserData/HotReload/Shadow`, so the originals are never locked. Restart to apply. |
| `ReplaySceneEvents` | `true` | After a reload, call the mod's `OnSceneWasLoaded` / `OnSceneWasInitialized` for scenes already open. |
| `ReloadDependents` | `true` | After a mod reloads, reload the loaded mods that reference it. |
| `RetireOldBuild` | `true` | Turn the old build's delegate targets and coroutine/async steps into no-ops after a reload (see below). |
| `DestroyPersistentObjects` | `true` | Destroy the GameObjects the old build passed to `DontDestroyOnLoad`. |
| `InputBackend` | `"Auto"` | How the reload key is read. `Auto` tries legacy `UnityEngine.Input`, then the Input System package (by reflection, for games that disabled legacy input), then the Windows key state while the game has focus (also under Wine/Proton). Or force `Legacy`, `InputSystem` or `Windows`. |

## What a reload does

1. `MelonAssembly.UnregisterMelons`: the mod's `OnDeinitializeMelon` runs, MelonLoader callbacks are unsubscribed, and its
   `HarmonyInstance` is unpatched.
2. Every remaining Harmony patch whose patch method lives in the old assembly is removed, whatever Harmony instance made it.
   Objects the old build kept across scenes are destroyed, and the old build is retired (next section).
3. The mod's preference categories are saved and released, so the new build can create them again with the saved values.
   They are found through the old build's own fields (the categories and entries it keeps), reflective `CreateCategory<T>`
   categories typed with its classes, and categories named like the mod.
4. The new DLL is loaded from bytes, with its `.pdb`, into its own `AssemblyLoadContext`. `Assembly.Location` reports the
   `Mods/` path.
5. Its melons are registered: `OnEarlyInitializeMelon`, Harmony auto-patching, `OnInitializeMelon`, `OnLateInitializeMelon`.
6. Scene callbacks are replayed for the scenes already open.
7. Loaded mods that reference it are reloaded the same way, so they call the new build.

## Retiring the old build

Unregistering stops MelonLoader callbacks and Harmony patches, but the game can still call old code it was handed:
UI listeners, settings rows, Il2Cpp delegates, coroutines, timers, tasks, event handlers. After a reload HotReload patches
every such entry point of the old build with a prefix that skips the body and returns the default value:

* every method the old build turned into a delegate, found by scanning its IL for `ldftn` / `ldvirtftn`;
* every iterator and async state machine `MoveNext`, so coroutines end on their next step and pending async work stops.

Stale behaviour stops instead of running old code against state that is gone. A leftover settings row or button does
nothing until the game rebuilds that UI (reopen the screen). Typical mods have 0-15 such methods; large UI mods a few hundred.
Retiring is skipped, with a log line, if a mod that references the old build is not being reloaded with it.

Objects the old build passed to `DontDestroyOnLoad` (UI roots, canvases, EventSystems) are destroyed. HotReload learns
the owner from the managed call stack when `DontDestroyOnLoad` is called; the game itself calls it natively, so only mods are tracked.

## Rules for mods that should hot-reload

Only one thing is off-limits:

* **Il2Cpp class injection**: `ClassInjector.RegisterTypeInIl2Cpp`, `[RegisterTypeInIl2Cpp]`, or classes deriving from
  `MonoBehaviour` and other Il2Cpp types. Il2CppInterop refuses a second type with the same full name.

What HotReload cannot undo, so the mod must in `OnDeinitializeMelon`:

* **AssetBundles**: loading the same bundle twice fails, so `Unload` it.
* **Hooks made outside Harmony** (MonoMod `Hook`/`Detour`, native hooks): dispose them. With retiring on, a hook left in
  place would call a retired handler and the hooked function would return defaults.
* **Loops on threads the mod started itself**: a call that is already running finishes; signal it to stop.
* **Game state the mod changed**, such as static fields: re-apply it in `OnInitializeMelon`, because the new build starts
  with fresh static state.

Other limits: old assemblies stay in memory, about the DLL's size per reload. `MelonPlugin`s and DLLs in `Mods/` subfolders or
`UserLibs` are not reloaded. HotReload cannot reload itself.

## Checking existing mods: `checker/`

`HotReloadCheck` reads compiled mod DLLs, with no source and no game needed, and reports whether HotReload can reload them
and what the author would have to add. It only reads metadata: which APIs a mod calls, which types it defines, which
callbacks it overrides.

```bash
dotnet build checker -c Release
dotnet checker/bin/Release/net8.0/HotReloadCheck.dll <dll-or-folder>... --md report.md --csv report.csv
```

| Verdict | Meaning |
|---|---|
| READY | Nothing found that HotReload cannot clean up. |
| REVIEW | Uses something HotReload cannot clean up, but has `OnDeinitializeMelon`; check that it undoes it. |
| NEEDS CLEANUP | Uses something HotReload cannot clean up and has no `OnDeinitializeMelon`. |
| BLOCKED | Uses Il2Cpp class injection. |
| PLUGIN | A `MelonPlugin`; not reloaded. |
| UNSUPPORTED | Built for a Mono game, or for MelonLoader 0.5 (Unhollower). |

A static scan cannot tell whether `OnDeinitializeMelon` undoes everything, and misses behaviour hidden behind reflection or
obfuscation. REVIEW means "read the cleanup code or try it".

## Test mods: `tests/`

`HRTestBase` uses a separate Harmony instance, a reflective preference category, scene callbacks, a `DontDestroyOnLoad`
object, an endless coroutine and a timer, and cleans up none of them. `HRTestDependent`
references it. Build `tests/HRTestDependent` to deploy both, then rebuild `HRTestBase` with `-p:Version=1.0.1` while the game runs.

## Building

HotReload and the test mods compile against the MelonLoader and Il2Cpp interop assemblies of an installed game. Point the
build at one (MelonLoader installed and the game started once), in any of these ways:

* copy `Local.props.example` to `Local.props` (git-ignored) and set `GameDir`;
* set the `MELONLOADER_GAME_DIR` environment variable;
* `dotnet build -c Release -p:GameDir="C:\path\to\game"`.

The build copies the DLL into `<game>/Plugins` (`-p:DeployToGame=false` to skip). The published DLL works in other
IL2CPP games too: interop assemblies are bound by name at runtime.

## How it works (MelonLoader internals it relies on)

MelonLoader marks its own assembly with `[PatchShield]` (every release since 0.6.0): Harmony patches on MelonLoader's
methods are silently skipped. So HotReload never patches MelonLoader. It changes MelonLoader's data, calls its public API,
and patches only the game, the runtime and the mods.

| What | Why |
|---|---|
| HotReload is a `MelonPlugin` | It registers before MelonLoader scans `Mods/`, so the shadow copy is in place before any mod loads. No Unity type may appear in its fields; see `UnityApi.cs`. |
| Shadow copy by editing `MelonFolderHandler._modDirs` (0.7.1+) | MelonLoader loads mods with `LoadFromAssemblyPath`, which locks the file, and PatchShield rules out redirecting that call. The folder list is plain data read later. |
| Remove the old `MelonAssembly` from the internal `loadedAssemblies` list | `LoadMelonAssembly(path, assembly)` returns the cached entry with the same `FullName`. |
| One `AssemblyLoadContext` per reload | The default context refuses a second assembly with the same name. |
| `MelonBase.RegisterSorted` | `LoadMelons` only creates melons. |
| Preference categories found through the old build's fields | `CreateEntry` throws on duplicates. Recording who creates a category would need a hook on MelonLoader, which PatchShield blocks. |
| Reflective categories matched by their private `SystemType` | `CreateCategory<T>` makes a new category on every call. |
| Postfix on `RuntimeAssembly.Location` | Assemblies loaded from bytes report an empty location. |
| Postfix on `UnityEngine.Object.DontDestroyOnLoad` | Records which mod kept an object across scenes (managed call stack). |
| Own watcher for `HotReload.toml` | MelonLoader's preferences watcher misses rename-style saves and swallows the first change after a save. |

A MelonLoader update that renames `loadedAssemblies`, `_modDirs` or `SystemType` switches off the matching feature with a
warning.

Source: `src/HotReloadPlugin.cs` (config, watchers), `src/Reloader.cs` (unload/load/dependents/Harmony cleanup),
`src/StartupLoader.cs` (shadow copy, `Assembly.Location`), `src/Retirer.cs`, `src/PrefOwnership.cs`, `src/Callers.cs`,
`src/KeyInput.cs` (reload key backends), `src/UnityApi.cs` (Unity calls, `DontDestroyOnLoad` tracking).
