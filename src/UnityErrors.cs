using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;

namespace HotReload;

/// <summary>
/// Echoes Unity's errors and exceptions into HotReload's log. An exception thrown inside game code (the game walking a
/// list that still holds an object the old build destroyed, say) goes only to Unity's Player.log, so a reload looks
/// clean in MelonLoader's log while a game feature has stopped working. This decides what to echo; <see cref="UnityErrors"/>
/// feeds it from <c>Application.logMessageReceived</c>. No Unity types here, so the tests can drive it.
/// </summary>
internal sealed class ErrorEcho
{
    public enum Mode { Off, AfterReload, Always }

    // UnityEngine.LogType
    private const int LogError = 0, LogAssert = 1, LogException = 4;
    private const int TraceLines = 6, SignatureLines = 3, MaxShownPerReload = 20, MaxSeenBefore = 2000;
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromSeconds(10);

    private sealed class Entry
    {
        public string Headline = "";
        public int Pending;
        public DateTime LastPrinted;
    }

    private readonly Action<string> _print;
    // AfterReload: what the game already logged before the first reload is its normal noise, not news.
    private readonly HashSet<string> _seenBeforeReload = new HashSet<string>(StringComparer.Ordinal);
    // Everything shown since the last reload, so a repeat only counts up.
    private readonly Dictionary<string, Entry> _shown = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private string? _lastReload;
    private DateTime _lastReloadAt;
    private bool _hintShown, _capNoted;

    public ErrorEcho(Action<string> print) => _print = print;

    public Mode Current { get; set; } = Mode.AfterReload;

    public static bool TryParse(string? value, out Mode mode)
    {
        foreach (Mode m in Enum.GetValues(typeof(Mode)))
            if (string.Equals(m.ToString(), value?.Trim(), StringComparison.OrdinalIgnoreCase)) { mode = m; return true; }
        mode = Mode.AfterReload;
        return false;
    }

    /// <summary>A reload (or unload) finished: later errors are attributed to it and shown once more each.</summary>
    public void Reloaded(string what, DateTime now)
    {
        Flush(now, force: true);
        _lastReload = what;
        _lastReloadAt = now;
        _shown.Clear();
        _capNoted = false;
    }

    public void Log(string? condition, string? stackTrace, int logType, DateTime now)
    {
        if (Current == Mode.Off || (logType != LogError && logType != LogAssert && logType != LogException)) return;
        var lines = ((condition ?? "") + "\n" + (stackTrace ?? ""))
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return;
        var headline = Shorten(lines[0], 400);
        var signature = string.Join("\n", lines.Take(1 + SignatureLines).ToArray());

        if (Current == Mode.AfterReload)
        {
            if (_lastReload == null)
            {
                if (_seenBeforeReload.Count < MaxSeenBefore) _seenBeforeReload.Add(signature);
                return;
            }
            if (_seenBeforeReload.Contains(signature)) return;
        }

        if (_shown.TryGetValue(signature, out var entry)) { entry.Pending++; return; }
        if (_shown.Count >= MaxShownPerReload)
        {
            if (!_capNoted) _print("More Unity errors " + Since(now) + " are not shown here; see Player.log.");
            _capNoted = true;
            return;
        }
        _shown[signature] = new Entry { Headline = headline, LastPrinted = now };

        var text = Kind(logType) + (_lastReload != null ? " " + Since(now) : "") + ": " + headline;
        foreach (var l in lines.Skip(1).Take(TraceLines)) text += "\n    " + l.Trim();
        if (lines.Count > 1 + TraceLines) text += "\n    ...";
        if (!_hintShown)
        {
            _hintShown = true;
            text += "\n  (HotReload copies Unity errors " + (Current == Mode.AfterReload ? "that are new since a reload " : "")
                    + "into this log, once per reload with repeats counted; the full text is in Player.log. An error in game code "
                    + "often means the game still holds something the old build destroyed or changed: see \"Unregister your "
                    + "objects before you destroy them\" in HotReload's guide. Setting: EchoUnityErrors in HotReload.toml.)";
        }
        _print(text);
    }

    /// <summary>Reports repeats: at most every 10 s per error, and all of them when a reload starts.</summary>
    public void Flush(DateTime now, bool force = false)
    {
        foreach (var e in _shown.Values)
        {
            if (e.Pending == 0 || (!force && now - e.LastPrinted < RepeatInterval)) continue;
            _print("  ... again " + e.Pending + " time(s): " + Shorten(e.Headline, 120));
            e.Pending = 0;
            e.LastPrinted = now;
        }
    }

