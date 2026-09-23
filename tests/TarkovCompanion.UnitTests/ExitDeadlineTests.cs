using Avalonia.Controls;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Notifications;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// #735: an explicit exit is bounded from the moment it is asked for, and never becomes a hide.
/// </summary>
public sealed class ExitDeadlineTests
{
    [Fact]
    public void AnArmedDeadlineEndsTheProcessAndNamesHowFarTheExitGot()
    {
        using var ended = new ManualResetEventSlim();
        var lines = new List<string>();
        var deadline = new ExitDeadline(TimeSpan.FromMilliseconds(100), ended.Set, line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        });

        Assert.True(deadline.Arm("main window closing, WindowClosing"));
        deadline.Reached("teardown");

        Assert.True(ended.Wait(TimeSpan.FromSeconds(10)), "The deadline never ended the process.");
        lock (lines)
        {
            Assert.Contains(lines, line => line.StartsWith("Exit requested (main window closing, WindowClosing); the process ends within", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("still running; last stage: teardown. Ending it.", StringComparison.Ordinal));
        }
    }

    /// <summary>The first request starts the clock; a later one cannot push it back.</summary>
    [Fact]
    public void OnlyTheFirstRequestArmsIt()
    {
        var ends = 0;
        using var ended = new ManualResetEventSlim();
        var lines = new List<string>();
        var deadline = new ExitDeadline(TimeSpan.FromMilliseconds(150), () =>
        {
            Interlocked.Increment(ref ends);
            ended.Set();
        }, line =>
        {
            lock (lines)
            {
                lines.Add(line);
            }
        });

        Assert.True(deadline.Arm("tray Quit"));
        Assert.False(deadline.Arm("desktop lifetime exit"));
        Assert.True(deadline.IsArmed);

        Assert.True(ended.Wait(TimeSpan.FromSeconds(10)));
        // Long enough for a second timer, had one been started, to have fired as well.
        Thread.Sleep(400);
        Assert.Equal(1, Volatile.Read(ref ends));
        lock (lines)
        {
            Assert.Single(lines, line => line.StartsWith("Exit requested (", StringComparison.Ordinal) && line.Contains("ends within", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.Contains("Exit requested (tray Quit)", StringComparison.Ordinal)
                && line.Contains("last stage: desktop lifetime exit", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, true)]
    [InlineData(WindowCloseReason.Undefined, true)]
    [InlineData(WindowCloseReason.OSShutdown, false)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, false)]
    public void OnlyACloseAimedAtTheWindowHidesIt(WindowCloseReason reason, bool hides)
    {
        Assert.Equal(hides, CloseToTrayDecision.ShouldHideOnClose(closesToTray: true, reason, alreadyInTray: false));
    }

    [Fact]
    public void ACloseWhileAlreadyInTheTrayOrWithoutCloseToTrayIsAnExit()
    {
        Assert.False(CloseToTrayDecision.ShouldHideOnClose(closesToTray: true, WindowCloseReason.WindowClosing, alreadyInTray: true));
        Assert.False(CloseToTrayDecision.ShouldHideOnClose(closesToTray: false, WindowCloseReason.WindowClosing, alreadyInTray: false));
    }
}
