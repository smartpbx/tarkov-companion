using System.Collections.Immutable;

namespace TarkovCompanion.Application.Services.Execution;

public enum FeatureDependencyKind
{
    Hard = 1,
    Optional,
}

public enum FeatureStartupPriority
{
    Background = 1,
    Normal,
    WorkspaceCritical,
}

public enum FeatureLifecycleState
{
    NotStarted = 1,
    Starting,
    Running,
    Degraded,
    Failed,
    Blocked,
    Stopping,
    Stopped,
    StopFailed,
    StopTimedOut,
}

public sealed record RuntimeFeatureDependency
{
    public RuntimeFeatureDependency(RuntimeFeatureId featureId, FeatureDependencyKind kind)
    {
        FeatureId = featureId.IsDefined ? featureId : throw new ArgumentException("A dependency feature id is required.", nameof(featureId));
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
    }

    public RuntimeFeatureId FeatureId { get; }

    public FeatureDependencyKind Kind { get; }
}

public sealed record RuntimeFeatureDefinition
{
    public RuntimeFeatureDefinition(
        RuntimeFeatureId featureId,
        FeatureStartupPriority priority,
        IReadOnlyList<RuntimeFeatureDependency> dependencies,
        Func<CancellationToken, Task> start,
        Func<CancellationToken, Task>? stop = null)
    {
        FeatureId = featureId.IsDefined ? featureId : throw new ArgumentException("A feature id is required.", nameof(featureId));
        Priority = Enum.IsDefined(priority) ? priority : throw new ArgumentOutOfRangeException(nameof(priority));
        ArgumentNullException.ThrowIfNull(dependencies);
        Dependencies = dependencies.ToImmutableArray();
        if (Dependencies.Any(dependency => dependency is null))
        {
            throw new ArgumentException("Feature dependencies cannot contain null.", nameof(dependencies));
        }

        if (Dependencies.Any(dependency => dependency.FeatureId == featureId)
            || Dependencies.Select(dependency => dependency.FeatureId).Distinct().Count() != Dependencies.Length)
        {
            throw new ArgumentException("Feature dependencies must be unique and cannot reference the feature itself.", nameof(dependencies));
        }

        Start = start ?? throw new ArgumentNullException(nameof(start));
        Stop = stop ?? (_ => Task.CompletedTask);
    }

    public RuntimeFeatureId FeatureId { get; }

    public FeatureStartupPriority Priority { get; }

    public ImmutableArray<RuntimeFeatureDependency> Dependencies { get; }

    public Func<CancellationToken, Task> Start { get; }

    public Func<CancellationToken, Task> Stop { get; }
}

public sealed record FeatureLifecycleOptions
{
    public FeatureLifecycleOptions(int maxParallelStarts, TimeSpan startTimeout, TimeSpan stopTimeout)
    {
        if (maxParallelStarts is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxParallelStarts));
        }

        if (startTimeout <= TimeSpan.Zero || startTimeout > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(startTimeout));
        }

        if (stopTimeout <= TimeSpan.Zero || stopTimeout > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        }

        MaxParallelStarts = maxParallelStarts;
        StartTimeout = startTimeout;
        StopTimeout = stopTimeout;
    }

    public int MaxParallelStarts { get; }

    public TimeSpan StartTimeout { get; }

    public TimeSpan StopTimeout { get; }

    public static FeatureLifecycleOptions Default { get; } = new(
        4,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(10));
}

public sealed record RuntimeFeatureSnapshot(
    RuntimeFeatureId FeatureId,
    FeatureStartupPriority Priority,
    FeatureLifecycleState State,
    ImmutableArray<RuntimeFeatureDependency> Dependencies,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    RuntimeFault? LastFault);

public sealed record FeatureLifecycleSnapshot(
    bool StartupCompleted,
    bool StopStarted,
    ImmutableArray<RuntimeFeatureSnapshot> Features)
{
    public static FeatureLifecycleSnapshot Empty { get; } = new(false, false, []);
}

