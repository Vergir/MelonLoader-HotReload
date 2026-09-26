using System;
using System.Collections.Generic;
using MelonLoader;

namespace HRTestLib;

/// <summary>
/// Test library for HotReload (deployed to UserLibs, no [MelonInfo]). Like UniverseLib it keeps global registrations
/// and refuses a second registration under the same id, so a mod reloaded without a fresh library fails.
/// </summary>
public static class Registry
{
    private static readonly HashSet<string> Ids = new HashSet<string>();

    public static string Build => typeof(Registry).Assembly.GetName().Version?.ToString() ?? "?";

    public static void Register(string id)
    {
        if (!Ids.Add(id)) throw new InvalidOperationException("HRTestLib: '" + id + "' is already registered");
        MelonLogger.Msg("[HRTestLib] registered " + id + " (library build " + Build + ")");
    }
}
