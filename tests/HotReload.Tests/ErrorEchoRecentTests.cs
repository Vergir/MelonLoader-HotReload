using System;
using System.Collections.Generic;
using HotReload;
using Xunit;

public class ErrorEchoRecentTests
{
    private static readonly DateTime T0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Names_earlier_reloads_of_other_mods_within_30_seconds()
    {
        var printed = new List<string>();
        var echo = new ErrorEcho(printed.Add) { Current = ErrorEcho.Mode.Always };
        echo.Reloaded("unloading RuneDetails", T0);
        echo.Reloaded("reloading MyMod", T0.AddSeconds(5));
        echo.Reloaded("reloading MyMod", T0.AddSeconds(8));
        echo.Log("NullReferenceException", "at Game.Update()", 4, T0.AddSeconds(10));
        Assert.StartsWith("Unity exception 2 s after reloading MyMod (earlier: unloading RuneDetails 10 s ago): NullReferenceException", printed[0]);
    }

    [Fact]
    public void Forgets_reloads_older_than_30_seconds()
    {
        var printed = new List<string>();
        var echo = new ErrorEcho(printed.Add) { Current = ErrorEcho.Mode.Always };
        echo.Reloaded("unloading RuneDetails", T0);
        echo.Reloaded("reloading MyMod", T0.AddSeconds(45));
        echo.Log("NullReferenceException", "at Game.Update()", 4, T0.AddSeconds(46));
        Assert.StartsWith("Unity exception 1 s after reloading MyMod: NullReferenceException", printed[0]);
    }
}
