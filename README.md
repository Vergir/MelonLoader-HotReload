# HotReload for MelonLoader

![HotReload for MelonLoader](docs/images/header.png)

A [MelonLoader](https://github.com/LavaGang/MelonLoader) plugin for mod authors: rebuild your mod and the running game
picks up the new build, without a restart. It works in any Unity game, Mono or IL2CPP, and cleans up after the old build
so the new one starts as if the game had just launched.

**[Download the latest release](https://github.com/Vergir/MelonLoader-HotReload/releases/latest/download/MelonLoader-HotReload.zip)**
(one DLL for every game) · [all releases](https://github.com/Vergir/MelonLoader-HotReload/releases)

## Features

* **Reload on build.** Copy a new build into `Mods/` while the game runs; HotReload reloads it a moment later.
  `Mods/` DLLs are never locked, so your build's copy step just works. Or press **F8**.
* **Cleans up the old build.** Harmony patches (from any Harmony instance), MelonLoader callbacks and event handlers,
  preference categories, coroutines, timers and other callbacks the game still holds, objects kept across scenes,
  injected Il2Cpp classes, AssetBundles, and hooks made outside Harmony that the mod keeps in a field.
* **Starts the new build properly.** It replays the scene callbacks for the scenes already open, calls the start
  callbacks that already fired, and can hand state over from the old build to the new one.
* **Reloads what belongs together.** Mods that reference the changed one, and the helper libraries they use, reload with
  it. Plugins and `UserLibs` libraries reload too. UnityExplorer with UniverseLib reloads fully.
* **Tells you what happened.** One log line per reload, and a clear message when a new build fails to load.

```
[HotReload] Reloaded MyMod 1.2.0 -> 1.2.1 (Harmony: 6 method(s) unpatched, 6 patched; replayed 4 scene(s)) in 110 ms (from Mods\MyMod.dll)
```

## Install

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader/releases) 0.6.0 or newer (0.7.1 or newer recommended)
   into the game and start the game once.
2. Extract the release zip into the game folder: `HotReload.dll` goes into **`Plugins/`**, not `Mods/`.

On a **Steam Deck or Linux** (Proton) it works the same: install MelonLoader with the launch option
`WINEDLLOVERRIDES="version=n,b" %command%`, then put `HotReload.dll` into `Plugins/`.

## Quick start

1. Start the game. The MelonLoader log shows what HotReload could enable:
   `[HotReload] MelonLoader 0.7.3, .NET 6.0.16 (IL2CPP game). Shadow copy: on; ...; reload key: F8 (legacy Input).`
2. Build your mod and copy the DLL (and its `.pdb`) into `Mods/`, as your build probably already does.
3. The mod reloads. **F8** reloads every DLL that changed, if you turned automatic reloading off or a change was missed.

Plugins (`Plugins/`) and libraries (`UserLibs/`) are loaded before HotReload and stay locked while the game runs. Add
your project's build folder to `ExtraWatchPaths` in the settings instead: HotReload reloads them straight from there.

On a Steam Deck, bind a back grip to F8 in the game's Steam Input layout; games never see the grips, so nothing
collides with it.

## Making your mod reload cleanly

Most mods reload as they are. HotReload removes what it can find; a few things only your mod can undo. The short
version:

* **Must:** in `OnDeinitializeMelon`, undo what you changed in the game outside Harmony (object properties, UI you added,
  hooks or bundles you did not keep in a field), and unregister your objects from game lists before you destroy them.
* **Must:** in `OnInitializeMelon`, set things up for what is already there. The game has already started, and one-time
  events (a menu being built, a save being loaded) will not fire again for the new build.
* **Must:** stop threads you started yourself.
* **Should:** keep `OnSceneWasLoaded` cheap. HotReload replays it for every open scene, which can be dozens.
* **Should:** find your own objects in the scene instead of remembering them in static fields.
* **Can:** hand state to the new build with `OnHotReloadSaveState` / `OnHotReloadRestoreState` (no reference to
  HotReload needed).

**[The guide for mod authors](docs/writing-reloadable-mods.md)** explains each rule with code and shows how to test
your mod's reload. `HotReloadCheck`
([docs/hotreloadcheck.md](docs/hotreloadcheck.md)) scans compiled mods and lists what they would need.

## Settings

`<game>/UserData/HotReload.toml`, created on the first start. Edits apply while the game runs.

| Setting | Default | |
|---|---|---|
| `AutoReload` | `true` | Reload as soon as a DLL changes; `false` = only the reload key. |
| `ReloadKey` | `"F8"` | A Unity `KeyCode` name, or a chord like `"LeftControl+F8"`; `"None"` turns it off. |
| `ExtraWatchPaths` | `[]` | Build folders or DLLs to watch besides the game's folders, e.g. your plugin's `bin/Release`. |
| `Ignore` | `[]` | Assembly names never to reload. |

All settings: [docs/configuration.md](docs/configuration.md).

## Compatibility

| | |
|---|---|
| MelonLoader | 0.6.0 to 0.7.3, checked against each release's binaries on every build. Keeping `Mods/` DLLs unlocked needs 0.7.1 or newer. |
| Games | IL2CPP games, and Mono games on Unity 2018.1 or newer. One DLL for both. |
| Tested | No Rest for the Wicked (IL2CPP) on Windows and on a Steam Deck; PEAK (Mono) on Windows. With the included test mods, Display & UI Tweaks, NRftW Item Manager, and UnityExplorer with UniverseLib. |

Limits: old builds stay in memory (about the DLL's size per reload), HotReload cannot reload itself, and code that only
runs at game start is not run again. More in the [guide](docs/writing-reloadable-mods.md#what-hotreload-cannot-do).

## Build

```bash
dotnet build -c Release
```

No game needed: everything comes from NuGet. [docs/building.md](docs/building.md) covers deploying to a game, tests,
releases and the test mods; [docs/how-it-works.md](docs/how-it-works.md) explains what a reload does inside.

## Credits

The idea comes from [AutoReload](https://github.com/Hamunii/AutoReload) for BepInEx by Hamunii. Built on
[MelonLoader](https://github.com/LavaGang/MelonLoader), [HarmonyX](https://github.com/BepInEx/HarmonyX),
[Mono.Cecil](https://github.com/jbevain/cecil) and [Il2CppInterop](https://github.com/BepInEx/Il2CppInterop).
The watermelon in the [header](docs/images/header.png) and [banner](docs/images/banner.png) images is MelonLoader's
icon, from the MelonLoader project.

## License

[MIT](LICENSE)