/// <summary>Starts a feature DAG incrementally and stops it in reverse topological order.</summary>
public sealed class FeatureLifecycleCoordinator
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly FeatureLifecycleOptions _options;
    private readonly ImmutableDictionary<RuntimeFeatureId, FeatureNode> _nodes;
    private readonly ImmutableArray<RuntimeFeatureId> _topologicalOrder;
    private EventHandler? _changed;
    private bool _startupCompleted;
    private bool _stopStarted;
    private bool _startInvoked;

    public FeatureLifecycleCoordinator(
        IEnumerable<RuntimeFeatureDefinition> features,
        TimeProvider timeProvider,
        FeatureLifecycleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? FeatureLifecycleOptions.Default;
        var definitions = features.ToImmutableArray();
        if (definitions.Any(definition => definition is null))
        {
            throw new ArgumentException("Feature definitions cannot contain null.", nameof(features));
        }

        if (definitions.Select(definition => definition.FeatureId).Distinct().Count() != definitions.Length)
        {
            throw new ArgumentException("Feature ids must be unique.", nameof(features));
        }

        var known = definitions.Select(definition => definition.FeatureId).ToHashSet();
        var missing = definitions
            .SelectMany(definition => definition.Dependencies)
            .FirstOrDefault(dependency => !known.Contains(dependency.FeatureId));
        if (missing is not null)
        {
            throw new ArgumentException($"Unknown feature dependency '{missing.FeatureId}'.", nameof(features));
        }

        _nodes = definitions.ToImmutableDictionary(
            definition => definition.FeatureId,
            definition => new FeatureNode(definition));
        _topologicalOrder = BuildTopologicalOrder(definitions);
    }

    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    public FeatureLifecycleSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return SnapshotUnsafe();
            }
        }
    }

    public async Task<FeatureLifecycleSnapshot> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_startInvoked)
            {
                return SnapshotUnsafe();
            }

            _startInvoked = true;
        }

        var priorityClosure = WorkspacePriorityClosure();
        await StartPhaseAsync(priorityClosure, cancellationToken).ConfigureAwait(false);
        await StartPhaseAsync(
                _topologicalOrder.Where(featureId => !priorityClosure.Contains(featureId)).ToHashSet(),
                cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _startupCompleted = true;
        }

        PublishChanged();
        return Snapshot;
    }

    public async Task<FeatureLifecycleSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_stopStarted)
            {
                return SnapshotUnsafe();
            }

            _stopStarted = true;
        }

        PublishChanged();
        foreach (var featureId in _topologicalOrder.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = _nodes[featureId];
            FeatureLifecycleState state;
            lock (_gate)
            {
                state = node.State;
                if (state is FeatureLifecycleState.Running or FeatureLifecycleState.Degraded)
                {
                    node.State = FeatureLifecycleState.Stopping;
                }
            }

            if (state is not (FeatureLifecycleState.Running or FeatureLifecycleState.Degraded))
            {
                continue;
            }

            PublishChanged();
            using var stopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var stopping = node.Definition.Stop(stopCancellation.Token)
                    ?? throw new InvalidOperationException("The feature stop callback returned no task.");
                await stopping.WaitAsync(_options.StopTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
                SetTerminal(node, FeatureLifecycleState.Stopped, null);
            }
            catch (TimeoutException)
            {
                stopCancellation.Cancel();
                SetTerminal(node, FeatureLifecycleState.StopTimedOut, FeatureFault(node, RuntimeFailureKind.Timeout, "feature-stop-timeout"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (RuntimeFaultException exception)
            {
                SetTerminal(node, FeatureLifecycleState.StopFailed, exception.Fault);
            }
            catch (Exception exception)
            {
                SetTerminal(
                    node,
                    FeatureLifecycleState.StopFailed,
                    RuntimeFault.FromException(exception, _timeProvider, FeatureReference(node)));
            }

            PublishChanged();
        }

        return Snapshot;
    }

    private async Task StartPhaseAsync(HashSet<RuntimeFeatureId> phase, CancellationToken cancellationToken)
    {
        var remaining = new HashSet<RuntimeFeatureId>(phase);
        var running = new Dictionary<RuntimeFeatureId, Task>();
        while (remaining.Count > 0 || running.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = _topologicalOrder
                .Where(remaining.Contains)
                .Where(featureId => DependenciesReady(featureId, phase))
                .OrderByDescending(featureId => _nodes[featureId].Definition.Priority)
                .Take(_options.MaxParallelStarts - running.Count)
                .ToArray();

            foreach (var featureId in ready)
            {
                remaining.Remove(featureId);
                running.Add(featureId, StartOneAsync(_nodes[featureId], cancellationToken));
            }

            if (running.Count == 0)
            {
                throw new InvalidOperationException("The feature graph stopped making progress.");
            }

            await Task.WhenAny(running.Values).ConfigureAwait(false);
            foreach (var finished in running.Where(pair => pair.Value.IsCompleted).ToArray())
            {
                await finished.Value.ConfigureAwait(false);
                running.Remove(finished.Key);
            }
        }
    }

    private async Task StartOneAsync(FeatureNode node, CancellationToken cancellationToken)
    {
        RuntimeFeatureDependency? failedHard;
        bool degraded;
        lock (_gate)
        {
            failedHard = node.Definition.Dependencies.FirstOrDefault(dependency =>
                dependency.Kind == FeatureDependencyKind.Hard
                && _nodes[dependency.FeatureId].State is FeatureLifecycleState.Failed or FeatureLifecycleState.Blocked);
            degraded = node.Definition.Dependencies.Any(dependency =>
                _nodes[dependency.FeatureId].State == FeatureLifecycleState.Degraded
                || dependency.Kind == FeatureDependencyKind.Optional
                && _nodes[dependency.FeatureId].State is FeatureLifecycleState.Failed or FeatureLifecycleState.Blocked);
            if (failedHard is not null)
            {
                node.State = FeatureLifecycleState.Blocked;
                node.CompletedUtc = _timeProvider.GetUtcNow();
                node.LastFault = FeatureFault(node, RuntimeFailureKind.Conflict, "feature-hard-dependency-failed");
            }
            else
            {
                node.State = FeatureLifecycleState.Starting;
                node.StartedUtc = _timeProvider.GetUtcNow();
            }
        }

        PublishChanged();
        if (failedHard is not null)
        {
            return;
        }

        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var starting = node.Definition.Start(startCancellation.Token)
                ?? throw new InvalidOperationException("The feature start callback returned no task.");
            await starting.WaitAsync(_options.StartTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            SetTerminal(node, degraded ? FeatureLifecycleState.Degraded : FeatureLifecycleState.Running, null);
        }
        catch (TimeoutException)
        {
            startCancellation.Cancel();
            SetTerminal(node, FeatureLifecycleState.Failed, FeatureFault(node, RuntimeFailureKind.Timeout, "feature-start-timeout"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RuntimeFaultException exception)
        {
            SetTerminal(node, FeatureLifecycleState.Failed, exception.Fault);
        }
        catch (Exception exception)
        {
            SetTerminal(
                node,
                FeatureLifecycleState.Failed,
                RuntimeFault.FromException(exception, _timeProvider, FeatureReference(node)));
        }

        PublishChanged();
    }

    private bool DependenciesReady(RuntimeFeatureId featureId, HashSet<RuntimeFeatureId> phase)
    {
        lock (_gate)
        {
            return _nodes[featureId].Definition.Dependencies.All(dependency =>
                dependency.Kind == FeatureDependencyKind.Optional && !phase.Contains(dependency.FeatureId)
                || _nodes[dependency.FeatureId].State is not (FeatureLifecycleState.NotStarted or FeatureLifecycleState.Starting));
        }
    }

    private HashSet<RuntimeFeatureId> WorkspacePriorityClosure()
    {
        var closure = new HashSet<RuntimeFeatureId>();
        foreach (var feature in _nodes.Values.Where(node =>
                     node.Definition.Priority == FeatureStartupPriority.WorkspaceCritical))
        {
            AddHardDependencies(feature.Definition.FeatureId, closure);
        }

        return closure;
    }

    private void AddHardDependencies(RuntimeFeatureId featureId, HashSet<RuntimeFeatureId> closure)
    {
        if (!closure.Add(featureId))
        {
            return;
        }

        foreach (var dependency in _nodes[featureId].Definition.Dependencies.Where(dependency =>
                     dependency.Kind == FeatureDependencyKind.Hard))
        {
            AddHardDependencies(dependency.FeatureId, closure);
        }
    }

    private void SetTerminal(FeatureNode node, FeatureLifecycleState state, RuntimeFault? fault)
    {
        lock (_gate)
        {
            node.State = state;
            node.LastFault = fault;
            node.CompletedUtc = _timeProvider.GetUtcNow();
        }
    }

    private RuntimeFault FeatureFault(FeatureNode node, RuntimeFailureKind kind, string code) => new(
        kind,
        new(code),
        kind == RuntimeFailureKind.Timeout
            ? RuntimeRecoveryAction.RetryManually
            : RuntimeRecoveryAction.ResolveConflict,
        FeatureReference(node),
        _timeProvider.GetUtcNow());

    private static DiagnosticReference FeatureReference(FeatureNode node) =>
        new($"feature:{node.Definition.FeatureId}");

    private FeatureLifecycleSnapshot SnapshotUnsafe() => new(
        _startupCompleted,
        _stopStarted,
        [.. _topologicalOrder.Select(featureId => _nodes[featureId].Snapshot())]);

    private void PublishChanged()
    {
        EventHandler[] handlers;
        lock (_gate)
        {
            handlers = _changed?.GetInvocationList().Cast<EventHandler>().ToArray() ?? [];
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
            }
        }
    }

    private static ImmutableArray<RuntimeFeatureId> BuildTopologicalOrder(
        ImmutableArray<RuntimeFeatureDefinition> definitions)
    {
        var indegree = definitions.ToDictionary(definition => definition.FeatureId, _ => 0);
        var dependants = definitions.ToDictionary(definition => definition.FeatureId, _ => new List<RuntimeFeatureId>());
        foreach (var definition in definitions)
        {
            foreach (var dependency in definition.Dependencies)
            {
                indegree[definition.FeatureId]++;
                dependants[dependency.FeatureId].Add(definition.FeatureId);
            }
        }

        var definitionById = definitions.ToDictionary(definition => definition.FeatureId);
        var ready = new List<RuntimeFeatureId>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var order = ImmutableArray.CreateBuilder<RuntimeFeatureId>(definitions.Length);
        while (ready.Count > 0)
        {
            var next = ready
                .OrderByDescending(featureId => definitionById[featureId].Priority)
                .ThenBy(featureId => featureId.Value, StringComparer.Ordinal)
                .First();
            ready.Remove(next);
            order.Add(next);
            foreach (var dependant in dependants[next])
            {
                indegree[dependant]--;
                if (indegree[dependant] == 0)
                {
                    ready.Add(dependant);
                }
            }
        }

        if (order.Count != definitions.Length)
        {
            throw new ArgumentException("The feature dependency graph contains a cycle.", nameof(definitions));
        }

        return order.MoveToImmutable();
    }

    private sealed class FeatureNode(RuntimeFeatureDefinition definition)
    {
        public RuntimeFeatureDefinition Definition { get; } = definition;
        public FeatureLifecycleState State { get; set; } = FeatureLifecycleState.NotStarted;
        public DateTimeOffset? StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public RuntimeFault? LastFault { get; set; }

        public RuntimeFeatureSnapshot Snapshot() => new(
            Definition.FeatureId,
            Definition.Priority,
            State,
            Definition.Dependencies,
            StartedUtc,
            CompletedUtc,
            LastFault);
    }
}
