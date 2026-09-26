using System;
using System.Collections.Generic;
using System.Diagnostics;

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

    /// <summary>Simple name of the first non-infrastructure assembly on the stack, or null (e.g. the game itself called).</summary>
    public static string? FindModAssembly(int skipFrames = 1)
    {
        var frames = new StackTrace(skipFrames + 1, false).GetFrames();
        foreach (var f in frames)
        {
            var asm = f.GetMethod()?.DeclaringType?.Assembly;
            if (asm == null || asm.IsDynamic) continue;
            var name = asm.GetName().Name;
            if (name == null || Infrastructure.Contains(name)) continue;
            bool infra = false;
            foreach (var p in InfrastructurePrefixes)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { infra = true; break; }
            if (!infra) return name;
        }
        return null;
    }
}
