using System.Collections.Immutable;

namespace TarkovCompanion.Application.Services.Execution;

public enum BackgroundWorkState
{
    Pending = 1,
    Running,
    Succeeded,
    Faulted,
    Cancelled,
    TimedOut,
    Rejected,
    Restarting,
}

public sealed record BackgroundWorkSupervisorOptions
{
    public BackgroundWorkSupervisorOptions(
        int capacity,
        int reservedInteractiveAdmission,
        int maxConcurrent,
        int lightLimit,
        int ioLimit,
        int cpuLimit,
        int maxPriorityBurst,
        int terminalHistoryLimit,
        TimeSpan defaultStopTimeout,
        TimeSpan? minimumRestartDelay = null)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (reservedInteractiveAdmission is < 0 || reservedInteractiveAdmission >= capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedInteractiveAdmission));
        }

        if (maxConcurrent is < 1 || maxConcurrent > capacity)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        }

        if (lightLimit is < 1 || lightLimit > maxConcurrent)
        {
            throw new ArgumentOutOfRangeException(nameof(lightLimit));
        }

        if (ioLimit is < 1 || ioLimit > maxConcurrent)
        {
            throw new ArgumentOutOfRangeException(nameof(ioLimit));
        }

        if (cpuLimit is < 1 || cpuLimit > maxConcurrent)
        {
            throw new ArgumentOutOfRangeException(nameof(cpuLimit));
        }

        if (maxPriorityBurst is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPriorityBurst));
        }

        if (terminalHistoryLimit < capacity || terminalHistoryLimit > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalHistoryLimit));
        }

        if (defaultStopTimeout <= TimeSpan.Zero || defaultStopTimeout > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultStopTimeout));
        }

        // A zero floor would let an operation that fails immediately restart in a hot loop and
        // hold a CPU while never being admitted as new work.
        var restartDelay = minimumRestartDelay ?? TimeSpan.FromSeconds(1);
        if (restartDelay <= TimeSpan.Zero || restartDelay > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumRestartDelay));
        }

        Capacity = capacity;
        ReservedInteractiveAdmission = reservedInteractiveAdmission;
        MaxConcurrent = maxConcurrent;
        LightLimit = lightLimit;
        IoLimit = ioLimit;
        CpuLimit = cpuLimit;
        MaxPriorityBurst = maxPriorityBurst;
        TerminalHistoryLimit = terminalHistoryLimit;
        DefaultStopTimeout = defaultStopTimeout;
        MinimumRestartDelay = restartDelay;
    }

    public int Capacity { get; }

    public int ReservedInteractiveAdmission { get; }

    public int MaxConcurrent { get; }

    public int LightLimit { get; }

    public int IoLimit { get; }

    public int CpuLimit { get; }

    public int MaxPriorityBurst { get; }

    public int TerminalHistoryLimit { get; }

    public TimeSpan DefaultStopTimeout { get; }

    /// <summary>The shortest pause before an automatically restarted operation runs again.</summary>
    public TimeSpan MinimumRestartDelay { get; }

    public static BackgroundWorkSupervisorOptions Default { get; } = new(
        capacity: 64,
        reservedInteractiveAdmission: 4,
        maxConcurrent: 8,
        lightLimit: 8,
        ioLimit: 4,
        cpuLimit: 2,
        maxPriorityBurst: 8,
        terminalHistoryLimit: 256,
        defaultStopTimeout: TimeSpan.FromSeconds(10));
}

public sealed record BackgroundWorkRequest
{
    public BackgroundWorkRequest(
        OperationExecutionRequest execution,
        OperationScopeId scopeId,
        WorkPriority priority)
    {
        Execution = execution ?? throw new ArgumentNullException(nameof(execution));
        ScopeId = scopeId.IsDefined ? scopeId : throw new ArgumentException("An operation scope is required.", nameof(scopeId));
        Priority = Enum.IsDefined(priority) ? priority : throw new ArgumentOutOfRangeException(nameof(priority));
    }

    public OperationExecutionRequest Execution { get; }

    public OperationScopeId ScopeId { get; }

    public WorkPriority Priority { get; }
}

public sealed record BackgroundWorkResult(
    OperationId OperationId,
    BackgroundWorkState State,
    RuntimeFault? Fault,
    int Attempts,
    DateTimeOffset CompletedUtc);

