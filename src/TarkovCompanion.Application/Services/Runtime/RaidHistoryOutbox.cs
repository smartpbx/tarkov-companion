using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Runtime;

/// <summary>Marks a raid-history adapter whose writes are accepted only after outbox enqueue.</summary>
public interface IAtLeastOnceRaidHistoryService
{
}

/// <summary>Adapts raid history to the typed, leased, at-least-once runtime outbox.</summary>
/// <remarks>
/// The former queue held delegates, retried against wall-clock time, and silently discarded its
/// last failure. A stored command now exists before acceptance, retains one aggregate sequence,
/// and reaches a visible retry or dead-letter state through lease-token compare-and-swap. The
/// default fixture store holds accepted items for this object's lifetime but not across process
/// restart; issue #270 replaces it with SQLite and owns that durability claim.
/// </remarks>
public sealed class RaidHistoryOutbox : IRaidHistoryService, IAtLeastOnceRaidHistoryService, IAsyncDisposable
{
    private const int BatchSize = 32;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetainFor = TimeSpan.FromDays(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly RuntimeFeatureId FeatureId = new("raid-history");
    private readonly IRaidHistoryService _inner;
    private readonly ILogger<RaidHistoryOutbox>? _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IOutboxStore _store;
    private readonly OutboxProcessor _processor;
    private readonly SemaphoreSlim _enqueueLock = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Guid, long> _sequences = [];
    private readonly Dictionary<OperationId, TaskCompletionSource<OutboxDeliveryState>> _accepted = [];
    private readonly object _gate = new();
    private readonly Task _pump;
    private OutboxSnapshot _snapshot = OutboxSnapshot.Empty;
    private bool _accepting = true;
    private bool _disposed;

    public RaidHistoryOutbox(
        IRaidHistoryService inner,
        ILogger<RaidHistoryOutbox>? logger = null,
        TimeProvider? timeProvider = null,
        IOutboxStore? store = null,
        IRetryJitter? jitter = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _store = store ?? new FixtureOutboxStore();
        _processor = new(_store, new RaidHistoryCommandHandler(_inner), _timeProvider, jitter);
        _pump = Task.Factory.StartNew(
                PumpAsync,
                _lifetime.Token,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();
    }

    public OutboxSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public IOutboxStore Store => _store;

    public async Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raid);
        await EnqueueAsync(
                raid.Id,
                OutboxCommandKind.RaidStarted,
                OutboxPayload.FromTypedJson(new RaidStartedPayload(raid)),
                cancellationToken)
            .ConfigureAwait(false);
        return raid.Id;
    }

    public Task RecordEventAsync(
        Guid raidId,
        string type,
        DateTimeOffset timestampUtc,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        return EnqueueAsync(
            raidId,
            OutboxCommandKind.RaidEventRecorded,
            OutboxPayload.FromTypedJson(new RaidEventPayload(
                raidId,
                type,
                timestampUtc.ToUniversalTime(),
                payloadJson)),
            cancellationToken);
    }

    public Task EndAsync(
        Guid raidId,
        DateTimeOffset endUtc,
        string? outcome,
        string? notes,
        CancellationToken cancellationToken) =>
        EnqueueAsync(
            raidId,
            OutboxCommandKind.RaidEnded,
            OutboxPayload.FromTypedJson(new RaidEndedPayload(
                raidId,
                endUtc.ToUniversalTime(),
                outcome,
                notes)),
            cancellationToken);

    public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
        _inner.ListAsync(cancellationToken);

    public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(
        Guid raidId,
        CancellationToken cancellationToken) =>
        _inner.ListPositionsAsync(raidId, cancellationToken);

    public Task<IReadOnlyList<string>> ListEventPayloadsAsync(
        Guid raidId,
        string type,
        CancellationToken cancellationToken) =>
        _inner.ListEventPayloadsAsync(raidId, type, cancellationToken);

    public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(
        string mapId,
        int limit,
        CancellationToken cancellationToken) =>
        _inner.ListTrailsForMapAsync(mapId, limit, cancellationToken);

