# Making your mod reload cleanly

Most mods reload with HotReload as they are. This page is for the rest: what HotReload does for you, what only your mod
can do, and how to test it.

## What HotReload does for you

You do not need any code for these:

* **Harmony patches** are removed, whichever Harmony instance made them.
* **MelonLoader callbacks** (`OnUpdate`, `OnGUI`, ...) stop, and so do handlers you subscribed to `MelonEvents` yourself.
* **Preferences** keep their values: the old build's categories are saved and released, and the new build's
  `CreateCategory` / `CreateEntry` calls get the saved values.
* **Callbacks the game still holds** (UI listeners, Il2Cpp delegates, timers, tasks, event handlers) and **coroutines**
  are turned into no-ops, so old code does not run against state that is gone.
* **Objects you passed to `DontDestroyOnLoad`** are destroyed, and so are live instances of your injected Il2Cpp classes
  and your `MonoBehaviour`s. Your classes can be injected again by the new build. You do not need to destroy them
  yourself; do it only if you also reset state that points at them.
* **Textures, materials, meshes, sprites and ScriptableObjects** your code created are destroyed
  ([`DestroyOldAssets`](configuration.md)). GameObjects you created with `new GameObject` can be too, if the user turns on
  `DestroyOldGameObjects`.
* **AssetBundles** you keep in a field, or loaded through `AssetBundle.LoadFrom*`, are unloaded.
* **MonoMod hooks** (`Hook`, `Detour`, `ILHook`) are disposed, kept in a field or not. MelonLoader's `NativeHook` and
  MonoMod's `NativeDetour` are disposed when you keep them in a field.
* **Saving on quit**: if your melon has `OnApplicationQuit` but no `OnDeinitializeMelon`, its `OnApplicationQuit` runs
  before a reload too ([`CallQuitOnUnload`](configuration.md)).
