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
    StopTimedOut,
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
        TimeSpan defaultStopTimeout)
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

        Capacity = capacity;
        ReservedInteractiveAdmission = reservedInteractiveAdmission;
        MaxConcurrent = maxConcurrent;
        LightLimit = lightLimit;
        IoLimit = ioLimit;
        CpuLimit = cpuLimit;
        MaxPriorityBurst = maxPriorityBurst;
        TerminalHistoryLimit = terminalHistoryLimit;
        DefaultStopTimeout = defaultStopTimeout;
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
    long SubscriberFaults);

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
    private long _nextSequence;
    private long _admitted;
    private long _rejected;
    private long _completed;
    private long _subscriberFaults;
    private int _running;
    private int _runningLight;
    private int _runningIo;
    private int _runningCpu;
    private bool _heavyExclusiveRunning;
    private int _peakRunning;
    private int _priorityBurst;
    private bool _stopping;
    private bool _disposed;

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
                item.LastFault = CapacityFault(request.Execution.OperationId);
                item.CompletedUtc = _timeProvider.GetUtcNow();
                item.Completion.TrySetResult(new(
                    request.Execution.OperationId,
                    BackgroundWorkState.Rejected,
                    item.LastFault,
                    0,
                    item.CompletedUtc.Value));
                _items.Add(request.Execution.OperationId, item);
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
                item.CancellationRegistration = cancellationToken.Register(
                    static state =>
                    {
                        var cancellation = (PendingCancellation)state!;
                        cancellation.Owner.CancelPending(cancellation.OperationId);
                    },
                    new PendingCancellation(this, request.Execution.OperationId));
                accepted = true;
            }
        }

        PublishChanged();
        if (accepted)
        {
            _signal.Release();
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

            if (original.Request.Execution.Policy.RestartMode == OperationRestartMode.Never)
            {
                throw new InvalidOperationException("The operation policy forbids restart.");
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

        Task[] completions;
        lock (_gate)
        {
            if (!_stopping)
            {
                _stopping = true;
                foreach (var pending in _pending.ToArray())
                {
                    CompletePendingUnsafe(pending, BackgroundWorkState.Cancelled);
                }

                _pending.Clear();
            }

            completions = [.. _items.Values
                .Where(item => !IsTerminal(item.State) || item.ExecutionTask is { IsCompleted: false })
                .Select(item => item.ExecutionTask ?? item.Completion.Task)];
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _signal.Release();
        PublishChanged();
        var combined = Task.WhenAll(completions.Append(_dispatcher));
        try
        {
            await combined.WaitAsync(timeout, _timeProvider, cancellationToken).ConfigureAwait(false);
            return new(true, 0);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (_items.Values.All(item => IsTerminal(item.State)))
                {
                    return new(true, 0);
                }
            }

            return MarkStopTimedOut();
        }
        catch (TimeoutException)
        {
            return MarkStopTimedOut();
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new(true, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        var stopped = await StopAsync(_options.DefaultStopTimeout).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var item in _items.Values)
            {
                item.CancellationRegistration.Dispose();
            }
        }

        // A timed-out operation may still be returning from dependency code that ignored
        // cancellation. Leave the already-cancelled synchronization objects alive for that
        // bounded late completion instead of turning it into an ObjectDisposedException.
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
                    lock (_gate)
                    {
                        item = ChooseNextUnsafe();
                        if (item is null)
                        {
                            break;
                        }

                        _pending.Remove(item);
                        item.CancellationRegistration.Dispose();
                        item.State = BackgroundWorkState.Running;
                        item.StartedUtc = _timeProvider.GetUtcNow();
                        AcquireResourcesUnsafe(item.Request.Execution.Policy.WorkloadClass);
                    }

                    item.ExecutionTask = ExecuteItemAsync(item);
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
        OperationExecutionResult<bool> result;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            item.CallerCancellation);
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

        lock (_gate)
        {
            ReleaseResourcesUnsafe(item.Request.Execution.Policy.WorkloadClass);
            if (item.State != BackgroundWorkState.StopTimedOut)
            {
                item.State = result.Succeeded
                    ? BackgroundWorkState.Succeeded
                    : result.Fault?.Kind switch
                    {
                        RuntimeFailureKind.Cancelled => BackgroundWorkState.Cancelled,
                        RuntimeFailureKind.Timeout => BackgroundWorkState.TimedOut,
                        _ => BackgroundWorkState.Faulted,
                    };
                item.Attempts = result.Attempts;
                item.LastFault = result.Fault;
                item.CompletedUtc = result.CompletedUtc;
                item.Completion.TrySetResult(new(
                    item.Request.Execution.OperationId,
                    item.State,
                    result.Fault,
                    result.Attempts,
                    result.CompletedUtc));
                _completed = checked(_completed + 1);
            }

            TrimHistoryUnsafe();
        }

        PublishChanged();
        _signal.Release();
    }

    private WorkItem? ChooseNextUnsafe()
    {
        if (_running >= _options.MaxConcurrent || _heavyExclusiveRunning)
        {
            return null;
        }

        var eligible = _pending.Where(item => CanRunUnsafe(item.Request.Execution.Policy.WorkloadClass)).ToArray();
        if (eligible.Length == 0)
        {
            return null;
        }

        var high = eligible
            .Where(item => item.Request.Priority >= WorkPriority.Interactive)
            .OrderByDescending(item => item.Request.Priority)
            .ThenBy(item => item.Sequence)
            .FirstOrDefault();
        var lower = eligible
            .Where(item => item.Request.Priority < WorkPriority.Interactive)
            .OrderByDescending(item => item.Request.Priority)
            .ThenBy(item => item.Sequence)
            .FirstOrDefault();

        if (high is not null && (lower is null || _priorityBurst < _options.MaxPriorityBurst))
        {
            _priorityBurst++;
            return high;
        }

        _priorityBurst = 0;
        return lower ?? high;
    }

    private bool HasAdmissionUnsafe(WorkPriority priority)
    {
        var admittedNow = _pending.Count + _running;
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

    private void CancelPending(OperationId operationId)
    {
        var changed = false;
        lock (_gate)
        {
            if (_items.TryGetValue(operationId, out var item) && item.State == BackgroundWorkState.Pending)
            {
                _pending.Remove(item);
                CompletePendingUnsafe(item, BackgroundWorkState.Cancelled);
                changed = true;
            }
        }

        if (changed)
        {
            PublishChanged();
            _signal.Release();
        }
    }

    private void CompletePendingUnsafe(WorkItem item, BackgroundWorkState state)
    {
        item.CancellationRegistration.Dispose();
        item.State = state;
        item.CompletedUtc = _timeProvider.GetUtcNow();
        item.Completion.TrySetResult(new(item.Request.Execution.OperationId, state, null, 0, item.CompletedUtc.Value));
        _completed = checked(_completed + 1);
    }

    private SupervisorStopResult MarkStopTimedOut()
    {
        var unfinished = 0;
        lock (_gate)
        {
            foreach (var item in _items.Values.Where(item =>
                         !IsTerminal(item.State) || item.ExecutionTask is { IsCompleted: false }))
            {
                unfinished++;
                if (item.State != BackgroundWorkState.StopTimedOut)
                {
                    item.State = BackgroundWorkState.StopTimedOut;
                    item.CompletedUtc = _timeProvider.GetUtcNow();
                    item.LastFault = new(
                        RuntimeFailureKind.Timeout,
                        new("supervisor-stop-timeout"),
                        RuntimeRecoveryAction.RetryManually,
                        new($"operation:{item.Request.Execution.OperationId}"),
                        item.CompletedUtc.Value);
                    item.Completion.TrySetResult(new(
                        item.Request.Execution.OperationId,
                        item.State,
                        item.LastFault,
                        item.Attempts,
                        item.CompletedUtc.Value));
                }
            }
        }

        PublishChanged();
        return new(false, unfinished);
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
                     .Where(item => IsTerminal(item.State))
                     .OrderBy(item => item.CompletedUtc)
                     .Take(_items.Count - _options.TerminalHistoryLimit)
                     .ToArray())
        {
            _items.Remove(item.Request.Execution.OperationId);
            item.CancellationRegistration.Dispose();
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
            _subscriberFaults),
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

    private static bool IsTerminal(BackgroundWorkState state) => state is
        BackgroundWorkState.Succeeded
        or BackgroundWorkState.Faulted
        or BackgroundWorkState.Cancelled
        or BackgroundWorkState.TimedOut
        or BackgroundWorkState.Rejected
        or BackgroundWorkState.StopTimedOut;

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
        public long Sequence { get; }
        public DateTimeOffset SubmittedUtc { get; }
        public TaskCompletionSource<BackgroundWorkResult> Completion { get; }
        public BackgroundWorkHandle Handle { get; }
        public BackgroundWorkState State { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset? StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public RuntimeFault? LastFault { get; set; }
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public Task? ExecutionTask { get; set; }

        public BackgroundWorkSnapshot Snapshot() => new(
            Request.Execution.FeatureId,
            Request.Execution.OperationId,
            Request.Execution.CorrelationId,
            Request.ScopeId,
            Request.Execution.Policy.WorkloadClass,
            Request.Priority,
            State,
            Attempts,
            SubmittedUtc,
            StartedUtc,
            CompletedUtc,
            LastFault);
    }

    private sealed record PendingCancellation(BackgroundWorkSupervisor Owner, OperationId OperationId);
}
