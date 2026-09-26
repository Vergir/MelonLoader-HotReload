using System.Collections;
using System.Linq;
using HarmonyLib;
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
/// across scenes, an endless coroutine and a timer callback, none of which this mod cleans up itself (0.4).
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

        var go = new GameObject(PersistentName);
        Object.DontDestroyOnLoad(go);
        MelonCoroutines.Start(Ticker());
        var build = Greeting();
        _timer = new System.Threading.Timer(_ => MelonLogger.Msg("[HRTestBase] timer from " + build), null, 3000, 3000);
    }

    private const string PersistentName = "HRTestBase_Persistent";
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
        }
    }

    private static void ScalerPostfix() { }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName) =>
        LoggerInstance.Msg("OnSceneWasLoaded " + sceneName + " (" + Greeting() + ")");
}
