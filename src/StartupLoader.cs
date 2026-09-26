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
/// Keeps Mods/*.dll (and DLLs in manifest subfolders of Mods/) unlocked while the game runs. MelonLoader loads mods with LoadFromAssemblyPath, which keeps the file
/// open, so a build cannot overwrite it. HotReload registers before mods are loaded, copies Mods/*.dll (+pdb) to a
/// per-session shadow folder and swaps that folder into MelonLoader's list of mod folders. MelonLoader locks the copies;
/// the originals stay free for builds, and HotReload maps every path back to Mods/.
///
/// Redirecting MelonAssembly.LoadMelonAssembly(string, bool) with a Harmony prefix does not work: MelonLoader marks its
/// own assembly with [PatchShield] (every release since 0.6.0), which silently skips any patch on its methods. The
/// folder list is plain data read later, so changing it needs no patch.
/// </summary>
internal static class StartupLoader
{
    private static readonly ConditionalWeakTable<Assembly, string> Locations = new ConditionalWeakTable<Assembly, string>();
    private static readonly Dictionary<string, string> ShadowToMods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> OriginalModDirs = new List<string>();

    /// <summary>The mod folders MelonLoader loads from (Mods/ and its manifest subfolders), as the user sees them.</summary>
    public static IEnumerable<string> ModDirectories() =>
        OriginalModDirs.Count > 0 ? OriginalModDirs : new[] { Path.GetFullPath(MelonEnvironment.ModsDirectory) };