* **Libraries** in `UserLibs` that your mod uses reload with it, so state you registered with them starts fresh.
* **Scene callbacks** `OnSceneWasLoaded` and `OnSceneWasInitialized` are replayed for the scenes already open, and
  start callbacks that already fired (`OnApplicationStart`, a plugin's `OnApplicationStarted`) are called.
* **Several mods changing at once** (a build of a whole solution, a tool that switches mods on and off) load in the order
  MelonLoader uses at startup, one per frame.

## The rules

### 1. Set up for what is already there

After a reload, your `OnInitializeMelon` runs in a game that is already running. Events that set things up the first
time (a menu being built, a level being loaded, a save being read) have already happened and will not fire again for the
new build. A mod that adds its UI from a patch on "menu built" finds, after a reload, that its UI is gone and does not
come back until that menu is built again, which may be never in the current session.

**Anything you attach to game objects from an event must also be attachable on demand.** Do it from
`OnInitializeMelon` for the objects that already exist, as well as from the event:

```csharp
public override void OnInitializeMelon()
{
    // First start: nothing exists yet, the patch does the work. After a reload: the menus exist already.
    foreach (var menu in Resources.FindObjectsOfTypeAll<OptionsMenu>())
        AddMyControls(menu);
}

[HarmonyPatch(typeof(OptionsMenu), nameof(OptionsMenu.Build))]
private static class BuildPatch
{
    private static void Postfix(OptionsMenu __instance) => AddMyControls(__instance);
}
```

`AddMyControls` must then be safe to call twice for the same menu (rule 3).

### 2. Undo what you changed outside Harmony

Removing a Harmony patch does not undo what the patch did. The same goes for everything your mod set directly: fields
and properties of game objects, UI styles and scales, controls you added.

**In `OnDeinitializeMelon`, restore what you changed; in `OnInitializeMelon`, apply it again to what is there.** Keep
the original values while your mod runs so you can put them back. Some changes are harmless to leave (a flag the new
build sets to the same value); decide per change.

MelonLoader also calls `OnDeinitializeMelon` when the game quits. Keep it safe and cheap there, or tell the cases apart:
while HotReload takes your build down, the AppDomain data `HotReload.Unloading` is `"reload"` or `"unload"`; at game quit
it is unset.

```csharp
public override void OnDeinitializeMelon()
{
    if (AppDomain.CurrentDomain.GetData("HotReload.Unloading") == null) return; // the game is quitting
    RestoreEverything();
}
```

### 3. Find your objects instead of remembering them

A new build starts with empty static fields. Your old build's own `OnDeinitializeMelon` still sees its fields, so it can
remove what it remembered; the trouble starts when that cleanup misses something, or when it was written after the build
that is running. If your mod only remembers the controls it added in a static list, the new build thinks none exist and
adds them again. Games often key their UI elements by id in a dictionary, so adding the same
id twice can also throw halfway, after the element was already created, and leave a broken, empty element behind.

**Find your own objects in the scene as well.** Give them a recognisable name and look them up by it; before adding an element
the game registers by id, remove your ids from the game's registry.

```csharp
const string Prefix = "MyMod_";
bool HasMyControl(Transform parent, string id) => parent.Find(Prefix + id) != null;
```

### 4. Unregister your objects before you destroy them

Games keep lists of their UI elements, listeners and entities, and walk them later: to close open dropdowns when Back is
pressed, to update every element each frame, to save them. If your mod destroys an object the game still has in such a
list, the next walk hits a destroyed object, throws, and whatever the game was doing (often something unrelated, such as
the Back button) silently stops working.

**If the game can reach your object, remove it from every game list and cached field that holds it before you destroy
it.** On load, also drop dead entries an older build may have left, so a broken session repairs itself on the next
reload.

A mod that hosts other mods' objects (moves their settings rows into its own tab, say) cannot rely on their cleanup,
which looks where it put them. Subscribe to MelonLoader's `MelonBase.OnMelonUnregistered` and remove an unregistered
mod's objects right away.

Such exceptions go to the game's own log, not to MelonLoader's. HotReload copies the ones that appear after a reload
into its log (`Unity exception 3 s after reloading MyMod: ...`); see [Testing](#testing-your-reload).

### 5. Keep scene callbacks cheap

HotReload replays `OnSceneWasLoaded` for every scene that is open. Games that stream their world as additive scenes can
have dozens open at once, so an expensive callback (several `Resources.FindObjectsOfTypeAll` calls, a full re-apply)
turns a reload into seconds, and costs the same during normal play whenever the world streams in.

**Keep `OnSceneWasLoaded` cheap, or only schedule the work there and do it once.**

```csharp
private float _applyAt = -1;

public override void OnSceneWasLoaded(int buildIndex, string sceneName) => _applyAt = Time.unscaledTime + 0.5f;

public override void OnUpdate()
{
    if (_applyAt < 0 || Time.unscaledTime < _applyAt) return;
    _applyAt = -1;
    ApplyEverything(); // once, half a second after the last scene event
}
```

### 6. Keep native hooks and bundles in fields

HotReload disposes MonoMod's `Hook`, `Detour` and `ILHook` wherever you keep them. MelonLoader's `NativeHook` and
MonoMod's `NativeDetour` carry no handler HotReload can trace, so it finds them only in a field (static, or an instance
field of your melon, also inside a list or dictionary); one created and forgotten stays active and keeps calling your old
build's handler until the game restarts, and HotReload names the method that creates it in the log. AssetBundles are
unloaded when kept in a field, or when HotReload could track the load, which some IL2CPP games prevent by stripping the
load method.

```csharp
private static NativeHook<MyDelegate>? _hook;  // found and detached on reload
private static AssetBundle? _bundle;           // found and unloaded on reload
```

Or dispose and unload them yourself in `OnDeinitializeMelon`.

### 7. Stop your own threads

HotReload stops later calls into your old build, but a loop already running on a thread you started keeps running.
Signal it to stop in `OnDeinitializeMelon`.

### 8. Keep start-only code small

Code that only runs while the game starts (a plugin's `OnPreInitialization`, a patch on a method the game calls once at
boot, a setting the game reads once) is not run again by a reload. Changes there still need a restart to test. Keep such
code small and separate, so the rest of the mod stays reloadable.

The opposite also happens: code meant to run once per game start (skip the intro, press Continue on the first main menu)
must not run when the mod is loaded into a game that is already running. While HotReload loads a melon after startup,
it sets an AppDomain flag you can read without referencing HotReload:

```csharp
public override void OnInitializeMelon()
{
    // "reload" (an earlier build ran), "new" (a DLL added while the game runs), or null at game start.
    var loadedLate = AppDomain.CurrentDomain.GetData("HotReload.LoadingLate") as string;
    if (loadedLate == null) ArmFirstMenuAutoContinue();
}
```

The flag is set from the melon's constructor to the end of the reload (`OnEarlyInitializeMelon`, `OnInitializeMelon`,
`OnHotReloadRestoreState`, the replayed scene callbacks) and cleared afterwards, so read it there and keep the answer.

### 9. Optional: hand state to the new build

Static and instance fields start fresh. A melon can pass state to its next build without referencing HotReload:

```csharp
private int _count;

private object OnHotReloadSaveState() =>                       // old build, before it is unloaded
    new Dictionary<string, object> { ["count"] = _count };

private void OnHotReloadRestoreState(object state) =>          // new build, after its OnInitializeMelon
    _count = (int)((Dictionary<string, object>)state)["count"];
```

Melons are matched by name. The state also survives an unload: when a mod's DLL is removed and later comes back in
the same session, the new build gets what the last one saved. Use framework types only (numbers, strings, arrays, `List` / `Dictionary` of those, or a JSON
string): the old and new builds are different assemblies, so the new build cannot cast an object of the old build's
classes.

### 10. Optional: HotReload's API

A mod that loads or switches other mods, or wants to know when they reload, can use `HotReload.Api`. Reach it by
reflection, so the mod also runs without HotReload:

