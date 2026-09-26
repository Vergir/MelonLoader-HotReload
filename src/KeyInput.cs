using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;

namespace HotReload;

/// <summary>
/// The reload key, read through whichever input API the game allows. Configured with UnityEngine.KeyCode names; a chord
/// is written "LeftControl+F8" or "JoystickButton4+JoystickButton5" and fires when its last key goes down while the
/// others are held, so a key the game uses on its own does not trigger it.
///  1. Legacy UnityEngine.Input: throws when the game switched "Active Input Handling" to the Input System package.
///     The only backend that reads gamepad buttons (JoystickButtonN).
///  2. Input System (UnityEngine.InputSystem.Keyboard.current[Key]), by reflection, so HotReload has no compile-time
///     dependency on a package most games do not ship. Keyboard keys only.
///  3. Windows GetAsyncKeyState while the game window has focus (also works under Wine/Proton). Keyboard keys only.
/// "Auto" tries them in that order and remembers the first that works.
/// </summary>
internal sealed class KeyInput
{
    private enum Backend { None, Legacy, InputSystem, Windows }

    private readonly MelonLogger.Instance _log;
    private string _keyName = "F8";
    private string[] _keys = { "F8" };
    private Backend _backend = Backend.None;
    private Backend[] _candidates = Array.Empty<Backend>();
    private int[] _legacyKeys = Array.Empty<int>();
    private object?[] _inputSystemKeys = Array.Empty<object?>();
    private PropertyInfo? _keyboardCurrent, _keyboardItem, _wasPressed, _isPressed;
    private int[] _vks = Array.Empty<int>();
    private bool[] _vkWasDown = Array.Empty<bool>();
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

