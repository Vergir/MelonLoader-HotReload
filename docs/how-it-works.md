# How it works

What a reload does, and which MelonLoader, runtime and Unity details HotReload relies on.

## A reload, step by step

A change reloads a **group**: the changed assembly, the loaded mods and libraries that reference it, and (with
`FreshLibraries`) the stateful libraries those mods use. For example:
`Reloading together: HRTestLib (library used by HRTestBase), HRTestBase (changed), HRTestDependent (references HRTestBase)`.
The group goes down dependents first and comes up libraries first, so every mod binds to the fresh libraries.

For each assembly:

0. The old build's state is saved if it opts in (`OnHotReloadSaveState`).
1. `MelonAssembly.UnregisterMelons`: the mod's `OnDeinitializeMelon` runs, its MelonLoader callbacks are unsubscribed, and
   its `HarmonyInstance` is unpatched.
2. Every remaining Harmony patch whose patch method lives in the old assembly is removed, whatever Harmony instance made
   it, and so is every MelonLoader event handler it declares.
3. Hooks outside Harmony that the old build keeps in its fields are disposed. Objects it kept across scenes, live
   instances of its injected Il2Cpp classes (or, on Mono, its components) are destroyed, and its AssetBundles unloaded.
4. The old build is retired (below), and its injected class names are released so the new build can inject them again.
5. Its preference categories are saved and released, so the new build can create them again with the saved values.
6. The new DLL is copied, with its `.pdb`, into this session's shadow folder and loaded from there: on IL2CPP into its own
   `AssemblyLoadContext`, on Mono under a unique name. `Assembly.Location` reports the path in `Mods/`.
7. Its melons are registered: `OnEarlyInitializeMelon`, Harmony auto-patching, `OnInitializeMelon`,
   `OnLateInitializeMelon`. Start callbacks hanging off one-time events that already fired are called if the melon
   overrides them: the obsolete `OnApplicationStart` / `OnApplicationLateStart` and a plugin's `OnApplicationStarted`. A
   plugin's earlier hooks (`OnPreInitialization`, `OnApplicationEarlyStart`, `OnPreModsLoaded`) belong to game startup and
   are not run again.
8. Saved state is handed to the new build, and scene callbacks are replayed for the scenes already open.

From step 6 to the end of step 8 the AppDomain data `HotReload.LoadingLate` is `"reload"` (or `"new"` for a DLL added
while the game runs), so a mod can tell a late load from a game start. After the group, Unity errors that are new are
copied into the log with the reload they followed (`EchoUnityErrors`), because exceptions in game code otherwise reach
only `Player.log`.

**When a reload fails** (the new build does not load, or a melon fails to register because its `OnEarlyInitializeMelon`
throws), HotReload names what is not running (`Not running after the failed reload: X`) and forgets its file hash, so the
reload key or the next copy retries it. MelonLoader leaves the callbacks of a melon that failed to register subscribed;
HotReload removes them, so the broken build does not keep running `OnUpdate`. The state the last working build saved is
kept for the next working build. An exception in `OnInitializeMelon` is not a failure to MelonLoader: it logs it and keeps
the melon running.

## Retiring the old build

Unregistering stops MelonLoader callbacks and Harmony patches, but the game can still call old code it was handed: UI
listeners, settings rows, Il2Cpp delegates, coroutines, timers, tasks, event handlers. HotReload patches each such entry
point of the old build with a prefix that skips the body and returns the default value:

* every method the old build turned into a delegate, found by scanning its IL for `ldftn` / `ldvirtftn`;
* every iterator and async state machine `MoveNext`, so coroutines end on their next step and pending async work stops;
* every method of its injected Il2Cpp classes and (on Mono) its components, until their instances are gone.

Delegates handed to a hook constructor (MonoMod, `NativeHook`) are left alone: a hook HotReload could not dispose keeps
calling working old code instead of a handler that returns defaults.

Typical mods have 0-15 such methods; large UI mods a few hundred. Retiring is skipped, with a log line, when a mod that
references the old build is not reloaded with it.

