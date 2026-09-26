# HotReload

MelonLoader mod for No Rest for the Wicked: rebuild a mod and the running game picks it up without a relaunch,
like [AutoReload](https://github.com/Hamunii/AutoReload) does for BepInEx 5. Dev tool; tested in-game with
MelonLoader 0.7.3 and MoreAspectRatios on 2026-09-26.

## Use

1. `dotnet build -c Release` here once (deploys `HotReload.dll` to `<game>/Mods`), then start the game.
2. Build any mod whose csproj has the deploy target below. HotReload sees the new DLL in `Mods/` and reloads it
   (log: `[HotReload] Reloaded X 0.2.2 -> 0.2.3 (Harmony: 6 method(s) unpatched, 6 patched) in 450 ms`).
3. **F8** reloads every mod whose DLL changed, for when auto reload is off or a file event was missed.

Also handled: deleting a mod DLL from `Mods/` unloads that mod, and a new mod DLL dropped into `Mods/` is loaded.

## Config: `<game>/UserData/HotReload.toml`

Created on first launch. Edits apply immediately.

| Key | Default | Meaning |
|---|---|---|
| `AutoReload` | `true` | Reload as soon as a DLL changes. `false` = only the reload key. |
| `ReloadKey` | `"F8"` | Any [`UnityEngine.KeyCode`](https://docs.unity3d.com/ScriptReference/KeyCode.html) name; `"None"` disables it. UnityExplorer uses F7. |
| `ExtraWatchPaths` | `[]` | More folders (every `*.dll`) or DLL files to watch, e.g. a project's `bin/Release`. Relative paths are relative to the game folder. |
| `Ignore` | `["UnityExplorer.ML.IL2CPP.CoreCLR"]` | Assembly names never reloaded. |
| `DebounceMs` | `500` | Quiet time after the last file event before reloading. |

## Deploy target for reloadable mods

A running game keeps the DLLs it loaded at startup open. Windows refuses to overwrite such a file but allows renaming it.
So the deploy step renames the old copy to `*.hotreload-old` and then copies the new build in. HotReload deletes those
leftovers on the next launch, and so does the next build once nothing holds them. HotReload loads new builds from bytes,
so after the first reload a mod's DLL is not locked at all. Copy this target from `HotReload.csproj` (MoreAspectRatios already has it):

```xml
<Target Name="DeployToGame" AfterTargets="Build" Condition="'$(DeployToGame)' == 'true' And Exists('$(GameDir)\Mods')">
  <PropertyGroup>
    <_DeployStamp>$([System.DateTime]::UtcNow.Ticks)</_DeployStamp>
  </PropertyGroup>
  <ItemGroup>
    <_DeployFile Include="$(TargetPath);$(TargetDir)$(TargetName).pdb" />
    <_DeployFile Remove="@(_DeployFile)" Condition="!Exists('%(FullPath)')" />
    <_DeployExisting Include="@(_DeployFile->'$(GameDir)\Mods\%(Filename)%(Extension)')" />
    <_DeployExisting Remove="@(_DeployExisting)" Condition="!Exists('%(FullPath)')" />
  </ItemGroup>
  <Move SourceFiles="@(_DeployExisting)" DestinationFiles="@(_DeployExisting->'%(FullPath).$(_DeployStamp).hotreload-old')" ContinueOnError="true" />
  <Copy SourceFiles="@(_DeployFile)" DestinationFolder="$(GameDir)\Mods" Retries="3" RetryDelayMilliseconds="300" ContinueOnError="true" />
  <Exec Condition="'$(OS)' == 'Windows_NT'" Command="del /f /q &quot;$(GameDir)\Mods\*.hotreload-old&quot; 2&gt;nul" IgnoreExitCode="true" StandardOutputImportance="low" StandardErrorImportance="low" />
  <Message Importance="high" Text="$(TargetFileName) -> $(GameDir)\Mods" />
</Target>
```

The `.pdb` is deployed too. HotReload loads it with the DLL, so exceptions from reloaded code keep file and line numbers.

## Writing a mod that reloads cleanly

What a reload does, in order:

1. `MelonAssembly.UnregisterMelons`: your `OnDeinitializeMelon` runs, MelonLoader callbacks are unsubscribed, and
   everything patched through the melon's `HarmonyInstance` is unpatched. HotReload logs a warning if patches remain.
2. Your preference categories are saved and emptied, so the new build's `CreateEntry` calls succeed and read the saved values.
   MelonLoader throws if an entry is created twice.
3. The new DLL is loaded from bytes into its own `AssemblyLoadContext`. The default context refuses a second assembly
   with the same name.
4. Its melons are registered: `OnEarlyInitializeMelon`, Harmony auto-patching, `OnInitializeMelon`, `OnLateInitializeMelon`.
   `OnSceneWasLoaded` does not fire for the scene that is already open.

Rules that follow:

* **Patch with `HarmonyInstance`**, auto-patching or `HarmonyInstance.PatchAll(...)`. A separate `new Harmony("id")` is not removed.
* **Undo in `OnDeinitializeMelon`** whatever is not a Harmony patch and should not outlive the old build:
  GameObjects and components you created, `MelonCoroutines` you started, event handlers on game objects,
  static game fields you changed. Anything you do not undo keeps running the old code.
* **Re-apply in `OnInitializeMelon` / the first `OnUpdate`** whatever the running game needs, because the new build
  starts with fresh static state.
* **No Il2Cpp class injection** (`ClassInjector.RegisterTypeInIl2Cpp`, `[RegisterTypeInIl2Cpp]`): a type cannot be registered twice.
* **Preferences**: plain `MelonPreferences.CreateCategory` + `CreateEntry` work. Reflective categories
  (`CreateCategory<T>`) are not handled.
* UI rows or callbacks that the old build handed to the game, such as settings sliders, keep calling the old code until the game rebuilds them.
* If mod A references mod B and both are reloaded, A binds to B's newest reloaded build. A mod that was never reloaded
  keeps the startup build of B.
* Old assemblies stay in memory; each reload costs the DLL's size. HotReload cannot reload itself; restart the game after rebuilding it.

## How it works (MelonLoader 0.7.3 internals it relies on)

| What | Where |
|---|---|
| Mods are loaded with `AssemblyLoadContext.Default.LoadFromAssemblyPath`, which locks the file. | `MelonAssembly.LoadMelonAssembly(string)` |
| `LoadMelonAssembly(path, assembly)` returns the cached `MelonAssembly` if one with the same `FullName` exists, so HotReload removes the old entry from the internal static `loadedAssemblies` list via reflection. | `MelonAssembly` |
| `LoadMelons` only creates melons; `MelonBase.RegisterSorted` registers them. | `MelonAssembly`, `MelonBase` |
| The melon Harmony id is `Assembly.FullName + ":" + Info.Name`; `UnregisterInstance` calls `HarmonyInstance.UnpatchSelf()`. | `MelonBase.Register` / `UnregisterInstance` |
| `MelonPreferences_Category.CreateEntry` throws on duplicates. Category ownership is recorded by postfixes on every `CreateCategory` overload and the category constructor, which catches the owning assembly from the stack. MelonLoader creates its own categories before mods load, so the short overloads are already compiled with the long one inlined. Hooking only one overload missed MoreAspectRatios in testing. Categories named like the mod are the fallback. | `PrefOwnership.cs` |
| MelonLoader's preferences file watcher ignores rename-style saves and swallows the first event after a save, so HotReload watches `HotReload.toml` itself. | `Preferences/IO/Watcher.cs` |
| The game supports legacy `UnityEngine.Input` (UniverseLib logs "Initialized Legacy Input support"). | |

Source: `src/HotReloadMod.cs` (config, watchers, key), `src/Reloader.cs` (unload/load), `src/PrefOwnership.cs`.
