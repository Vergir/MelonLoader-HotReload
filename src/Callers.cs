using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MelonLoader;

namespace HotReload;

/// <summary>Finds which mod assembly is behind a call, by walking the managed stack past loader, runtime and interop frames.</summary>
internal static class Callers
{
    private static readonly HashSet<string> Infrastructure = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MelonLoader", "0Harmony", "System.Private.CoreLib", typeof(Callers).Assembly.GetName().Name!,
    };

    private static readonly string[] InfrastructurePrefixes =
    {
        "MonoMod", "Il2CppInterop", "Il2Cpp", "UnityEngine", "Unity.", "System.", "Microsoft.",
    };

    /// <summary>
    /// Simple name of the mod behind the current call: the first loaded melon assembly on the stack (so a helper library
    /// such as UniverseLib acting for UnityExplorer counts as UnityExplorer), else the first non-infrastructure assembly,
    /// else null (e.g. the game itself called).
    /// </summary>
    public static string? FindModAssembly(int skipFrames = 1)
    {
        var frames = new StackTrace(skipFrames + 1, false).GetFrames();
        string? firstOther = null;
        HashSet<System.Reflection.Assembly>? melons = null;
        foreach (var f in frames)
        {
            var asm = f.GetMethod()?.DeclaringType?.Assembly;
            if (asm == null || asm.IsDynamic) continue;
            var name = asm.GetName().Name;
            if (name == null || Infrastructure.Contains(name)) continue;
            bool infra = false;
            foreach (var p in InfrastructurePrefixes)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { infra = true; break; }
            if (infra) continue;
            melons ??= new HashSet<System.Reflection.Assembly>(MelonAssembly.LoadedAssemblies.Select(a => a.Assembly));
            if (melons.Contains(asm)) return name;
            firstOther ??= name;
        }
        return firstOther;
    }
}