public sealed record BackgroundWorkSnapshot(
    RuntimeFeatureId FeatureId,
    OperationId OperationId,
    CorrelationId CorrelationId,
    OperationScopeId ScopeId,
    WorkloadClass WorkloadClass,
    WorkPriority Priority,
    BackgroundWorkState State,
    int Attempts,
    int Restarts,
    DateTimeOffset SubmittedUtc,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    RuntimeFault? LastFault);

public sealed record SupervisorResourceSnapshot(
    int Capacity,
    int ReservedInteractiveAdmission,
    int Pending,
    int Running,
    int RunningLight,
    int RunningIO,
    int RunningCPU,
    bool HeavyExclusiveRunning,
    int PeakRunning,
    long Admitted,
    long Rejected,
    long Completed,
    long SubscriberFaults,
    int Restarting = 0);

public sealed record BackgroundWorkSupervisorSnapshot(
    bool IsStopping,
    SupervisorResourceSnapshot Resources,
    ImmutableArray<BackgroundWorkSnapshot> Operations)
{
    public static BackgroundWorkSupervisorSnapshot Empty { get; } = new(
        false,
        new(0, 0, 0, 0, 0, 0, 0, false, 0, 0, 0, 0, 0),
        []);
}

public sealed record BackgroundWorkAdmission(
    bool Accepted,
    BackgroundWorkHandle Handle,
    RuntimeFault? Fault);

public sealed class BackgroundWorkHandle
{
    internal BackgroundWorkHandle(OperationId operationId, Task<BackgroundWorkResult> completion)
    {
        OperationId = operationId;
        Completion = completion;
    }

    public OperationId OperationId { get; }

    public Task<BackgroundWorkResult> Completion { get; }
}

/// <summary>How a bounded stop ended.</summary>
/// <param name="CompletedWithinDeadline">Every admitted operation reached a terminal state and its
/// user work returned before the deadline.</param>
/// <param name="UnfinishedOperations">Operations still executing, or still owning their resources,
/// when the deadline passed. They stay non-terminal and are not reported as cancelled.</param>
public sealed record SupervisorStopResult(bool CompletedWithinDeadline, int UnfinishedOperations);

public interface IBackgroundWorkSupervisor : IAsyncDisposable
{
    event EventHandler? Changed;

    BackgroundWorkSupervisorSnapshot Snapshot { get; }

    BackgroundWorkAdmission Submit(
        BackgroundWorkRequest request,
        Func<OperationAttemptContext, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    BackgroundWorkAdmission Retry(
        OperationId operationId,
        OperationId retryOperationId,
        CancellationToken cancellationToken = default);

    Task<SupervisorStopResult> StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>A bounded priority scheduler with explicit resource-class limits.</summary>
/// <remarks>
/// <para>
/// A stop used to mark an operation that ignored cancellation as terminal and hand its slot back,
/// so a second copy of the same user work could be admitted while the first was still running.
/// An operation now keeps its state, its completion and its exact resource class until the
/// dependency really returns; a stop that runs out of time says how many are still unfinished
/// instead of pretending they ended.
/// </para>
/// <para>
/// Cancellation registrations are only ever unregistered while the scheduler lock is held.
/// Disposing one there waits for a callback that is itself waiting for the lock.
/// </para>
/// </remarks>
public sealed class BackgroundWorkSupervisor : IBackgroundWorkSupervisor
{
    private readonly object _gate = new();
    private readonly BackgroundWorkSupervisorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ResilienceExecutor _executor;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly List<WorkItem> _pending = [];
    private readonly Dictionary<OperationId, WorkItem> _items = [];
    private readonly Task _dispatcher;
    private EventHandler? _changed;
    private Task _lifetimeCancellation = Task.CompletedTask;
    private long _nextSequence;
    private long _admitted;
    private long _rejected;
    private long _completed;
    private long _subscriberFaults;
    private int _running;
    private int _restarting;
    private int _runningLight;
    private int _runningIo;
    private int _runningCpu;
    private bool _heavyExclusiveRunning;
    private int _peakRunning;
    private int _priorityBurst;
    private bool _stopping;
    private bool _disposed;
    private bool _signalDisposed;

    public BackgroundWorkSupervisor(
        TimeProvider timeProvider,
        BackgroundWorkSupervisorOptions? options = null,
        IRetryJitter? jitter = null)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? BackgroundWorkSupervisorOptions.Default;
        _executor = new(timeProvider, jitter);
        _dispatcher = Task.Factory.StartNew(
                DispatchLoopAsync,
                _lifetime.Token,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();
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

    public BackgroundWorkSupervisorSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return SnapshotUnsafe();
            }
        }
    }

