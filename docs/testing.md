# Testing

Two kinds of tests: offline tests that need no game, and test mods that exercise every feature in a real game.

## Offline tests

```bash
dotnet test tests/HotReload.Tests
pwsh tools/apicompat.ps1
```

`tests/HotReload.Tests` (xunit, .NET 8) covers the parts that are pure logic: assembly names and the Mono renaming,
load order, finding retirable methods and hook handlers in IL, the field scan, key names and chords, and the checker's
rules. `tools/apicompat.ps1` checks that every MelonLoader, HarmonyX, Mono.Cecil and Il2CppInterop member HotReload uses,
directly or by reflection, exists in each supported MelonLoader release. Both run in CI.

## Test mods

The test mods in `tests/` compile against a real game (`GameDir` / `MonoGameDir` in `Local.props`); the projects in
`tests/mono/` build the same sources for a Mono game.

| Mod | Deploys to | Exercises |
|---|---|---|
| `HRTestLib` | `UserLibs/` | A library that refuses a second registration: works only if it reloads with the mod. |
| `HRTestBase` | `Mods/` | A second Harmony instance, a reflective and a plainly named preference category, scene callbacks, a `DontDestroyOnLoad` object, an endless coroutine, a timer, an injected `MonoBehaviour`, two AssetBundles (one kept in a field, one dropped), two MonoMod hooks (one kept, one dropped), two textures (one kept, one dropped), `OnApplicationQuit` without `OnDeinitializeMelon`, state handoff, the `HotReload.Api` events. It cleans up none of them itself. With a file `UserData/HRTestBlockInput` it also blocks `Input.GetKeyDown` like a mod with an open window. |
| `HRTestDependent` | `Mods/HRTestSub/` | References `HRTestBase`, from a manifest subfolder (create it with a `manifest.json`). |
| `HRTestPlugin` | `Plugins/` | A plugin; also the hook target (`Probe()`, logged every 3 seconds). |

Build each once and start the game:

```bash
dotnet build tests/HRTestPlugin -c Release
dotnet build tests/HRTestDependent -c Release        # builds HRTestLib and HRTestBase too
```

For the AssetBundle test, put two AssetBundles into `<game>/UserData` as `HRTestBundleKept.bundle` and
`HRTestBundleDropped.bundle`. Any bundles work, as long as the game does not load them itself; without them the test
mod skips that part.

Then rebuild `HRTestBase` while the game runs and read `MelonLoader/Latest.log`:

```bash
dotnet build tests/HRTestBase -c Release -p:Version=1.0.1 -p:BuildProjectReferences=false   # the mod only
dotnet build tests/HRTestBase -c Release -p:Version=1.0.2                                   # with its library
dotnet build tests/HRTestBase -c Release -p:Version=1.0.3 -p:HRTestThrow=true              # a build that fails to register
```

What to look for after a reload: one `HRTestBase_Persistent` object and one `HRTestBehaviour` instance alive, timer,
coroutine and `Update` lines only from the new build, both bundles loaded again, `Probe()` showing the new build's hooks
(plus the dropped hooks of older builds, which keep working), and `state restored from ...`.

## Steam Deck

`tools/deck-test.ps1` runs the same tests on a Steam Deck over SSH from Windows: `Setup` (installs an SSH key once),
`Check`, `Deploy`, `Log`, `Reload`, `Cleanup`. The script's header lists what to prepare on the Deck; `-Bundles <folder>`
also copies the two test bundles. Start the game on the Deck yourself; launching it over SSH does not work.

## Test games

Test each release in at least one IL2CPP game and one Mono game, and on Linux (Proton) if you can; the games used so
far are listed under Compatibility in the README. A game that already has BepInEx installed needs a separate copy for
MelonLoader.
