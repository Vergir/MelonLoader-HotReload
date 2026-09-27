# HotReload

MelonLoader plugin for mod authors: rebuild a mod and the running game picks it up without a relaunch, like
[AutoReload](https://github.com/Hamunii/AutoReload) does for BepInEx 5. Works in any Unity game, IL2CPP or Mono.

| | |
|---|---|
| Games | IL2CPP and Mono, with one `HotReload.dll` (.NET Framework 4.7.2 API): it runs on the .NET 6 runtime MelonLoader uses in IL2CPP games and on Unity's Mono (Unity 2018.1 or newer, the .NET 4.x scripting runtime). |
| MelonLoader | 0.6.0 or newer. Every API it uses exists in the 0.6.0-0.7.3 release binaries. Shadow-copying `Mods/` needs 0.7.1+; on older versions mod DLLs stay locked and builds need the rename-then-copy deploy step below. |
| Tested in-game (IL2CPP) | MelonLoader 0.7.3 in No Rest for the Wicked (Unity 6000.1, .NET 6.0.16), 2026-09-26: own test mods, MoreAspectRatios, NRftW Item Manager, UnityExplorer 4.13.2 with UniverseLib. |
| Tested in-game (Mono) | MelonLoader 0.7.3 in PEAK (Unity 6000.3, Mono 6.13), 2026-09-26: own test mods (library, Mods subfolder, plugin, MonoBehaviour, state handoff), UnityExplorer 4.13.6 Mono with UniverseLib (reloads as far as it starts in that game). |

At startup it logs what it could enable, e.g.
`MelonLoader 0.7.3, .NET 6.0.16. Shadow copy: on; Assembly.Location patch: on; DontDestroyOnLoad tracking: on; reload key: F8 (legacy Input).`
Each feature degrades on its own: if a MelonLoader internal is missing, that feature is switched off with a warning.

## Use

1. Put `HotReload.dll` in `<game>/Plugins` (not `Mods`: MelonLoader skips a plugin there with a "wrong folder" warning).
2. Start the game, then build a mod and copy its DLL (and `.pdb`) into `Mods/`. A plain copy works while the game runs.
   HotReload sees the change and reloads it:
   `[HotReload] Reloaded X 1.0.0 -> 1.0.1 (Harmony: 6 method(s) unpatched, 6 patched; replayed 4 scene(s)) in 110 ms`.
3. **F8** reloads every mod whose DLL changed, for when auto reload is off or a file event was missed. It only checks
   for changed DLLs; nothing changed means nothing is reloaded. On a Steam Deck, bind a back grip (L4/R4) to F8 in the
   game's Steam Input layout: the grips are invisible to games unless bound, so no game action collides with it.

Watched and reloaded: mods in `Mods/` and its manifest subfolders, other plugins in `Plugins/`, and libraries in `UserLibs/`.
Deleting a mod or plugin DLL unloads it, and a new mod DLL dropped in is loaded. Mods that reference a reloaded assembly
reload with it, and so do the stateful libraries a reloaded mod uses (see below).

`Mods/` DLLs are never locked, so a plain copy works. `Plugins/` and `UserLibs/` are loaded before HotReload, so their DLLs are
locked while the game runs and a build cannot overwrite them. For those, add the project's build folder to
`ExtraWatchPaths` (e.g. `["C:/src/MyPlugin/bin/Release"]`): HotReload then reloads the plugin or library straight from
there, together with everything that depends on it. The copy in `Plugins/` or `UserLibs/` is what the next game start
loads, so deploy there when the game is closed (or move the locked DLL aside first: Windows allows renaming a loaded DLL,
and HotReload deletes `*.hotreload-old` files at the next start).

## Config: `<game>/UserData/HotReload.toml`

Created on first launch. Edits apply immediately, except `ShadowCopyMods`.

| Key | Default | Meaning |
|---|---|---|
| `AutoReload` | `true` | Reload as soon as a DLL changes. `false` = only the reload key. |
| `ReloadKey` | `"F8"` | Any [`UnityEngine.KeyCode`](https://docs.unity3d.com/ScriptReference/KeyCode.html) name, or a chord such as `"LeftControl+F8"` (fires when the last key goes down while the others are held); `"None"` disables it. Gamepad buttons (`JoystickButton0`...) work through legacy input only; avoid a button the game uses on its own. |
| `ExtraWatchPaths` | `[]` | More folders (every `*.dll`) or DLL files to watch, e.g. a project's `bin/Release`. Relative to the game folder. |
| `Ignore` | `[]` | Assembly names never reloaded. |
| `DebounceMs` | `500` | Quiet time after the last file event before reloading. |
| `ShadowCopyMods` | `true` | MelonLoader loads `Mods/*.dll` from copies in `UserData/HotReload/Shadow`, so the originals are never locked. Restart to apply. |
| `ReplaySceneEvents` | `true` | After a reload, call the mod's `OnSceneWasLoaded` / `OnSceneWasInitialized` for scenes already open. |
| `ReloadDependents` | `true` | Reload the loaded mods and libraries that reference a reloaded assembly, in the same group. |
| `FreshLibraries` | `true` | Reload the `UserLibs` libraries a mod uses together with it, so state the old build registered with them is gone. Only libraries that reference MelonLoader, Il2CppInterop or Unity; other mods using the same library reload too. |
| `RetireOldBuild` | `true` | Turn the old build's delegate targets and coroutine/async steps into no-ops after a reload (see below). |
| `DestroyOldObjects` | `true` | Destroy the GameObjects the old build passed to `DontDestroyOnLoad`, and live instances of its injected Il2Cpp classes. |
| `InputBackend` | `"Auto"` | How the reload key is read. `Auto` tries legacy `UnityEngine.Input`, then the Input System package (by reflection, for games that disabled legacy input), then the Windows key state while the game has focus (also under Wine/Proton). Or force `Legacy`, `InputSystem` or `Windows`. |

## What a reload does

A change reloads a **group**: the changed assembly, the loaded mods and libraries that reference it, and (with
`FreshLibraries`) the stateful libraries those mods use. Example from the tests:
`Reloading together: HRTestLib (library used by HRTestBase), HRTestBase (changed), HRTestDependent (references HRTestBase)`.
The group goes down dependents-first and comes up libraries-first, so every mod binds to the fresh libraries. Per assembly:

0. The old build's state is saved if it opts in (see "State handoff").
1. `MelonAssembly.UnregisterMelons`: the mod's `OnDeinitializeMelon` runs, MelonLoader callbacks are unsubscribed, and its
   `HarmonyInstance` is unpatched.
2. Every remaining Harmony patch whose patch method lives in the old assembly is removed, whatever Harmony instance made it,
   and so is every MelonLoader event handler it declares (`MelonEvents.OnUpdate.Subscribe(...)` made by hand, or the
   callbacks of a build that failed to register). Objects the old build kept across scenes and live instances of its injected Il2Cpp classes are destroyed, the old build
   is retired (next section), and its injected class names are released so the new build can inject them again.
3. The mod's preference categories are saved and released, so the new build can create them again with the saved values.
   They are found through the old build's own fields (the categories and entries it keeps), reflective `CreateCategory<T>`
   categories typed with its classes, and categories named like the mod.
4. The new DLL is copied, with its `.pdb`, into this session's shadow folder and loaded from there into its own
   `AssemblyLoadContext`. `Assembly.Location` reports the `Mods/` path.
5. Its melons are registered: `OnEarlyInitializeMelon`, Harmony auto-patching, `OnInitializeMelon`, `OnLateInitializeMelon`.
   Start callbacks that hang off one-time events that already fired are called by HotReload if the melon overrides them:
   the obsolete `OnApplicationStart` / `OnApplicationLateStart` (UnityExplorer starts from the first) and a plugin's
   `OnApplicationStarted`. A plugin's earlier hooks (`OnPreInitialization`, `OnApplicationEarlyStart`, `OnPreModsLoaded`)
   belong to game startup and are not re-run.
6. Saved state is handed to the new build, and scene callbacks are replayed for the scenes already open.

**When a reload fails** (the new build does not load, or a melon fails to register because its `OnEarlyInitializeMelon`
throws), HotReload names what is not running: `Not running after the failed reload: X`. Copy a fixed build, or press the
reload key to retry the same one. MelonLoader leaves the callbacks of a melon that failed to register subscribed; HotReload
removes them, so the broken build does not keep running `OnUpdate`. State the last working build saved is kept for the
next working build. An exception in `OnInitializeMelon` does not count as a failure: MelonLoader logs it and keeps the
melon running.

## State handoff

Static and instance fields start fresh after a reload. A melon can hand state over, without referencing HotReload:

```csharp
private object OnHotReloadSaveState() => new Dictionary<string, object> { ["count"] = _count };   // old build
private void OnHotReloadRestoreState(object state) => _count = (int)((Dictionary<string, object>)state)["count"]; // new build
```

Save runs before the old build is unregistered, restore after the new build's `OnInitializeMelon`; melons are matched by
name. Use framework types only (primitives, string, arrays, `List`/`Dictionary` of those, or a JSON string): the old and new
builds are different assemblies, so an object of the old build's own classes cannot be cast by the new one.

## Retiring the old build

Unregistering stops MelonLoader callbacks and Harmony patches, but the game can still call old code it was handed:
UI listeners, settings rows, Il2Cpp delegates, coroutines, timers, tasks, event handlers. After a reload HotReload patches
every such entry point of the old build with a prefix that skips the body and returns the default value:

* every method the old build turned into a delegate, found by scanning its IL for `ldftn` / `ldvirtftn`;
* every iterator and async state machine `MoveNext`, so coroutines end on their next step and pending async work stops.

Stale behaviour stops instead of running old code against state that is gone. A leftover settings row or button does
nothing until the game rebuilds that UI (reopen the screen). Typical mods have 0-15 such methods; large UI mods a few hundred.
Retiring is skipped, with a log line, if a mod that references the old build is not being reloaded with it.

If Harmony cannot compile the skip-prefix version of a method, HotReload replaces the method's whole body with "return
the default value" instead. If that fails too and the runtime cannot compile the original method either, the method can
never run in this game and is skipped ("cannot run in this game"); this happens with mods built against another game's
interop assemblies, such as UniverseLib's `EnumerateCppHashTable` in No Rest for the Wicked. Only a method that runs but
cannot be patched is reported as failed; that one old method would still run if something calls it.

Objects the old build passed to `DontDestroyOnLoad` (UI roots, canvases, EventSystems) are destroyed. HotReload learns
the owner from the managed call stack when `DontDestroyOnLoad` is called; the game itself calls it natively, so only mods are tracked.

## Rules for mods that should hot-reload

Nothing is strictly off-limits any more. Il2Cpp class injection (`ClassInjector.RegisterTypeInIl2Cpp`,
`[RegisterTypeInIl2Cpp]`, `MonoBehaviour` subclasses) works: Il2CppInterop refuses a second class with the same full name,
so HotReload removes the old build's names from `ClassInjector.InjectedTypes` and `InjectorHelpers.s_ClassNameLookup`
after destroying the old instances and retiring their methods. Code that looks a class up by name gets the new one.

State a mod registered with a helper library is reset by reloading the library with it (`FreshLibraries`). UnityExplorer
is the known case: it registers its UI and log callback with UniverseLib; with UniverseLib reloaded alongside, UnityExplorer
comes back fully initialized after a hot reload. A library in `Mods/` (loaded on demand, not a MelonLoader library) is not
reloaded.

**AssetBundles** are unloaded with the old build (`Unload(false)`: objects already created from them stay intact), so the
new build can load them again. HotReload finds the bundles the old build keeps in its fields (also a mod's own wrapper
type named `...AssetBundle` with an `Unload(bool)` method, as UniverseLib has), and the ones it loaded through
`AssetBundle.LoadFrom*`, which it tracks. Tracking is best effort in IL2CPP games, where some of those methods are
stripped from the game; keep bundles in a field to be safe.

**Hooks made outside Harmony** (MonoMod `Hook` / `Detour` / `ILHook` / `NativeDetour`, MelonLoader's `NativeHook<T>`)
are disposed with the old build when it keeps them in a field. Methods handed to a hook constructor are not retired, so a
hook HotReload cannot find keeps running the old build's (working) handler instead of breaking the hooked function;
HotReload warns when it found hook code but no hook in a field.

What HotReload cannot undo, so the mod must in `OnDeinitializeMelon`:

* **Hooks made outside Harmony that are not kept in a field**: dispose them.
* **Loops on threads the mod started itself**: a call that is already running finishes; signal it to stop.
* **Game state the mod changed**, such as static fields: re-apply it in `OnInitializeMelon`, because the new build starts
  with fresh static state.

And in `OnInitializeMelon`:

* **UI the old build added to a screen that stays open** (settings rows, buttons): the old rows call retired code and do
  nothing until the game rebuilds that screen. Rebuild or refresh your UI when the screen is already open, and guard
  against adding a row twice.

Other limits: old assemblies stay in memory, about the DLL's size per reload. HotReload cannot reload itself. On Mono,
each reloaded build carries a changed assembly name (`Name__hrN`, see below); code that compares its own assembly name to
a constant sees the suffix. HotReload does not hide it: the old builds stay loaded, so two assemblies would share one name
and a lookup by name would find the old build first. Resource names, Harmony IDs from `GetName().Name` and MelonLoader's
own names are unaffected in practice.

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
| BLOCKED | Uses something HotReload cannot reload (no current rule produces this). |
| UNSUPPORTED | Built for MelonLoader 0.5 (Unhollower). |

A static scan cannot tell whether `OnDeinitializeMelon` undoes everything, and misses behaviour hidden behind reflection or
obfuscation. REVIEW means "read the cleanup code or try it".

## Test mods: `tests/`

`HRTestBase` uses a separate Harmony instance, a reflective preference category, a category with an unrelated name,
scene callbacks, a `DontDestroyOnLoad` object, an endless coroutine, a timer and an injected `MonoBehaviour`, and cleans up
none of them; it registers with the library `HRTestLib` (UserLibs), which refuses duplicate registrations, and hands state
over. `HRTestDependent` references it and deploys into the manifest subfolder `Mods/HRTestSub/` (create it with a
`manifest.json` first). `HRTestPlugin` is a plugin. Build each once, start the game, then rebuild `HRTestBase` with
`-p:Version=1.0.1 -p:BuildProjectReferences=false` (mod only) or without the second switch (library too).

## Building

HotReload compiles against the `LavaGang.MelonLoader` NuGet package and reaches Unity through reflection, so it builds
without a game:

```bash
dotnet build -c Release          # bin/Release/HotReload.dll, for Mono and IL2CPP games
```

It targets net472 and compiles against the net35 assemblies of the MelonLoader, HarmonyX and Mono.Cecil packages, the
oldest copies any game has: MelonLoader runs its net35 build in Mono games, and .NET 6 accepts the newer copies of IL2CPP
games. Differences between the two runtimes are decided at runtime; on IL2CPP, the load context type each reload needs
is generated with Reflection.Emit, because net472 has no `AssemblyLoadContext` to subclass.

To copy each build into games' `Plugins/` folders, set `GameDir` and/or `MonoGameDir` in `Local.props` (copy
`Local.props.example`, git-ignored), or use `MELONLOADER_GAME_DIR` / `MELONLOADER_MONO_GAME_DIR` or `-p:GameDir=...`.
`-p:DeployToGame=false` skips the copy. `pwsh ./package.ps1` builds the release zips into `dist/`.

```bash
dotnet test tests/HotReload.Tests     # offline tests, no game
pwsh tools/apicompat.ps1              # the built DLL against MelonLoader 0.6.0-0.7.3 (downloads them once)
```

GitHub Actions run the same on every push (`.github/workflows/ci.yml`, Windows and Linux). Pushing a tag `vX.Y.Z` that
matches `HotReloadVersion` in `Directory.Build.props` builds, tests and publishes the release with its zips
(`.github/workflows/release.yml`).

The test fixtures in `tests/` (and their Mono projects in `tests/mono/`) do need a game: they use real Unity and Il2Cpp
interop types, so they compile against the game set in `Local.props`.

## How it works (MelonLoader internals it relies on)

MelonLoader marks its own assembly with `[PatchShield]` (every release since 0.6.0): Harmony patches on MelonLoader's
methods are silently skipped. So HotReload never patches MelonLoader. It changes MelonLoader's data, calls its public API,
and patches only the game, the runtime and the mods.

| What | Why |
|---|---|
| HotReload is a `MelonPlugin` | It registers before MelonLoader scans `Mods/`, so the shadow copy is in place before any mod loads. No Unity type may appear in its fields; see `UnityApi.cs`. |
| Shadow copy by editing `MelonFolderHandler._modDirs` (0.7.1+) | MelonLoader loads mods with `LoadFromAssemblyPath`, which locks the file, and PatchShield rules out redirecting that call. The folder list is plain data read later. |
| Remove the old `MelonAssembly` from the internal `loadedAssemblies` list | `LoadMelonAssembly(path, assembly)` returns the cached entry with the same `FullName`. |
| One `AssemblyLoadContext` per reload (IL2CPP) | The default context refuses a second assembly with the same name. The context type is emitted at runtime and overrides `Load`, so references resolve to the newest reloaded builds before the default context answers with the old ones. |
| A unique assembly name per reload, via Mono.Cecil (Mono) | Mono has no load contexts and binds a reference to the first loaded assembly of a name, so a reloaded mod would keep calling the old library. Each reloaded build is renamed `Name__hrN`, its references to other reloaded assemblies are rewritten to their current names, and HotReload strips the suffix wherever it compares names. |
| `MelonBase.RegisterSorted` | `LoadMelons` only creates melons. |
| Preference categories found through the old build's fields | `CreateEntry` throws on duplicates. Recording who creates a category would need a hook on MelonLoader, which PatchShield blocks. |
| Reflective categories matched by their private `SystemType` | `CreateCategory<T>` makes a new category on every call. |
| Postfix on `RuntimeAssembly.Location` | Assemblies loaded from bytes report an empty location. |
| Postfix on `UnityEngine.Object.DontDestroyOnLoad` | Records which mod kept an object across scenes (managed call stack; only loaded mods and MelonLoader libraries count, so the game's own calls on Mono are ignored). |
| Remove old names from `ClassInjector.InjectedTypes` and `InjectorHelpers.s_ClassNameLookup` (Il2CppInterop) | Both reject a second injected class with the same full name. |
| Re-apply the `Assembly.Location` postfix when it stops working | The getter is precompiled runtime code; the tiered JIT can recompile it without the patch. Reloaded builds are loaded from a file copy, so their `Location` is never empty either way. |
| Own watcher for `HotReload.toml` | MelonLoader's preferences watcher misses rename-style saves and swallows the first change after a save. |

A MelonLoader or Il2CppInterop update that renames `loadedAssemblies`, `_modDirs`, `SystemType`, `InjectedTypes` or
`s_ClassNameLookup` switches off the matching feature with a warning.

Source: `src/HotReloadPlugin.cs` (config, watchers), `src/Reloader.cs` (unload/load/dependents/Harmony cleanup),
`src/StartupLoader.cs` (shadow copy, `Assembly.Location`), `src/Retirer.cs`, `src/PrefOwnership.cs`, `src/Callers.cs`,
`src/KeyInput.cs` (reload key backends), `src/InjectedTypes.cs` (class re-injection, IL2CPP), `src/UnityApi.cs` (Unity calls,
`DontDestroyOnLoad` tracking, destroying old instances), `src/Compat.cs` (runtime differences, Cecil metadata and renaming).