    public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) =>
        _inner.ExportCsvAsync(destination, cancellationToken);

    public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) =>
        _inner.ExportJsonAsync(destination, cancellationToken);

    /// <summary>Waits for every command accepted before this call to reach a terminal state.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        Task[] completions;
        lock (_gate)
        {
            completions = [.. _accepted.Values.Select(completion => completion.Task)];
        }

        if (completions.Length == 0)
        {
            return;
        }

        _signal.Release();
        await Task.WhenAll(completions).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RetryDeadLetterAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        var retried = await _store.ManualRetryAsync(
                operationId,
                _timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (retried)
        {
            lock (_gate)
            {
                _accepted[operationId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _signal.Release();
        }

        return retried;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _accepting = false;
        }

        _signal.Release();
        try
        {
            await _pump.WaitAsync(StopTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }

        lock (_gate)
        {
            _disposed = true;
        }

        _enqueueLock.Dispose();
        _signal.Dispose();
        _lifetime.Dispose();
    }

    private async Task EnqueueAsync(
        Guid raidId,
        OutboxCommandKind command,
        OutboxPayload payload,
        CancellationToken cancellationToken)
    {
        if (raidId == Guid.Empty)
        {
            throw new ArgumentException("A raid id is required.", nameof(raidId));
        }

        await _enqueueLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_accepting)
                {
                    throw new InvalidOperationException("The raid history outbox is stopping.");
                }
            }

            var sequence = checked(_sequences.GetValueOrDefault(raidId) + 1);
            var operationId = OperationId.New();
            var now = _timeProvider.GetUtcNow();
            var aggregateId = new OutboxAggregateId($"raid:{raidId:N}");
            var item = new OutboxItem(
                operationId,
                new($"{aggregateId}:{sequence}"),
                CorrelationId.New(),
                FeatureId,
                command,
                OutboxContractVersion.Current,
                aggregateId,
                sequence,
                now,
                now,
                AddBounded(now, RetainFor),
                payload,
                OutboxAttemptPolicy.Default);
            var receipt = await _store.EnqueueAsync(item, cancellationToken).ConfigureAwait(false);
            if (!receipt.Added && receipt.OperationId != operationId)
            {
                throw new InvalidOperationException("An outbox idempotency key resolved to another operation.");
            }

            _sequences[raidId] = sequence;
            lock (_gate)
            {
                _accepted[receipt.OperationId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _enqueueLock.Release();
        }

        _signal.Release();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                while (true)
                {
                    var result = await _processor.ProcessBatchAsync(BatchSize, LeaseDuration, _lifetime.Token)
                        .ConfigureAwait(false);
                    await ResolveTerminalOperationsAsync(_lifetime.Token).ConfigureAwait(false);
                    if (result.DeadLettered > 0)
                    {
                        _logger?.LogWarning(
                            "Raid history moved {Count} command(s) to the visible dead-letter state.",
                            result.DeadLettered);
                    }

                    if (result.Leased > 0)
                    {
                        continue;
                    }

                    var nextDue = await FindNextDeliverableUtcAsync(_lifetime.Token).ConfigureAwait(false);
                    bool accepting;
                    lock (_gate)
                    {
                        accepting = _accepting;
                    }

                    if (nextDue is null)
                    {
                        if (!accepting)
                        {
                            return;
                        }

                        break;
                    }

                    var delay = nextDue.Value - _timeProvider.GetUtcNow();
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, _timeProvider, _lifetime.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task ResolveTerminalOperationsAsync(CancellationToken cancellationToken)
    {
        var items = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var item in items.Where(item => item.State is OutboxDeliveryState.Completed or OutboxDeliveryState.DeadLetter))
            {
                if (_accepted.TryGetValue(item.Item.OperationId, out var completion))
                {
                    completion.TrySetResult(item.State);
                }
            }
        }

        await RefreshSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<DateTimeOffset?> FindNextDeliverableUtcAsync(CancellationToken cancellationToken)
    {
        var items = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return items
            .Where(item => item.State != OutboxDeliveryState.Completed)
            .GroupBy(item => item.Item.AggregateId)
            .Select(group => group.OrderBy(item => item.Item.AggregateSequence).First())
            .Where(item => item.State is OutboxDeliveryState.Pending or OutboxDeliveryState.Retrying)
            .Select(item => (DateTimeOffset?)item.NextAttemptUtc)
            .Min();
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _store.GetSnapshotAsync(_timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _snapshot = snapshot;
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

    private sealed record RaidStartedPayload(RaidHistoryEntry Raid);

    private sealed record RaidEventPayload(
        Guid RaidId,
        string Type,
        DateTimeOffset TimestampUtc,
        string PayloadJson);

    private sealed record RaidEndedPayload(
        Guid RaidId,
        DateTimeOffset EndUtc,
        string? Outcome,
        string? Notes);

    private sealed class RaidHistoryCommandHandler(IRaidHistoryService inner) : IOutboxCommandHandler
    {
        public async Task HandleAsync(
            OutboxItem item,
            OutboxDeliveryContext context,
            CancellationToken cancellationToken)
        {
            if (item.Version != OutboxContractVersion.Current)
            {
                throw new RuntimeFaultException(new(
                    RuntimeFailureKind.Version,
                    new("outbox-command-version"),
                    RuntimeRecoveryAction.Upgrade,
                    new($"operation:{context.OperationId}"),
                    item.CreatedUtc));
            }

            switch (item.Command)
            {
                case OutboxCommandKind.RaidStarted:
                    var started = item.Payload.ReadTypedJson<RaidStartedPayload>();
                    var id = await inner.StartAsync(started.Raid, cancellationToken).ConfigureAwait(false);
                    if (id != started.Raid.Id)
                    {
                        throw new RuntimeFaultException(new(
                            RuntimeFailureKind.Conflict,
                            new("raid-start-id-conflict"),
                            RuntimeRecoveryAction.ResolveConflict,
                            new($"operation:{context.OperationId}"),
                            item.CreatedUtc));
                    }

                    break;
                case OutboxCommandKind.RaidEventRecorded:
                    var recorded = item.Payload.ReadTypedJson<RaidEventPayload>();
                    await inner.RecordEventAsync(
                            recorded.RaidId,
                            recorded.Type,
                            recorded.TimestampUtc,
                            recorded.PayloadJson,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case OutboxCommandKind.RaidEnded:
                    var ended = item.Payload.ReadTypedJson<RaidEndedPayload>();
                    await inner.EndAsync(
                            ended.RaidId,
                            ended.EndUtc,
                            ended.Outcome,
                            ended.Notes,
                            cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new RuntimeFaultException(new(
                        RuntimeFailureKind.Unsupported,
                        new("outbox-command-unsupported"),
                        RuntimeRecoveryAction.Upgrade,
                        new($"operation:{context.OperationId}"),
                        item.CreatedUtc));
            }
        }
    }
}
