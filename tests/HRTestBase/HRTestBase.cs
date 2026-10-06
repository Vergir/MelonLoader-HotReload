using System.Collections;
using System.Linq;
using HarmonyLib;
#if !MONO
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
#endif
using HRTestBase;
using MelonLoader;
using MelonLoader.Utils;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(HRTestBaseMod), "HRTestBase", "1.0.0", "HotReload tests")]
[assembly: HarmonyDontPatchAll]

namespace HRTestBase;

/// <summary>Reflective preference category: MelonLoader creates a new one per CreateCategory&lt;T&gt; call.</summary>
public class TestConfig
{
    public int InitCount = 0;
}

/// <summary>
/// Exercises what HotReload cleans up: a patch through a separate Harmony instance, a reflective preference
/// category whose value must survive reloads, scene callbacks for scenes that are already open (0.3), and an object kept
/// across scenes, an endless coroutine and a timer callback, none of which this mod cleans up itself (0.4), and an
/// injected MonoBehaviour on a normal scene object (0.6).
/// </summary>
public class HRTestBaseMod : MelonMod
{
    public static string Greeting() => "HRTestBase build " + typeof(HRTestBaseMod).Assembly.GetName().Version;

#if HRTEST_THROW
    // Failed-reload recovery test (-p:HRTestThrow=true). MelonLoader refuses to register a melon only when
    // OnEarlyInitializeMelon throws; an exception in OnInitializeMelon is logged and the melon keeps running.
    public override void OnEarlyInitializeMelon() =>
        throw new System.InvalidOperationException("HRTestBase: deliberate registration failure (built with -p:HRTestThrow=true)");
#endif

