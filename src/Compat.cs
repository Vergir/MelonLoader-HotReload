using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace HotReload;

/// <summary>
/// The runtime HotReload runs on. It is one net472 DLL: in IL2CPP games MelonLoader runs it on .NET 6, in Mono games
/// on Unity's Mono. Code that differs between the two branches on <see cref="IsMono"/> at runtime.
/// </summary>
internal static class Compat
{
    /// <summary>True in Mono games, false in IL2CPP games (.NET 6).</summary>
    public static readonly bool IsMono = !MelonLoader.MelonUtils.IsGameIl2Cpp();

    private static int _pid;
    public static int ProcessId => _pid != 0 ? _pid : _pid = System.Diagnostics.Process.GetCurrentProcess().Id;

    public static bool IsWindows => Environment.OSVersion.Platform == PlatformID.Win32NT;

    public static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    /// <summary>Path of <paramref name="path"/> relative to <paramref name="root"/> ("." for the root itself).</summary>
    public static string RelativePath(string root, string path)
    {
        var r = Path.GetFullPath(root).TrimEnd('\\', '/');
        var p = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (string.Equals(r, p, StringComparison.OrdinalIgnoreCase)) return ".";
        return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? p.Substring(r.Length + 1) : p;
    }

    public static string RuntimeDescription()
    {
        var mono = Type.GetType("Mono.Runtime");
        var display = mono?.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null) as string;
        return display != null ? "Mono " + display : ".NET " + Environment.Version;
    }
}

/// <summary>
/// Assembly names as HotReload tracks them. The Mono build loads each reloaded build under a unique name
/// ("Name__hr3"), because Mono binds references to the first loaded assembly of a name; everything that compares
/// names goes through <see cref="Of(Assembly)"/> / <see cref="Strip"/> to get the original name back.
/// </summary>
internal static class AsmNames
{
    private static readonly Regex Suffix = new Regex(@"__hr\d+$", RegexOptions.Compiled);

    public static string Strip(string? name) => name == null ? "" : Suffix.Replace(name, "");

    public static string Of(Assembly asm) => Strip(asm.GetName().Name);

    public static string Unique(string name, int generation) => Strip(name) + "__hr" + generation;
}

/// <summary>Reads assembly metadata from bytes with Mono.Cecil (shipped with every MelonLoader build) without loading it.</summary>
internal static class AssemblyMeta
{
    /// <summary>Name and [MelonInfo] presence. False for partially written or non-.NET files.</summary>
    public static bool TryInspect(byte[] bytes, out string name, out bool isMelon)
    {
        name = ""; isMelon = false;
        try
        {
            using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
            name = asm.Name.Name;
            isMelon = asm.CustomAttributes.Any(a => a.AttributeType.FullName == "MelonLoader.MelonInfoAttribute");
            return name.Length > 0;
        }
        catch { return false; }
    }

    private static DefaultAssemblyResolver? _resolver;

    /// <summary>Resolves references from the folders of everything loaded (game Managed/, MelonLoader, Mods, UserLibs, shadow copies).</summary>
    private static DefaultAssemblyResolver Resolver()
    {
        _resolver ??= new DefaultAssemblyResolver();
        foreach (var dir in AppDomain.CurrentDomain.GetAssemblies()
                     .Select(a => { try { return a.IsDynamic ? "" : Path.GetDirectoryName(a.Location) ?? ""; } catch { return ""; } })
                     .Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_resolver.GetSearchDirectories().Contains(dir, StringComparer.OrdinalIgnoreCase)) _resolver.AddSearchDirectory(dir);
        }
        return _resolver;
    }

    /// <summary>
    /// What MelonLoader sorts melons by at startup: [MelonPriority] (lower first) and the names the assembly depends on
    /// (assembly references, [MelonAdditionalDependencies], [MelonOptionalDependencies]). False for unreadable files.
    /// </summary>
    public static bool TryReadLoadOrder(byte[] bytes, out string name, out int priority, out List<string> dependsOn)
    {
        name = ""; priority = 0; dependsOn = new List<string>();
        try
        {
            using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
            name = asm.Name.Name;
            dependsOn.AddRange(asm.MainModule.AssemblyReferences.Select(r => AsmNames.Strip(r.Name)));
            foreach (var a in asm.CustomAttributes)
            {
                switch (a.AttributeType.FullName)
                {
                    case "MelonLoader.MelonPriorityAttribute":
                        if (a.ConstructorArguments.Count > 0 && a.ConstructorArguments[0].Value is int p) priority = p;
                        break;
                    case "MelonLoader.MelonAdditionalDependenciesAttribute":
                    case "MelonLoader.MelonOptionalDependenciesAttribute":
                        foreach (var arg in a.ConstructorArguments)
                            if (arg.Value is CustomAttributeArgument[] names)
                                dependsOn.AddRange(names.Select(n => n.Value as string).Where(n => !string.IsNullOrEmpty(n)).Select(n => AsmNames.Strip(n!)));
                        break;
                }
            }
            name = AsmNames.Strip(name);
            return name.Length > 0;
        }
        catch { return false; }
    }

    public static List<string> ReferencedNames(byte[] bytes)
    {
        try
        {
            using var asm = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes));
            return asm.MainModule.AssemblyReferences.Select(r => AsmNames.Strip(r.Name)).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// Mono build: renames the assembly to <paramref name="newName"/> and points its references at the currently loaded
    /// builds (<paramref name="currentNames"/>: original name -> loaded name), so it binds to the fresh copies. The
    /// assembly is left unsigned. Symbols are rewritten when a pdb is given.
    /// </summary>
    public static (byte[] dll, byte[]? pdb) Rename(byte[] bytes, byte[]? pdb, string newName, IDictionary<string, string> currentNames)
    {
        var rp = new ReaderParameters { ReadSymbols = pdb != null, InMemory = true, AssemblyResolver = Resolver() };
        if (pdb != null) { rp.SymbolStream = new MemoryStream(pdb); rp.SymbolReaderProvider = new Mono.Cecil.Cil.PortablePdbReaderProvider(); }
        AssemblyDefinition asm;
        try { asm = AssemblyDefinition.ReadAssembly(new MemoryStream(bytes), rp); }
        catch when (pdb != null) { return Rename(bytes, null, newName, currentNames); } // unreadable symbols: go without
        using (asm)
        {
            asm.Name.Name = newName;
            asm.Name.PublicKey = Array.Empty<byte>();
            asm.Name.HasPublicKey = false;
            asm.MainModule.Name = newName + ".dll";
            foreach (var r in asm.MainModule.AssemblyReferences)
            {
                if (!currentNames.TryGetValue(AsmNames.Strip(r.Name), out var current)) continue;
                r.Name = current;
                r.PublicKeyToken = Array.Empty<byte>();
            }
            var dllOut = new MemoryStream();
            var pdbOut = pdb != null ? new MemoryStream() : null;
            var wp = new WriterParameters();
            if (pdbOut != null) { wp.WriteSymbols = true; wp.SymbolStream = pdbOut; wp.SymbolWriterProvider = new Mono.Cecil.Cil.PortablePdbWriterProvider(); }
            asm.Write(dllOut, wp);
            return (dllOut.ToArray(), pdbOut?.ToArray());
        }
    }
}
