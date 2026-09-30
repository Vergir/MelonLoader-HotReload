using HotReload;
using Xunit;

namespace HotReload.Tests;

public class ErrorEchoTests
{
    private const int Error = 0, Warning = 2, Log = 3, Exception = 4;
    private static readonly DateTime T0 = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private const string Nre = "NullReferenceException: Object reference not set to an instance of an object.";
    private const string Trace = "ActualDropDownSettingsItemGUI.get_IsOpen ()\nSettingsScreenControls.IsAnyDropDownOpen ()\nUIScreen`1.Back ()";

    private static (ErrorEcho echo, List<string> printed) Make(ErrorEcho.Mode mode = ErrorEcho.Mode.AfterReload)
    {
        var printed = new List<string>();
        return (new ErrorEcho(printed.Add) { Current = mode }, printed);
    }

    [Fact]
    public void AfterReload_shows_nothing_before_the_first_reload()
    {
        var (echo, printed) = Make();
        echo.Log(Nre, Trace, Exception, T0);
        Assert.Empty(printed);
    }

    [Fact]
    public void Shows_an_exception_with_its_trace_and_the_reload_it_followed()
    {
        var (echo, printed) = Make();
        echo.Reloaded("reloading QuickStart", T0);
        echo.Log(Nre, Trace, Exception, T0.AddSeconds(3));
        var line = Assert.Single(printed);
        Assert.StartsWith("Unity exception 3 s after reloading QuickStart: NullReferenceException", line);
        Assert.Contains("get_IsOpen", line);
        Assert.Contains("UIScreen`1.Back", line);
        Assert.Contains("EchoUnityErrors", line); // the one-time hint
    }

    [Fact]
    public void Repeats_are_counted_and_reported_at_most_every_10_s()
    {
        var (echo, printed) = Make();
        echo.Reloaded("reloading QuickStart", T0);
        for (int i = 0; i < 72; i++) echo.Log(Nre, Trace, Exception, T0.AddSeconds(1));
        echo.Flush(T0.AddSeconds(5));
        Assert.Single(printed);
        echo.Flush(T0.AddSeconds(12));
        Assert.Equal(2, printed.Count);
        Assert.Contains("again 71 time(s)", printed[1]);
        echo.Flush(T0.AddSeconds(30));
        Assert.Equal(2, printed.Count); // nothing new
    }

    [Fact]
    public void Errors_the_game_logged_before_any_reload_are_left_out()
    {
        var (echo, printed) = Make();
        echo.Log("IndexOutOfRangeException: Index was outside the bounds of the array.", "Game.Tick ()", Exception, T0);
        echo.Reloaded("reloading MyMod", T0.AddSeconds(1));
        echo.Log("IndexOutOfRangeException: Index was outside the bounds of the array.", "Game.Tick ()", Exception, T0.AddSeconds(2));
        Assert.Empty(printed);
        echo.Log(Nre, Trace, Exception, T0.AddSeconds(2));
        Assert.Single(printed);
    }

    [Fact]
    public void The_same_error_is_shown_again_after_the_next_reload()
    {
        var (echo, printed) = Make();
        echo.Reloaded("reloading MyMod", T0);
        echo.Log(Nre, Trace, Exception, T0.AddSeconds(1));
        echo.Log(Nre, Trace, Exception, T0.AddSeconds(2));
        echo.Reloaded("reloading MyMod", T0.AddSeconds(3)); // flushes the pending repeat first
        echo.Log(Nre, Trace, Exception, T0.AddSeconds(4));
        Assert.Equal(3, printed.Count);
        Assert.Contains("again 1 time(s)", printed[1]);
        Assert.StartsWith("Unity exception 1 s after reloading MyMod", printed[2]);
        Assert.DoesNotContain("EchoUnityErrors", printed[2]); // hint only once
    }

    [Fact]
    public void Warnings_and_plain_logs_are_ignored_errors_are_shown()
    {
        var (echo, printed) = Make(ErrorEcho.Mode.Always);
        echo.Log("just a warning", "", Warning, T0);
        echo.Log("just a log", "", Log, T0);
        echo.Log("Shader error in 'X'", "", Error, T0);
        Assert.StartsWith("Unity error: Shader error", Assert.Single(printed));
    }

    [Fact]
    public void Off_shows_nothing()
    {
        var (echo, printed) = Make(ErrorEcho.Mode.Off);
        echo.Reloaded("reloading MyMod", T0);
        echo.Log(Nre, Trace, Exception, T0);
        Assert.Empty(printed);
    }

    [Fact]
    public void Distinct_errors_per_reload_are_capped()
    {
        var (echo, printed) = Make();
        echo.Reloaded("reloading MyMod", T0);
        for (int i = 0; i < 30; i++) echo.Log("Exception number " + i, "", Exception, T0);
        Assert.Equal(21, printed.Count);
        Assert.StartsWith("More Unity errors", printed[20]);
    }

    [Theory]
    [InlineData("afterreload", "AfterReload", true)]
    [InlineData(" Always ", "Always", true)]
    [InlineData("OFF", "Off", true)]
    [InlineData("sometimes", "AfterReload", false)]
    public void Parses_the_setting(string value, string expected, bool ok)
    {
        Assert.Equal(ok, ErrorEcho.TryParse(value, out var mode));
        Assert.Equal(expected, mode.ToString());
    }
}