    public override void OnInitializeMelon()
    {
        var cat = MelonPreferences.CreateCategory<TestConfig>("HRTestBase", "HotReload test");
        var cfg = cat.GetValue<TestConfig>();
        cfg.InitCount++;
        cat.SaveToFile(false);

        new HarmonyLib.Harmony("hrtest.foreign").Patch(
            AccessTools.Method(typeof(CanvasScaler), "HandleScaleWithScreenSize"),
            postfix: new HarmonyMethod(typeof(HRTestBaseMod), nameof(ScalerPostfix)));

        LoggerInstance.Msg(Greeting() + ", init #" + cfg.InitCount + ", Assembly.Location='" + typeof(HRTestBaseMod).Assembly.Location + "'");

        // Late-load flag (1.1): "reload" / "new" while HotReload loads this build into a running game, null at game start.
        var late = System.AppDomain.CurrentDomain.GetData("HotReload.LoadingLate") as string;
        LoggerInstance.Msg("HotReload.LoadingLate = " + (late ?? "(unset: game start)"));
        // Unity error echo (1.1): an error every build logs is known from before the first reload and must not be echoed;
        // a reloaded build's behaviour logs a new exception every 3 s, echoed once per reload with repeats counted.
        Debug.LogError("HRTestBase: an error every build logs");
        HRTestBehaviour.LogExceptions = late == "reload";

        // A library that refuses a second registration: works only if HotReload reloads the library too (0.7).
        HRTestLib.Registry.Register("hrtestbase");

        // A plain category whose name has nothing to do with the mod: found through this static field on reload.
        var settings = MelonPreferences.CreateCategory("HRTest Unrelated Name");
        _reloads = settings.CreateEntry("Reloads", 0);
        _reloads.Value++;
        settings.SaveToFile(false);
        LoggerInstance.Msg("plain category entry Reloads = " + _reloads.Value);

#if !MONO
        // Il2Cpp class injection: the reloaded build registers a class with the same full name.
        ClassInjector.RegisterTypeInIl2Cpp<HRTestBehaviour>();
#endif
        HRTestBehaviour.Build = Greeting();
        new GameObject("HRTestBase_Behaviour").AddComponent<HRTestBehaviour>(); // normal scene object, not kept across scenes

        var go = new GameObject(PersistentName);
        Object.DontDestroyOnLoad(go);
        MelonCoroutines.Start(Ticker());
        var build = Greeting();
        _timer = new System.Threading.Timer(_ => MelonLogger.Msg("[HRTestBase] timer from " + build), null, 3000, 3000);
        LoadBundles();
        InstallHooks();

        // Created objects (1.2): one texture kept in a field, one dropped; HotReload destroys both on reload (DestroyOldAssets).
        _keptTexture = new Texture2D(4, 4) { name = "HRTestBase_TexKept" };
        new Texture2D(4, 4) { name = "HRTestBase_TexDropped" };
        SubscribeToApi();
        if (System.IO.File.Exists(System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "HRTestBlockInput")))
        {
            // Another mod blocking legacy input (1.2): the reload key must still work.
            new HarmonyLib.Harmony("hrtest.blockinput").Patch(AccessTools.Method(AccessTools.TypeByName("UnityEngine.Input"), "GetKeyDown", new[] { AccessTools.TypeByName("UnityEngine.KeyCode") }),
                prefix: new HarmonyMethod(typeof(HRTestBaseMod), nameof(BlockKey)));
            LoggerInstance.Msg("blocking Input.GetKeyDown(KeyCode)");
        }
    }

    private static bool BlockKey(ref bool __result) { __result = false; return false; }

    private static Texture2D? _keptTexture;

    // Public API (1.2), by reflection as the docs show.
    private void SubscribeToApi()
    {
        var api = System.AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "HotReload")?.GetType("HotReload.Api");
        if (api == null) { LoggerInstance.Msg("HotReload.Api not found"); return; }
        var build = Greeting();
        api.GetEvent("Reloaded")!.AddEventHandler(null, new System.Action<string>(n => MelonLogger.Msg("[HRTestBase] API Reloaded(" + n + ") seen by " + build)));
        api.GetEvent("BatchFinished")!.AddEventHandler(null, new System.Action(() => MelonLogger.Msg("[HRTestBase] API BatchFinished seen by " + build)));
        LoggerInstance.Msg("HotReload.Api " + api.GetProperty("Version")!.GetValue(null) + ", AutoReload=" + api.GetProperty("AutoReload")!.GetValue(null));
    }

    // Quit fallback (1.2): no OnDeinitializeMelon here, so HotReload calls this on reload.
    public override void OnApplicationQuit() =>
        LoggerInstance.Msg("OnApplicationQuit from " + Greeting() + ", HotReload.Unloading = " + (System.AppDomain.CurrentDomain.GetData("HotReload.Unloading") as string ?? "(unset: game quit)"));

    // AssetBundles (step 4): the test deploy extracts two bundles into UserData. One is kept in a field, one is loaded
    // and dropped; a reload must unload both, or loading them again fails.
    private static UnityEngine.AssetBundle? _keptBundle;

    private void LoadBundles()
    {
        try
        {
            var kept = System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "HRTestBundleKept.bundle");
            if (System.IO.File.Exists(kept))
            {
                _keptBundle = LoadBundle(kept);
                LoggerInstance.Msg("kept bundle loaded: " + (_keptBundle != null));
            }
            var dropped = System.IO.Path.Combine(MelonEnvironment.UserDataDirectory, "HRTestBundleDropped.bundle");
            if (System.IO.File.Exists(dropped))
                LoggerInstance.Msg("dropped bundle loaded: " + (LoadBundle(dropped) != null));
        }
        catch (System.Exception e) { LoggerInstance.Error("loading the test bundles failed: " + e.Message); }
    }

    // IL2CPP games can strip AssetBundle.LoadFromFile; games that use Addressables keep the async loader.
    private static UnityEngine.AssetBundle? LoadBundle(string path)
    {
        try { return UnityEngine.AssetBundle.LoadFromFile(path); }
        catch (System.NotSupportedException) { return UnityEngine.AssetBundle.LoadFromFileAsync(path).assetBundle; }
    }

    // Hooks outside Harmony (step 4): two MonoMod hooks on HRTestPlugin.Probe, one kept in a field (HotReload disposes
    // it), one dropped (it stays, and its handler keeps working instead of being retired).
    // MelonLoader's MonoMod wants static handlers; they are still handed over as delegates (ldftn), which is what
    // HotReload's retiring would otherwise turn into no-ops.
    private static Hook? _keptHook;
    private static readonly string HookBuild = typeof(HRTestBaseMod).Assembly.GetName().Version?.ToString(3) ?? "?";

    private static string KeptProbe(System.Func<string> orig) => orig() + " +kept(" + HookBuild + ")";
    private static string DroppedProbe(System.Func<string> orig) => orig() + " +dropped(" + HookBuild + ")";

    private static void InstallHooks()
    {
        var probe = typeof(HRTestPlugin.HRTestPluginMelon).GetMethod(nameof(HRTestPlugin.HRTestPluginMelon.Probe))!;
        _keptHook = new Hook(probe, new System.Func<System.Func<string>, string>(KeptProbe));
        new Hook(probe, new System.Func<System.Func<string>, string>(DroppedProbe));
    }

    private static MelonPreferences_Entry<int> _reloads = null!;
    private const string PersistentName = "HRTestBase_Persistent";

    // State handoff (0.7): framework types only, the old and new builds do not share classes.
    private int _reloadsSeen;

    private object OnHotReloadSaveState() => new System.Collections.Generic.Dictionary<string, object>
    {
        ["reloadsSeen"] = _reloadsSeen, ["from"] = Greeting(),
    };

    private void OnHotReloadRestoreState(object state)
    {
        var d = (System.Collections.Generic.Dictionary<string, object>)state;
        _reloadsSeen = (int)d["reloadsSeen"] + 1;
        LoggerInstance.Msg("state restored from " + d["from"] + ", reloads seen: " + _reloadsSeen);
    }
    private static System.Threading.Timer? _timer; // no cleanup on purpose
    private int _frames;

    private IEnumerator Ticker()
    {
        while (true)
        {
            LoggerInstance.Msg("coroutine tick from " + Greeting());
            yield return new WaitForSeconds(3f);
        }
    }

    public override void OnUpdate()
    {
        // A few frames after init (Destroy is deferred to the end of a frame): how many persistent test objects exist?
        if (++_frames == 5)
        {
            int n = Resources.FindObjectsOfTypeAll<GameObject>().Count(g => g.name == PersistentName);
            LoggerInstance.Msg(PersistentName + " objects alive: " + n);
            LoggerInstance.Msg("HRTestBase textures alive: " + Resources.FindObjectsOfTypeAll<Texture2D>().Count(t => t.name.StartsWith("HRTestBase_Tex")) +
                               ", HRTestBase_Behaviour objects: " + Resources.FindObjectsOfTypeAll<GameObject>().Count(g => g.name == "HRTestBase_Behaviour"));
            LoggerInstance.Msg("HRTestBehaviour instances alive: " + Resources.FindObjectsOfTypeAll(
#if MONO
                typeof(HRTestBehaviour)
#else
                Il2CppType.Of<HRTestBehaviour>()
#endif
                ).Length);
        }
    }

    private static void ScalerPostfix() { }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName) =>
        LoggerInstance.Msg("OnSceneWasLoaded " + sceneName + " (" + Greeting() + ")");
}

/// <summary>A MonoBehaviour (injected into Il2Cpp on IL2CPP); logs from its Update every 3 seconds with the build that created it.</summary>
public class HRTestBehaviour : MonoBehaviour
{
    public static string Build = "";
    public static bool LogExceptions;
    private float _next;

#if !MONO
    public HRTestBehaviour(System.IntPtr ptr) : base(ptr) { }
#endif

    private void Update()
    {
        if (Time.time < _next) return;
        _next = Time.time + 3f;
        MelonLogger.Msg("[HRTestBase] behaviour Update from " + Build);
        if (LogExceptions)
#if MONO
            Debug.LogException(new System.InvalidOperationException("HRTestBase: deliberate exception from " + Build));
#else
            Debug.LogException(new Il2CppSystem.InvalidOperationException("HRTestBase: deliberate exception from " + Build));
#endif
    }
}