If Harmony cannot compile the skip-prefix version of a method, HotReload replaces the method's whole body with "return
the default value". If that fails too and the runtime cannot compile the original method either, the method can never run
in this game and is skipped ("cannot run in this game"). That happens with mods and libraries built against another game's
(or game version's) interop assemblies. Only a method that runs but cannot be patched is reported as failed; that one old
method would still run if called.

## Finding what the old build owns

* **Objects kept across scenes**: a postfix on `Object.DontDestroyOnLoad` records the calling mod from the managed call
  stack. The game calls it natively, so only mods are recorded.
* **AssetBundles**: postfixes on every `AssetBundle.LoadFrom*` method (and the `Async` variants) record the calling mod;
  some are stripped in IL2CPP games and cannot be hooked. Bundles in the old build's fields are found as well, including a
  mod's own wrapper type named `...AssetBundle` with an `Unload(bool)` method. They are unloaded with
  `Unload(false)`, so objects already created from them stay intact.
* **Hooks and bundles in fields**: static fields of the old build's types and instance fields of its melons, also inside
  arrays, lists and dictionaries. Only fields whose declared type can hold such an object are read, so no unrelated static
  constructor of the old build runs.
* **Preference categories**: through the old build's fields (the categories and entries it keeps), reflective
  `CreateCategory<T>` categories typed with its classes, and categories named like the mod.

## MelonLoader and runtime details

MelonLoader marks its own assembly with `[PatchShield]` (every release since 0.6.0): Harmony patches on MelonLoader's
methods are silently skipped. So HotReload never patches MelonLoader. It changes MelonLoader's data, calls its public API,
and patches only the game, the runtime and the mods.

| What | Why |
|---|---|
| HotReload is a `MelonPlugin` | Plugins register before MelonLoader scans `Mods/`, so the shadow copy is in place before any mod loads. |
| Shadow copy by editing `MelonFolderHandler._modDirs` (0.7.1+) | MelonLoader loads mods with `LoadFromAssemblyPath`, which locks the file, and PatchShield rules out redirecting that call. The folder list is plain data read later. |
| Remove the old `MelonAssembly` from the internal `loadedAssemblies` list | `LoadMelonAssembly(path, assembly)` returns the cached entry with the same `FullName`. |
| One `AssemblyLoadContext` per reload (IL2CPP) | The default context refuses a second assembly with the same name. The context type is emitted at runtime and overrides `Load`, so references resolve to the newest reloaded builds before the default context answers with the old ones (the public `Resolving` event fires too late). |
| A unique assembly name per reload, via Mono.Cecil (Mono) | Mono has no load contexts and binds a reference to the first loaded assembly of a name. Each reloaded build is renamed `Name__hrN`, its references to other reloaded assemblies are rewritten, and HotReload strips the suffix wherever it compares names. The suffix stays visible: old builds stay loaded, so hiding it would give two assemblies one name. |
| `MelonBase.RegisterSorted` | `LoadMelons` only creates melons. |
| Remove MelonLoader event subscriptions of the old build (public `GetSubscribers` / `Unsubscribe`) | Unregistering removes only a registered melon's own callbacks. A melon whose `OnEarlyInitializeMelon` throws keeps its callbacks subscribed. |
| Preference categories found through the old build's fields | `CreateEntry` throws on duplicates. Recording who creates a category would need a hook on MelonLoader, which PatchShield blocks. |
| Reflective categories matched by their private `SystemType` | `CreateCategory<T>` makes a new category on every call. |
| Postfix on `RuntimeAssembly.Location`, re-applied when it stops working | Assemblies loaded from bytes report an empty location; the getter is precompiled runtime code that the tiered JIT can recompile without the patch. |
| Remove old names from `ClassInjector.InjectedTypes` and `InjectorHelpers.s_ClassNameLookup` (Il2CppInterop) | Both reject a second injected class with the same full name. |
| Unity and Il2CppInterop only through reflection | One net472 DLL for both runtimes, built without a game. The types are looked up in the game's Unity modules or the interop assemblies, which carry the same names. |
| Own watcher for `HotReload.toml` | MelonLoader's preferences watcher misses rename-style saves and swallows the first change after a save. |

A MelonLoader or Il2CppInterop update that renames one of these internals switches off the matching feature with a
warning at startup; the rest keeps working.

## Source map

| File | What |
|---|---|
| `src/HotReloadPlugin.cs` | Settings, file watchers, the reload key loop. |
| `src/Reloader.cs` | Groups, unload and load, dependents, Harmony cleanup, failed reloads. |
| `src/StartupLoader.cs` | Shadow copy of `Mods/`, `Assembly.Location`. |
| `src/LoadContexts.cs` | The emitted `AssemblyLoadContext` (IL2CPP). |
| `src/Retirer.cs` | Retiring the old build; hook handler detection. |
| `src/FieldScan.cs` | Objects the old build holds in fields. |
| `src/AssetBundles.cs`, `src/ForeignHooks.cs` | Bundle tracking and unloading; disposing hooks outside Harmony. |
| `src/MelonEventCleanup.cs` | MelonLoader event subscriptions of old builds. |
| `src/PrefOwnership.cs`, `src/StateHandoff.cs` | Preferences; state handoff. |
| `src/InjectedTypes.cs` | Il2Cpp class re-injection. |
| `src/UnityApi.cs`, `src/KeyInput.cs`, `src/Callers.cs` | Unity by reflection; the reload key; which mod is calling. |
| `src/UnityErrors.cs` | Unity's log callback; which errors to copy into the log. |
| `src/Compat.cs` | Runtime detection, Cecil metadata and renaming. |