```csharp
var api = AppDomain.CurrentDomain.GetAssemblies()
    .FirstOrDefault(a => a.GetName().Name == "HotReload")?.GetType("HotReload.Api");
// Rename several DLLs, then process them as one batch, in load order, one per frame:
api?.GetMethod("Queue", new[] { typeof(string[]) })?.Invoke(null, new object[] { paths });
api?.GetEvent("BatchFinished")?.AddEventHandler(null, new Action(OnModsSwitched));
```

| Member | What |
|---|---|
| `Version`, `IsRunning`, `AutoReload`, `Busy` | HotReload's version; whether it runs; whether file changes reload on their own; whether a batch is still waiting. |
| `Process(string path)` | Load, reload or unload the DLL at `path` now. Returns `"Reloaded"`, `"Unloaded"`, `"Unchanged"`, `"Skipped"`, `"NotReady"` or `"Failed"`. |
| `Queue(string[] paths)` | Add files to the current batch, without waiting for the file watcher. |
| `Reloaded`, `Unloaded` (`Action<string>`) | A melon assembly or library was loaded into the running game / a mod was unloaded; the assembly name. |
| `BatchFinished` (`Action`) | Every file of the batch has been processed. |

Handlers of a build that is reloaded or unloaded are removed by HotReload.

## Testing your reload

* **Reload twice.** The first reload into a fixed build runs the *old* build's `OnDeinitializeMelon`; only the second
  one runs your new unload code.
* **Force a reload of identical code** with the force-reload key (`LeftShift+F8`): it reloads the mods of the last
  reload with the same bytes, so you can run your unload code again without a new build. A rebuild without changes
  produces the same bytes, and HotReload skips unchanged DLLs; to reload on every build instead, stamp debug builds so
  every build differs:

  ```xml
  <PropertyGroup Condition="'$(Configuration)' == 'Debug'">
    <InformationalVersion>$(Version)+dev$([System.DateTime]::UtcNow.ToString(yyyyMMddHHmmss))</InformationalVersion>
  </PropertyGroup>
  ```

* **Watch for `Unity exception ... after reloading` lines.** Exceptions thrown inside game code, for example when the
  game calls something you destroyed, go to Unity's `Player.log`. HotReload copies the ones that are new since a reload
  into its log, once each with a repeat count ([`EchoUnityErrors`](configuration.md#unity-errors)). For the full text,
  read `Player.log`; on Windows it is in `%USERPROFILE%\AppData\LocalLow\<company>\<game>\Player.log`.
* **Deploy with a plain copy.** `Mods/` is shadow-copied, so a build can overwrite the DLL there while the game runs.
  Moving the old DLL aside first (`*.hotreload-old`) is only needed for plugins and `UserLibs` libraries, which are
  locked; for those, [`ExtraWatchPaths`](configuration.md) pointing at the build folder is simpler.
* **Read HotReload's lines.** Each reload lists what was removed ("removed 3 patch(es)", "retired 12 old method(s)",
  "unloaded 1 AssetBundle(s)") and what failed. `Not running after the failed reload: MyMod` means the new build did not
  come up: fix it and copy it again, or press the reload key to retry.
* **Scan the compiled mod** with [HotReloadCheck](hotreloadcheck.md) for a quick list of what it would need.

## What HotReload cannot do

* Undo changes your mod made to the game outside Harmony (rule 2).
* Stop a loop that is already running on your own thread (rule 7).
* Re-run code that only runs at game start (rule 8).
* Find `NativeHook`s, `NativeDetour`s and bundles that are not kept in a field (rule 6), or objects your mod made that
  the game still holds (rule 4).
* Bring back an asset the old build handed to the game for good: it is destroyed with the old build
  (`DestroyOldAssets`), so set it again from `OnInitializeMelon`.
* Update UI the old build added to a screen that stays open: the old controls call retired code and do nothing until the
  game rebuilds that screen, so rebuild them from `OnInitializeMelon` (rule 1).
* Free memory: each old build stays loaded, about the DLL's size per reload. Restart the game after many reloads.
* Reload itself: a new HotReload build needs a restart.
* Hide the reload suffix on Mono: in Mono games each reloaded build carries a changed assembly name (`MyMod__hr3`). Code
  that compares its own assembly name with a constant sees the suffix.

## Checklist

* Attach to game objects both from events and in `OnInitializeMelon`, and make it safe to do twice.
* Undo changes made outside Harmony in `OnDeinitializeMelon`; apply them again in `OnInitializeMelon`.
* Find your own objects by name, not through static fields.
* Unregister your objects from game lists before destroying them; clean up dead entries on load.
* Keep `OnSceneWasLoaded` cheap, or schedule the work.
* Keep `NativeHook`s and AssetBundles in fields, or dispose them yourself.
* Keep `OnDeinitializeMelon` cheap at game quit (`HotReload.Unloading` is unset then).
* Stop your own threads.
* Skip once-per-launch work when `HotReload.LoadingLate` is set.
* Test with two reloads, stamp debug builds, and watch for Unity exceptions after a reload.
