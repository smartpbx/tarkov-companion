namespace TarkovCompanion.Application.Services.Execution;

/// <summary>
/// Commits a result only while its scope and process-local generation are still current.
/// </summary>
/// <remarks>
/// Cancellation alone lost a race when an older dependency ignored it and returned after its
/// replacement. The generation check and publication share this lock, so there is no gap in
/// which a newer operation can begin after the check but before the old result is published.
/// </remarks>
public sealed class LatestOperationGate : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<OperationScopeId, Entry> _current = [];
    private long _nextGeneration;
    private bool _disposed;

    public LatestOperationLease Begin(OperationScopeId scope, CancellationToken cancellationToken = default)
    {
        if (!scope.IsDefined)
        {
            throw new ArgumentException("An operation scope is required.", nameof(scope));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_current.Remove(scope, out var previous))
            {
                previous.Cancellation.Cancel();
                previous.Cancellation.Dispose();
            }

            var generation = new OperationGeneration(checked(++_nextGeneration));
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _current.Add(scope, new(generation, cancellation));
            return new(scope, generation, cancellation.Token);
        }
    }

    public bool IsCurrent(LatestOperationLease lease)
    {
        lock (_gate)
        {
            return !_disposed
                && _current.TryGetValue(lease.Scope, out var current)
                && current.Generation == lease.Generation;
        }
    }

    public bool TryCommit(LatestOperationLease lease, Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_gate)
        {
            if (_disposed
                || !_current.TryGetValue(lease.Scope, out var current)
                || current.Generation != lease.Generation)
            {
                return false;
            }

            publish();
            return true;
        }
    }

    public void Invalidate(OperationScopeId scope)
    {
        lock (_gate)
        {
            if (_current.Remove(scope, out var current))
            {
                current.Cancellation.Cancel();
                current.Cancellation.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var entry in _current.Values)
            {
                entry.Cancellation.Cancel();
                entry.Cancellation.Dispose();
            }

            _current.Clear();
        }
    }

    private sealed record Entry(OperationGeneration Generation, CancellationTokenSource Cancellation);
}

public readonly record struct LatestOperationLease(
    OperationScopeId Scope,
    OperationGeneration Generation,
    CancellationToken CancellationToken);
