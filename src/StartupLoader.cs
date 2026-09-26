using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;

namespace HotReload;

/// <summary>
/// Keeps Mods/*.dll unlocked while the game runs. MelonLoader loads mods with LoadFromAssemblyPath, which keeps the file
/// open, so a build cannot overwrite it. HotReload registers before mods are loaded, copies Mods/*.dll (+pdb) to a
/// per-session shadow folder and swaps that folder into MelonLoader's list of mod folders. MelonLoader locks the copies;
/// the originals stay free for builds, and HotReload maps every path back to Mods/.
///
/// Redirecting MelonAssembly.LoadMelonAssembly(string, bool) with a Harmony prefix does not work: the game runs on the
/// installed .NET runtime (10.x), whose JIT inlines that method into MelonLoader's folder loader, which is already
/// compiled (for UserLibs) before any plugin can patch it. The folder list is plain data read later, so it is reliable.
/// </summary>
internal static class StartupLoader
{
    private static readonly ConditionalWeakTable<Assembly, string> Locations = new ConditionalWeakTable<Assembly, string>();
    private static readonly Dictionary<string, string> ShadowToMods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly PropertyInfo? MelonAssemblyLocation = typeof(MelonAssembly).GetProperty(nameof(MelonAssembly.Location));
    private static MelonLogger.Instance _log = null!;
    private static bool _locationPatched;
    private static string? _shadowDir;
    public static int Shadowed { get; private set; }

    /// <summary>Makes Assembly.Location of <paramref name="asm"/> return <paramref name="path"/>.</summary>
    public static void RememberLocation(Assembly asm, string path) => Locations.AddOrUpdate(asm, path);

    public static void InstallLocationPatch(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        _log = log;
        try
        {
            var runtimeAssembly = typeof(object).Assembly.GetType("System.Reflection.RuntimeAssembly", throwOnError: true)!;
            var getter = AccessTools.PropertyGetter(runtimeAssembly, nameof(Assembly.Location));
            harmony.Patch(getter, postfix: new HarmonyMethod(typeof(StartupLoader).GetMethod(nameof(LocationPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            _locationPatched = true;
        }
        catch (Exception e)
        {
            log.Warning("Could not patch Assembly.Location; reloaded mods will see an empty Location: " + e.Message);
        }
    }

    private static void LocationPostfix(Assembly __instance, ref string __result)
    {
        if (Locations.TryGetValue(__instance, out var path)) __result = path;
    }

    /// <summary>Called while plugins register, before MelonLoader scans Mods/.</summary>
    public static void InstallShadowMods(MelonLogger.Instance log)
    {
        _log = log;
        try
        {
            var handler = typeof(MelonAssembly).Assembly.GetType("MelonLoader.Melons.MelonFolderHandler", throwOnError: true)!;
            var field = handler.GetField("_modDirs", BindingFlags.NonPublic | BindingFlags.Static)
                        ?? throw new MissingFieldException("MelonFolderHandler._modDirs");
            var dirs = (List<string>)field.GetValue(null)!;
            var mods = Path.GetFullPath(MelonEnvironment.ModsDirectory).TrimEnd('\\', '/');
            int idx = dirs.FindIndex(d => string.Equals(Path.GetFullPath(d).TrimEnd('\\', '/'), mods, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { log.Warning("Mods folder not in MelonLoader's folder list; mod DLLs stay locked."); return; }

            var root = Path.Combine(MelonEnvironment.UserDataDirectory, "HotReload", "Shadow");
            Directory.CreateDirectory(root);
            foreach (var old in Directory.EnumerateDirectories(root))
            {
                try { Directory.Delete(old, recursive: true); } catch { /* another game instance still holds it */ }
            }
            _shadowDir = Path.Combine(root, Environment.ProcessId.ToString());
            Directory.CreateDirectory(_shadowDir);
            foreach (var f in Directory.EnumerateFiles(mods, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
            {
                var copy = Path.Combine(_shadowDir, Path.GetFileName(f));
                File.Copy(f, copy, overwrite: true);
                ShadowToMods[copy] = f;
            }
            dirs[idx] = _shadowDir;

            // Map paths back as soon as MelonLoader has an Assembly / a melon, before mods run any code.
            MelonAssembly.OnAssemblyResolving.Subscribe(OnAssemblyResolving);
            MelonBase.OnMelonInitializing.Subscribe(OnMelonInitializing);
        }
        catch (Exception e)
        {
            log.Warning("Could not shadow-copy Mods/; mod DLLs stay locked while the game runs, so builds need the rename-then-copy deploy step: " + e.Message);
        }
    }

    private static string? ModsPathFor(string? shadowPath) =>
        shadowPath != null && ShadowToMods.TryGetValue(Path.GetFullPath(shadowPath), out var p) ? p : null;

    private static void OnAssemblyResolving(Assembly asm)
    {
        string? real;
        try { real = ModsPathFor(asm.Location); } catch { return; }
        if (real == null) return;
        RememberLocation(asm, real);
        Shadowed++;
    }

    private static void OnMelonInitializing(MelonBase melon) => FixMelonAssemblyLocation(melon.MelonAssembly);

    /// <summary>MelonAssembly.Location is what HotReload and other tools use to find a mod's file.</summary>
    public static void FixMelonAssemblyLocation(MelonAssembly? ma)
    {
        if (ma == null) return;
        var real = ModsPathFor(ma.Location);
        if (real == null) return;
        try { MelonAssemblyLocation?.SetValue(ma, real); } catch { /* setter missing in another ML version: HotReload maps it itself */ }
    }

    /// <summary>For melon assemblies that had no melon to trigger OnMelonInitializing.</summary>
    public static void FixAllLocations()
    {
        foreach (var ma in MelonAssembly.LoadedAssemblies) FixMelonAssemblyLocation(ma);
    }

    /// <summary>One-line self-test for the log.</summary>
    public static string Describe()
    {
        if (_shadowDir == null) return "Mods/ not shadow-copied; mod DLLs are locked while the game runs.";
        foreach (var ma in MelonAssembly.LoadedAssemblies)
        {
            if (!Locations.TryGetValue(ma.Assembly, out var expected)) continue;
            var reported = ma.Assembly.Location;
            return Shadowed + " mod(s) loaded from shadow copies; Mods/*.dll stay unlocked. Assembly.Location "
                   + (reported == expected ? "reports the Mods/ path" : _locationPatched ? "patch NOT effective ('" + reported + "')" : "shows the shadow path (patch failed)");
        }
        return Shadowed + " mod(s) loaded from shadow copies; Mods/*.dll stay unlocked.";
    }
}
