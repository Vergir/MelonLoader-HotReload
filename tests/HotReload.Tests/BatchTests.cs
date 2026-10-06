using HotReload;
using MelonLoader;
using Xunit;

public class BatchOrderingTests
{
    private sealed record M(string Name, int Priority, params string[] DependsOn);

    private static List<string> Order(params M[] items) =>
        LoadOrdering.OrderBatch(items, m => m.Name, m => m.Priority, m => m.DependsOn).Select(m => m.Name).ToList();

    [Fact]
    public void Lower_priority_loads_first() =>
        Assert.Equal(new[] { "ModSettingsTab", "CompareAtVendors", "QuickStart" },
            Order(new M("QuickStart", 0), new M("CompareAtVendors", 0), new M("ModSettingsTab", -100)));

    [Fact]
    public void Same_priority_goes_by_name() =>
        Assert.Equal(new[] { "Alpha", "beta", "Gamma" }, Order(new M("Gamma", 0), new M("beta", 0), new M("Alpha", 0)));

    [Fact]
    public void Dependencies_beat_priority() =>
        Assert.Equal(new[] { "Lib", "Early" }, Order(new M("Early", -50, "Lib"), new M("Lib", 10)));

    [Fact]
    public void Dependencies_outside_the_batch_are_ignored() =>
        Assert.Equal(new[] { "A", "B" }, Order(new M("B", 0, "MelonLoader", "A"), new M("A", 0, "UnityEngine")));
}

public class LoadOrderMetadataTests
{
    [Fact]
    public void Reads_priority_and_references_of_a_built_assembly()
    {
        var path = typeof(AsmNames).Assembly.Location;
        Assert.True(AssemblyMeta.TryReadLoadOrder(File.ReadAllBytes(path), out var name, out int priority, out var dependsOn));
        Assert.Equal("HotReload", name);
        Assert.Equal(-10000, priority);
        Assert.Contains("MelonLoader", dependsOn);
    }

    [Fact]
    public void Garbage_is_not_readable() =>
        Assert.False(AssemblyMeta.TryReadLoadOrder(new byte[] { 1, 2, 3 }, out _, out _, out _));
}

public class QuitFallbackTests
{
    private sealed class QuitOnly : MelonMod { public override void OnApplicationQuit() { } }
    private sealed class Both : MelonMod { public override void OnApplicationQuit() { } public override void OnDeinitializeMelon() { } }
    private sealed class Neither : MelonMod { }
    private class Base : MelonMod { public override void OnApplicationQuit() { } }
    private sealed class Derived : Base { }

    [Fact] public void Quit_without_deinit_wants_the_fallback() => Assert.True(QuitFallback.Wants(typeof(QuitOnly)));
    [Fact] public void Quit_with_deinit_does_not() => Assert.False(QuitFallback.Wants(typeof(Both)));
    [Fact] public void No_quit_does_not() => Assert.False(QuitFallback.Wants(typeof(Neither)));
    [Fact] public void An_inherited_quit_counts() => Assert.True(QuitFallback.Wants(typeof(Derived)));
}
