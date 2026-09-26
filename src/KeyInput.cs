using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;

namespace HotReload;

/// <summary>
/// The reload key, read through whichever input API the game allows. Configured with a UnityEngine.KeyCode name.
///  1. Legacy UnityEngine.Input: throws when the game switched "Active Input Handling" to the Input System package.
///  2. Input System (UnityEngine.InputSystem.Keyboard.current[Key].wasPressedThisFrame), by reflection, so HotReload
///     has no compile-time dependency on a package most games do not ship.
///  3. Windows GetAsyncKeyState while the game window has focus (also works under Wine/Proton).
/// "Auto" tries them in that order and remembers the first that works.
/// </summary>
internal sealed class KeyInput
{
    private enum Backend { None, Legacy, InputSystem, Windows }

    private readonly MelonLogger.Instance _log;
    private string _keyName = "F8";
    private Backend _backend = Backend.None;
    private Backend[] _candidates = Array.Empty<Backend>();
    private int _legacyKey;
    private object? _inputSystemKey;
    private PropertyInfo? _keyboardCurrent, _keyboardItem, _wasPressed;
    private int _vk;
    private bool _vkWasDown;
    private IntPtr _window;

    public KeyInput(MelonLogger.Instance log) => _log = log;

    public string Describe() => _backend switch
    {
        Backend.None => "none",
        Backend.Legacy => _keyName + " (legacy Input)",
        Backend.InputSystem => _keyName + " (Input System)",
        _ => _keyName + " (Windows key state)",
    };

    public string KeyName => _keyName;

    /// <param name="keyName">UnityEngine.KeyCode name, or "None".</param>
    /// <param name="backend">"Auto", "Legacy", "InputSystem" or "Windows".</param>
    public void Configure(string keyName, string backend)
    {
        _keyName = string.IsNullOrWhiteSpace(keyName) ? "None" : keyName.Trim();
        _backend = Backend.None;
        if (_keyName.Equals("None", StringComparison.OrdinalIgnoreCase)) { _candidates = Array.Empty<Backend>(); return; }

        _candidates = (backend ?? "Auto").Trim().ToLowerInvariant() switch
        {
            "legacy" => new[] { Backend.Legacy },
            "inputsystem" => new[] { Backend.InputSystem },
            "windows" => new[] { Backend.Windows },
            _ => new[] { Backend.Legacy, Backend.InputSystem, Backend.Windows },
        };
        NextBackend(null);
    }

    /// <summary>True in the frame the key goes down. Never throws.</summary>
    public bool Pressed()
    {
        if (_backend == Backend.None) return false;
        try
        {
            return _backend switch
            {
                Backend.Legacy => LegacyDown(_legacyKey),
                Backend.InputSystem => InputSystemDown(),
                Backend.Windows => WindowsDown(),
                _ => false,
            };
        }
        catch (Exception e)
        {
            // Typically: legacy Input disabled by the game (InvalidOperationException), or an interop assembly missing.
            NextBackend(e.GetBaseException().Message);
            return false;
        }
    }

    /// <summary>Picks the next candidate after the current one that can resolve the key.</summary>
    private void NextBackend(string? failure)
    {
        var failed = _backend;
        int start = failed == Backend.None ? 0 : Array.IndexOf(_candidates, failed) + 1;
        _backend = Backend.None;
        var notes = new List<string>();
        if (failed != Backend.None) notes.Add(failed + " failed: " + failure);
        for (int i = start; i < _candidates.Length && _backend == Backend.None; i++)
        {
            var b = _candidates[i];
            string? why = b switch
            {
                Backend.Legacy => TrySetupLegacy(),
                Backend.InputSystem => TrySetupInputSystem(),
                Backend.Windows => TrySetupWindows(),
                _ => "unknown",
            };
            if (why == null) _backend = b;
            else notes.Add(b + ": " + why);
        }
        if (_backend == Backend.None)
            _log.Warning("Reload key " + _keyName + " unavailable (" + string.Join("; ", notes) + "). Auto reload still works.");
        else if (failed != Backend.None || notes.Count > 0)
            _log.Msg("Reload key " + Describe() + (notes.Count > 0 ? " [" + string.Join("; ", notes) + "]" : ""));
    }

    // ---- Legacy UnityEngine.Input ---------------------------------------------------------------------------------

    private string? TrySetupLegacy()
    {
        try
        {
            if (!UnityApi.TryParseKey(_keyName, out _legacyKey)) return "'" + _keyName + "' is not a KeyCode name";
            return UnityApi.LegacyInputProblem();
        }
        catch (Exception e) { return e.GetBaseException().Message; }
    }

    private static bool LegacyDown(int key) => UnityApi.KeyDown(key);

    // ---- Input System package, by reflection ----------------------------------------------------------------------

