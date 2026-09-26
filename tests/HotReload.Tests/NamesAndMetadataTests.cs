using HotReload;
using Mono.Cecil;
using Xunit;

namespace HotReload.Tests;

public class AsmNamesTests
{
    [Theory]
    [InlineData("MyMod", "MyMod")]
    [InlineData("MyMod__hr3", "MyMod")]
    [InlineData("MyMod__hr12", "MyMod")]
    [InlineData("My__hrMod", "My__hrMod")]      // suffix only at the end, digits required
    [InlineData("MyMod__hr", "MyMod__hr")]
    [InlineData(null, "")]
    public void Strip_removes_only_the_reload_suffix(string? name, string expected) =>
        Assert.Equal(expected, AsmNames.Strip(name));

    [Fact]
    public void Unique_replaces_an_existing_suffix() =>
        Assert.Equal("MyMod__hr7", AsmNames.Unique("MyMod__hr3", 7));
}

public class AssemblyMetaTests
{
    private static string HotReloadPath => typeof(AsmNames).Assembly.Location;
    private static byte[] HotReloadBytes => File.ReadAllBytes(HotReloadPath);

    [Fact]
    public void Inspect_reads_name_and_MelonInfo()
    {
        Assert.True(AssemblyMeta.TryInspect(HotReloadBytes, out var name, out var isMelon));
        Assert.Equal("HotReload", name);
        Assert.True(isMelon);
    }

    [Fact]
    public void Inspect_sees_a_plain_library_as_no_melon()
    {
        Assert.True(AssemblyMeta.TryInspect(File.ReadAllBytes(typeof(AssemblyMetaTests).Assembly.Location), out var name, out var isMelon));
        Assert.Equal("HotReload.Tests", name);
        Assert.False(isMelon);
    }

    [Fact]
    public void Inspect_rejects_garbage_and_half_written_files()
    {
        Assert.False(AssemblyMeta.TryInspect(new byte[] { 1, 2, 3, 4 }, out _, out _));
        var bytes = HotReloadBytes;
        Assert.False(AssemblyMeta.TryInspect(bytes.Take(bytes.Length / 3).ToArray(), out _, out _));
    }

    [Fact]
    public void ReferencedNames_lists_assembly_references() =>
        Assert.Contains("MelonLoader", AssemblyMeta.ReferencedNames(HotReloadBytes));

    [Fact]
    public void ReferencedNames_strips_the_reload_suffix()
    {
        var (dll, _) = AssemblyMeta.Rename(HotReloadBytes, null, "HotReload__hr1",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MelonLoader"] = "MelonLoader__hr4" });
        var refs = AssemblyMeta.ReferencedNames(dll);
        Assert.Contains("MelonLoader", refs);
        Assert.DoesNotContain("MelonLoader__hr4", refs);
    }

    [Fact]
    public void Rename_renames_the_assembly_and_points_references_at_current_builds()
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["MelonLoader"] = "MelonLoader__hr2" };
        var (dll, _) = AssemblyMeta.Rename(HotReloadBytes, null, "HotReload__hr7", current);

        using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(dll));
        Assert.Equal("HotReload__hr7", asm.Name.Name);
        Assert.Equal("HotReload__hr7.dll", asm.MainModule.Name);
        Assert.False(asm.Name.HasPublicKey);
        var refs = asm.MainModule.AssemblyReferences.Select(r => r.Name).ToList();
        Assert.Contains("MelonLoader__hr2", refs);
        Assert.DoesNotContain("MelonLoader", refs);
        Assert.Contains("0Harmony", refs); // not reloaded: untouched
    }

    [Fact]
    public void Rename_rewrites_the_pdb_when_there_is_one()
    {
        var pdbPath = Path.ChangeExtension(HotReloadPath, ".pdb");
        Assert.True(File.Exists(pdbPath), "HotReload.pdb should be copied next to HotReload.dll");
        var (_, pdb) = AssemblyMeta.Rename(HotReloadBytes, File.ReadAllBytes(pdbPath), "HotReload__hr1", new Dictionary<string, string>());
        Assert.NotNull(pdb);
        Assert.NotEmpty(pdb!);
    }

    [Fact]
    public void Rename_goes_without_symbols_when_the_pdb_is_unreadable()
    {
        var (dll, _) = AssemblyMeta.Rename(HotReloadBytes, new byte[] { 0, 1, 2 }, "HotReload__hr1", new Dictionary<string, string>());
        Assert.True(AssemblyMeta.TryInspect(dll, out var name, out _));
        Assert.Equal("HotReload__hr1", name);
    }
}

public class LoadOrderingTests
{
    private sealed record A(string Name, params string[] Refs);

    private static List<string> Order(params A[] items) =>
        LoadOrdering.Order(items, a => a.Name, a => a.Refs).Select(a => a.Name).ToList();

    [Fact]
    public void Libraries_come_before_the_mods_that_use_them() =>
        Assert.Equal(new[] { "Lib", "Base", "Dependent" },
            Order(new A("Dependent", "Base", "MelonLoader"), new A("Base", "Lib"), new A("Lib")));

    [Fact]
    public void Independent_items_keep_their_order() =>
        Assert.Equal(new[] { "X", "Y", "Z" }, Order(new A("X"), new A("Y"), new A("Z")));

    [Fact]
    public void Names_match_case_insensitively() =>
        Assert.Equal(new[] { "lib", "Mod" }, Order(new A("Mod", "LIB"), new A("lib")));

    [Fact]
    public void A_cycle_is_broken_instead_of_hanging()
    {
        var order = Order(new A("P", "Q"), new A("Q", "P"), new A("R", "P"));
        Assert.Equal(3, order.Count);
        Assert.True(order.IndexOf("R") > order.IndexOf("P"));
    }

    [Fact]
    public void Self_references_are_ignored() =>
        Assert.Equal(new[] { "Self" }, Order(new A("Self", "Self")));
}

public class KeyNameTests
{
    [Theory]
    [InlineData("F8", "F8")]
    [InlineData("Alpha1", "Digit1")]
    [InlineData("Keypad5", "Numpad5")]
    [InlineData("Return", "Enter")]
    [InlineData("LeftControl", "LeftCtrl")]
    [InlineData(" BackQuote ", "Backquote")]
    public void InputSystem_names(string keyCode, string expected) =>
        Assert.Equal(expected, KeyInput.InputSystemKeyName(keyCode));

    [Theory]
    [InlineData("F8", 0x77)]
    [InlineData("f1", 0x70)]
    [InlineData("F24", 0x87)]
    [InlineData("A", 0x41)]
    [InlineData("z", 0x5A)]
    [InlineData("Alpha1", 0x31)]
    [InlineData("Keypad3", 0x63)]
    [InlineData("Insert", 0x2D)]
    [InlineData("BackQuote", 0xC0)]
    public void Windows_virtual_keys(string keyCode, int expected) =>
        Assert.Equal(expected, KeyInput.VirtualKey(keyCode));

    [Theory]
    [InlineData("F25")]
    [InlineData("Mouse0")]
    [InlineData("Nonsense")]
    public void Keys_without_a_virtual_key(string keyCode) =>
        Assert.Null(KeyInput.VirtualKey(keyCode));
}
