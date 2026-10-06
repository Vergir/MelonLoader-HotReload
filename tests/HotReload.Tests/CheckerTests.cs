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
    public void Bundles_and_hooks_kept_in_fields_are_handled()
    {
        var s = Mod(LoadBundle, MonoModHook);
        s.FieldTypes.Add("UnityEngine.AssetBundle");
        s.FieldTypes.Add("MonoMod.RuntimeDetour.Hook");
        var r = Rules.Evaluate(s);
        Assert.Equal("READY", r.Verdict);
        Assert.All(r.Findings.Where(f => f.Id is "assetbundles" or "nativehooks"), f => Assert.Equal(Severity.Info, f.Severity));
    }

    [Fact]
    public void Field_types_are_read_from_metadata_including_generic_arguments()
    {
        var s = ModScan.Read(typeof(CheckerTests).Assembly.Location)!;
        Assert.Contains("MonoMod.RuntimeDetour.Hook", s.FieldTypes);          // HookSamples.KeptHook
        Assert.Contains("HotReload.Tests.FakeAssetBundle", s.FieldTypes);    // inside List<FakeAssetBundle>
    }

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

    private static Finding Single(Report r, string id) => Assert.Single(r.Findings, f => f.Id == id);

    [Fact]
    public void Quit_only_cleanup_is_handled_and_points_to_OnDeinitializeMelon()
    {
        var s = Mod();
        s.MelonType = "SomeMod.Main, SomeMod";
        s.MelonMethods.Add("OnApplicationQuit");
        var r = Rules.Evaluate(s);
        Assert.Equal("READY", r.Verdict);
        var f = Single(r, "quitonly");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal("SomeMod.Main::OnApplicationQuit", Assert.Single(f.Evidence));
        Assert.Contains("OnDeinitializeMelon", f.Advice);
    }

    [Fact]
    public void Quit_is_not_reported_with_OnDeinitializeMelon_or_on_other_types()
    {
        var withDeinit = Mod();
        withDeinit.MelonMethods.UnionWith(new[] { "OnApplicationQuit", "OnDeinitializeMelon" });
        Assert.DoesNotContain(Rules.Evaluate(withDeinit).Findings, f => f.Id == "quitonly");

        var monoBehaviour = Mod();                       // a MonoBehaviour's Unity message, not the melon's override
        monoBehaviour.DefinedMethods.Add("OnApplicationQuit");
        Assert.DoesNotContain(Rules.Evaluate(monoBehaviour).Findings, f => f.Id == "quitonly");
    }

    [Fact]
    public void Input_patches_are_handled()
    {
        var s = Mod();
        s.InputPatches.Add("[HarmonyPatch(typeof(UnityEngine.Input), \"GetKeyDown\")] on SomeMod.Patches");
        var r = Rules.Evaluate(s);
        Assert.Equal("READY", r.Verdict);
        Assert.Contains("reload key", Single(r, "inputpatch").Advice);
    }

    [Fact]
    public void Input_patches_are_found_in_attributes_and_IL_but_not_in_plain_calls()
    {
        var p = ModScan.Read(typeof(CheckerTests).Assembly.Location)!.InputPatches;
        Assert.Contains("[HarmonyPatch(typeof(UnityEngine.Input), \"GetKeyDown\")] on HotReload.Tests.CheckerSamples.AttributeInputPatch", p);
        Assert.Contains("typeof(UnityEngine.Input) \"GetKey\" in HotReload.Tests.CheckerSamples.ManualInputPatch::Apply", p);
        Assert.DoesNotContain(p, e => e.Contains("ReadsInputPlainly"));
        Assert.Equal(2, p.Count);
    }

    [Theory]
    [InlineData("UnityEngine.Texture2D", ".ctor", false, "UnityEngine.CoreModule")]
    [InlineData("UnityEngine.RenderTexture", ".ctor", false, "UnityEngine.CoreModule")]
    [InlineData("UnityEngine.Material", ".ctor", false, "UnityEngine")]
    [InlineData("UnityEngine.Mesh", ".ctor", false, "UnityEngine")]
    [InlineData("UnityEngine.Cubemap", ".ctor", false, "UnityEngine.CoreModule")]
    [InlineData("UnityEngine.Sprite", "Create", false, "UnityEngine.CoreModule")]
    [InlineData("UnityEngine.ScriptableObject", "CreateInstance", true, "UnityEngine.CoreModule")]
    [InlineData("UnityEngine.ScriptableObject", "CreateInstance", false, "UnityEngine")]
    public void Created_assets_are_handled(string type, string member, bool generic, string scope)
    {
        var r = Rules.Evaluate(Mod(new MemberRef(type, member, scope, generic)));
        Assert.Equal("READY", r.Verdict);
        var f = Single(r, "assets");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("DestroyOldAssets", f.Advice);
        Assert.Contains("blank", f.Advice);
    }

    [Fact]
    public void Asset_rule_ignores_other_constructors() =>
        Assert.DoesNotContain(Rules.Evaluate(Mod(new MemberRef("UnityEngine.Rect", ".ctor", "UnityEngine.CoreModule", false))).Findings, f => f.Id == "assets");

    [Fact]
    public void New_GameObjects_are_info_and_name_the_opt_in_setting()
    {
        var r = Rules.Evaluate(Mod(new MemberRef("UnityEngine.GameObject", ".ctor", "UnityEngine.CoreModule", false),
                                   new MemberRef("UnityEngine.GameObject", "AddComponent", "UnityEngine.CoreModule", true)));
        Assert.Equal("READY", r.Verdict);
        var f = Single(r, "objects");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("DestroyOldGameObjects", f.Advice);
        Assert.Equal("UnityEngine.GameObject::.ctor", Assert.Single(f.Evidence));
        Assert.Equal("UnityEngine.GameObject::AddComponent<T>", Assert.Single(Single(r, "components").Evidence));
    }

    [Fact]
    public void Assembly_Location_advice_names_the_load_time_caveat()
    {
        var f = Single(Rules.Evaluate(Mod(new MemberRef("System.Reflection.Assembly", "get_Location", "System.Runtime", false))), "location");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Contains("OnEarlyInitializeMelon", f.Advice);
        Assert.Contains("MelonEnvironment.ModsDirectory", f.Advice);
    }

    [Fact]
    public void UI_Toolkit_types_are_reported_from_type_references()
    {
        var s = Mod();
        s.TypeRefs.UnionWith(new[] { "UnityEngine.UIElements.UIDocument", "UnityEngine.UIElements.PanelSettings", "UnityEngine.UIElements.Label" });
        var f = Single(Rules.Evaluate(s), "uitoolkit");
        Assert.Equal(Severity.Info, f.Severity);
        Assert.Equal(new[] { "UnityEngine.UIElements.PanelSettings", "UnityEngine.UIElements.UIDocument" }, f.Evidence);
    }

    [Fact]
    public void Type_references_and_melon_methods_are_read_from_metadata()
    {
        var s = ModScan.Read(typeof(HotReload.AsmNames).Assembly.Location)!;
        Assert.Contains("MelonLoader.MelonPlugin", s.TypeRefs);
        Assert.Contains("OnInitializeMelon", s.MelonMethods);
        Assert.DoesNotContain("Strip", s.MelonMethods);             // AsmNames.Strip is not on the melon type
    }

    [Fact]
    public void Timer_advice_mentions_in_flight_work() =>
        Assert.Contains("HttpClient", Single(Rules.Evaluate(Mod(new MemberRef("System.Threading.Tasks.Task", "Run", "System.Runtime", false))), "timers").Advice);

    private static readonly MemberRef HideCursor = new("UnityEngine.Cursor", "set_visible", "UnityEngine.CoreModule", false);

    [Theory]
    [InlineData("UnityEngine.Cursor", "set_lockState")]
    [InlineData("UnityEngine.Time", "set_timeScale")]
    [InlineData("UnityEngine.Application", "set_targetFrameRate")]
    [InlineData("UnityEngine.QualitySettings", "set_vSyncCount")]
    [InlineData("UnityEngine.EventSystems.EventSystem", "set_current")]
    public void Global_state_without_OnDeinitializeMelon_needs_review(string type, string member)
    {
        var r = Rules.Evaluate(Mod(new MemberRef(type, member, "UnityEngine.CoreModule", false)));
        Assert.Equal("REVIEW", r.Verdict);
        Assert.Equal(Severity.Review, Single(r, "globalstate").Severity);
    }

    [Fact]
    public void Global_state_is_not_reported_with_OnDeinitializeMelon_and_never_raises_to_needs_cleanup()
    {
        var withDeinit = Mod(HideCursor);
        withDeinit.DefinedMethods.Add("OnDeinitializeMelon");
        var r = Rules.Evaluate(withDeinit);
        Assert.Equal("READY", r.Verdict);
        Assert.DoesNotContain(r.Findings, f => f.Id == "globalstate");

        Assert.Equal("NEEDS CLEANUP", Rules.Evaluate(Mod(HideCursor, LoadBundle)).Verdict);   // the cleanup item decides
        Assert.DoesNotContain(Rules.Evaluate(Mod(new MemberRef("UnityEngine.Cursor", "get_visible", "UnityEngine.CoreModule", false))).Findings,
            f => f.Id == "globalstate");
    }

    [Fact]
    public void Review_items_are_listed_with_cleanup_items_in_the_report()
    {
        var reports = new List<Report> { Rules.Evaluate(Mod(HideCursor)) };
        var md = Output.Markdown(reports);
        Assert.Contains("**review: Changes global Unity state.**", md);
        Assert.Contains("| **REVIEW** | globalstate |", md);
        Assert.Contains(",REVIEW,no,,globalstate,", Output.Csv(reports));
    }
}
