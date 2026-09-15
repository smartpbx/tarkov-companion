using System.Collections.Immutable;

namespace TarkovCompanion.Application.Services.Execution;

/// <summary>
/// A deterministic fixture store with the same atomic lease-token rules as the durable store.
/// It intentionally makes no process-restart durability claim; issue #270 owns SQLite durability.
/// </summary>
/// <remarks>
/// Capacity bounds every unresolved command. Completed rows have a separate bounded retention
/// window, while a dead letter stays admitted until it is retried or explicitly resolved. That
/// makes a poison head visible and recoverable without letting process-local history grow forever
/// or silently dropping the command that holds its aggregate in order.
/// </remarks>
public sealed class FixtureOutboxStore : IOutboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<OperationId, MutableItem> _items = [];
    private readonly Dictionary<IdempotencyKey, OperationId> _idempotency = [];
    private readonly int _capacity;
    private readonly int _completedRetention;

    public FixtureOutboxStore(int capacity = 1000, int? completedRetention = null)
    {
        if (capacity is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        var retention = completedRetention ?? capacity;
        if (retention is < 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(completedRetention));
        }

        _capacity = capacity;
        _completedRetention = retention;
    }

    public async Task<OutboxEnqueueReceipt> EnqueueAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var receipts = await EnqueueBatchAsync([item], cancellationToken).ConfigureAwait(false);
        return receipts[0];
    }

    public Task<ImmutableArray<OutboxEnqueueReceipt>> EnqueueBatchAsync(
        ImmutableArray<OutboxItem> items,
        CancellationToken cancellationToken)
    {
        if (items.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one outbox item is required.", nameof(items));
        }

        if (items.Any(item => item is null))
        {
            throw new ArgumentException("Outbox items cannot contain null.", nameof(items));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // Everything is validated before anything is stored, so a refusal leaves no part of
            // the batch behind.
            var receipts = new OutboxEnqueueReceipt[items.Length];
            var added = new List<OutboxItem>();
            var batchKeys = new Dictionary<IdempotencyKey, OperationId>();
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                if (_idempotency.TryGetValue(item.IdempotencyKey, out var duplicateOf)
                    || batchKeys.TryGetValue(item.IdempotencyKey, out duplicateOf))
                {
                    receipts[index] = new(false, duplicateOf);
                    continue;
                }

                if (_items.ContainsKey(item.OperationId)
                    || added.Any(candidate => candidate.OperationId == item.OperationId))
                {
                    throw new InvalidOperationException("An outbox operation id may be enqueued only once.");
                }

                if (_items.Values.Any(stored =>
                        stored.Item.AggregateId == item.AggregateId
                        && stored.Item.AggregateSequence == item.AggregateSequence)
                    || added.Any(candidate =>
                        candidate.AggregateId == item.AggregateId
                        && candidate.AggregateSequence == item.AggregateSequence))
                {
                    throw new InvalidOperationException("An aggregate sequence may be enqueued only once.");
                }

                batchKeys.Add(item.IdempotencyKey, item.OperationId);
                added.Add(item);
                receipts[index] = new(true, item.OperationId);
            }

            var unresolvedCount = _items.Values.Count(stored => stored.State != OutboxDeliveryState.Completed);
            if (added.Count > 0 && unresolvedCount + added.Count > _capacity)
            {
                throw new OutboxCapacityException();
            }

            foreach (var accepted in added)
            {
                _items.Add(accepted.OperationId, new(accepted));
                _idempotency.Add(accepted.IdempotencyKey, accepted.OperationId);
            }

            return Task.FromResult<ImmutableArray<OutboxEnqueueReceipt>>([.. receipts]);
        }
    }

    public Task<ImmutableArray<OutboxStoredItem>> LeaseNextAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (maximumCount is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        nowUtc = nowUtc.ToUniversalTime();
        lock (_gate)
        {
            RecoverExpiredUnsafe(nowUtc);
            ExpireOutstandingUnsafe(nowUtc);
            var aggregateHeads = _items.Values
                .Where(item => item.State != OutboxDeliveryState.Completed)
                .GroupBy(item => item.Item.AggregateId)
                .Select(group => group.OrderBy(item => item.Item.AggregateSequence).First())
                .Where(item => item.State is OutboxDeliveryState.Pending or OutboxDeliveryState.Retrying)
                .Where(item => item.NextAttemptUtc <= nowUtc && item.Item.ExpiresUtc > nowUtc)
                .OrderBy(item => item.NextAttemptUtc)
                .ThenBy(item => item.Item.CreatedUtc)
                .ThenBy(item => item.Item.AggregateId.Value, StringComparer.Ordinal)
                .Take(maximumCount)
                .ToArray();
            foreach (var item in aggregateHeads)
            {
                item.State = OutboxDeliveryState.Processing;
                item.AttemptCount = checked(item.AttemptCount + 1);
                item.LeaseToken = OutboxLeaseToken.New();
                item.LeaseExpiresUtc = AddBounded(nowUtc, leaseDuration);
            }

            return Task.FromResult<ImmutableArray<OutboxStoredItem>>([.. aggregateHeads.Select(item => item.Snapshot())]);
        }
    }

    public Task<bool> CompleteAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken) =>
        MutateLeaseAsync(
            operationId,
            leaseToken,
            item =>
            {
                if (completedUtc.ToUniversalTime() < item.Item.CreatedUtc)
                {
                    throw new ArgumentOutOfRangeException(nameof(completedUtc));
                }

                item.State = OutboxDeliveryState.Completed;
                item.CompletedUtc = completedUtc.ToUniversalTime();
                item.LeaseToken = null;
                item.LeaseExpiresUtc = null;
                item.LastFault = null;
                PruneCompletedUnsafe();
            },
            cancellationToken);

    public Task<bool> RenewLeaseAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var renewedAt = nowUtc.ToUniversalTime();
        return MutateLeaseAsync(
            operationId,
            leaseToken,
            item => item.LeaseExpiresUtc = AddBounded(renewedAt, leaseDuration),
            cancellationToken);
    }

    public Task<bool> RetryAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        DateTimeOffset notBeforeUtc,
        RuntimeFault fault,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fault);
        return MutateLeaseAsync(
            operationId,
            leaseToken,
            item =>
            {
                var retryUtc = notBeforeUtc.ToUniversalTime();
                if (retryUtc < item.Item.CreatedUtc || retryUtc >= item.Item.ExpiresUtc)
                {
                    throw new ArgumentOutOfRangeException(nameof(notBeforeUtc));
                }

                item.State = OutboxDeliveryState.Retrying;
                item.NextAttemptUtc = retryUtc;
                item.LeaseToken = null;
                item.LeaseExpiresUtc = null;
                item.LastFault = fault;
            },
            cancellationToken);
    }

    public Task<bool> DeadLetterAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        RuntimeFault fault,
        DateTimeOffset deadLetteredUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fault);
        return MutateLeaseAsync(
            operationId,
            leaseToken,
            item =>
            {
                if (deadLetteredUtc.ToUniversalTime() < item.Item.CreatedUtc)
                {
                    throw new ArgumentOutOfRangeException(nameof(deadLetteredUtc));
                }

                item.State = OutboxDeliveryState.DeadLetter;
                item.CompletedUtc = deadLetteredUtc.ToUniversalTime();
                item.LeaseToken = null;
                item.LeaseExpiresUtc = null;
                item.LastFault = fault;
            },
            cancellationToken);
    }

    public Task<int> RecoverExpiredLeasesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(RecoverExpiredUnsafe(nowUtc.ToUniversalTime()));
        }
    }

    public Task<bool> ManualRetryAsync(
        OperationId operationId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_items.TryGetValue(operationId, out var item)
                || item.State != OutboxDeliveryState.DeadLetter
                || item.Item.ExpiresUtc <= nowUtc.ToUniversalTime())
            {
                return Task.FromResult(false);
            }

            item.State = OutboxDeliveryState.Retrying;
            item.AttemptCount = 0;
            item.NextAttemptUtc = nowUtc.ToUniversalTime();
            item.CompletedUtc = null;
            item.LastFault = null;
            return Task.FromResult(true);
        }
    }

    public Task<bool> ResolveDeadLetterAsync(
        OperationId operationId,
        DateTimeOffset resolvedUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedAt = resolvedUtc.ToUniversalTime();
        lock (_gate)
        {
            if (!_items.TryGetValue(operationId, out var item)
                || item.State != OutboxDeliveryState.DeadLetter)
            {
                return Task.FromResult(false);
            }

            if (resolvedAt < item.Item.CreatedUtc)
            {
                throw new ArgumentOutOfRangeException(nameof(resolvedUtc));
            }

            // Resolution is an operator's recorded decision not to replay this command. It is
            // terminal, so the next aggregate sequence can progress and normal retention bounds
            // the process-local idempotency and history maps.
            item.State = OutboxDeliveryState.Completed;
            item.CompletedUtc = resolvedAt;
            item.LeaseToken = null;
            item.LeaseExpiresUtc = null;
            PruneCompletedUnsafe();
            return Task.FromResult(true);
        }
    }

    public Task<OutboxSnapshot> GetSnapshotAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nowUtc = nowUtc.ToUniversalTime();
        lock (_gate)
        {
            var counts = new OutboxCounts(
                _items.Values.Count(item => item.State == OutboxDeliveryState.Pending),
                _items.Values.Count(item => item.State == OutboxDeliveryState.Processing),
                _items.Values.Count(item => item.State == OutboxDeliveryState.Retrying),
                _items.Values.Count(item => item.State == OutboxDeliveryState.DeadLetter),
                _items.Values.Count(item => item.State == OutboxDeliveryState.Completed));
            var oldest = _items.Values
                .Where(item => IsActive(item.State))
                .Select(item => (DateTimeOffset?)item.Item.CreatedUtc)
                .Min();
            var age = oldest is null
                ? (TimeSpan?)null
                : oldest > nowUtc
                    ? TimeSpan.Zero
                    : nowUtc - oldest.Value;
            return Task.FromResult(new OutboxSnapshot(counts, age)
            {
                DeadLetters = [.. _items.Values
                    .Where(item => item.State == OutboxDeliveryState.DeadLetter)
                    .OrderByDescending(item => item.Item.ExpiresUtc > nowUtc)
                    .ThenBy(item => item.CompletedUtc)
                    .ThenBy(item => item.Item.AggregateId.Value, StringComparer.Ordinal)
                    .ThenBy(item => item.Item.AggregateSequence)
                    .Take(OutboxSnapshot.MaxListedDeadLetters)
                    .Select(item => new OutboxDeadLetterSnapshot(
                        item.Item.OperationId,
                        item.Item.AggregateId,
                        item.Item.AggregateSequence,
                        item.Item.Command,
                        item.AttemptCount,
                        item.LastFault,
                        item.CompletedUtc)
                    {
                        CanRetry = item.Item.ExpiresUtc > nowUtc,
                    })],
            });
        }
    }

    public Task<ImmutableArray<OutboxStoredItem>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<ImmutableArray<OutboxStoredItem>>(
                [.. _items.Values
                    .OrderBy(item => item.Item.AggregateId.Value, StringComparer.Ordinal)
                    .ThenBy(item => item.Item.AggregateSequence)
                    .Select(item => item.Snapshot())]);
        }
    }

    private Task<bool> MutateLeaseAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        Action<MutableItem> mutate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!leaseToken.IsDefined)
        {
            throw new ArgumentException("A lease token is required.", nameof(leaseToken));
        }

        lock (_gate)
        {
            if (!_items.TryGetValue(operationId, out var item)
                || item.State != OutboxDeliveryState.Processing
                || item.LeaseToken != leaseToken)
            {
                return Task.FromResult(false);
            }

            mutate(item);
            return Task.FromResult(true);
        }
    }

    /// <summary>Returns expired leases to retry, or dead-letters them once attempts are spent.</summary>
    /// <remarks>
    /// A lease that expires is an attempt that crashed or hung. It already counted when it was
    /// leased, so recovering it into another attempt without checking the budget let a command
    /// that kills its handler replay for ever.
    /// </remarks>
    private int RecoverExpiredUnsafe(DateTimeOffset nowUtc)
    {
        var recovered = 0;
        foreach (var item in _items.Values.Where(item =>
                     item.State == OutboxDeliveryState.Processing
                     && item.LeaseExpiresUtc <= nowUtc))
        {
            var exhausted = item.AttemptCount >= item.Item.AttemptPolicy.MaxAttempts
                || item.Item.ExpiresUtc <= nowUtc;
            item.State = exhausted ? OutboxDeliveryState.DeadLetter : OutboxDeliveryState.Retrying;
            item.NextAttemptUtc = nowUtc;
            item.LeaseToken = null;
            item.LeaseExpiresUtc = null;
            item.LastFault = new(
                exhausted ? RuntimeFailureKind.Timeout : RuntimeFailureKind.Transient,
                new(exhausted ? "outbox-lease-attempts-exhausted" : "outbox-lease-expired"),
                exhausted ? RuntimeRecoveryAction.RetryManually : RuntimeRecoveryAction.RetryAutomatically,
                new($"operation:{item.Item.OperationId}"),
                nowUtc);
            item.CompletedUtc = exhausted ? nowUtc : null;
            recovered++;
        }

        return recovered;
    }

    private void ExpireOutstandingUnsafe(DateTimeOffset nowUtc)
    {
        foreach (var item in _items.Values.Where(item =>
                     (item.State is OutboxDeliveryState.Pending or OutboxDeliveryState.Retrying)
                     && item.Item.ExpiresUtc <= nowUtc))
        {
            item.State = OutboxDeliveryState.DeadLetter;
            item.CompletedUtc = nowUtc;
            item.LastFault = new(
                RuntimeFailureKind.Validation,
                new("outbox-item-expired"),
                RuntimeRecoveryAction.None,
                new($"operation:{item.Item.OperationId}"),
                nowUtc);
        }
    }

    private void PruneCompletedUnsafe()
    {
        var completed = _items.Values.Count(item => item.State == OutboxDeliveryState.Completed);
        if (completed <= _completedRetention)
        {
            return;
        }

        foreach (var item in _items.Values
                     .Where(item => item.State == OutboxDeliveryState.Completed)
                     .OrderBy(item => item.CompletedUtc)
                     .ThenBy(item => item.Item.AggregateId.Value, StringComparer.Ordinal)
                     .ThenBy(item => item.Item.AggregateSequence)
                     .Take(completed - _completedRetention)
                     .ToArray())
        {
            _items.Remove(item.Item.OperationId);
            _idempotency.Remove(item.Item.IdempotencyKey);
        }
    }

    private static bool IsActive(OutboxDeliveryState state) => state is
        OutboxDeliveryState.Pending or
        OutboxDeliveryState.Processing or
        OutboxDeliveryState.Retrying;

    private static DateTimeOffset AddBounded(DateTimeOffset value, TimeSpan duration)
    {
        try
        {
            return value + duration;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private sealed class MutableItem(OutboxItem item)
    {
        public OutboxItem Item { get; } = item;
        public OutboxDeliveryState State { get; set; } = OutboxDeliveryState.Pending;
        public int AttemptCount { get; set; }
        public DateTimeOffset NextAttemptUtc { get; set; } = item.NotBeforeUtc;
        public OutboxLeaseToken? LeaseToken { get; set; }
        public DateTimeOffset? LeaseExpiresUtc { get; set; }
        public RuntimeFault? LastFault { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }

        public OutboxStoredItem Snapshot() => new(
            Item,
            State,
            AttemptCount,
            NextAttemptUtc,
            LeaseToken,
            LeaseExpiresUtc,
            LastFault,
            CompletedUtc);
    }
}
