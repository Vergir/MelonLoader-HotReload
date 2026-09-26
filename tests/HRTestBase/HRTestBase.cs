using System.Collections;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using HRTestBase;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

[assembly: MelonInfo(typeof(HRTestBaseMod), "HRTestBase", "1.0.0", "HotReload tests")]
[assembly: MelonGame("Moon Studios", "NoRestForTheWicked")]
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

        // A library that refuses a second registration: works only if HotReload reloads the library too (0.7).
        HRTestLib.Registry.Register("hrtestbase");

        // A plain category whose name has nothing to do with the mod: found through this static field on reload.
        var settings = MelonPreferences.CreateCategory("HRTest Unrelated Name");
        _reloads = settings.CreateEntry("Reloads", 0);
        _reloads.Value++;
        settings.SaveToFile(false);
        LoggerInstance.Msg("plain category entry Reloads = " + _reloads.Value);

        // Il2Cpp class injection: the reloaded build registers a class with the same full name.
        ClassInjector.RegisterTypeInIl2Cpp<HRTestBehaviour>();
        HRTestBehaviour.Build = Greeting();
        new GameObject("HRTestBase_Behaviour").AddComponent<HRTestBehaviour>(); // normal scene object, not kept across scenes

        var go = new GameObject(PersistentName);
        Object.DontDestroyOnLoad(go);
        MelonCoroutines.Start(Ticker());
        var build = Greeting();
        _timer = new System.Threading.Timer(_ => MelonLogger.Msg("[HRTestBase] timer from " + build), null, 3000, 3000);
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
            LoggerInstance.Msg("HRTestBehaviour instances alive: " + Resources.FindObjectsOfTypeAll(Il2CppType.Of<HRTestBehaviour>()).Length);
        }
    }

    private static void ScalerPostfix() { }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName) =>
        LoggerInstance.Msg("OnSceneWasLoaded " + sceneName + " (" + Greeting() + ")");
}

/// <summary>Injected into Il2Cpp; logs from its Update every 3 seconds with the build that created it.</summary>
public class HRTestBehaviour : MonoBehaviour
{
    public static string Build = "";
    private float _next;

    public HRTestBehaviour(System.IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        if (Time.time < _next) return;
        _next = Time.time + 3f;
        MelonLogger.Msg("[HRTestBase] behaviour Update from " + Build);
    }
}
