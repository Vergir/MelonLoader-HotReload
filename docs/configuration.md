# Configuration

HotReload's settings are in `<game>/UserData/HotReload.toml`, created with the defaults and a description of each
setting on the first start. Edits apply while the game runs, except `ShadowCopyMods`.

| Setting | Default | What it does |
|---|---|---|
| `AutoReload` | `true` | Reload as soon as a DLL changes. `false`: only the reload key reloads. |
| `ReloadKey` | `"F8"` | The key that reloads every DLL that changed. A [`UnityEngine.KeyCode`](https://docs.unity3d.com/ScriptReference/KeyCode.html) name, or a chord such as `"LeftControl+F8"`, which fires when its last key goes down while the others are held. `"None"` turns the key off. |
| `InputBackend` | `"Auto"` | How the key is read (below). `Auto`, `Legacy`, `InputSystem` or `Windows`. |
| `ExtraWatchPaths` | `[]` | More folders (every `*.dll` in them) or single DLL files to watch, relative to the game folder or absolute. Use it for plugins and `UserLibs` libraries, whose DLLs are locked while the game runs: point it at the project's build folder. |
| `Ignore` | `[]` | Assembly names (file name without `.dll`) that are never reloaded. |
| `DebounceMs` | `500` | Quiet time after the last file change before reloading, so a build has finished writing. |
| `ShadowCopyMods` | `true` | MelonLoader loads `Mods/*.dll` from copies in `UserData/HotReload/Shadow`, so the files in `Mods/` are never locked and a build can overwrite them. Needs MelonLoader 0.7.1 or newer. Takes effect at the next start. |
| `ReplaySceneEvents` | `true` | After a reload, call the mod's `OnSceneWasLoaded` / `OnSceneWasInitialized` for the scenes already open. |
| `ReloadDependents` | `true` | Reload the loaded mods and libraries that reference a reloaded assembly together with it. |
| `FreshLibraries` | `true` | Reload the `UserLibs` libraries a mod uses together with it, so state the old build registered with them is gone. Only libraries that reference MelonLoader, Il2CppInterop or Unity count; other mods using the same library reload too. |
| `RetireOldBuild` | `true` | Turn the old build's callbacks, coroutine steps and async steps into no-ops after a reload. |
| `DestroyOldObjects` | `true` | Destroy the old build's objects kept across scenes and live instances of its classes, and unload its AssetBundles. |

## The reload key

The key only checks for DLLs that changed; when nothing changed, nothing is reloaded. It is still a bad idea to use a key
or button the game uses, so pick something the game leaves alone, or a chord.

`InputBackend = "Auto"` tries, in order:

1. **Legacy `UnityEngine.Input`**, if the game has it enabled. The only backend that reads gamepad buttons
   (`JoystickButton0`, `JoystickButton1`, ...).
2. **The Input System package**, for games that turned legacy input off. Keyboard keys only.
3. **The Windows key state** while the game window has focus. Keyboard keys only; also works under Wine and Proton.

The first one that can read every key of the chord is used; the startup line in the log says which one.

**Steam Deck:** the back grips (L4, R4, L5, R5) are invisible to games unless Steam Input binds them. Bind one to F8 in
the game's controller layout, and it can never collide with a game action.
