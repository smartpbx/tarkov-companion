using TarkovCompanion.Application.Services.Execution;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class FeatureLifecycleCoordinatorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WorkspaceFeatureAndItsHardDependencyStartBeforeUnrelatedWarmup()
    {
        var order = new List<string>();
        var foundation = new RuntimeFeatureId("foundation");
        var workspace = new RuntimeFeatureId("workspace");
        var lifecycle = Lifecycle(
        [
            Feature(foundation, FeatureStartupPriority.Normal, [], "foundation"),
            Feature(
                workspace,
                FeatureStartupPriority.WorkspaceCritical,
                [new(foundation, FeatureDependencyKind.Hard)],
                "workspace"),
            Feature(new("unrelated"), FeatureStartupPriority.Normal, [], "unrelated"),
        ]);

        await lifecycle.StartAsync();

        Assert.Equal(["foundation", "workspace", "unrelated"], order);

        RuntimeFeatureDefinition Feature(
            RuntimeFeatureId id,
            FeatureStartupPriority priority,
            IReadOnlyList<RuntimeFeatureDependency> dependencies,
            string name) =>
            new(id, priority, dependencies, _ =>
            {
                order.Add(name);
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task HardFailureBlocksOnlyHardDependantsAndOptionalFailureDegrades()
    {
        var failed = new RuntimeFeatureId("failed-dependency");
        var lifecycle = Lifecycle(
        [
            new(failed, FeatureStartupPriority.Normal, [], _ => Task.FromException(new IOException("private"))),
            new(
                new("hard-dependant"),
                FeatureStartupPriority.Normal,
                [new(failed, FeatureDependencyKind.Hard)],
                _ => Task.CompletedTask),
            new(
                new("optional-dependant"),
                FeatureStartupPriority.Normal,
                [new(failed, FeatureDependencyKind.Optional)],
                _ => Task.CompletedTask),
            new(new("unrelated"), FeatureStartupPriority.Normal, [], _ => Task.CompletedTask),
        ], maxParallel: 2);

        var snapshot = await lifecycle.StartAsync();

        Assert.Equal(FeatureLifecycleState.Failed, State("failed-dependency"));
        Assert.Equal(FeatureLifecycleState.Blocked, State("hard-dependant"));
        Assert.Equal(FeatureLifecycleState.Degraded, State("optional-dependant"));
        Assert.Equal(FeatureLifecycleState.Running, State("unrelated"));
        Assert.True(snapshot.StartupCompleted);

        FeatureLifecycleState State(string id) =>
            Assert.Single(snapshot.Features, feature => feature.FeatureId == new RuntimeFeatureId(id)).State;
    }

    [Fact]
    public async Task AReadyDependantFillsTheSlotLeftByAFastFeature()
    {
        var foundation = new RuntimeFeatureId("a-foundation");
        var slow = new RuntimeFeatureId("b-slow");
        var dependantStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycle = Lifecycle(
        [
            new(foundation, FeatureStartupPriority.Normal, [], _ => Task.CompletedTask),
            new(slow, FeatureStartupPriority.Normal, [], _ => releaseSlow.Task),
            new(
                new("c-dependant"),
                FeatureStartupPriority.Normal,
                [new(foundation, FeatureDependencyKind.Hard)],
                _ =>
                {
                    dependantStarted.TrySetResult();
                    return Task.CompletedTask;
                }),
        ], maxParallel: 2);

        var startup = lifecycle.StartAsync();
        await dependantStarted.Task;
        Assert.False(releaseSlow.Task.IsCompleted);

        releaseSlow.TrySetResult();
        await startup;
    }

    [Fact]
    public async Task StopIsReverseTopologicalAndIdempotent()
    {
        var stopOrder = new List<string>();
        var dependency = new RuntimeFeatureId("dependency");
        var dependant = new RuntimeFeatureId("dependant");
        var lifecycle = Lifecycle(
        [
            new(
                dependency,
                FeatureStartupPriority.Normal,
                [],
                _ => Task.CompletedTask,
                _ => Stop("dependency")),
            new(
                dependant,
                FeatureStartupPriority.Normal,
                [new(dependency, FeatureDependencyKind.Hard)],
                _ => Task.CompletedTask,
                _ => Stop("dependant")),
        ]);
        await lifecycle.StartAsync();

        await lifecycle.StopAsync();
        await lifecycle.StopAsync();

        Assert.Equal(["dependant", "dependency"], stopOrder);
        Assert.All(lifecycle.Snapshot.Features, feature => Assert.Equal(FeatureLifecycleState.Stopped, feature.State));

        Task Stop(string value)
        {
            stopOrder.Add(value);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StopRacingAnIgnoredStartupNeverLetsTheFeatureBecomeRunning()
    {
        var time = new ManualTimeProvider(Epoch);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopCalls = 0;
        var lifecycle = new FeatureLifecycleCoordinator(
        [
            new(
                new("slow-start"),
                FeatureStartupPriority.Normal,
                [],
                async _ =>
                {
                    started.TrySetResult();
                    await releaseStart.Task;
                },
                _ =>
                {
                    stopCalls++;
                    return Task.CompletedTask;
                }),
        ], time, new(1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));
        var startup = lifecycle.StartAsync();
        await started.Task;

        // The stop bound is armed inside StopAsync itself, so advancing straight away is exact.
        var stopping = lifecycle.StopAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        var bounded = await stopping;

        Assert.Equal(FeatureLifecycleState.Stopping, Assert.Single(bounded.Features).State);
        Assert.Null(Assert.Single(bounded.Features).CompletedUtc);
        Assert.Equal(0, stopCalls);
        Assert.False(bounded.IsQuiescent);

        releaseStart.TrySetResult();
        await startup;
        var stopped = await lifecycle.StopAsync();
        Assert.Equal(1, stopCalls);
        Assert.Equal(FeatureLifecycleState.Stopped, Assert.Single(stopped.Features).State);
        Assert.True(stopped.IsQuiescent);
    }

    /// <summary>Shutdown used to skip a feature that was still starting and release what it needed.</summary>
    [Fact]
    public async Task StopWaitsForAnInFlightDependantBeforeStoppingItsDependency()
    {
        var time = new ManualTimeProvider(Epoch);
        var dependantStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDependant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        var stopOrder = new List<string>();
        var dependency = new RuntimeFeatureId("dependency");
        var lifecycle = new FeatureLifecycleCoordinator(
        [
            new(dependency, FeatureStartupPriority.Normal, [], _ => Task.CompletedTask, _ => Stop("dependency")),
            new(
                new("dependant"),
                FeatureStartupPriority.Normal,
                [new(dependency, FeatureDependencyKind.Hard)],
                async _ =>
                {
                    dependantStarted.TrySetResult();
                    await releaseDependant.Task;
                },
                _ => Stop("dependant")),
        ], time, new(1, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1)));
        var startup = lifecycle.StartAsync();
        await dependantStarted.Task;

        var stopping = lifecycle.StopAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        var bounded = await stopping;

        Assert.Equal(FeatureLifecycleState.Stopping, State(bounded, "dependant"));
        Assert.Equal(FeatureLifecycleState.Running, State(bounded, "dependency"));
        lock (gate)
        {
            Assert.Empty(stopOrder);
        }

        releaseDependant.TrySetResult();
        await startup;
        var stopped = await lifecycle.StopAsync();

        lock (gate)
        {
            Assert.Equal(["dependant", "dependency"], stopOrder);
        }

        Assert.All(stopped.Features, feature => Assert.Equal(FeatureLifecycleState.Stopped, feature.State));
        Assert.False(stopped.StartupCompleted);

        Task Stop(string name)
        {
            lock (gate)
            {
                stopOrder.Add(name);
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StartTimeoutStopsTheFeatureOnceItsIgnoredStartReturns()
    {
        var time = new ManualTimeProvider(Epoch);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopCalls = 0;
        var slow = new RuntimeFeatureId("slow");
        var lifecycle = new FeatureLifecycleCoordinator(
        [
            new(
                slow,
                FeatureStartupPriority.Normal,
                [],
                async _ => await release.Task,
                _ =>
                {
                    Interlocked.Increment(ref stopCalls);
                    return Task.CompletedTask;
                }),
            new(
                new("hard-dependant"),
                FeatureStartupPriority.Normal,
                [new(slow, FeatureDependencyKind.Hard)],
                _ => Task.CompletedTask),
        ], time, new(1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

        var startup = lifecycle.StartAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        var snapshot = await startup;

        Assert.Equal(FeatureLifecycleState.StartTimedOut, State(snapshot, "slow"));
        Assert.Null(Feature(snapshot, "slow").CompletedUtc);
        Assert.Equal(FeatureLifecycleState.Blocked, State(snapshot, "hard-dependant"));
        Assert.Equal(0, Volatile.Read(ref stopCalls));
        Assert.False(snapshot.IsQuiescent);

        release.TrySetResult();
        await RuntimeTestTasks.UntilAsync(() => State(lifecycle.Snapshot, "slow") == FeatureLifecycleState.Failed);
        Assert.Equal(1, Volatile.Read(ref stopCalls));
        Assert.Equal("feature-start-timeout", Feature(lifecycle.Snapshot, "slow").LastFault!.Code.Value);
    }

    [Fact]
    public async Task StopBeforeStartMeansNothingEverStarts()
    {
        var starts = 0;
        var lifecycle = Lifecycle(
        [
            new(new("never"), FeatureStartupPriority.WorkspaceCritical, [], _ =>
            {
                starts++;
                return Task.CompletedTask;
            }),
        ]);

        var stopped = await lifecycle.StopAsync();
        var started = await lifecycle.StartAsync();

        Assert.Equal(0, starts);
        Assert.True(stopped.IsQuiescent);
        Assert.False(started.StartupCompleted);
        Assert.Equal(FeatureLifecycleState.NotStarted, Assert.Single(started.Features).State);
    }

    [Fact]
    public async Task StopTimeoutKeepsTheNodeNonterminalAndDelaysDependencyRelease()
    {
        var time = new ManualTimeProvider(Epoch);
        var releaseDependant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependencyStopped = false;
        var dependency = new RuntimeFeatureId("dependency");
        var lifecycle = new FeatureLifecycleCoordinator(
        [
            new(
                dependency,
                FeatureStartupPriority.Normal,
                [],
                _ => Task.CompletedTask,
                _ =>
                {
                    dependencyStopped = true;
                    return Task.CompletedTask;
                }),
            new(
                new("dependant"),
                FeatureStartupPriority.Normal,
                [new(dependency, FeatureDependencyKind.Hard)],
                _ => Task.CompletedTask,
                _ => releaseDependant.Task),
        ], time, new(1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1)));
        await lifecycle.StartAsync();

        var stopping = lifecycle.StopAsync();
        await RuntimeTestTasks.DrainAsync();
        time.Advance(TimeSpan.FromSeconds(1));
        var bounded = await stopping;

        var dependant = Assert.Single(bounded.Features, feature => feature.FeatureId == new RuntimeFeatureId("dependant"));
        Assert.Equal(FeatureLifecycleState.Stopping, dependant.State);
        Assert.Null(dependant.CompletedUtc);
        Assert.False(dependencyStopped);

        releaseDependant.TrySetResult();
        await lifecycle.StopAsync();
        Assert.True(dependencyStopped);
        Assert.All(lifecycle.Snapshot.Features, feature => Assert.Equal(FeatureLifecycleState.Stopped, feature.State));
    }

    [Fact]
    public void CyclesAreRejectedBeforeAnyFeatureStarts()
    {
        var a = new RuntimeFeatureId("a");
        var b = new RuntimeFeatureId("b");

        Assert.Throws<ArgumentException>(() => Lifecycle(
        [
            new(a, FeatureStartupPriority.Normal, [new(b, FeatureDependencyKind.Hard)], _ => Task.CompletedTask),
            new(b, FeatureStartupPriority.Normal, [new(a, FeatureDependencyKind.Hard)], _ => Task.CompletedTask),
        ]));
    }

    private static RuntimeFeatureSnapshot Feature(FeatureLifecycleSnapshot snapshot, string id) =>
        Assert.Single(snapshot.Features, feature => feature.FeatureId == new RuntimeFeatureId(id));

    private static FeatureLifecycleState State(FeatureLifecycleSnapshot snapshot, string id) =>
        Feature(snapshot, id).State;

    private static FeatureLifecycleCoordinator Lifecycle(
        IEnumerable<RuntimeFeatureDefinition> features,
        int maxParallel = 1) =>
        new(
            features,
            new ManualTimeProvider(Epoch),
            new(maxParallel, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
}
