using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests.CaptureSessions;

public sealed class CaptureWorkSchedulerTests
{
    private static readonly DateTimeOffset Epoch =
        DateTimeOffset.Parse("2026-09-15T00:00:00Z");

    [Theory]
    [InlineData(CaptureWorkPriority.Intake, WorkPriority.Interactive)]
    [InlineData(CaptureWorkPriority.ReviewBlocking, WorkPriority.UserBlocking)]
    public async Task ProductionAdapterUsesCpuAdmissionAndCapturePriority(
        CaptureWorkPriority capturePriority,
        WorkPriority expectedPriority)
    {
        var clock = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(clock, Options(capacity: 2));
        var scheduler = new SupervisedCaptureWorkScheduler(supervisor, TimeSpan.FromSeconds(30));
        var ran = false;

        var result = await scheduler.RunAsync(
            new(CaptureCorrelationId.New(), "capture-adapter-test", capturePriority),
            _ =>
            {
                ran = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.True(result.Succeeded);
        Assert.True(ran);
        var operation = Assert.Single(supervisor.Snapshot.Operations);
        Assert.Equal(expectedPriority, operation.Priority);
        Assert.Equal(WorkloadClass.CPU, operation.WorkloadClass);
        Assert.Equal(BackgroundWorkState.Succeeded, operation.State);
    }

    [Fact]
    public async Task ProductionAdapterReportsSupervisorAdmissionRejection()
    {
        var clock = new ManualTimeProvider(Epoch);
        await using var supervisor = new BackgroundWorkSupervisor(clock, Options(capacity: 1));
        var scheduler = new SupervisedCaptureWorkScheduler(supervisor, TimeSpan.FromSeconds(30));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = scheduler.RunAsync(
            new(CaptureCorrelationId.New(), "capture-adapter-first", CaptureWorkPriority.Intake),
            async token =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            },
            CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var rejected = await scheduler.RunAsync(
                new(CaptureCorrelationId.New(), "capture-adapter-second", CaptureWorkPriority.Intake),
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert.False(rejected.Accepted);
            Assert.False(rejected.Succeeded);
            Assert.NotNull(rejected.DiagnosticCode);
        }
        finally
        {
            release.TrySetResult();
        }

        var completed = await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(completed.Accepted);
        Assert.True(completed.Succeeded);
        Assert.Equal(1, supervisor.Snapshot.Resources.Rejected);
    }

    private static BackgroundWorkSupervisorOptions Options(int capacity) => new(
        capacity,
        reservedInteractiveAdmission: 0,
        maxConcurrent: 1,
        lightLimit: 1,
        ioLimit: 1,
        cpuLimit: 1,
        maxPriorityBurst: 2,
        terminalHistoryLimit: 8,
        defaultStopTimeout: TimeSpan.FromSeconds(2));
}
