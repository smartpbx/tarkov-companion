using TarkovCompanion.App.Services.Updates;

namespace TarkovCompanion.UnitTests.Updates;

public sealed class UpdateHandOverTests
{
    private sealed class RecordingEnder(List<string> calls) : IProcessEnder
    {
        public TimeSpan? Armed { get; private set; }

        public void ArmKill(TimeSpan after)
        {
            Armed = after;
            calls.Add("arm-kill");
        }

        public void EndNow() => calls.Add("end");
    }

    private static UpdateHandOver HandOver(
        List<string> calls,
        RecordingEnder ender,
        Func<TimeSpan, Task>? flush = null,
        Action? stop = null) =>
        new(
            new UpdateHandOverSteps(
                StopAcceptingWork: stop ?? (() => calls.Add("stop")),
                CloseInterface: () => calls.Add("close"),
                FlushAsync: flush ?? (budget =>
                {
                    calls.Add($"flush {budget.TotalSeconds:0}s");
                    return Task.CompletedTask;
                })),
            ender,
            _ => { });

    [Fact]
    public void TheUpdaterIsStartedLastAndTheProcessEndsStraightAfter()
    {
        var calls = new List<string>();
        var ender = new RecordingEnder(calls);

        HandOver(calls, ender).Run("2.0.1400", () => calls.Add("start-updater"));

        Assert.Equal(["stop", "close", "flush 2s", "start-updater", "arm-kill", "end"], calls);
        Assert.Equal(UpdateHandOver.KillAfter, ender.Armed);
        Assert.True(UpdateHandOver.KillAfter <= TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public void AFlushThatNeverFinishesIsAbandonedAtTheBudget()
    {
        var calls = new List<string>();
        var ender = new RecordingEnder(calls);
        var never = new TaskCompletionSource();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        HandOver(calls, ender, flush: _ => never.Task).Run("2.0.1400", () => calls.Add("start-updater"));

        // The budget, and the slack of a loaded test host; nowhere near the updater's ten seconds.
        Assert.InRange(elapsed.Elapsed, UpdateHandOver.FlushBudget - TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(6));
        Assert.Equal(["stop", "close", "start-updater", "arm-kill", "end"], calls);
    }

    [Fact]
    public void AStepThatThrowsDoesNotStopTheHandOver()
    {
        var calls = new List<string>();
        var ender = new RecordingEnder(calls);

        HandOver(calls, ender, flush: _ => throw new InvalidOperationException("locked"), stop: () => throw new IOException("gone"))
            .Run("2.0.1400", () => calls.Add("start-updater"));

        Assert.Equal(["close", "start-updater", "arm-kill", "end"], calls);
    }

    [Fact]
    public void AnUpdaterThatWillNotStartStillEndsTheProcess()
    {
        var calls = new List<string>();
        var ender = new RecordingEnder(calls);

        HandOver(calls, ender).Run("2.0.1400", () => throw new FileNotFoundException("Update.exe"));

        // Nothing is left to go back to: windows hidden, services stopped. The package is still
        // on disk and the next launch applies it.
        Assert.Equal(["stop", "close", "flush 2s", "arm-kill", "end"], calls);
    }

    [Theory]
    [InlineData(true, "--veloapp-obsolete", "2.0.1324")]
    [InlineData(true, "--veloapp-updated", "2.0.1337")]
    [InlineData(true, "--veloapp-install", "2.0.1337")]
    [InlineData(true, "--veloapp-uninstall", "2.0.1337")]
    [InlineData(false, "--ui-shell", "v2-a")]
    [InlineData(false)]
    public void AnInstallerHookIsRecognisedFromItsArguments(bool expected, params string[] args) =>
        Assert.Equal(expected, InstallerHook.IsHookInvocation(args));
}
