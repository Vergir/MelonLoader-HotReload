using HRTestDependent;
using MelonLoader;

[assembly: MelonInfo(typeof(HRTestDependentMod), "HRTestDependent", "1.0.0", "HotReload tests")]
[assembly: MelonGame("Moon Studios", "NoRestForTheWicked")]

namespace HRTestDependent;

/// <summary>References HRTestBase; after HRTestBase reloads, HotReload should reload this one so it calls the new build.</summary>
public class HRTestDependentMod : MelonMod
{
    public override void OnInitializeMelon() =>
        LoggerInstance.Msg("sees " + HRTestBase.HRTestBaseMod.Greeting());
}