    private static readonly Dictionary<string, string> KeyCodeToInputSystem = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = "Enter", ["KeypadEnter"] = "NumpadEnter", ["KeypadPlus"] = "NumpadPlus", ["KeypadMinus"] = "NumpadMinus",
        ["KeypadMultiply"] = "NumpadMultiply", ["KeypadDivide"] = "NumpadDivide", ["KeypadPeriod"] = "NumpadPeriod",
        ["KeypadEquals"] = "NumpadEquals", ["LeftControl"] = "LeftCtrl", ["RightControl"] = "RightCtrl",
        ["LeftCommand"] = "LeftMeta", ["RightCommand"] = "RightMeta", ["LeftWindows"] = "LeftMeta", ["RightWindows"] = "RightMeta",
        ["BackQuote"] = "Backquote", ["Equals"] = "Equals", ["Print"] = "PrintScreen",
    };

    private string? TrySetupInputSystem()
    {
        Type? keyboard = FindType("UnityEngine.InputSystem.Keyboard", "Unity.InputSystem");
        if (keyboard == null) return "the game has no Input System package";
        var keyType = keyboard.Assembly.GetType("UnityEngine.InputSystem.Key");
        if (keyType == null) return "UnityEngine.InputSystem.Key not found";

        var name = _keyName;
        if (name.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase) && name.Length == 6) name = "Digit" + name[5];
        else if (name.StartsWith("Keypad", StringComparison.OrdinalIgnoreCase) && name.Length == 7 && char.IsDigit(name[6])) name = "Numpad" + name[6];
        else if (KeyCodeToInputSystem.TryGetValue(name, out var mapped)) name = mapped;
        try { _inputSystemKey = Enum.Parse(keyType, name, ignoreCase: true); }
        catch { return "no Input System key for '" + _keyName + "'"; }

        _keyboardCurrent = keyboard.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
        _keyboardItem = keyboard.GetProperty("Item", new[] { keyType });
        _wasPressed = null;
        return _keyboardCurrent == null || _keyboardItem == null ? "Keyboard.current / Keyboard[Key] not found" : null;
    }

    private bool InputSystemDown()
    {
        var kb = _keyboardCurrent!.GetValue(null);
        if (kb == null) return false; // no keyboard connected
        var control = _keyboardItem!.GetValue(kb, new[] { _inputSystemKey });
        if (control == null) return false;
        _wasPressed ??= control.GetType().GetProperty("wasPressedThisFrame");
        return _wasPressed != null && _wasPressed.GetValue(control) is true;
    }

    private static Type? FindType(string fullName, string assemblyName) => UnityApi.FindType(fullName, assemblyName);

    // ---- Windows key state ----------------------------------------------------------------------------------------

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static readonly Dictionary<string, int> VirtualKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Return"] = 0x0D, ["Pause"] = 0x13, ["Escape"] = 0x1B, ["Space"] = 0x20,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24, ["LeftArrow"] = 0x25, ["UpArrow"] = 0x26,
        ["RightArrow"] = 0x27, ["DownArrow"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["KeypadMultiply"] = 0x6A,
        ["KeypadPlus"] = 0x6B, ["KeypadMinus"] = 0x6D, ["KeypadPeriod"] = 0x6E, ["KeypadDivide"] = 0x6F, ["ScrollLock"] = 0x91,
        ["Semicolon"] = 0xBA, ["Equals"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD, ["Period"] = 0xBE, ["Slash"] = 0xBF,
        ["BackQuote"] = 0xC0, ["LeftBracket"] = 0xDB, ["Backslash"] = 0xDC, ["RightBracket"] = 0xDD, ["Quote"] = 0xDE,
    };

    private string? TrySetupWindows()
    {
        if (!Compat.IsWindows) return "not on Windows";
        var n = _keyName;
        if (VirtualKeys.TryGetValue(n, out _vk)) { }
        else if (n.Length == 1 && char.IsLetter(n[0])) _vk = char.ToUpperInvariant(n[0]);
        else if (n.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase) && n.Length == 6 && char.IsDigit(n[5])) _vk = n[5];
        else if (n.StartsWith("Keypad", StringComparison.OrdinalIgnoreCase) && n.Length == 7 && char.IsDigit(n[6])) _vk = 0x60 + (n[6] - '0');
        else if (n.Length >= 2 && (n[0] == 'F' || n[0] == 'f') && int.TryParse(n.Substring(1), out var f) && f is >= 1 and <= 24) _vk = 0x70 + f - 1;
        else return "no virtual-key code for '" + _keyName + "'";
        _vkWasDown = true; // ignore a key already held when switching
        return null;
    }

    private bool WindowsDown()
    {
        bool down = (GetAsyncKeyState(_vk) & 0x8000) != 0;
        bool pressed = down && !_vkWasDown;
        _vkWasDown = down;
        return pressed && GameHasFocus();
    }

    private bool GameHasFocus()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == _window) return true;
        GetWindowThreadProcessId(fg, out var pid);
        if (pid != (uint)Compat.ProcessId) return false;
        _window = fg;
        return true;
    }
}
