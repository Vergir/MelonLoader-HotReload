using HRTestPlugin;
using MelonLoader;

[assembly: MelonInfo(typeof(HRTestPluginMelon), "HRTestPlugin", "1.0.0", "HotReload tests")]

namespace HRTestPlugin;

/// <summary>
/// A plugin that HotReload should reload like a mod; logs from OnInitializeMelon and OnApplicationStarted. Also the
/// target of HRTestBase's MonoMod hooks: it logs Probe() every 3 seconds, so the log shows which hooks are active.
/// </summary>
public class HRTestPluginMelon : MelonPlugin
{
    private static string Build => typeof(HRTestPluginMelon).Assembly.GetName().Version?.ToString() ?? "?";
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _next = 3000;

    public override void OnInitializeMelon() => LoggerInstance.Msg("OnInitializeMelon, build " + Build);

    public override void OnApplicationStarted() => LoggerInstance.Msg("OnApplicationStarted, build " + Build);

    /// <summary>Hooked by HRTestBase; not inlined, so the hooks always apply.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static string Probe() => "probe";

    public override void OnUpdate()
    {
        if (_clock.ElapsedMilliseconds < _next) return;
        _next = _clock.ElapsedMilliseconds + 3000;
        LoggerInstance.Msg("Probe() = " + Probe());
    }
}
