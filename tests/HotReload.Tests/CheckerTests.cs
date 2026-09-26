using HotReloadCheck;
using Xunit;

namespace HotReload.Tests;

public class CheckerTests
{
    private static ModScan Mod(params MemberRef[] refs)
    {
        var s = new ModScan { AssemblyName = "SomeMod", MelonName = "SomeMod", Kind = MelonKind.Mod, Flavor = Flavor.Il2CppInterop };
        s.MemberRefs.AddRange(refs);
        return s;
    }

    private static readonly MemberRef LoadBundle = new("UnityEngine.AssetBundle", "LoadFromFile", "UnityEngine.AssetBundleModule", false);
    private static readonly MemberRef MonoModHook = new("MonoMod.RuntimeDetour.Hook", ".ctor", "MonoMod.RuntimeDetour", false);
    private static readonly MemberRef Coroutine = new("MelonLoader.MelonCoroutines", "Start", "MelonLoader", false);

    [Fact]
    public void A_mod_with_only_handled_things_is_ready()
    {
        var r = Rules.Evaluate(Mod(Coroutine));
        Assert.Equal("READY", r.Verdict);
        Assert.Contains(r.Findings, f => f.Id == "coroutines" && f.Severity == Severity.Info);
    }

    [Fact]
    public void Cleanup_items_without_OnDeinitializeMelon_need_cleanup() =>
        Assert.Equal("NEEDS CLEANUP", Rules.Evaluate(Mod(LoadBundle)).Verdict);

    [Fact]
    public void Cleanup_items_with_OnDeinitializeMelon_need_review()
    {
        var s = Mod(LoadBundle, MonoModHook);
        s.DefinedMethods.Add("OnDeinitializeMelon");
        var r = Rules.Evaluate(s);
        Assert.Equal("REVIEW", r.Verdict);
        Assert.Contains(r.Findings, f => f.Id == "assetbundles");
        Assert.Contains(r.Findings, f => f.Id == "nativehooks");
    }

    [Fact]
    public void MelonLoader_05_mods_are_unsupported()
    {
        var s = Mod();
        s.Flavor = Flavor.Unhollower;
        Assert.StartsWith("UNSUPPORTED", Rules.Evaluate(s).Verdict);
    }

    [Fact]
    public void Libraries_are_reported_as_libraries()
    {
        var s = Mod(LoadBundle);
        s.Kind = MelonKind.Library;
        Assert.Equal("LIBRARY", Rules.Evaluate(s).Verdict);
    }

    [Fact]
    public void Scanning_a_real_plugin_reads_its_MelonInfo()
    {
        var s = ModScan.Read(typeof(HotReload.AsmNames).Assembly.Location);
        Assert.NotNull(s);
        Assert.Equal(MelonKind.Plugin, s!.Kind);
        Assert.Equal("HotReload", s.MelonName);
        Assert.Contains("MelonLoader", s.AssemblyRefs);
    }

    [Fact]
    public void The_report_names_the_HotReload_version()
    {
        var md = Output.Markdown(new List<Report> { Rules.Evaluate(Mod(Coroutine)) });
        Assert.Contains("against HotReload " + Program.HotReloadVersion, md);
        Assert.DoesNotContain("?", Program.HotReloadVersion);
    }
}
