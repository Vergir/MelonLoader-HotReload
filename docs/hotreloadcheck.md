# HotReloadCheck

`HotReloadCheck` reads compiled mod DLLs, with no source and no game, and reports whether HotReload can reload them and
what the author would have to add. It only reads metadata: which APIs a mod calls, which types and fields it defines,
which callbacks it overrides. It never loads or runs a mod.

Download `HotReloadCheck.zip` from the [releases](https://github.com/vergir/MelonLoader-HotReload/releases), extract it,
and run it with the [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or newer):

```bash
dotnet HotReloadCheck.dll <dll-or-folder>... --md report.md --csv report.csv
```

The zip holds only managed code (no `.exe` launcher). To build it from source instead:

```bash
dotnet publish checker -c Release -o out
dotnet out/HotReloadCheck.dll <dll-or-folder>...
```

Folders are searched recursively. `--libs` also lists DLLs without `[MelonInfo]`.

| Verdict | Meaning |
|---|---|
| READY | Nothing found that HotReload cannot clean up. |
| REVIEW | Uses something HotReload cannot clean up, but has `OnDeinitializeMelon`: check that it undoes it. Also: changes global Unity state and has no `OnDeinitializeMelon`. |
| NEEDS CLEANUP | Uses something HotReload cannot clean up and has no `OnDeinitializeMelon`. |
| UNSUPPORTED | Built for MelonLoader 0.5 (Unhollower); it does not load on 0.6 or newer anyway. |
| LIBRARY | No `[MelonInfo]`: not a mod. |

Each finding says whether it is **handled** (HotReload cleans it up; listed for information), needs **cleanup** by the
mod, or needs a **review** (may need undoing; never makes a mod NEEDS CLEANUP), with the evidence (the API calls it
found) and what to do.

| Finding | Kind | What it means |
|---|---|---|
| `threads` | cleanup | Starts its own threads: a running loop keeps running. Signal it to stop in `OnDeinitializeMelon`. |
| `nativehooks` | cleanup / handled | MonoMod or `NativeHook` hooks. Handled when kept in a field, otherwise they keep calling the old build. |
| `assetbundles` | cleanup / handled | AssetBundles. Cleanup only in IL2CPP games when the bundle is not kept in a field. |
| `globalstate` | review | Sets `Cursor.visible` / `lockState`, `Time.timeScale`, `Application.targetFrameRate`, `QualitySettings`, or `EventSystem.current`, and has no `OnDeinitializeMelon`: the old value stays after a reload. |
| `injection` | handled | Il2Cpp class injection. |
| `coroutines`, `callbacks`, `staticevents` | handled | Coroutines, delegates handed to the game, process-wide .NET events: the old build's code is retired. |
| `timers` | handled | Timers, tasks, thread-pool work, file watchers: retired. Work already in flight (an `HttpClient` request, file IO) runs to its end; work queued for the main thread is dropped. |
| `persistent` | handled | Objects passed to `DontDestroyOnLoad` are destroyed. |
| `objects` | handled (opt-in) | `new GameObject(...)`: destroyed only with `DestroyOldGameObjects` on (default off); otherwise they stay until their scene unloads. |
| `components` | handled | `AddComponent` / `Instantiate`: go away with their scene. |
| `assets` | handled | `new Texture2D` / `RenderTexture` / `Material` / `Mesh` / `Cubemap`, `Sprite.Create`, `ScriptableObject.CreateInstance` (Mono and IL2CPP): destroyed on reload (`DestroyOldAssets`). An asset handed to the game (a registered icon) goes blank until the new build registers its own. |
| `uitoolkit` | handled | References `UIDocument` / `PanelSettings`. The panel goes with its GameObject; a `PanelSettings` made with `CreateInstance` counts as an asset. |
| `quitonly` | handled | Overrides `OnApplicationQuit` without `OnDeinitializeMelon`: HotReload calls `OnApplicationQuit` on reload and unload (`CallQuitOnUnload`). Prefer `OnDeinitializeMelon`. |
| `inputpatch` | handled | Harmony patches on `UnityEngine.Input` (`[HarmonyPatch(typeof(Input), ...)]`, or `typeof(Input)` next to `AccessTools` / `harmony.Patch`) may swallow the reload key; HotReload reads its key through an unpatched path. |
| `location` | handled | Reads `Assembly.Location`: the Mods/ path once all mods have loaded (`PatchAssemblyLocation`); code that runs while mods load (`OnEarlyInitializeMelon`, static initialisers) sees the shadow-copy path. Prefer `MelonEnvironment.ModsDirectory` or `MelonAssembly.Location`. |
| `harmony`, `reflectiveprefs`, `scenes` | handled | Own Harmony instance, `CreateCategory<T>`, scene callbacks (replayed after a reload). |

A static scan cannot tell whether `OnDeinitializeMelon` undoes everything, and misses behaviour hidden behind reflection
or obfuscation. REVIEW means "read the cleanup code or try it". The problems in
[the guide for mod authors](writing-reloadable-mods.md) that come from game logic (UI attached from events, objects left in
game lists) cannot be seen in metadata at all.
