using TarkovCompanion.App.Services;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class DatabaseMaintenanceCoordinatorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Maintenance_runs_after_six_hours_on_a_worker_and_then_reschedules()
    {
        var clock = new ManualTimeProvider(Epoch);
        var runtime = Runtime(clock);
        var callerThread = Environment.CurrentManagedThreadId;
        var ran = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new DatabaseMaintenanceCoordinator(
            _ =>
            {
                ran.TrySetResult(Environment.CurrentManagedThreadId);
                return Task.CompletedTask;
            },
            runtime,
            clock);

        coordinator.Start();
        clock.Advance(DatabaseMaintenanceCoordinator.CheckInterval - TimeSpan.FromMinutes(1));
        await RuntimeTestTasks.DrainAsync();
        Assert.False(ran.Task.IsCompleted);

        clock.Advance(TimeSpan.FromMinutes(1));
        var workerThread = await ran.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await RuntimeTestTasks.UntilAsync(() => clock.NextTimerUtc == Epoch.AddHours(12));

        Assert.NotEqual(callerThread, workerThread);
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

        await ran.Task.WaitAsync(TimeSpan.FromSeconds(2));
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
                using var registration = token.Register(cancelled.SetResult);
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            runtime,
            clock);

        coordinator.Start();
        clock.Advance(DatabaseMaintenanceCoordinator.CheckInterval);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        SetRaidState(runtime, RaidLifecycleState.LoadingRaid);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static RuntimeStateStore Runtime(TimeProvider clock) => new(
        new RuntimeOptions(false, false, GameMode.Regular, "en", TimeSpan.FromHours(1), TimeSpan.FromMinutes(1)),
        clock);

    private static void SetRaidState(IRuntimeStateStore runtime, RaidLifecycleState state) =>
        runtime.Update(snapshot => snapshot with { Raid = snapshot.Raid with { State = state } });
}
