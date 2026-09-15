namespace TarkovCompanion.App.Services.V2.Shell;

internal enum V2ShellPersistenceOperationKind
{
    Save = 1,
    Reset,
}

internal sealed record V2ShellPersistenceResult(
    V2ShellPersistenceOperationKind Kind,
    bool Succeeded,
    Exception? Error);

/// <summary>Serializes preview saves, resets, and the final close-time save.</summary>
/// <remarks>
/// Reset used to suspend the save loop across UI-context awaits. If the window closed while the
/// dispatcher was stopping, the reset continuation could never lift that suspension and shutdown
/// waited forever for a save loop that was forbidden to start. This queue has no UI dependency:
/// operations are ordered once, consecutive saves coalesce, and close can wait for one finite
/// worker even when a reset is already in flight.
/// </remarks>
internal sealed class V2ShellPersistenceQueue
{
    private readonly object _sync = new();
    private readonly Func<V2ShellPreviewState, CancellationToken, Task> _save;
    private readonly Func<CancellationToken, Task> _reset;
    private readonly LinkedList<Operation> _operations = [];
    private Task _worker = Task.CompletedTask;
    private Task? _disposeTask;
    private Exception? _failure;
    private bool _workerRunning;
    private bool _disposed;

    public V2ShellPersistenceQueue(
        Func<V2ShellPreviewState, CancellationToken, Task> save,
        Func<CancellationToken, Task> reset)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _reset = reset ?? throw new ArgumentNullException(nameof(reset));
    }

    /// <summary>
    /// Reports the durable outcome after an operation leaves the serialized queue. A subscriber
    /// cannot break the queue; the UI uses this to keep a failed save visible and retryable.
    /// </summary>
    public event Action<V2ShellPersistenceResult>? Completed;

    public void QueueSave(V2ShellPreviewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            AddOrReplaceSaveLocked(state);
            StartWorkerLocked();
        }
    }

    /// <summary>Queues a reset after earlier saves and before any later state.</summary>
    public Task ResetAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (_operations.Last?.Value is ResetOperation pending)
            {
                return pending.Completion.Task;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _operations.AddLast(new ResetOperation(completion));
            StartWorkerLocked();
            return completion.Task;
        }
    }

    /// <summary>
    /// Stops accepting work and waits for the queue. A close racing a reset suppresses the stale
    /// pre-reset snapshot so the completed reset cannot immediately be undone.
    /// </summary>
    public ValueTask DisposeAsync(V2ShellPreviewState finalState, bool suppressFinalSave)
    {
        ArgumentNullException.ThrowIfNull(finalState);
        lock (_sync)
        {
            if (_disposeTask is not null)
            {
                return new(_disposeTask);
            }

            _disposed = true;
            if (suppressFinalSave)
            {
                for (var node = _operations.First; node is not null;)
                {
                    var next = node.Next;
                    if (node.Value is SaveOperation)
                    {
                        _operations.Remove(node);
                    }

                    node = next;
                }
            }
            else
            {
                AddOrReplaceSaveLocked(finalState);
            }

            StartWorkerLocked();
            _disposeTask = AwaitWorkerAsync(_worker);
            return new(_disposeTask);
        }
    }

    private void AddOrReplaceSaveLocked(V2ShellPreviewState state)
    {
        if (_operations.Last is { Value: SaveOperation } last)
        {
            last.Value = new SaveOperation(state);
        }
        else
        {
            _operations.AddLast(new SaveOperation(state));
        }
    }

    private void StartWorkerLocked()
    {
        if (_workerRunning || _operations.Count == 0)
        {
            return;
        }

        _workerRunning = true;
        _worker = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            Operation operation;
            lock (_sync)
            {
                if (_operations.First is not { } first)
                {
                    _workerRunning = false;
                    return;
                }

                operation = first.Value;
                _operations.RemoveFirst();
            }

            try
            {
                switch (operation)
                {
                    case SaveOperation save:
                        await _save(save.State, CancellationToken.None).ConfigureAwait(false);
                        break;
                    case ResetOperation reset:
                        await _reset(CancellationToken.None).ConfigureAwait(false);
                        reset.Completion.TrySetResult();
                        break;
                }

                lock (_sync)
                {
                    _failure = null;
                }

                Publish(new(OperationKind(operation), true, null));
            }
            catch (Exception exception)
            {
                if (operation is ResetOperation reset)
                {
                    reset.Completion.TrySetException(exception);
                }

                lock (_sync)
                {
                    _failure = exception;
                }

                Publish(new(OperationKind(operation), false, exception));
            }
        }
    }

    private static V2ShellPersistenceOperationKind OperationKind(Operation operation) => operation switch
    {
        SaveOperation => V2ShellPersistenceOperationKind.Save,
        ResetOperation => V2ShellPersistenceOperationKind.Reset,
        _ => throw new InvalidOperationException("Unknown preview persistence operation."),
    };

    private void Publish(V2ShellPersistenceResult result)
    {
        var completed = Completed;
        if (completed is null)
        {
            return;
        }

        foreach (Action<V2ShellPersistenceResult> subscriber in completed.GetInvocationList())
        {
            try
            {
                subscriber(result);
            }
            catch
            {
                // Persistence completed independently of observers. A presentation callback
                // cannot strand later saves or turn a successful durable write into a failure.
            }
        }
    }

    private async Task AwaitWorkerAsync(Task worker)
    {
        await worker.ConfigureAwait(false);
        lock (_sync)
        {
            if (_failure is { } failure)
            {
                throw new InvalidOperationException("Preview state persistence failed.", failure);
            }
        }
    }

    private abstract record Operation;

    private sealed record SaveOperation(V2ShellPreviewState State) : Operation;

    private sealed record ResetOperation(TaskCompletionSource Completion) : Operation;
}
