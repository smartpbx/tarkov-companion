using TarkovCompanion.App.Services;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class DatabaseMaintenanceCoordinatorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    /// <summary>How long a signal may take to arrive before the test gives up on it.</summary>
    /// <remarks>
    /// A liveness bound, not a measurement: every wait here is on a signal, and the clock is the
    /// manual one. The failures seen under load were never slowness — see the cancellation test —
    /// so this only has to be longer than a busy pool takes to start a work item.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Maintenance_runs_after_six_hours_on_a_worker_and_then_reschedules()
    {
        var clock = new ManualTimeProvider(Epoch);
        var runtime = Runtime(clock);
        // Whether maintenance ran inside the timer callback, on the thread that fired it — in the
        // app, whichever thread that is. Comparing thread ids with the test's first line said
        // nothing: the test resumes on pool threads, and the maintenance work item once landed on
        // the very thread the test had started on, which is not the fault being guarded.
        var advancing = 0;
        var advancingThread = 0;
        var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DatabaseMaintenanceCoordinator(
            _ =>
            {
                ran.TrySetResult(Volatile.Read(ref advancing) == 1 && Environment.CurrentManagedThreadId == advancingThread);
                return Task.CompletedTask;
            },
            runtime,
            clock);

        coordinator.Start();
        clock.Advance(DatabaseMaintenanceCoordinator.CheckInterval - TimeSpan.FromMinutes(1));
        await RuntimeTestTasks.DrainAsync();
        Assert.False(ran.Task.IsCompleted);

        advancingThread = Environment.CurrentManagedThreadId;
        Volatile.Write(ref advancing, 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        Volatile.Write(ref advancing, 0);
        var ranInsideTheTimer = await ran.Task.WaitAsync(Patience);
        await RuntimeTestTasks.UntilAsync(() => clock.NextTimerUtc == Epoch.AddHours(12));

        Assert.False(ranInsideTheTimer, "Maintenance ran on the thread that fired the timer, inside its callback.");
    }

    [Fact]
    public async Task A_due_check_waits_for_idle_then_retries_after_five_minutes()
    {
        var clock = new ManualTimeProvider(Epoch);
        var runtime = Runtime(clock);
        SetRaidState(runtime, RaidLifecycleState.InRaid);
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DatabaseMaintenanceCoordinator(
            _ =>
            {
                ran.TrySetResult();
                return Task.CompletedTask;
            },
            runtime,
            clock);

        coordinator.Start();
        clock.Advance(DatabaseMaintenanceCoordinator.CheckInterval);
        await RuntimeTestTasks.UntilAsync(() =>
            clock.NextTimerUtc == Epoch.Add(DatabaseMaintenanceCoordinator.CheckInterval + DatabaseMaintenanceCoordinator.RaidRetryInterval));
        Assert.False(ran.Task.IsCompleted);

        SetRaidState(runtime, RaidLifecycleState.Menu);
        clock.Advance(DatabaseMaintenanceCoordinator.RaidRetryInterval);

        await ran.Task.WaitAsync(Patience);
    }

    [Fact]
    public async Task A_raid_start_cancels_maintenance_that_was_already_running()
    {
        var clock = new ManualTimeProvider(Epoch);
        var runtime = Runtime(clock);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DatabaseMaintenanceCoordinator(
            async token =>
            {
                entered.SetResult();
                // Observed where the maintenance sees it, not through a registration. The
                // registration was disposed by this lambda's own `using` when the delay's
                // cancellation resumed it inline, which on some runs happened before the
                // registration's own callback had been reached: the token was cancelled and the
                // coordinator had moved on to its retry, but the test waited for a signal that had
                // been unregistered (6 of 20 runs under load, 2026-09-24).
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.SetResult();
                    throw;
                }
            },
            runtime,
            clock);

        coordinator.Start();
        clock.Advance(DatabaseMaintenanceCoordinator.CheckInterval);
        await entered.Task.WaitAsync(Patience);

        SetRaidState(runtime, RaidLifecycleState.LoadingRaid);

        await cancelled.Task.WaitAsync(Patience);
    }

    private static RuntimeStateStore Runtime(TimeProvider clock) => new(
        new RuntimeOptions(false, false, GameMode.Regular, "en", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)),
        clock);

    private static void SetRaidState(IRuntimeStateStore runtime, RaidLifecycleState state) =>
        runtime.Update(snapshot => snapshot with { Raid = snapshot.Raid with { State = state } });
}
