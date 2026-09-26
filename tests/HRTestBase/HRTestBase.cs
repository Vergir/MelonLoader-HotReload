using HarmonyLib;
using HRTestBase;
using MelonLoader;
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
/// Exercises what HotReload 0.3 cleans up: a patch through a separate Harmony instance, a reflective preference
/// category whose value must survive reloads, and scene callbacks for scenes that are already open.
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
    }

    private static void ScalerPostfix() { }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName) =>
        LoggerInstance.Msg("OnSceneWasLoaded " + sceneName + " (" + Greeting() + ")");
}
