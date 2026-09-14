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

    private static FeatureLifecycleCoordinator Lifecycle(
        IEnumerable<RuntimeFeatureDefinition> features,
        int maxParallel = 1) =>
        new(
            features,
            new ManualTimeProvider(Epoch),
            new(maxParallel, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
}