    /// <summary>The key names of a chord ("LeftControl + F8" -> LeftControl, F8). Empty for "None" or blank.</summary>
    internal static string[] ParseChord(string? keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName) || keyName!.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)) return Array.Empty<string>();
        return keyName.Split('+').Select(k => k.Trim()).Where(k => k.Length > 0).ToArray();
    }

    /// <param name="keyName">UnityEngine.KeyCode name or chord ("LeftControl+F8"), or "None".</param>
    /// <param name="backend">"Auto", "Legacy", "InputSystem" or "Windows".</param>
    public void Configure(string keyName, string backend)
    {
        _keys = ParseChord(keyName);
        _keyName = _keys.Length == 0 ? "None" : string.Join("+", _keys);
        _backend = Backend.None;
        if (_keys.Length == 0) { _candidates = Array.Empty<Backend>(); return; }

        _candidates = (backend ?? "Auto").Trim().ToLowerInvariant() switch
        {
            "legacy" => new[] { Backend.Legacy },
            "inputsystem" => new[] { Backend.InputSystem },
            "windows" => new[] { Backend.Windows },
            _ => new[] { Backend.Legacy, Backend.InputSystem, Backend.Windows },
        };
        NextBackend(null);
    }

    /// <summary>True in the frame the key (or the last key of the chord) goes down. Never throws.</summary>
    public bool Pressed()
    {
        if (_backend == Backend.None) return false;
        try
        {
            return _backend switch
            {
                Backend.Legacy => LegacyPressed(),
                Backend.InputSystem => InputSystemPressed(),
                Backend.Windows => WindowsPressed(),
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

    /// <summary>A chord fires when all its keys are held and at least one of them went down this frame.</summary>
    internal static bool ChordPressed(int count, Func<int, bool> held, Func<int, bool> wentDown)
    {
        bool any = false;
        for (int i = 0; i < count; i++)
        {
            if (!held(i)) return false;
            any |= wentDown(i);
        }
        return any;
    }

    /// <summary>Picks the next candidate after the current one that can resolve every key.</summary>
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
            var keys = new int[_keys.Length];
            for (int i = 0; i < _keys.Length; i++)
                if (!UnityApi.TryParseKey(_keys[i], out keys[i])) return "'" + _keys[i] + "' is not a KeyCode name";
            _legacyKeys = keys;
            return UnityApi.LegacyInputProblem();
        }
        catch (Exception e) { return e.GetBaseException().Message; }
    }

    private bool LegacyPressed() =>
        ChordPressed(_legacyKeys.Length, i => UnityApi.KeyHeld(_legacyKeys[i]), i => UnityApi.KeyDown(_legacyKeys[i]));

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
        Type? keyboard = UnityApi.FindType("UnityEngine.InputSystem.Keyboard", "Unity.InputSystem");
        if (keyboard == null) return "the game has no Input System package";
        var keyType = keyboard.Assembly.GetType("UnityEngine.InputSystem.Key");
        if (keyType == null) return "UnityEngine.InputSystem.Key not found";

        var keys = new object?[_keys.Length];
        for (int i = 0; i < _keys.Length; i++)
        {
            try { keys[i] = Enum.Parse(keyType, InputSystemKeyName(_keys[i]), ignoreCase: true); }
            catch { return "no Input System key for '" + _keys[i] + "' (gamepad buttons need legacy input)"; }
        }
        _inputSystemKeys = keys;
        _keyboardCurrent = keyboard.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
        _keyboardItem = keyboard.GetProperty("Item", new[] { keyType });
        _wasPressed = _isPressed = null;
        return _keyboardCurrent == null || _keyboardItem == null ? "Keyboard.current / Keyboard[Key] not found" : null;
    }

    /// <summary>The Input System <c>Key</c> name for a <c>KeyCode</c> name (most are the same).</summary>
    internal static string InputSystemKeyName(string keyCode)
    {
        var name = keyCode.Trim();
        if (name.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase) && name.Length == 6 && char.IsDigit(name[5])) return "Digit" + name[5];
        if (name.StartsWith("Keypad", StringComparison.OrdinalIgnoreCase) && name.Length == 7 && char.IsDigit(name[6])) return "Numpad" + name[6];
        return KeyCodeToInputSystem.TryGetValue(name, out var mapped) ? mapped : name;
    }

    private bool InputSystemPressed()
    {
        var kb = _keyboardCurrent!.GetValue(null);
        if (kb == null) return false; // no keyboard connected
        var controls = new object?[_inputSystemKeys.Length];
        for (int i = 0; i < controls.Length; i++) controls[i] = _keyboardItem!.GetValue(kb, new[] { _inputSystemKeys[i] });
        if (controls.Any(c => c == null)) return false;
        _wasPressed ??= controls[0]!.GetType().GetProperty("wasPressedThisFrame");
        _isPressed ??= controls[0]!.GetType().GetProperty("isPressed");
        if (_wasPressed == null || _isPressed == null) return false;
        return ChordPressed(controls.Length, i => _isPressed.GetValue(controls[i]) is true, i => _wasPressed.GetValue(controls[i]) is true);
    }

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
        ["LeftShift"] = 0xA0, ["RightShift"] = 0xA1, ["LeftControl"] = 0xA2, ["RightControl"] = 0xA3, ["LeftAlt"] = 0xA4, ["RightAlt"] = 0xA5,
        ["Semicolon"] = 0xBA, ["Equals"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD, ["Period"] = 0xBE, ["Slash"] = 0xBF,
        ["BackQuote"] = 0xC0, ["LeftBracket"] = 0xDB, ["Backslash"] = 0xDC, ["RightBracket"] = 0xDD, ["Quote"] = 0xDE,
    };

    private string? TrySetupWindows()
    {
        if (!Compat.IsWindows) return "not on Windows";
        var vks = new int[_keys.Length];
        for (int i = 0; i < _keys.Length; i++)
        {
            if (VirtualKey(_keys[i]) is not { } vk) return "no virtual-key code for '" + _keys[i] + "' (gamepad buttons need legacy input)";
            vks[i] = vk;
        }
        _vks = vks;
        _vkWasDown = Enumerable.Repeat(true, vks.Length).ToArray(); // ignore keys already held when switching
        return null;
    }

    /// <summary>The Windows virtual-key code for a <c>KeyCode</c> name, or null when there is none.</summary>
    internal static int? VirtualKey(string keyCode)
    {
        var n = keyCode.Trim();
        if (VirtualKeys.TryGetValue(n, out var vk)) return vk;
        if (n.Length == 1 && char.IsLetter(n[0])) return char.ToUpperInvariant(n[0]);
        if (n.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase) && n.Length == 6 && char.IsDigit(n[5])) return n[5];
        if (n.StartsWith("Keypad", StringComparison.OrdinalIgnoreCase) && n.Length == 7 && char.IsDigit(n[6])) return 0x60 + (n[6] - '0');
        if (n.Length >= 2 && (n[0] == 'F' || n[0] == 'f') && int.TryParse(n.Substring(1), out var f) && f is >= 1 and <= 24) return 0x70 + f - 1;
        return null;
    }

    /// <summary>
    /// Polled once per frame. Bit 0x8000 is "down now"; bit 1 is "pressed since the last query", which catches a tap that
    /// went down and up between two frames (seen at low frame rates).
    /// </summary>
    private bool WindowsPressed()
    {
        var down = new bool[_vks.Length];
        var tapped = new bool[_vks.Length];
        for (int i = 0; i < _vks.Length; i++)
        {
            short state = GetAsyncKeyState(_vks[i]);
            down[i] = (state & 0x8000) != 0;
            tapped[i] = (state & 1) != 0;
        }
        var was = _vkWasDown;
        _vkWasDown = down;
        return ChordPressed(down.Length, i => down[i] || tapped[i], i => (down[i] && !was[i]) || (tapped[i] && !down[i])) && GameHasFocus();
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