    private string Since(DateTime now)
    {
        var d = now - _lastReloadAt;
        var ago = d.TotalSeconds < 60 ? d.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s"
            : d.TotalMinutes < 60 ? (int)d.TotalMinutes + " min" : (int)d.TotalHours + " h";
        return ago + " after " + _lastReload;
    }

    private static string Kind(int logType) =>
        logType == LogException ? "Unity exception" : logType == LogAssert ? "Unity assert" : "Unity error";

    private static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";
}

/// <summary>
/// Subscribes an <see cref="ErrorEcho"/> to <c>UnityEngine.Application.logMessageReceived</c>, by reflection: a plain
/// delegate on Mono, an Il2Cpp delegate made by Il2CppInterop on IL2CPP. Skipped when MelonLoader already copies
/// the player log into its own (Loader.cfg <c>capture_player_logs</c>, 0.7.1+).
/// </summary>
internal static class UnityErrors
{
    private static ErrorEcho? _echo;
    private static object? _subscribed; // keeps the Il2Cpp delegate alive
    private static bool _inside;

    /// <summary>Returns the status for the startup line.</summary>
    public static string Install(ErrorEcho echo)
    {
        if (MelonLoaderCapturesPlayerLogs()) return "MelonLoader copies the player log (capture_player_logs)";
        try
        {
            string[] core = { "UnityEngine.CoreModule", "UnityEngine" };
            var app = UnityApi.FindType("UnityEngine.Application", core) ?? throw new InvalidOperationException("UnityEngine.Application not found");
            var logType = UnityApi.FindType("UnityEngine.LogType", core) ?? throw new InvalidOperationException("UnityEngine.LogType not found");
            var add = app.GetMethod("add_logMessageReceived", BindingFlags.Public | BindingFlags.Static)
                      ?? throw new MissingMethodException(app.FullName, "add_logMessageReceived");
            var callbackType = add.GetParameters()[0].ParameterType;
            var handler = typeof(UnityErrors).GetMethod(nameof(OnLog), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(logType);

            object callback;
            if (typeof(Delegate).IsAssignableFrom(callbackType))
                callback = Delegate.CreateDelegate(callbackType, handler); // Mono
            else
            {
                // IL2CPP: the interop delegate type wraps a native delegate; Il2CppInterop builds one from a managed delegate.
                var action = Delegate.CreateDelegate(typeof(Action<,,>).MakeGenericType(typeof(string), typeof(string), logType), handler);
                var op = callbackType.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "op_Implicit"
                             && m.ReturnType == callbackType && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == action.GetType());
                if (op != null) callback = op.Invoke(null, new object[] { action })!;
                else
                {
                    var convert = UnityApi.FindType("Il2CppInterop.Runtime.DelegateSupport", "Il2CppInterop.Runtime")?
                                      .GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "ConvertDelegate" && m.IsGenericMethodDefinition)
                                  ?? throw new MissingMethodException("Il2CppInterop.Runtime.DelegateSupport", "ConvertDelegate");
                    callback = convert.MakeGenericMethod(callbackType).Invoke(null, new object[] { action })
                               ?? throw new InvalidOperationException("DelegateSupport.ConvertDelegate returned null");
                }
            }
            add.Invoke(null, new[] { callback });
            _subscribed = callback;
            _echo = echo;
            return "on";
        }
        catch (Exception e)
        {
            return "off (" + (e is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : e.Message) + ")";
        }
    }

    /// <summary>Called by Unity on the main thread for every log message.</summary>
    private static void OnLog<TLogType>(string condition, string stackTrace, TLogType type)
    {
        if (_inside || _echo == null) return;
        _inside = true;
        try { _echo.Log(condition, stackTrace, Convert.ToInt32(type), DateTime.UtcNow); }
        catch { /* never throw back into Unity's logger */ }
        finally { _inside = false; }
    }

    /// <summary>MelonLoader 0.7.1+ LoaderConfig.Current.*.CapturePlayerLogs; false when absent.</summary>
    private static bool MelonLoaderCapturesPlayerLogs()
    {
        try
        {
            var ml = typeof(MelonLogger).Assembly;
            var config = ml.GetType("MelonLoader.LoaderConfig", false) ?? ml.GetTypes().FirstOrDefault(t => t.Name == "LoaderConfig");
            var current = config?.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null, null);
            if (current == null) return false;
            foreach (var section in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (section.GetIndexParameters().Length > 0) continue;
                var value = section.GetValue(current, null);
                if (value?.GetType().GetProperty("CapturePlayerLogs", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value, null) is bool on)
                    return on;
            }
        }
        catch { /* an older MelonLoader */ }
        return false;
    }
}