    private static bool IsUnder(string dir, string root)
    {
        var d = Path.GetFullPath(dir).TrimEnd('\\', '/');
        return string.Equals(d, root, StringComparison.OrdinalIgnoreCase) || d.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static readonly PropertyInfo? MelonAssemblyLocation = typeof(MelonAssembly).GetProperty(nameof(MelonAssembly.Location));
    private static MelonLogger.Instance _log = null!;
    private static bool _locationPatched;
    private static HarmonyLib.Harmony? _harmony;
    private static MethodInfo? _locationGetter;
    private static bool _repatchLogged;
    private static string? _shadowDir;
    public static int Shadowed { get; private set; }

    /// <summary>Makes Assembly.Location of <paramref name="asm"/> return <paramref name="path"/>.</summary>
    public static void RememberLocation(Assembly asm, string path)
    {
        Locations.Remove(asm);
        Locations.Add(asm, path);
    }

    public static bool InstallLocationPatch(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
    {
        _log = log;
        try
        {
            // .NET: System.Reflection.RuntimeAssembly; Mono: RuntimeAssembly or (older) MonoAssembly.
            var runtimeAssembly = typeof(object).Assembly.GetType("System.Reflection.RuntimeAssembly", throwOnError: false)
                                  ?? typeof(object).Assembly.GetType("System.Reflection.MonoAssembly", throwOnError: true)!;
            _locationGetter = AccessTools.PropertyGetter(runtimeAssembly, nameof(Assembly.Location));
            _harmony = harmony;
            harmony.Patch(_locationGetter, postfix: LocationPostfixMethod);
            _locationPatched = true;
        }
        catch (Exception e)
        {
            log.Warning("Could not patch Assembly.Location; reloaded mods will see an empty Location: " + e.Message);
        }
        return _locationPatched;
    }

    private static HarmonyMethod LocationPostfixMethod =>
        new HarmonyMethod(typeof(StartupLoader).GetMethod(nameof(LocationPostfix), BindingFlags.Static | BindingFlags.NonPublic));

    /// <summary>
    /// The Location getter lives in the runtime's precompiled (ReadyToRun) code. Once it is called often enough the
    /// tiered JIT recompiles it and the new code does not carry the patch (seen in-game after the first reload). Checks
    /// the patch on a known assembly and re-applies it; the recompiled (final-tier) code keeps the new patch.
    /// </summary>
    public static void EnsureLocationPatch(Assembly probe, string expected)
    {
        if (!_locationPatched || _harmony == null || _locationGetter == null) return;
        string actual;
        try { actual = probe.Location; } catch { return; }
        if (string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _harmony.Unpatch(_locationGetter, HarmonyPatchType.Postfix, _harmony.Id);
            _harmony.Patch(_locationGetter, postfix: LocationPostfixMethod);
            if (!_repatchLogged)
            {
                _repatchLogged = true;
                _log.Msg("Assembly.Location patch was dropped by the runtime's tiered JIT; re-applied" +
                         (string.Equals(probe.Location, expected, StringComparison.OrdinalIgnoreCase) ? "." : ", but it still does not take effect."));
            }
        }
        catch (Exception e) { _log.Warning("Could not re-apply the Assembly.Location patch: " + e.Message); }
    }

    /// <summary>
    /// Copies a new build (and its pdb) into this session's shadow folder and returns the copy's path. Loading from a
    /// file instead of from bytes gives the assembly a real Location even without the Location patch.
    /// </summary>
    public static string ShadowCopyForReload(string originalPath, byte[] dll, byte[]? pdb, int generation)
    {
        var dir = Path.Combine(_shadowDir ?? Path.Combine(MelonEnvironment.UserDataDirectory, "HotReload", "Shadow", Compat.ProcessId.ToString()),
                               "reload-" + generation);
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, Path.GetFileName(originalPath));
        File.WriteAllBytes(copy, dll);
        if (pdb != null) File.WriteAllBytes(Path.ChangeExtension(copy, ".pdb"), pdb);
        ShadowToMods[Path.GetFullPath(copy)] = originalPath;
        return copy;
    }

    private static void LocationPostfix(Assembly __instance, ref string __result)
    {
        if (Locations.TryGetValue(__instance, out var path)) __result = path;
    }

    /// <summary>Called while plugins register, before MelonLoader scans Mods/. Returns a status for the log.</summary>
    public static string InstallShadowMods(MelonLogger.Instance log)
    {
        _log = log;
        try
        {
            var field = typeof(MelonAssembly).Assembly.GetType("MelonLoader.Melons.MelonFolderHandler", throwOnError: false)
                ?.GetField("_modDirs", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
            {
                // MelonLoader 0.6.x / 0.7.0 keep the mod folder list in a local variable.
                log.Warning("This MelonLoader has no MelonFolderHandler._modDirs (added in 0.7.1), so Mods/*.dll stay locked while the game runs. " +
                            "Deploy builds with the rename-then-copy step from the README, or update MelonLoader.");
                return "unavailable (needs MelonLoader 0.7.1+)";
            }
            var dirs = (List<string>)field.GetValue(null)!;
            var mods = Path.GetFullPath(MelonEnvironment.ModsDirectory).TrimEnd('\\', '/');
            // Mods/ itself and the subfolders MelonLoader added (those with a manifest.json).
            var modIdx = Enumerable.Range(0, dirs.Count).Where(i => IsUnder(dirs[i], mods)).ToList();
            if (modIdx.Count == 0) { log.Warning("Mods folder not in MelonLoader's folder list; mod DLLs stay locked."); return "unavailable (Mods not in folder list)"; }

            var root = Path.Combine(MelonEnvironment.UserDataDirectory, "HotReload", "Shadow");
            Directory.CreateDirectory(root);
            foreach (var old in Directory.EnumerateDirectories(root))
            {
                try { Directory.Delete(old, recursive: true); } catch { /* another game instance still holds it */ }
            }
            _shadowDir = Path.Combine(root, Compat.ProcessId.ToString());
            Directory.CreateDirectory(_shadowDir);
            foreach (var i in modIdx)
            {
                var original = Path.GetFullPath(dirs[i]).TrimEnd('\\', '/');
                var rel = Compat.RelativePath(mods, original);
                var target = rel == "." ? _shadowDir : Path.Combine(_shadowDir, "sub", rel);
                Directory.CreateDirectory(target);
                foreach (var f in Directory.EnumerateFiles(original, "*.*", SearchOption.TopDirectoryOnly)
                             .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)))
                {
                    var copy = Path.Combine(target, Path.GetFileName(f));
                    File.Copy(f, copy, overwrite: true);
                    ShadowToMods[Path.GetFullPath(copy)] = f;
                }
                OriginalModDirs.Add(original);
                dirs[i] = target;
            }

            // Map paths back as soon as MelonLoader has an Assembly / a melon, before mods run any code.
            MelonAssembly.OnAssemblyResolving.Subscribe(OnAssemblyResolving);
            MelonBase.OnMelonInitializing.Subscribe(OnMelonInitializing);
            return "on";
        }
        catch (Exception e)
        {
            log.Warning("Could not shadow-copy Mods/; mod DLLs stay locked while the game runs, so builds need the rename-then-copy deploy step: " + e.Message);
            return "failed (" + e.Message + ")";
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
