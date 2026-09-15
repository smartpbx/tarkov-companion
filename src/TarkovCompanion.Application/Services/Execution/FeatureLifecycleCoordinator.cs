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

    /// <summary>
    /// Start exceeded its deadline and its callback has not returned. Not terminal: the feature
    /// is stopped once the callback does return, and only then settles as failed.
    /// </summary>
    StartTimedOut,
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

    /// <summary>Whether no feature is still starting, running, or stopping.</summary>
    public bool IsQuiescent => Features.IsDefault || Features.All(feature => feature.State is
        FeatureLifecycleState.NotStarted
        or FeatureLifecycleState.Failed
        or FeatureLifecycleState.Blocked
        or FeatureLifecycleState.Stopped
        or FeatureLifecycleState.StopFailed);
}

/// <summary>Starts a feature DAG incrementally and stops it in reverse topological order.</summary>
/// <remarks>
/// <para>
/// Stop used to walk the graph once and skip anything that was not yet running. A feature whose
/// start was still in flight was passed over, finished starting afterwards, and stayed Running
/// after shutdown had reported completion — with its dependencies already released beneath it.
/// </para>
/// <para>
/// Every start now publishes its invocation before any stop can look at it, and only this
/// coordinator's shutdown walk launches stops once stopping has begun. Once stopping has begun
/// nothing new may enter Starting, so the walk cannot pass a feature whose start is in flight:
/// it waits for that start to return and then stops it, before its dependencies.
/// </para>
/// </remarks>
public sealed class FeatureLifecycleCoordinator
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly FeatureLifecycleOptions _options;
    private readonly ImmutableDictionary<RuntimeFeatureId, FeatureNode> _nodes;
    private readonly ImmutableArray<RuntimeFeatureId> _topologicalOrder;
    private readonly CancellationTokenSource _startupCancellation = new();
    private EventHandler? _changed;
    private bool _startupCompleted;
    private bool _stopStarted;
    private bool _startInvoked;
    private TaskCompletionSource? _shutdown;

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
            if (_startInvoked || _stopStarted)
            {
                _startInvoked = true;
                return SnapshotUnsafe();
            }

            _startInvoked = true;
        }

        // Disposed only once every start has settled. Leaving startup early can leave starts
        // still waiting on this token, and disposing it then could drop the cancellation they
        // have not yet received.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _startupCancellation.Token);
        try
        {
            var priorityClosure = WorkspacePriorityClosure();
            await StartPhaseAsync(priorityClosure, linked.Token).ConfigureAwait(false);
            await StartPhaseAsync(
                    _topologicalOrder.Where(featureId => !priorityClosure.Contains(featureId)).ToHashSet(),
                    linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _startupCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Snapshot;
        }

        linked.Dispose();
        lock (_gate)
        {
            _startupCompleted = !_stopStarted;
        }

        PublishChanged();
        return Snapshot;
    }

    /// <summary>Stops every started feature in reverse dependency order, within a bound.</summary>
    /// <remarks>
    /// The returned snapshot is truthful rather than terminal: a feature whose start or stop
    /// has not returned when the bound expires is still reported as stopping. The same
    /// shutdown continues, and a later call waits for it again.
    /// </remarks>
    public async Task<FeatureLifecycleSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource? owner = null;
        Task shutdown;
        lock (_gate)
        {
            _stopStarted = true;
            if (_shutdown is null)
            {
                _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
                owner = _shutdown;
            }

            shutdown = _shutdown.Task;
        }

        // Bounded from the moment stop is requested, before any callback is allowed to run.
        var bounded = shutdown.WaitAsync(_options.StopTimeout, _timeProvider, cancellationToken);
        if (owner is not null)
        {
            var cancelling = ObserveAsync(_startupCancellation.CancelAsync());
            PublishChanged();
            ShutdownWork = RunShutdownAsync(owner, cancelling);
        }

        try
        {
            await bounded.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The retained workflow continues in reverse dependency order. Returning a bounded
            // snapshot does not claim the still-stopping nodes are terminal.
        }

        return Snapshot;
    }

    /// <summary>The shutdown walk, retained for its lifetime; callers observe its owner instead.</summary>
    private Task? ShutdownWork { get; set; }

    private async Task RunShutdownAsync(TaskCompletionSource owner, Task cancelling)
    {
        try
        {
            foreach (var featureId in _topologicalOrder.Reverse())
            {
                var stopping = EnsureStopTask(_nodes[featureId]);
                if (stopping is not null)
                {
                    await stopping.ConfigureAwait(false);
                }
            }

            await cancelling.ConfigureAwait(false);
        }
        finally
        {
            owner.TrySetResult();
            PublishChanged();
        }
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
        bool degraded;
        TaskCompletionSource<Task>? invocation = null;
        CancellationToken startToken = default;
        lock (_gate)
        {
            var failedHard = node.Definition.Dependencies.FirstOrDefault(dependency =>
                dependency.Kind == FeatureDependencyKind.Hard
                && (_nodes[dependency.FeatureId].StartupFailed
                    || _nodes[dependency.FeatureId].State is FeatureLifecycleState.Failed or FeatureLifecycleState.Blocked));
            degraded = node.Definition.Dependencies.Any(dependency =>
                _nodes[dependency.FeatureId].State == FeatureLifecycleState.Degraded
                || dependency.Kind == FeatureDependencyKind.Optional
                && (_nodes[dependency.FeatureId].StartupFailed
                    || _nodes[dependency.FeatureId].State is FeatureLifecycleState.Failed or FeatureLifecycleState.Blocked));
            if (failedHard is not null)
            {
                node.State = FeatureLifecycleState.Blocked;
                node.StartupFailed = true;
                node.StartupSettled = true;
                node.CompletedUtc = _timeProvider.GetUtcNow();
                node.LastFault = FeatureFault(node, RuntimeFailureKind.Conflict, "feature-hard-dependency-failed");
            }
            else if (_stopStarted)
            {
                // Stopping has begun, so nothing new may start. The node stays NotStarted and
                // there is nothing for the shutdown walk to stop.
                node.StartupFailed = true;
                node.StartupSettled = true;
            }
            else
            {
                node.State = FeatureLifecycleState.Starting;
                node.StartedUtc = _timeProvider.GetUtcNow();
                node.StartCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startToken = node.StartCancellation.Token;
                invocation = new(TaskCreationOptions.RunContinuationsAsynchronously);
                node.StartInvocation = invocation;
            }
        }

        PublishChanged();
        if (invocation is null)
        {
            return;
        }

        Task starting;
        try
        {
            starting = node.Definition.Start(startToken)
                ?? Task.FromException(new InvalidOperationException("The feature start callback returned no task."));
        }
        catch (Exception exception)
        {
            starting = Task.FromException(exception);
        }

        invocation.TrySetResult(starting);
        try
        {
            await starting.WaitAsync(_options.StartTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is TimeoutException or OperationCanceledException
            && (!starting.IsCompleted || starting.IsCompletedSuccessfully))
        {
            // The wait gave up but the start itself did not fail: it is still running, or it
            // succeeded in the instant after the deadline. Either way something may have
            // started, so it is stopped once the callback returns rather than recorded as a
            // plain failure and left running.
            // Cancelled explicitly for both reasons: the wait can observe a startup cancellation
            // before the token link has passed it on to the start itself.
            var timedOut = exception is TimeoutException;
            TryCancelStart(node);

            SettleUnfinishedStart(
                node,
                timedOut
                    ? FeatureFault(node, RuntimeFailureKind.Timeout, "feature-start-timeout")
                    : FeatureFault(node, RuntimeFailureKind.Cancelled, "feature-start-cancelled"),
                timedOut);
            if (!timedOut && !_startupCancellation.IsCancellationRequested)
            {
                throw;
            }

            return;
        }
        catch (Exception exception)
        {
            var fault = exception is RuntimeFaultException classified
                ? classified.Fault
                : RuntimeFault.FromException(exception, _timeProvider, FeatureReference(node));
            lock (_gate)
            {
                node.StartupFailed = true;
                node.StartupSettled = true;
                node.LastFault = fault;
                if (node.StopCompletion is null)
                {
                    node.State = FeatureLifecycleState.Failed;
                    node.CompletedUtc = _timeProvider.GetUtcNow();
                }
            }

            DisposeStartCancellation(node);
            PublishChanged();
            if (exception is OperationCanceledException
                && cancellationToken.IsCancellationRequested
                && !_startupCancellation.IsCancellationRequested)
            {
                throw;
            }

            return;
        }

        lock (_gate)
        {
            node.StartupSettled = true;
            if (node.StopCompletion is null && !_stopStarted)
            {
                node.State = degraded ? FeatureLifecycleState.Degraded : FeatureLifecycleState.Running;
                node.LastFault = null;
                node.CompletedUtc = _timeProvider.GetUtcNow();
            }
            else if (node.StopCompletion is null)
            {
                // Stop won the race. The shutdown walk reaches this node after its dependants and
                // stops it then; it must not be reported as running in the meantime.
                node.State = FeatureLifecycleState.Stopping;
                node.CompletedUtc = null;
            }
        }

        DisposeStartCancellation(node);
        PublishChanged();
    }

    /// <summary>Records a start that has not returned and arranges its compensating stop.</summary>
    private void SettleUnfinishedStart(FeatureNode node, RuntimeFault fault, bool timedOut)
    {
        bool stopStarted;
        lock (_gate)
        {
            stopStarted = _stopStarted;
            node.StartupFailed = true;
            node.StartupSettled = true;
            node.CompletedUtc = null;
            node.LastFault = fault;
            if (node.StopCompletion is null)
            {
                node.State = stopStarted
                    ? FeatureLifecycleState.Stopping
                    : timedOut
                        ? FeatureLifecycleState.StartTimedOut
                        : FeatureLifecycleState.Starting;
            }
        }

        PublishChanged();
        if (!stopStarted)
        {
            // Outside shutdown nobody else will stop this feature once its start returns.
            // During shutdown the walk owns the stop so dependency order is preserved.
            EnsureStopTask(node);
        }
    }

    private bool DependenciesReady(RuntimeFeatureId featureId, HashSet<RuntimeFeatureId> phase)
    {
        lock (_gate)
        {
            return _nodes[featureId].Definition.Dependencies.All(dependency =>
                dependency.Kind == FeatureDependencyKind.Optional && !phase.Contains(dependency.FeatureId)
                || _nodes[dependency.FeatureId].StartupSettled);
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

    /// <summary>Returns the one stop of a node whose start was invoked, launching it once.</summary>
    private Task? EnsureStopTask(FeatureNode node)
    {
        TaskCompletionSource? owner = null;
        Task stop;
        lock (_gate)
        {
            if (node.StopCompletion is null)
            {
                var startedOrStarting = node.State is
                    FeatureLifecycleState.Running or
                    FeatureLifecycleState.Degraded or
                    FeatureLifecycleState.Starting or
                    FeatureLifecycleState.StartTimedOut or
                    FeatureLifecycleState.Stopping;
                if (!startedOrStarting || node.StartInvocation is null)
                {
                    return null;
                }

                node.StopCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                owner = node.StopCompletion;
                if (_stopStarted || node.State is FeatureLifecycleState.Running or FeatureLifecycleState.Degraded)
                {
                    node.State = FeatureLifecycleState.Stopping;
                    node.CompletedUtc = null;
                }
            }

            stop = node.StopCompletion.Task;
        }

        if (owner is not null)
        {
            PublishChanged();
            node.StopWork = StopAfterStartAsync(node, owner);
        }

        return stop;
    }

    private async Task StopAfterStartAsync(FeatureNode node, TaskCompletionSource owner)
    {
        try
        {
            var starting = await node.StartInvocation!.Task.ConfigureAwait(false);
            var started = true;
            try
            {
                await starting.ConfigureAwait(false);
            }
            catch (Exception)
            {
                started = false;
            }

            DisposeStartCancellation(node);
            if (!started)
            {
                // The start itself failed, so nothing began and there is nothing to stop.
                SettleStopped(node, stopFault: null, startSucceeded: false);
                return;
            }

            lock (_gate)
            {
                node.State = FeatureLifecycleState.Stopping;
                node.CompletedUtc = null;
            }

            PublishChanged();
            using var stopCancellation = new CancellationTokenSource();
            Task stopping;
            try
            {
                stopping = node.Definition.Stop(stopCancellation.Token)
                    ?? Task.FromException(new InvalidOperationException("The feature stop callback returned no task."));
            }
            catch (Exception exception)
            {
                stopping = Task.FromException(exception);
            }

            try
            {
                await stopping.WaitAsync(_options.StopTimeout, _timeProvider).ConfigureAwait(false);
            }
            catch (TimeoutException) when (!stopping.IsCompleted)
            {
                TryCancel(stopCancellation);
                lock (_gate)
                {
                    node.State = FeatureLifecycleState.Stopping;
                    node.CompletedUtc = null;
                    node.LastFault = FeatureFault(node, RuntimeFailureKind.Timeout, "feature-stop-timeout");
                }

                PublishChanged();
                try
                {
                    await stopping.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    SettleStopped(node, StopFault(node, exception), startSucceeded: true);
                    return;
                }
            }
            catch (Exception exception)
            {
                SettleStopped(node, StopFault(node, exception), startSucceeded: true);
                return;
            }

            SettleStopped(node, stopFault: null, startSucceeded: true);
        }
        finally
        {
            owner.TrySetResult();
        }
    }

    private void SettleStopped(FeatureNode node, RuntimeFault? stopFault, bool startSucceeded)
    {
        lock (_gate)
        {
            if (stopFault is not null)
            {
                node.State = FeatureLifecycleState.StopFailed;
                node.LastFault = stopFault;
            }
            else if (node.StartupFailed && !_stopStarted)
            {
                // A compensating stop after a timed-out or cancelled start: the feature never
                // became usable, and the fault that says why is kept.
                node.State = FeatureLifecycleState.Failed;
                node.LastFault ??= FeatureFault(node, RuntimeFailureKind.Timeout, "feature-start-timeout");
            }
            else
            {
                node.State = FeatureLifecycleState.Stopped;
                if (startSucceeded)
                {
                    node.LastFault = null;
                }
            }

            node.CompletedUtc = _timeProvider.GetUtcNow();
        }

        PublishChanged();
    }

    private RuntimeFault StopFault(FeatureNode node, Exception exception) =>
        exception is RuntimeFaultException classified
            ? classified.Fault
            : RuntimeFault.FromException(exception, _timeProvider, FeatureReference(node));

    private void TryCancelStart(FeatureNode node)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = node.StartCancellation;
        }

        if (cancellation is not null)
        {
            TryCancel(cancellation);
        }
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The start returned and its token was released between the deadline and here.
        }
        catch (AggregateException)
        {
            // A callback registered by the feature threw; the feature's own result reports it.
        }
    }

    private void DisposeStartCancellation(FeatureNode node)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = node.StartCancellation;
            node.StartCancellation = null;
        }

        cancellation?.Dispose();
    }

    private RuntimeFault FeatureFault(FeatureNode node, RuntimeFailureKind kind, string code) => new(
        kind,
        new(code),
        kind is RuntimeFailureKind.Timeout or RuntimeFailureKind.Cancelled
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

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A cancellation callback registered by a feature threw. The feature's own start
            // result records its failure; shutdown still proceeds.
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
        public bool StartupSettled { get; set; }
        public bool StartupFailed { get; set; }
        public CancellationTokenSource? StartCancellation { get; set; }

        /// <summary>The task the start callback returned, published before any stop may run.</summary>
        public TaskCompletionSource<Task>? StartInvocation { get; set; }

        public TaskCompletionSource? StopCompletion { get; set; }

        /// <summary>The stop itself, retained for its lifetime; callers observe the completion.</summary>
        public Task? StopWork { get; set; }

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
