# HotReload (dev tool, planned)

Goal: rebuild a mod and have the running game pick it up without a relaunch, like
[BepInEx.AutoPlugin](https://github.com/Hamunii/BepInEx.AutoPlugin) does for BepInEx.
No maintained MelonLoader equivalent exists (feature request open for years:
[LavaGang/MelonLoader#438](https://github.com/LavaGang/MelonLoader/issues/438)), but MelonLoader 0.7.3 ships
everything needed to write one in ~60 lines.

## What MelonLoader already provides (from `<game>/MelonLoader/net6/MelonLoader.xml`, 0.7.3)

| API | What it does |
|---|---|
| `MelonBase.Unregister(string reason, bool silent)` | "Unregisters the Melon and all other Melons located in the same Assembly. Only unsubscribes the Melons from all callbacks and **unpatches all methods that were patched by Harmony**, but doesn't unload the assembly." |
| `MelonAssembly.UnregisterMelons(string reason, bool silent)` | Same, for a whole `MelonAssembly`. |
| `MelonAssembly.LoadRawMelonAssembly(string filePath, byte[] assemblyData, byte[] symbolsData, bool loadMelons)` | "Loads or finds a MelonAssembly from raw assembly data." With `loadMelons = true` the Melons in it are created and registered (OnInitializeMelon runs). Loading from bytes avoids the file lock that stops `dotnet build` from copying over a loaded DLL. |
| `MelonAssembly.LoadMelonAssembly(string path, bool loadMelons)` | File variant (locks the file - not what we want). |
| `MelonAssembly.LoadedMelons` | The Melons of an assembly (to find the old instance to unregister). |
| `MelonBase.HarmonyInstance` | Per-mod Harmony instance; `Unregister` already calls `UnpatchSelf` on it. |

.NET 6 note: `Assembly.Load(byte[])` of the same assembly name twice yields two independent `Assembly` objects in the
default load context. That is fine; the old one just stays in memory (a few KB per reload). No `AssemblyLoadContext`
unloading is needed and MelonLoader does not support it anyway.

## Design

`HotReload` is itself a Melon (Mod or Plugin) that:

1. Reads a list of watched paths from `UserData/MelonPreferences.cfg` (`[HotReload] Paths = ["C:\...\mods\MoreAspectRatios\bin\Release\MoreAspectRatios.dll"]`)
   and optionally a hotkey (default F6) and an "auto" flag (FileSystemWatcher).
2. On trigger, for each watched DLL whose write time changed:
   1. find the currently registered melons whose `MelonAssembly.Assembly.GetName().Name` matches the DLL's name;
   2. call `melon.Unregister("HotReload", silent: true)` (Harmony patches removed, callbacks unsubscribed);
   3. `MelonAssembly.LoadRawMelonAssembly(path, File.ReadAllBytes(path), File.Exists(pdb) ? File.ReadAllBytes(pdb) : null, loadMelons: true)`.
3. Logs what it reloaded.

Because the target mod's `bin/Release` DLL is watched directly, the mod's csproj post-build copy into `<game>/Mods` is
irrelevant during a session (and it fails while the game runs anyway - that is expected).

## Requirements on the mods being reloaded

* **No Il2CppInterop class injection** (`ClassInjector.RegisterTypeInIl2Cpp`): injected types cannot be re-registered.
  MoreAspectRatios does not use it. EnchantTooltip must not either if it wants hot reload.
* **Re-applyable state**: whatever the mod changed in the game must be re-done in `OnInitializeMelon` / the first
  `OnUpdate`, because the old instance is gone. MoreAspectRatios already re-applies everything in `ApplyEverything`.
  Things it changed that are *not* Harmony patches persist across a reload (pipeline flag, PanelSettings modes,
  the two static `s_force...` bools, the settings rows already added to a live settings screen).
* **Static state** lives in the old assembly; the new one starts fresh. Cache nothing you cannot recompute.
* `MelonPreferences.CreateCategory` with the same name is fine (returns the existing category).
* Harmony patch classes are found via `HarmonyInstance.PatchAll(assembly)` - the new assembly's own instance,
  so patches from the new build are applied under the same Harmony id after the old ones were removed.

## Skeleton

`HotReload.csproj` and `src/HotReloadMod.cs` are a compilable starting point (same csproj layout as MoreAspectRatios:
references into `<game>/MelonLoader/net6` and `Il2CppAssemblies`, post-build copy to `<game>/Mods`).
`OnUpdate` polls `Input.GetKeyDown` for the hotkey; a `FileSystemWatcher` is left as a TODO because Unity callbacks
must run on the main thread (queue the event, act in `OnUpdate`).

## Things to verify in the first session

1. `LoadRawMelonAssembly` really registers and initializes the melons (check the log for the mod's "Patches applied").
2. `Unregister` removes the Harmony patches (make a visible change, reload, confirm).
3. Whether `MelonGame` attribute checks re-run and pass on the raw-loaded assembly.
4. That the old assembly's Harmony id does not collide (MelonLoader creates the id from the mod's Info; if the new one
   is rejected as "already registered", pass a different `reason`/`silent` combo or unregister via `MelonAssembly.UnregisterMelons`).