    public BackgroundWorkAdmission Submit(
        BackgroundWorkRequest request,
        Func<OperationAttemptContext, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        WorkItem item;
        bool accepted;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_items.ContainsKey(request.Execution.OperationId))
            {
                throw new ArgumentException("An operation id may be admitted only once.", nameof(request));
            }

            if (_stopping || !HasAdmissionUnsafe(request.Priority))
            {
                item = CreateItem(request, operation, cancellationToken, BackgroundWorkState.Rejected);
                _items.Add(request.Execution.OperationId, item);
                CompleteUnsafe(item, BackgroundWorkState.Rejected, CapacityFault(request.Execution.OperationId));
                _rejected = checked(_rejected + 1);
                TrimHistoryUnsafe();
                accepted = false;
            }
            else
            {
                item = CreateItem(request, operation, cancellationToken, BackgroundWorkState.Pending);
                _items.Add(request.Execution.OperationId, item);
                _pending.Add(item);
                _admitted = checked(_admitted + 1);
                accepted = true;
            }
        }

        if (accepted)
        {
            RegisterPendingCancellation(item);
        }

        PublishChanged();
        if (accepted)
        {
            Wake();
        }

        return new(accepted, item.Handle, item.LastFault);
    }

    public BackgroundWorkAdmission Retry(
        OperationId operationId,
        OperationId retryOperationId,
        CancellationToken cancellationToken = default)
    {
        WorkItem original;
        lock (_gate)
        {
            if (!_items.TryGetValue(operationId, out original!) || !IsTerminal(original.State))
            {
                throw new InvalidOperationException("Only a known terminal operation can be retried.");
            }

            // OnFailure and Always are restarted by the supervisor itself. A caller retry beside
            // that loop would run two copies of work the policy said should run as one.
            if (original.Request.Execution.Policy.RestartMode != OperationRestartMode.Manual)
            {
                throw new InvalidOperationException("Only a manually restartable operation can be retried by a caller.");
            }

            if (original.Request.Execution.Policy.IdempotencyRequirement == IdempotencyRequirement.SingleAttempt)
            {
                throw new InvalidOperationException("A non-idempotent operation cannot be retried.");
            }
        }

        var execution = new OperationExecutionRequest(
            original.Request.Execution.FeatureId,
            retryOperationId,
            original.Request.Execution.CorrelationId,
            original.Request.Execution.DependencyId,
            original.Request.Execution.Policy,
            original.Request.Execution.IdempotencyKey);
        return Submit(
            new(execution, original.Request.ScopeId, original.Request.Priority),
            original.Operation,
            cancellationToken);
    }

    public async Task<SupervisorStopResult> StopAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        List<CancellationTokenRegistration> released = [];
        Task[] runs;
        var beganStopping = false;
        lock (_gate)
        {
            if (!_stopping)
            {
                _stopping = true;
                beganStopping = true;
                foreach (var pending in _pending)
                {
                    released.Add(pending.CancellationRegistration);
                    pending.CancellationRegistration = default;
                    CompleteUnsafe(pending, BackgroundWorkState.Cancelled, pending.LastFault);
                }

                _pending.Clear();
            }

            runs = [.. _items.Values
                .Where(item => item.RunCompletion is { Task.IsCompleted: false })
                .Select(item => item.RunCompletion!.Task)];
        }

        foreach (var registration in released)
        {
            registration.Unregister();
        }

        Task cancelling;
        if (beganStopping)
        {
            cancelling = ObserveAsync(_lifetime.CancelAsync());
            lock (_gate)
            {
                _lifetimeCancellation = cancelling;
            }
        }
        else
        {
            lock (_gate)
            {
                cancelling = _lifetimeCancellation;
            }
        }

        // The deadline timer exists before anything is awaited, so a stop is bounded from the
        // moment it is requested rather than from whenever cancellation callbacks finished.
        var waiting = Task.WhenAll(runs.Append(_dispatcher).Append(cancelling))
            .WaitAsync(timeout, _timeProvider, cancellationToken);
        if (beganStopping)
        {
            PublishChanged();
            Wake();
        }

        try
        {
            await waiting.ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ReportStopTimeout();
        }

        int unfinished;
        lock (_gate)
        {
            unfinished = CountUnfinishedUnsafe();
        }

        return unfinished == 0 ? new(true, 0) : ReportStopTimeout();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        var stopped = await StopAsync(_options.DefaultStopTimeout).ConfigureAwait(false);
        List<CancellationTokenRegistration> registrations = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var item in _items.Values)
            {
                registrations.Add(item.CancellationRegistration);
                item.CancellationRegistration = default;
            }

            _signalDisposed = stopped.CompletedWithinDeadline;
        }

        foreach (var registration in registrations)
        {
            registration.Unregister();
        }

        // An operation still running past the deadline will release its slot and signal the
        // dispatcher when its dependency finally returns. Leave the already-cancelled
        // synchronization objects alive for that late completion instead of turning it into an
        // ObjectDisposedException.
        if (stopped.CompletedWithinDeadline)
        {
            _signal.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    WorkItem? item;
                    CancellationTokenRegistration registration;
                    lock (_gate)
                    {
                        item = ChooseNextUnsafe();
                        if (item is null)
                        {
                            break;
                        }

                        _pending.Remove(item);
                        registration = item.CancellationRegistration;
                        item.CancellationRegistration = default;
                        item.State = BackgroundWorkState.Running;
                        item.StartedUtc = _timeProvider.GetUtcNow();
                        item.RunCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        AcquireResourcesUnsafe(item.Request.Execution.Policy.WorkloadClass);
                    }

                    registration.Unregister();
                    item.RunTask = ExecuteItemAsync(item);
                    PublishChanged();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task ExecuteItemAsync(WorkItem item)
    {
        var run = item.RunCompletion!;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                item.CallerCancellation);
            OperationExecutionResult<bool> result;
            try
            {
                result = await _executor.ExecuteAsync(
                        item.Request.Execution,
                        async (context, token) =>
                        {
                            await item.Operation(context, token).ConfigureAwait(false);
                            return true;
                        },
                        linked.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                result = OperationExecutionResult<bool>.Failure(
                    RuntimeFault.FromException(
                        exception,
                        _timeProvider,
                        new($"operation:{item.Request.Execution.OperationId}")),
                    0,
                    item.StartedUtc ?? _timeProvider.GetUtcNow(),
                    _timeProvider.GetUtcNow());
            }

            if (result.UnfinishedAttempt is { } unfinished)
            {
                lock (_gate)
                {
                    item.LastFault = result.Fault;
                }

                PublishChanged();
                try
                {
                    await unfinished.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The sanitized result already records the failure. This await exists only
                    // to retain the scheduler's exact resource ownership until user work ends.
                }
            }

            var restart = false;
            var restartDelay = TimeSpan.Zero;
            lock (_gate)
            {
                ReleaseResourcesUnsafe(item.Request.Execution.Policy.WorkloadClass);
                item.Attempts = checked(item.Attempts + result.Attempts);
                item.ConsecutiveFailures = result.Succeeded ? 0 : checked(item.ConsecutiveFailures + 1);
                var outcome = OutcomeOf(result);
                restart = !_stopping
                    && !item.CallerCancellation.IsCancellationRequested
                    && ShouldRestart(item.Request.Execution.Policy.RestartMode, outcome);
                if (restart)
                {
                    item.LastFault = result.Fault;
                    item.State = BackgroundWorkState.Restarting;
                    item.Restarts = checked(item.Restarts + 1);
                    item.StartedUtc = null;
                    _restarting++;
                    restartDelay = RestartDelayUnsafe(item, result.Succeeded);
                }
                else
                {
                    CompleteUnsafe(item, outcome, result.Fault);
                }
            }

            PublishChanged();
            if (!restart)
            {
                return;
            }

            try
            {
                await Task.Delay(restartDelay, _timeProvider, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }

            var requeued = false;
            lock (_gate)
            {
                _restarting--;
                if (!_stopping && !item.CallerCancellation.IsCancellationRequested)
                {
                    item.Sequence = checked(++_nextSequence);
                    item.State = BackgroundWorkState.Pending;
                    _pending.Add(item);
                    requeued = true;
                }
                else
                {
                    CompleteUnsafe(item, BackgroundWorkState.Cancelled, item.LastFault);
                }
            }

            if (requeued)
            {
                RegisterPendingCancellation(item);
            }

            PublishChanged();
        }
        finally
        {
            run.TrySetResult();
            lock (_gate)
            {
                TrimHistoryUnsafe();
            }

            Wake();
        }
    }

    /// <summary>Chooses the next item that may start, or null.</summary>
    /// <remarks>
    /// Fairness is bounded in two ways. A higher priority may overtake the oldest eligible item
    /// at most <see cref="BackgroundWorkSupervisorOptions.MaxPriorityBurst"/> times in a row, and
    /// a pending heavy-exclusive item stops new admissions until running work drains, because a
    /// continuous stream of small work would otherwise always hold one slot and keep the
    /// exclusive item ineligible for ever.
    /// </remarks>
    private WorkItem? ChooseNextUnsafe()
    {
        if (_stopping || _running >= _options.MaxConcurrent || _heavyExclusiveRunning)
        {
            return null;
        }

        if (_running > 0 && _pending.Any(item =>
                item.Request.Execution.Policy.WorkloadClass == WorkloadClass.HeavyExclusive))
        {
            return null;
        }

        var eligible = _pending.Where(item => CanRunUnsafe(item.Request.Execution.Policy.WorkloadClass)).ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        var highest = eligible
            .OrderByDescending(item => item.Request.Priority)
            .ThenBy(item => item.Sequence)
            .First();
        var oldest = eligible.OrderBy(item => item.Sequence).First();

        if (highest.Request.Priority > oldest.Request.Priority
            && _priorityBurst < _options.MaxPriorityBurst)
        {
            _priorityBurst++;
            return highest;
        }

        _priorityBurst = 0;
        return oldest;
    }

    private bool HasAdmissionUnsafe(WorkPriority priority)
    {
        // An operation waiting out a restart delay still owns its admission; counting only
        // pending and running work let restart loops grow past the capacity bound.
        var admittedNow = _pending.Count + _running + _restarting;
        var limit = priority >= WorkPriority.Interactive
            ? _options.Capacity
            : _options.Capacity - _options.ReservedInteractiveAdmission;
        return admittedNow < limit;
    }

    private bool CanRunUnsafe(WorkloadClass workloadClass)
    {
        if (workloadClass == WorkloadClass.HeavyExclusive)
        {
            return _running == 0;
        }

        return workloadClass switch
        {
            WorkloadClass.Light => _runningLight < _options.LightLimit,
            WorkloadClass.IO => _runningIo < _options.IoLimit,
            WorkloadClass.CPU => _runningCpu < _options.CpuLimit,
            _ => false,
        };
    }

    private void AcquireResourcesUnsafe(WorkloadClass workloadClass)
    {
        _running++;
        _peakRunning = Math.Max(_peakRunning, _running);
        switch (workloadClass)
        {
            case WorkloadClass.Light:
                _runningLight++;
                break;
            case WorkloadClass.IO:
                _runningIo++;
                break;
            case WorkloadClass.CPU:
                _runningCpu++;
                break;
            case WorkloadClass.HeavyExclusive:
                _heavyExclusiveRunning = true;
                break;
        }
    }

    private void ReleaseResourcesUnsafe(WorkloadClass workloadClass)
    {
        _running--;
        switch (workloadClass)
        {
            case WorkloadClass.Light:
                _runningLight--;
                break;
            case WorkloadClass.IO:
                _runningIo--;
                break;
            case WorkloadClass.CPU:
                _runningCpu--;
                break;
            case WorkloadClass.HeavyExclusive:
                _heavyExclusiveRunning = false;
                break;
        }
    }

    private void RegisterPendingCancellation(WorkItem item)
    {
        if (!item.CallerCancellation.CanBeCanceled)
        {
            return;
        }

        // Registered outside the scheduler lock: a token that is already cancelled runs the
        // callback inline, and the callback takes that lock.
        var registration = item.CallerCancellation.Register(
            static state =>
            {
                var cancellation = (PendingCancellation)state!;
                cancellation.Owner.CancelPending(cancellation.OperationId);
            },
            new PendingCancellation(this, item.Request.Execution.OperationId));
        var kept = false;
        lock (_gate)
        {
            if (item.State == BackgroundWorkState.Pending && !_disposed)
            {
                item.CancellationRegistration.Unregister();
                item.CancellationRegistration = registration;
                kept = true;
            }
        }

        if (!kept)
        {
            registration.Unregister();
        }
    }

    private void CancelPending(OperationId operationId)
    {
        var changed = false;
        lock (_gate)
        {
            if (_items.TryGetValue(operationId, out var item) && item.State == BackgroundWorkState.Pending)
            {
                _pending.Remove(item);
                // This runs inside the registration's own callback; forgetting it is enough.
                item.CancellationRegistration = default;
                CompleteUnsafe(item, BackgroundWorkState.Cancelled, item.LastFault);
                TrimHistoryUnsafe();
                changed = true;
            }
        }

        if (changed)
        {
            PublishChanged();
            Wake();
        }
    }

    private void CompleteUnsafe(WorkItem item, BackgroundWorkState state, RuntimeFault? fault)
    {
        item.State = state;
        item.LastFault = fault;
        item.CompletedUtc = _timeProvider.GetUtcNow();
        item.Completion.TrySetResult(new(
            item.Request.Execution.OperationId,
            state,
            fault,
            item.Attempts,
            item.CompletedUtc.Value));
        if (state != BackgroundWorkState.Rejected)
        {
            _completed = checked(_completed + 1);
        }
    }

    private int CountUnfinishedUnsafe() => _items.Values.Count(item =>
        !IsTerminal(item.State) || item.RunCompletion is { Task.IsCompleted: false });

    private SupervisorStopResult ReportStopTimeout()
    {
        var unfinished = 0;
        lock (_gate)
        {
            foreach (var item in _items.Values.Where(item =>
                         !IsTerminal(item.State) || item.RunCompletion is { Task.IsCompleted: false }))
            {
                unfinished++;
                item.LastFault = new(
                    RuntimeFailureKind.Timeout,
                    new("supervisor-stop-timeout"),
                    RuntimeRecoveryAction.RetryManually,
                    new($"operation:{item.Request.Execution.OperationId}"),
                    _timeProvider.GetUtcNow());
            }
        }

        PublishChanged();
        return new(unfinished == 0, unfinished);
    }

    private TimeSpan RestartDelayUnsafe(WorkItem item, bool succeeded)
    {
        var policy = item.Request.Execution.Policy;
        var exponent = succeeded ? 0 : Math.Max(0, item.ConsecutiveFailures - 1);
        var ticks = Math.Min(
            policy.MaxRetryDelay.Ticks,
            policy.InitialRetryDelay.Ticks * Math.Pow(policy.RetryBackoffFactor, exponent));
        var delay = TimeSpan.FromTicks((long)Math.Round(ticks, MidpointRounding.AwayFromZero));
        return delay < _options.MinimumRestartDelay ? _options.MinimumRestartDelay : delay;
    }

    private void Wake()
    {
        lock (_gate)
        {
            if (!_signalDisposed)
            {
                _signal.Release();
            }
        }
    }

    private WorkItem CreateItem(
        BackgroundWorkRequest request,
        Func<OperationAttemptContext, CancellationToken, Task> operation,
        CancellationToken callerCancellation,
        BackgroundWorkState state) =>
        new(
            request,
            operation,
            callerCancellation,
            checked(++_nextSequence),
            state,
            _timeProvider.GetUtcNow());

    private RuntimeFault CapacityFault(OperationId operationId) => new(
        RuntimeFailureKind.Transient,
        new("supervisor-admission-full"),
        RuntimeRecoveryAction.RetryManually,
        new($"operation:{operationId}"),
        _timeProvider.GetUtcNow());

    private void TrimHistoryUnsafe()
    {
        if (_items.Count <= _options.TerminalHistoryLimit)
        {
            return;
        }

        foreach (var item in _items.Values
                     .Where(item => IsTerminal(item.State) && item.RunCompletion is null or { Task.IsCompleted: true })
                     .OrderBy(item => item.CompletedUtc)
                     .Take(_items.Count - _options.TerminalHistoryLimit)
                     .ToArray())
        {
            _items.Remove(item.Request.Execution.OperationId);
            item.CancellationRegistration.Unregister();
            item.CancellationRegistration = default;
        }
    }

    private BackgroundWorkSupervisorSnapshot SnapshotUnsafe() => new(
        _stopping,
        new(
            _options.Capacity,
            _options.ReservedInteractiveAdmission,
            _pending.Count,
            _running,
            _runningLight,
            _runningIo,
            _runningCpu,
            _heavyExclusiveRunning,
            _peakRunning,
            _admitted,
            _rejected,
            _completed,
            _subscriberFaults,
            _restarting),
        [.. _items.Values.OrderBy(item => item.Sequence).Select(item => item.Snapshot())]);

    private void PublishChanged()
    {
        EventHandler[] handlers;
        lock (_gate)
        {
            handlers = _changed?.GetInvocationList().Cast<EventHandler>().ToArray() ?? [];
        }

        InvokeHandlers(handlers);
    }

    private void InvokeHandlers(EventHandler[] handlers)
    {
        foreach (var handler in handlers)
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    _subscriberFaults = checked(_subscriberFaults + 1);
                }
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
            // A throwing cancellation callback belongs to the operation that registered it; the
            // stop still completes and the operation's own result records the failure.
        }
    }

    private static BackgroundWorkState OutcomeOf(OperationExecutionResult<bool> result) =>
        result.Succeeded
            ? BackgroundWorkState.Succeeded
            : result.Fault?.Kind switch
            {
                RuntimeFailureKind.Cancelled => BackgroundWorkState.Cancelled,
                RuntimeFailureKind.Timeout => BackgroundWorkState.TimedOut,
                _ => BackgroundWorkState.Faulted,
            };

    private static bool IsTerminal(BackgroundWorkState state) => state is
        BackgroundWorkState.Succeeded
        or BackgroundWorkState.Faulted
        or BackgroundWorkState.Cancelled
        or BackgroundWorkState.TimedOut
        or BackgroundWorkState.Rejected;

    /// <summary>Whether a finished run is queued again rather than completed.</summary>
    /// <remarks>
    /// OnFailure restarts a run that faulted or timed out; Always also restarts one that
    /// succeeded, for long-lived loops. Neither restarts a cancelled run, and neither restarts
    /// once the caller cancelled or the supervisor began stopping.
    /// </remarks>
    private static bool ShouldRestart(OperationRestartMode restartMode, BackgroundWorkState outcome) => restartMode switch
    {
        OperationRestartMode.OnFailure => outcome is BackgroundWorkState.Faulted or BackgroundWorkState.TimedOut,
        OperationRestartMode.Always => outcome is BackgroundWorkState.Succeeded
            or BackgroundWorkState.Faulted
            or BackgroundWorkState.TimedOut,
        _ => false,
    };

    private sealed class WorkItem
    {
        public WorkItem(
            BackgroundWorkRequest request,
            Func<OperationAttemptContext, CancellationToken, Task> operation,
            CancellationToken callerCancellation,
            long sequence,
            BackgroundWorkState state,
            DateTimeOffset submittedUtc)
        {
            Request = request;
            Operation = operation;
            CallerCancellation = callerCancellation;
            Sequence = sequence;
            State = state;
            SubmittedUtc = submittedUtc;
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Handle = new(request.Execution.OperationId, Completion.Task);
        }

        public BackgroundWorkRequest Request { get; }
        public Func<OperationAttemptContext, CancellationToken, Task> Operation { get; }
        public CancellationToken CallerCancellation { get; }
        public long Sequence { get; set; }
        public DateTimeOffset SubmittedUtc { get; }
        public TaskCompletionSource<BackgroundWorkResult> Completion { get; }
        public BackgroundWorkHandle Handle { get; }
        public BackgroundWorkState State { get; set; }
        public int Attempts { get; set; }
        public int Restarts { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTimeOffset? StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public RuntimeFault? LastFault { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }

        /// <summary>The current or most recent run, completed only once its user work returned.</summary>
        /// <remarks>
        /// Created under the scheduler lock before the run starts, so a stop taken at any moment
        /// sees every run that exists; the run completes it in a finally block.
        /// </remarks>
        public TaskCompletionSource? RunCompletion { get; set; }

        /// <summary>The run itself, retained for its lifetime. It never faults; stop observes
        /// <see cref="RunCompletion"/>.</summary>
        public Task? RunTask { get; set; }

        public BackgroundWorkSnapshot Snapshot() => new(
            Request.Execution.FeatureId,
            Request.Execution.OperationId,
            Request.Execution.CorrelationId,
            Request.ScopeId,
            Request.Execution.Policy.WorkloadClass,
            Request.Priority,
            State,
            Attempts,
            Restarts,
            SubmittedUtc,
            StartedUtc,
            CompletedUtc,
            LastFault);
    }

    private sealed record PendingCancellation(BackgroundWorkSupervisor Owner, OperationId OperationId);
}
