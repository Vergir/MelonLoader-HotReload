# HotReload

MelonLoader plugin: rebuild a mod and the running game picks it up without a relaunch, like
[AutoReload](https://github.com/Hamunii/AutoReload) does for BepInEx 5. Made for No Rest for the Wicked; nothing in it is
game-specific except the `MelonGame` attribute. Tested in-game with MelonLoader 0.7.3 on 2026-09-26.

## Use

1. Put `HotReload.dll` in `<game>/Plugins` (not `Mods`). `dotnet build -c Release` here does that.
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

## What a reload does

1. `MelonAssembly.UnregisterMelons`: the mod's `OnDeinitializeMelon` runs, MelonLoader callbacks are unsubscribed, and its
   `HarmonyInstance` is unpatched.
2. Every remaining Harmony patch whose patch method lives in the old assembly is removed, whatever Harmony instance made it.
3. The mod's preference categories are saved and released, so the new build can create them again with the saved values.
   This covers plain categories and reflective `CreateCategory<T>` ones.
4. The new DLL is loaded from bytes, with its `.pdb`, into its own `AssemblyLoadContext`. `Assembly.Location` reports the
   `Mods/` path.
5. Its melons are registered: `OnEarlyInitializeMelon`, Harmony auto-patching, `OnInitializeMelon`, `OnLateInitializeMelon`.
6. Scene callbacks are replayed for the scenes already open.
7. Loaded mods that reference it are reloaded the same way, so they call the new build.

## Rules for mods that should hot-reload

Only one thing is truly off-limits:

* **Il2Cpp class injection**: `ClassInjector.RegisterTypeInIl2Cpp`, `[RegisterTypeInIl2Cpp]`, or classes deriving from
  `MonoBehaviour` and other Il2Cpp types. Il2CppInterop refuses a second type with the same full name.

Everything below keeps working after a reload but keeps running the **old** code unless the mod undoes it in `OnDeinitializeMelon`:

* GameObjects and components it created, `DontDestroyOnLoad` objects.
* `MelonCoroutines.Start`: keep the token and `MelonCoroutines.Stop` it.
* AssetBundles: loading the same bundle twice fails, so `Unload` it.
* Callbacks handed to the game: `AddListener`, Il2Cpp delegates, event subscriptions, settings rows. The game may also rebuild that UI itself.
* Hooks made outside Harmony: MonoMod `Hook`/`Detour`, native hooks.
* Threads, timers, tasks, `FileSystemWatcher`, `AppDomain` events.
* Game state the mod changed, such as static fields. Re-apply it in `OnInitializeMelon` instead, because the new build starts
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
| READY | Nothing found that outlives a reload. |
| REVIEW | Creates things that outlive a reload but has `OnDeinitializeMelon`; check that it undoes them. |
| NEEDS CLEANUP | Creates things that outlive a reload and has no `OnDeinitializeMelon`. |
| BLOCKED | Uses Il2Cpp class injection. |
| PLUGIN | A `MelonPlugin`; not reloaded. |
| UNSUPPORTED | Built for a Mono game, or for MelonLoader 0.5 (Unhollower). |

A static scan cannot tell whether `OnDeinitializeMelon` undoes everything, and misses behaviour hidden behind reflection or
obfuscation. REVIEW means "read the cleanup code or try it".

## Test mods: `tests/`

`HRTestBase` uses a separate Harmony instance, a reflective preference category and scene callbacks. `HRTestDependent`
references it. Build `tests/HRTestDependent` to deploy both, then rebuild `HRTestBase` with `-p:Version=1.0.1` while the game runs.

## How it works (MelonLoader 0.7.3 internals it relies on)

| What | Why |
|---|---|
| HotReload is a `MelonPlugin` | It registers before MelonLoader scans `Mods/`, so the shadow copy and the preference hooks are in place before any mod runs. No Unity type may appear in its fields; see `UnityApi.cs`. |
| Shadow copy by editing `MelonFolderHandler._modDirs` | MelonLoader loads mods with `LoadFromAssemblyPath`, which locks the file. A Harmony prefix on `MelonAssembly.LoadMelonAssembly(string, bool)` never fires: the game runs on the installed .NET 10 runtime, whose JIT inlines that method into the folder loader, compiled for UserLibs before any plugin exists. The folder list is plain data read later. |
| Remove the old `MelonAssembly` from the internal `loadedAssemblies` list | `LoadMelonAssembly(path, assembly)` returns the cached entry with the same `FullName`. |
| One `AssemblyLoadContext` per reload | The default context refuses a second assembly with the same name. |
| `MelonBase.RegisterSorted` | `LoadMelons` only creates melons. |
| Postfixes on every `CreateCategory` overload and the category constructor | `CreateEntry` throws on duplicates, so HotReload records which assembly created each category. The short overloads are compiled with the long one inlined before plugins load, so one hook is not enough. |
| Reflective categories matched by their private `SystemType` | `CreateCategory<T>` makes a new category on every call. |
| Postfix on `RuntimeAssembly.Location` | Assemblies loaded from bytes report an empty location. |
| Own watcher for `HotReload.toml` | MelonLoader's preferences watcher misses rename-style saves and swallows the first change after a save. |

A MelonLoader update that renames `loadedAssemblies`, `_modDirs` or `SystemType` breaks the matching feature. HotReload logs a
warning when it cannot find them.

Source: `src/HotReloadPlugin.cs` (config, watchers, key), `src/Reloader.cs` (unload/load/dependents/Harmony cleanup),
`src/StartupLoader.cs` (shadow copy, `Assembly.Location`), `src/PrefOwnership.cs`, `src/UnityApi.cs`.
