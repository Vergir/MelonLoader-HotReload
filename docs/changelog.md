# Changelog

## 1.0.0

First public release. Changes since 0.8.0:

* **One DLL for Mono and IL2CPP games**, built from NuGet without a game. Unity and Il2CppInterop are reached through
  reflection; IL2CPP load contexts are generated at runtime.
* **AssetBundles** of the old build are unloaded on reload, so the new build can load them again.
* **Hooks outside Harmony** (MonoMod, `NativeHook`) kept in a field are disposed; hooks HotReload cannot find keep
  calling working old code instead of breaking the hooked function.
* **Failed reloads** name what is not running; the reload key or the next copy retries them. The callbacks MelonLoader
  leaves subscribed for a melon that failed to register are removed.
* **MelonLoader event handlers** a mod subscribed by hand are removed with its old build.
* **Reload key chords** (`"LeftControl+F8"`); the Windows key backend also catches very short taps.
* Retiring handles methods Harmony cannot patch: the body is replaced, or the method is skipped when it cannot run in the
  game at all.
* UnityExplorer is no longer ignored by default: it reloads fully with UniverseLib.
* F8 no longer reports "HotReload itself changed" on every press.
* HotReloadCheck recognises bundles and hooks kept in fields.
* Tested on a Steam Deck (Proton).
* For contributors: offline tests, a MelonLoader API check against 0.6.0-0.7.3, GitHub Actions, a solution file.

## 0.8.0

* Mono games, as a second build of the same source.

## 0.7.0

* Group reloads: mods that reference a reloaded assembly, and the `UserLibs` libraries a mod uses, reload with it.
* Plugins and mods in `Mods/` manifest subfolders reload.
* State handoff (`OnHotReloadSaveState` / `OnHotReloadRestoreState`).

## 0.6.0

* Il2Cpp class injection: the new build can inject classes with the same names.

## 0.5.0

* Preference categories are found without patching MelonLoader, which ignores patches on its own methods.

## 0.4.0

* The old build is retired: callbacks, coroutines and timers it left behind stop.
* Objects the old build kept across scenes are destroyed.

## 0.3.0

* A plugin that shadow-copies `Mods/`, so builds can overwrite mod DLLs while the game runs.
* All Harmony patches of the old build are removed, reflective preference categories are handled, scene callbacks are
  replayed, and dependent mods reload.
* HotReloadCheck.

## 0.2.0

* First working version: automatic and F8 reloads.
