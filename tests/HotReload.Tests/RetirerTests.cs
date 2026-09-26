using System.Reflection;
using HotReload;
using Xunit;

namespace HotReload.Tests;

/// <summary>Code shaped like a mod: what the game could still call after the mod is unloaded.</summary>
public class RetireSamples
{
    public static int Counter;
    public event Action? Changed;

    public static int HandedToTheGame() => 1;
    public static int NeverHandedOut() => 2;

    public void Subscribe()
    {
        Changed += () => Counter++;                  // lambda: delegate target
        Func<int> f = HandedToTheGame;               // method group: delegate target
        Changed?.Invoke();
        _ = f;
    }

    public System.Collections.IEnumerator Coroutine() // iterator: MoveNext
    {
        Counter++;
        yield return null;
    }

    public async Task PendingWork()                   // async: MoveNext
    {
        await Task.Yield();
        Counter++;
    }
}

/// <summary>Stands in for an injected Il2Cpp class / a Mono component: every method is retired.</summary>
public class WholeTypeSample
{
    public void Update() { }
    private void OnGUI() { }
}

public class RetirerTests
{
    private static readonly List<MethodInfo> Reachable =
        Retirer.ReachableMethods(typeof(RetireSamples).Assembly, new[] { typeof(WholeTypeSample) }).ToList();

    private static bool Has(Type t, Func<MethodInfo, bool> match) => Reachable.Any(m => m.DeclaringType == t && match(m));

    [Fact]
    public void Method_groups_turned_into_delegates_are_found() =>
        Assert.True(Has(typeof(RetireSamples), m => m.Name == nameof(RetireSamples.HandedToTheGame)));

    [Fact]
    public void Methods_never_turned_into_delegates_are_left_alone() =>
        Assert.False(Has(typeof(RetireSamples), m => m.Name == nameof(RetireSamples.NeverHandedOut)));

    [Fact]
    public void Lambdas_are_found() =>
        Assert.Contains(Reachable, m => m.DeclaringType?.DeclaringType == typeof(RetireSamples) && m.Name.Contains("<Subscribe>"));

    [Fact]
    public void Iterator_and_async_state_machines_are_found()
    {
        var nested = typeof(RetireSamples).GetNestedTypes(BindingFlags.NonPublic);
        var iterator = nested.Single(t => t.Name.Contains("<Coroutine>"));
        var async = nested.Single(t => t.Name.Contains("<PendingWork>"));
        Assert.True(Has(iterator, m => m.Name == "MoveNext"));
        Assert.True(Has(async, m => m.Name == "MoveNext"));
    }

    [Fact]
    public void Whole_types_have_every_method_retired()
    {
        Assert.True(Has(typeof(WholeTypeSample), m => m.Name == "Update"));
        Assert.True(Has(typeof(WholeTypeSample), m => m.Name == "OnGUI"));
    }

    [Fact]
    public void Only_methods_of_the_scanned_assembly_are_returned() =>
        Assert.All(Reachable, m => Assert.Equal(typeof(RetireSamples).Assembly, m.DeclaringType!.Assembly));

    [Fact]
    public void Abstract_and_open_generic_methods_are_skipped() =>
        Assert.All(Reachable, m => Assert.False(m.IsAbstract || m.ContainsGenericParameters));
}
