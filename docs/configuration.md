# Configuration

HotReload's settings are in `<game>/UserData/HotReload.toml`, created with the defaults and a description of each
setting on the first start. Edits apply while the game runs, except `ShadowCopyMods` and `PatchAssemblyLocation`.

| Setting | Default | What it does |
|---|---|---|
| `AutoReload` | `true` | Reload as soon as a DLL changes. `false`: only the reload key reloads. |
| `ReloadKey` | `"F8"` | The key that reloads every DLL that changed. A [`UnityEngine.KeyCode`](https://docs.unity3d.com/ScriptReference/KeyCode.html) name, or a chord such as `"LeftControl+F8"`, which fires when its last key goes down while the others are held. `"None"` turns the key off. |
| `ForceReloadKey` | `"LeftShift+F8"` | Reloads the mods of the last reload even when their DLL is unchanged (every loaded mod if nothing was reloaded yet), so unload code can be tested without a new build. Same format as `ReloadKey`; `"None"` turns it off. |
| `InputBackend` | `"Auto"` | How the key is read (below). `Auto`, `Legacy`, `InputSystem` or `Windows`. |
| `ExtraWatchPaths` | `[]` | More folders (every `*.dll` in them) or single DLL files to watch, relative to the game folder or absolute. Use it for plugins and `UserLibs` libraries, whose DLLs are locked while the game runs: point it at the project's build folder. |
| `Ignore` | `[]` | Assembly names (file name without `.dll`) that are never reloaded. |
| `DebounceMs` | `500` | Quiet time after the last file change before reloading, so a build has finished writing. A DLL renamed into place is complete at once and loads after 100 ms. |
| `ShadowCopyMods` | `true` | MelonLoader loads `Mods/*.dll` from copies in `UserData/HotReload/Shadow`, so the files in `Mods/` are never locked and a build can overwrite them. Needs MelonLoader 0.7.1 or newer. Takes effect at the next start. |
| `PatchAssemblyLocation` | `true` | `Assembly.Location` of a shadow-copied or reloaded mod reports its path in `Mods/` (a Harmony postfix on the runtime's getter, installed once MelonLoader has loaded every mod). `false`: such a mod sees the path of its copy. Takes effect at the next start. |
| `ReplaySceneEvents` | `true` | After a reload, call the mod's `OnSceneWasLoaded` / `OnSceneWasInitialized` for the scenes already open. |
| `ReplayActiveSceneOnly` | `false` | Replay the scene callbacks for the active scene only. For games that stream their world as many additive scenes, where replaying every open scene makes each reload slow. |
| `ReloadDependents` | `true` | Reload the loaded mods and libraries that reference a reloaded assembly together with it. |
| `FreshLibraries` | `true` | Reload the `UserLibs` libraries a mod uses together with it, so state the old build registered with them is gone. Only libraries that reference MelonLoader, Il2CppInterop or Unity count; other mods using the same library reload too. |
| `RetireOldBuild` | `true` | Turn the old build's callbacks, coroutine steps and async steps into no-ops after a reload. |
| `DestroyOldObjects` | `true` | Destroy the old build's objects kept across scenes and live instances of its classes, and unload its AssetBundles. |
| `DestroyOldAssets` | `true` | With `DestroyOldObjects`: also destroy the textures, render textures, materials, meshes, sprites and ScriptableObjects the old build created in code. One it handed to the game for good (an icon registered once) shows blank until the new build sets it again. |
| `DestroyOldGameObjects` | `false` | With `DestroyOldObjects`: also destroy every GameObject the old build created with `new GameObject(...)` that still exists, wherever it is attached. Off by default: most mods remove their own objects, and some hand them to the game. |
| `CallQuitOnUnload` | `true` | Before taking a mod down, call its `OnApplicationQuit` if it has one but no `OnDeinitializeMelon`, so a mod that saves or cleans up only on quit does it on a reload too. |
| `EchoUnityErrors` | `"AfterReload"` | Copy Unity errors and exceptions into the MelonLoader log (below). `AfterReload`, `Always` or `Off`. |

## Unity errors

An exception thrown inside game code goes to Unity's `Player.log`, not to MelonLoader's log. After a reload that is
often the only sign that something broke, for example when the game walks a list that still holds an object the old
build destroyed. HotReload listens to Unity's log and copies errors, asserts and exceptions into its own lines:

```
[HotReload] Unity exception 3 s after reloading MyMod: NullReferenceException: Object reference not set to an instance of an object.
    at SettingsScreen.IsAnyDropDownOpen ()
    at MenuScreen.Back ()
[HotReload]   ... again 71 time(s): NullReferenceException: Object reference not set to an instance of an object.
```

* `AfterReload` (default): nothing before the first reload. After it, errors the game had not logged before the first
  reload, so a game's usual noise stays out.
* `Always`: every error from the start.
* The line names the reload it followed and the other reloads and unloads of the last 30 seconds, since an error after
  a batch can come from any of them.
* Each error is shown once per reload with a few lines of its stack; repeats are counted and reported at most every
  10 seconds. At most 20 different errors per reload; the full text stays in `Player.log`.

MelonLoader 0.7.1 and newer can copy the whole player log itself (`capture_player_logs = true` in
`UserData/Loader.cfg`, or `--melonloader.captureplayerlogs`). With that on, HotReload leaves Unity errors to MelonLoader.

## The reload key

The key only checks for DLLs that changed; when nothing changed, nothing is reloaded. It is still a bad idea to use a key
or button the game uses, so pick something the game leaves alone, or a chord.

`InputBackend = "Auto"` tries, in order:

1. **Legacy `UnityEngine.Input`**, if the game has it enabled. The only backend that reads gamepad buttons
   (`JoystickButton0`, `JoystickButton1`, ...).
2. **The Input System package**, for games that turned legacy input off. Keyboard keys only.
3. **The Windows key state** while the game window has focus. Keyboard keys only; also works under Wine and Proton.

The first one that can read every key of the chord is used; the startup line in the log says which one.

**When another mod blocks input.** Some mods patch `Input.GetKey` / `GetKeyDown` to return `false` while their own window
is open. While such a patch is in place, HotReload reads the key around it: on Mono through an unpatched copy of the
method, on IL2CPP (where Il2CppInterop patches the game's native method) from the Windows key state or the Input System.
The log says so once.

**The force-reload key** (`ForceReloadKey`, `LeftShift+F8` by default) reloads the mods of the last reload with the same
bytes. A chord wins over the plain key: Shift+F8 does not also run F8.

**Steam Deck:** the back grips (L4, R4, L5, R5) are invisible to games unless Steam Input binds them. Bind one to F8 in
the game's controller layout, and it can never collide with a game action.
