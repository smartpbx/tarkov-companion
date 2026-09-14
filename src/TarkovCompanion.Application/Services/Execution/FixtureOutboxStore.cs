using System.Collections.Immutable;

namespace TarkovCompanion.Application.Services.Execution;

/// <summary>
/// A deterministic fixture store with the same atomic lease-token rules as the durable store.
/// It intentionally makes no process-restart durability claim; issue #270 owns SQLite durability.
/// </summary>
public sealed class FixtureOutboxStore : IOutboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<OperationId, MutableItem> _items = [];
    private readonly Dictionary<IdempotencyKey, OperationId> _idempotency = [];
    private readonly int _capacity;

    public FixtureOutboxStore(int capacity = 1000)
    {
        if (capacity is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public Task<OutboxEnqueueReceipt> EnqueueAsync(OutboxItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_idempotency.TryGetValue(item.IdempotencyKey, out var existing))
            {
                return Task.FromResult(new OutboxEnqueueReceipt(false, existing));
            }

            if (_items.Count >= _capacity)
            {
                throw new OutboxCapacityException();
            }

            if (_items.ContainsKey(item.OperationId))
            {
                throw new InvalidOperationException("An outbox operation id may be enqueued only once.");
            }

            if (_items.Values.Any(existing =>
                    existing.Item.AggregateId == item.AggregateId
                    && existing.Item.AggregateSequence == item.AggregateSequence))
            {
                throw new InvalidOperationException("An aggregate sequence may be enqueued only once.");
            }

            _items.Add(item.OperationId, new(item));
            _idempotency.Add(item.IdempotencyKey, item.OperationId);
            return Task.FromResult(new OutboxEnqueueReceipt(true, item.OperationId));
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
            },
            cancellationToken);

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
                .Where(item => item.State != OutboxDeliveryState.Completed)
                .Select(item => (DateTimeOffset?)item.Item.CreatedUtc)
                .Min();
            var age = oldest is null
                ? (TimeSpan?)null
                : oldest > nowUtc
                    ? TimeSpan.Zero
                    : nowUtc - oldest.Value;
            return Task.FromResult(new OutboxSnapshot(
                counts,
                age));
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

    private int RecoverExpiredUnsafe(DateTimeOffset nowUtc)
    {
        var recovered = 0;
        foreach (var item in _items.Values.Where(item =>
                     item.State == OutboxDeliveryState.Processing
                     && item.LeaseExpiresUtc <= nowUtc))
        {
            item.State = OutboxDeliveryState.Retrying;
            item.NextAttemptUtc = nowUtc;
            item.LeaseToken = null;
            item.LeaseExpiresUtc = null;
            item.LastFault = new(
                RuntimeFailureKind.Transient,
                new("outbox-lease-expired"),
                RuntimeRecoveryAction.RetryAutomatically,
                new($"operation:{item.Item.OperationId}"),
                nowUtc);
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
