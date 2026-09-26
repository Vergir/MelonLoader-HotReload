using System;
using System.Collections.Generic;
using System.IO;
using HotReload;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(HotReloadMod), "HotReload", "0.1.0", "vergir")]
[assembly: MelonGame("Moon Studios", "NoRestForTheWicked")]

namespace HotReload;

/// <summary>
/// Dev tool: press the hotkey to unregister the watched mods and load their freshly built DLLs from bytes.
/// See README.md for the MelonLoader APIs involved and the constraints on reloadable mods.
/// </summary>
public class HotReloadMod : MelonMod
{
    private MelonPreferences_Entry<string> _paths = null!;   // ';'-separated list of DLL paths (bin/Release outputs)
    private MelonPreferences_Entry<string> _hotkey = null!;  // UnityEngine.KeyCode name
    private readonly Dictionary<string, DateTime> _lastWrite = new Dictionary<string, DateTime>();
    private KeyCode _key = KeyCode.F6;

    public override void OnInitializeMelon()
    {
        var cat = MelonPreferences.CreateCategory("HotReload", "Hot Reload (dev)");
        _paths = cat.CreateEntry("Paths", "", description: "';'-separated full paths of mod DLLs to watch (their bin/Release output, not the Mods copy).");
        _hotkey = cat.CreateEntry("Hotkey", "F6", description: "UnityEngine.KeyCode name that triggers a reload of every changed DLL.");
        if (!Enum.TryParse(_hotkey.Value, true, out _key)) _key = KeyCode.F6;
        foreach (var p in Paths()) _lastWrite[p] = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue;
        LoggerInstance.Msg("Watching " + _lastWrite.Count + " DLL(s); hotkey " + _key);
    }

    public override void OnUpdate()
    {
        if (!Input.GetKeyDown(_key)) return;
        foreach (var path in Paths())
        {
            if (!File.Exists(path)) { LoggerInstance.Warning("Missing: " + path); continue; }
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_lastWrite.TryGetValue(path, out var last) && last == stamp) continue;
            _lastWrite[path] = stamp;
            Reload(path);
        }
    }

    private IEnumerable<string> Paths()
    {
        foreach (var p in (_paths.Value ?? "").Split(';'))
        {
            var t = p.Trim();
            if (t.Length > 0) yield return t;
        }
    }

    private void Reload(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        try
        {
            // 1. Unregister the old melons of that assembly (removes Harmony patches, unsubscribes callbacks).
            int removed = 0;
            foreach (var melon in new List<MelonBase>(MelonBase.RegisteredMelons))
            {
                if (melon.MelonAssembly?.Assembly?.GetName().Name != name) continue;
                melon.Unregister("HotReload", silent: true);
                removed++;
            }

            // 2. Load the new build from bytes (no file lock) and register its melons.
            byte[] dll = File.ReadAllBytes(path);
            string pdbPath = Path.ChangeExtension(path, ".pdb");
            byte[]? pdb = File.Exists(pdbPath) ? File.ReadAllBytes(pdbPath) : null;
            var asm = MelonAssembly.LoadRawMelonAssembly(path, dll, pdb, loadMelons: true);
            LoggerInstance.Msg("Reloaded " + name + ": unregistered " + removed + ", loaded " + (asm?.LoadedMelons?.Count ?? 0) + " melon(s)");
        }
        catch (Exception e)
        {
            LoggerInstance.Error("Reload of " + name + " failed: " + e);
        }
    }
}
