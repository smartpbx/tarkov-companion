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
    private readonly HashSet<CancellationRetirement> _retirements = [];
    private long _nextGeneration;
    private bool _disposed;

    public LatestOperationLease Begin(OperationScopeId scope, CancellationToken cancellationToken = default)
    {
        if (!scope.IsDefined)
        {
            throw new ArgumentException("An operation scope is required.", nameof(scope));
        }

        Entry? previous;
        LatestOperationLease lease;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _current.Remove(scope, out previous);
            var generation = new OperationGeneration(checked(++_nextGeneration));
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _current.Add(scope, new(generation, cancellation));
            lease = new(scope, generation, cancellation.Token);
        }

        // Cancellation is advisory and must never hold up admission. A callback belongs to an
        // arbitrary dependency: it can re-enter this gate, throw, or never return. Retire the
        // source asynchronously, observe its completion, and keep it alive until every callback
        // has settled.
        if (previous is not null)
        {
            Retire(previous.Cancellation);
        }

        return lease;
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
        Entry? removed;
        lock (_gate)
        {
            _current.Remove(scope, out removed);
        }

        if (removed is not null)
        {
            Retire(removed.Cancellation);
        }
    }

    public void Dispose()
    {
        Entry[] entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = [.. _current.Values];
            _current.Clear();
        }

        foreach (var entry in entries)
        {
            Retire(entry.Cancellation);
        }
    }

    private void Retire(CancellationTokenSource cancellation)
    {
        var retirement = new CancellationRetirement(cancellation);
        lock (_gate)
        {
            _retirements.Add(retirement);
        }

        retirement.Completion.GetAwaiter().OnCompleted(() => ForgetRetirement(retirement));
    }

    private void ForgetRetirement(CancellationRetirement retirement)
    {
        // CancellationRetirement converts every callback outcome into successful completion, so
        // the continuation only has to release the retained owner.
        lock (_gate)
        {
            _retirements.Remove(retirement);
        }
    }

    private sealed class CancellationRetirement
    {
        public CancellationRetirement(CancellationTokenSource cancellation) =>
            Completion = CancelAndDisposeAsync(cancellation);

        public Task Completion { get; }

        private static async Task CancelAndDisposeAsync(CancellationTokenSource cancellation)
        {
            try
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancellation callbacks are dependency code. A fault cannot undo invalidation.
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    private sealed record Entry(OperationGeneration Generation, CancellationTokenSource Cancellation);
}

public readonly record struct LatestOperationLease(
    OperationScopeId Scope,
    OperationGeneration Generation,
    CancellationToken CancellationToken);
