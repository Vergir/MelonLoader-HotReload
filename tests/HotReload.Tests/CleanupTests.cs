using HotReload;
using MonoMod.RuntimeDetour;
using Xunit;

namespace HotReload.Tests;

/// <summary>A mod that hooks outside Harmony. Never run: the tests only read its IL.</summary>
public class HookSamples
{
    public static int Target() => 1;
    public static int OtherCallback() => 2;

    public static Hook? KeptHook;

    public void InstallHooks()
    {
        KeptHook = new Hook(typeof(HookSamples).GetMethod(nameof(Target))!, new Func<Func<int>, int>(orig => orig() + 1));
    }

    public void NotAHook()
    {
        Func<int> f = OtherCallback; // an ordinary delegate target: retired
        _ = f;
    }
}

public class HookAnalysisTests
{
    private static readonly Retirer.Analysis Analysis = Retirer.Analyze(typeof(HookSamples).Assembly);

    [Fact]
    public void Methods_handed_to_a_hook_constructor_are_hook_handlers_not_retired()
    {
        var handler = Analysis.HookHandlers.SingleOrDefault(m => m.Name.Contains("<InstallHooks>"));
        Assert.NotNull(handler);
        Assert.DoesNotContain(handler!, Analysis.Reachable);
    }

    [Fact]
    public void Hook_construction_sites_are_counted() => Assert.True(Analysis.HookSites >= 1);

    [Fact]
    public void Ordinary_delegate_targets_are_still_retired() =>
        Assert.Contains(Analysis.Reachable, m => m.Name == nameof(HookSamples.OtherCallback));

    [Theory]
    [InlineData(typeof(Hook), true)]
    [InlineData(typeof(Detour), true)]
    [InlineData(typeof(ILHook), true)]
    [InlineData(typeof(IDetour), true)]
    [InlineData(typeof(string), false)]
    [InlineData(typeof(HookSamples), false)]
    public void Hook_types(Type type, bool expected) => Assert.Equal(expected, ForeignHooks.IsHookType(type));
}

/// <summary>Stands in for a mod's bundle type: named like AssetBundle, with Unload(bool).</summary>
public class FakeAssetBundle
{
    public bool Unloaded;
    public void Unload(bool unloadAllLoadedObjects) => Unloaded = true;
}

public static class HeldSamples
{
    public static FakeAssetBundle? Single = new FakeAssetBundle();
    public static List<FakeAssetBundle> Many = new() { new FakeAssetBundle(), new FakeAssetBundle() };
    public static Dictionary<string, FakeAssetBundle> ByName = new() { ["ui"] = new FakeAssetBundle() };
    public static string Unrelated = "not read";
}

/// <summary>Reading this type's statics would throw from its static constructor; FieldScan must not touch it.</summary>
public static class Explosive
{
    public static readonly string Boom = Fail();
    private static string Fail() => throw new InvalidOperationException("static constructor ran");
}

public class FieldScanTests
{
    private static List<object> Scan() =>
        FieldScan.Held(new[] { typeof(HeldSamples).Assembly }, Array.Empty<MelonLoader.MelonBase>(),
            t => t == typeof(FakeAssetBundle), o => o is FakeAssetBundle);

    [Fact]
    public void Finds_objects_in_fields_lists_and_dictionaries()
    {
        var found = Scan();
        Assert.Contains(HeldSamples.Single!, found);
        Assert.All(HeldSamples.Many, b => Assert.Contains(b, found));
        Assert.Contains(HeldSamples.ByName["ui"], found);
    }

    [Fact]
    public void Does_not_read_fields_that_cannot_hold_wanted_objects()
    {
        Scan(); // would throw TypeInitializationException if Explosive.Boom were read
        Assert.Throws<TypeInitializationException>(() => Explosive.Boom);
    }
}
