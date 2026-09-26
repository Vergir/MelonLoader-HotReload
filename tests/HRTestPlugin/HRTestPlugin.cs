using HRTestPlugin;
using MelonLoader;

[assembly: MelonInfo(typeof(HRTestPluginMelon), "HRTestPlugin", "1.0.0", "HotReload tests")]
[assembly: MelonGame("Moon Studios", "NoRestForTheWicked")]

namespace HRTestPlugin;

/// <summary>A plugin that HotReload should reload like a mod; logs from OnInitializeMelon and OnApplicationStarted.</summary>
public class HRTestPluginMelon : MelonPlugin
{
    private static string Build => typeof(HRTestPluginMelon).Assembly.GetName().Version?.ToString() ?? "?";

    public override void OnInitializeMelon() => LoggerInstance.Msg("OnInitializeMelon, build " + Build);

    public override void OnApplicationStarted() => LoggerInstance.Msg("OnApplicationStarted, build " + Build);
}
