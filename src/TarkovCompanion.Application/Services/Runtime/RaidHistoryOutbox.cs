using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Runtime;

/// <summary>The durable-acceptance and recovery seam of raid-history delivery.</summary>
public interface IAtLeastOnceRaidHistoryService
{
    /// <summary>Raised when delivery health or store counts change.</summary>
    event EventHandler? Changed;

    OutboxSnapshot Snapshot { get; }

    /// <summary>Durably accepts every command, in order, or none of them.</summary>
    /// <remarks>
    /// Returning is the publication boundary: once this returns the commands are stored and
    /// will be delivered at least once. If it throws, none of them were accepted by this call.
    /// </remarks>
    Task<ImmutableArray<OperationId>> AcceptAsync(
        IReadOnlyList<RaidHistoryCommand> commands,
        CancellationToken cancellationToken);

    /// <summary>Returns a dead-lettered command to delivery; false if it is not dead-lettered.</summary>
    Task<bool> RetryDeadLetterAsync(OperationId operationId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a deliberate decision not to replay a dead-lettered command, releasing its
    /// aggregate head. This is required for expired commands, which are never replayed blindly.
    /// </summary>
    Task<bool> ResolveDeadLetterAsync(OperationId operationId, CancellationToken cancellationToken);

    /// <summary>Wakes a paused or backed-off delivery pump now, restarting it if it ended.</summary>
    bool RequestPumpRecovery();
}

/// <summary>A target-owned atomic side-effect and replay-ledger scope.</summary>
/// <remarks>
/// The outbox is at-least-once, so a process can stop after the SQLite side effect commits but
/// before the queue acknowledgement does. A durable target implements this seam to commit the
/// side effect and operation id together; fixture targets keep their ordinary at-least-once
/// behavior. The scope carries no game data and never observes the game process.
/// </remarks>
public interface IRaidHistoryOperationStore
{
    Task ApplyOnceAsync(
        OperationId operationId,
        OutboxCommandKind commandKind,
        Guid raidId,
        Func<CancellationToken, Task> apply,
        CancellationToken cancellationToken);
}

/// <summary>Adapts raid history to the typed, leased, at-least-once runtime outbox.</summary>
/// <remarks>
/// <para>
/// The former queue held delegates, retried against wall-clock time, and silently discarded its
/// last failure. A stored command now exists before acceptance, retains one aggregate sequence,
/// and reaches a visible retry or dead-letter state through lease-token compare-and-swap. The
/// default fixture store holds accepted items for this object's lifetime but not across process
/// restart; issue #270 replaces it with SQLite and owns that durability claim.
/// </para>
/// <para>
/// Acceptance used to refresh the status snapshot after the store had already taken the command,
/// so a failed or cancelled status read threw at a producer whose write was in fact stored. Its
/// retry then duplicated the command. Nothing fallible follows the store call now, and delivery
/// health is maintained by the pump instead.
/// </para>
/// <para>
/// A store or processor failure used to escape the pump and end it, after which accepted commands
/// were never delivered and nothing said so. The pump now records a sanitized fault, backs off on
/// injected time, resumes by itself, and can be woken — or restarted — through
/// <see cref="RequestPumpRecovery"/>.
/// </para>
/// </remarks>
public sealed class RaidHistoryOutbox : IRaidHistoryService, IAtLeastOnceRaidHistoryService, IAsyncDisposable
{
    public const int MaxCommandsPerAcceptance = 16;
    private const int BatchSize = 32;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetainFor = TimeSpan.FromDays(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitialPumpBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumPumpBackoff = TimeSpan.FromSeconds(30);
    private static readonly RuntimeFeatureId FeatureId = new("raid-history");
    private static readonly DiagnosticReference OutboxReference = new("feature:raid-history-outbox");
    private static readonly string[] ScreenshotNameMarkers = [".png", ".jpg", ".jpeg", ".bmp"];
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
    private EventHandler? _changed;
    private Task _pump;
    private OutboxSnapshot _storeSnapshot = OutboxSnapshot.Empty;
    private OutboxPumpState _pumpState = OutboxPumpState.Idle;
    private RuntimeFault? _lastPumpFault;
    private RuntimeFault? _lastAcceptanceFault;
    private DateTimeOffset? _lastSuccessfulPumpUtc;
    private int _consecutivePumpFaults;
    private bool _aggregateSequencesRestored;
    private bool _accepting = true;
    private bool _processorStopping;
    private bool _disposing;
    private bool _disposed;
    private Task? _disposeTask;

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
        _processor = new(
            _store,
            new RaidHistoryCommandHandler(_inner),
            _timeProvider,
            jitter,
            progress: SignalProcessorProgress);
        _pump = StartPump();
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

    public OutboxSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return ComposeUnsafe();
            }
        }
    }

    public IOutboxStore Store => _store;

    public async Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raid);
        await AcceptAsync([RaidHistoryCommand.StartRaid(raid)], cancellationToken).ConfigureAwait(false);
        return raid.Id;
    }

    /// <summary>Refused: generic event JSON has no route into the outbox.</summary>
    /// <remarks>
    /// This used to wrap the caller's JSON string inside a typed payload, which let exact
    /// coordinates and screenshot filenames past the outbox's privacy boundary. Every event the
    /// application records has a closed command; use <see cref="AcceptAsync"/>.
    /// </remarks>
    public Task RecordEventAsync(
        Guid raidId,
        string type,
        DateTimeOffset timestampUtc,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        // These are bounded player actions, not captured game evidence. They write straight
        // through for the same reason CorrectAsync does, while every other generic type remains
        // refused by this privacy boundary.
        return type is "tag" or RaidScanCorrection.EventType or RaidPlannedRoute.EventType
                or RaidExtractUsed.EventType or RaidArchive.EventType or RaidOutcomeAnswer.EventType
            ? _inner.RecordEventAsync(raidId, type, timestampUtc, payloadJson, cancellationToken)
            : Task.FromException(new NotSupportedException(
                "Raid history accepts closed typed commands only; generic event JSON cannot enter the outbox."));
    }

    public Task EndAsync(
        Guid raidId,
        DateTimeOffset endUtc,
        string? outcome,
        string? notes,
        CancellationToken cancellationToken) =>
        AcceptAsync([RaidHistoryCommand.EndRaid(raidId, endUtc, outcome, notes)], cancellationToken);

    /// <summary>
    /// Writes straight through to the target instead of the durable queue.
    /// </summary>
    /// <remarks>
    /// A manual Debrief correction is a one-off UI action with no delivery-ordering relationship
    /// to the game-observed events above; it is not part of the outbox's closed command set (see
    /// <see cref="Encode"/>) and does not need at-least-once replay.
    /// </remarks>
    public Task CorrectAsync(Guid raidId, string? outcome, string? notes, CancellationToken cancellationToken) =>
        _inner.CorrectAsync(raidId, outcome, notes, cancellationToken);

    public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
        _inner.ListAsync(cancellationToken);

    /// <summary>
    /// Straight through, for the same reason as <see cref="CorrectAsync"/>: a Debrief delete or
    /// undo is a one-off UI action, not part of the outbox's closed, replayable command set.
    /// </summary>
    public Task SoftDeleteAsync(IReadOnlyCollection<Guid> raidIds, DateTimeOffset deletedUtc, CancellationToken cancellationToken) =>
        _inner.SoftDeleteAsync(raidIds, deletedUtc, cancellationToken);

    public Task RestoreDeletedAsync(IReadOnlyCollection<Guid> raidIds, CancellationToken cancellationToken) =>
        _inner.RestoreDeletedAsync(raidIds, cancellationToken);

    public Task PurgeDeletedAsync(IReadOnlyCollection<Guid> exceptRaidIds, CancellationToken cancellationToken) =>
        _inner.PurgeDeletedAsync(exceptRaidIds, cancellationToken);

    public Task<RaidManualMetadata?> GetManualMetadataAsync(Guid raidId, CancellationToken cancellationToken) =>
        _inner.GetManualMetadataAsync(raidId, cancellationToken);

    public Task SetManualMetadataAsync(Guid raidId, RaidManualMetadata metadata, CancellationToken cancellationToken) =>
        _inner.SetManualMetadataAsync(raidId, metadata, cancellationToken);

    public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(
        Guid raidId,
        CancellationToken cancellationToken) =>
        _inner.ListPositionsAsync(raidId, cancellationToken);

    public Task<IReadOnlyList<string>> ListEventPayloadsAsync(
        Guid raidId,
        string type,
        CancellationToken cancellationToken) =>
        _inner.ListEventPayloadsAsync(raidId, type, cancellationToken);

    public Task<IReadOnlyList<RaidHistoryEvent>> ListEventsAsync(
        Guid raidId,
        string type,
        CancellationToken cancellationToken) =>
        _inner.ListEventsAsync(raidId, type, cancellationToken);

    public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(
        string mapId,
        int limit,
        CancellationToken cancellationToken) =>
        _inner.ListTrailsForMapAsync(mapId, limit, cancellationToken);

    public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) =>
        _inner.ExportCsvAsync(destination, cancellationToken);

    public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) =>
        _inner.ExportJsonAsync(destination, cancellationToken);

    public async Task<ImmutableArray<OperationId>> AcceptAsync(
        IReadOnlyList<RaidHistoryCommand> commands,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count is < 1 or > MaxCommandsPerAcceptance)
        {
            throw new ArgumentOutOfRangeException(nameof(commands));
        }

        // Encoding validates every command against the closed contract, so a refused command is
        // rejected before anything is stored or any aggregate sequence is spent.
        var encoded = new EncodedCommand[commands.Count];
        for (var index = 0; index < commands.Count; index++)
        {
            encoded[index] = Encode(
                commands[index] ?? throw new ArgumentException("Commands cannot contain null.", nameof(commands)));
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

            try
            {
                await RestoreAggregateSequencesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecordAcceptanceFault(exception);
                throw;
            }

            var now = _timeProvider.GetUtcNow();
            var allocated = new Dictionary<Guid, long>();
            var items = ImmutableArray.CreateBuilder<OutboxItem>(encoded.Length);
            foreach (var command in encoded)
            {
                var previous = allocated.TryGetValue(command.RaidId, out var last)
                    ? last
                    : _sequences.GetValueOrDefault(command.RaidId);
                var sequence = checked(previous + 1);
                allocated[command.RaidId] = sequence;
                var operationId = OperationId.New();
                items.Add(new OutboxItem(
                    operationId,
                    new($"raid-history:{operationId}"),
                    CorrelationId.New(),
                    FeatureId,
                    command.Kind,
                    OutboxContractVersion.Current,
                    new($"raid:{command.RaidId:N}"),
                    sequence,
                    now,
                    now,
                    AddBounded(now, RetainFor),
                    command.Payload,
                    OutboxAttemptPolicy.Default));
            }

            var batch = items.MoveToImmutable();
            ImmutableArray<OutboxEnqueueReceipt> receipts;
            try
            {
                receipts = await _store.EnqueueBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RecordAcceptanceFault(exception);
                throw;
            }
            finally
            {
                // A durable store can fail after applying a write. Its sequences are never
                // reused, so a later command cannot collide with an ambiguously stored one; the
                // gap is harmless because delivery orders by sequence, not by contiguity.
                foreach (var (raidId, sequence) in allocated)
                {
                    _sequences[raidId] = sequence;
                }
            }

            // Durable acceptance is the publication boundary. Nothing below may throw, or the
            // producer would be told that a stored command failed.
            var accepted = receipts.IsDefaultOrEmpty
                ? batch.Select(item => item.OperationId).ToArray()
                : receipts.Select(receipt => receipt.OperationId).ToArray();
            lock (_gate)
            {
                _lastAcceptanceFault = null;
                foreach (var operationId in accepted)
                {
                    _accepted.TryAdd(operationId, new(TaskCreationOptions.RunContinuationsAsynchronously));
                }

                WakeUnsafe();
            }

            return [.. accepted];
        }
        finally
        {
            _enqueueLock.Release();
        }
    }

    /// <summary>Restores sequence cursors once, while the acceptance lock excludes new writes.</summary>
    private async Task RestoreAggregateSequencesAsync(CancellationToken cancellationToken)
    {
        if (_aggregateSequencesRestored)
        {
            return;
        }

        if (_store is IOutboxAggregateSequenceStore durableSequences)
        {
            var heads = await durableSequences.ReadAggregateSequenceHeadsAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The durable outbox returned no aggregate sequence ledger.");
            foreach (var (aggregateId, sequence) in heads)
            {
                if (sequence < 1)
                {
                    throw new InvalidDataException("The durable outbox contains an invalid aggregate sequence cursor.");
                }

                const string raidPrefix = "raid:";
                if (aggregateId.Value.StartsWith(raidPrefix, StringComparison.Ordinal)
                    && Guid.TryParseExact(aggregateId.Value[raidPrefix.Length..], "N", out var raidId))
                {
                    _sequences[raidId] = Math.Max(_sequences.GetValueOrDefault(raidId), sequence);
                }
            }
        }

        _aggregateSequencesRestored = true;
    }

    /// <summary>Waits for every command accepted before this call to reach a terminal state.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        Task[] completions;
        lock (_gate)
        {
            completions = [.. _accepted.Values.Select(completion => completion.Task)];
            if (completions.Length > 0)
            {
                WakeUnsafe();
            }
        }

        if (completions.Length == 0)
        {
            return;
        }

        await Task.WhenAll(completions).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RetryDeadLetterAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_accepting)
            {
                return false;
            }
        }

        await _enqueueLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_accepting)
                {
                    return false;
                }
            }

            var retried = await _store.ManualRetryAsync(
                    operationId,
                    _timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!retried)
            {
                return false;
            }

            // Manual retry is an explicit operator decision to replay an acknowledgement-unknown
            // delivery. Release its local fence only after the store made that decision durable.
            _processor.NotifyReconciled(operationId);
            lock (_gate)
            {
                _accepted[operationId] = new(TaskCreationOptions.RunContinuationsAsynchronously);
                WakeUnsafe();
            }

            PublishChanged();
            return true;
        }
        finally
        {
            _enqueueLock.Release();
        }
    }

    public async Task<bool> ResolveDeadLetterAsync(OperationId operationId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_accepting)
            {
                return false;
            }
        }

        await _enqueueLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_accepting)
                {
                    return false;
                }
            }

            var resolved = await _store.ResolveDeadLetterAsync(
                    operationId,
                    _timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!resolved)
            {
                return false;
            }

            _processor.NotifyReconciled(operationId);
            lock (_gate)
            {
                WakeUnsafe();
            }

            PublishChanged();
            return true;
        }
        finally
        {
            _enqueueLock.Release();
        }
    }

    public bool RequestPumpRecovery()
    {
        lock (_gate)
        {
            if (!_accepting || _disposed)
            {
                return false;
            }

            // Recovery means "try now": the backoff restarts from its shortest delay. The fault
            // stays visible until a pass actually succeeds.
            _consecutivePumpFaults = 0;
            WakeUnsafe();
        }

        PublishChanged();
        return true;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is { } currentAttempt
                && (_disposing || !currentAttempt.IsCompleted || _disposed))
            {
                return new(currentAttempt);
            }

            _disposing = true;
            _accepting = false;
            _pumpState = OutboxPumpState.Stopping;
            // The core yields before touching collaborators, so callbacks cannot run while this
            // gate is held. Every concurrent caller then joins this exact bounded attempt.
            _disposeTask = DisposeCoreAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            PublishChanged();
            var stopDeadlineUtc = AddBounded(_timeProvider.GetUtcNow(), StopTimeout);

            // An acceptance already past its admission check may still be storing commands.
            // Waiting for it means none can be accepted after the pump below drains and stops.
            // A timed-out SemaphoreSlim wait remains queued unless its own token is cancelled.
            // The previous wait stole the lock after this method returned, permanently wedging a
            // later dispose and every acceptance. The deadline owns and observes that waiter.
            var enteringBudget = stopDeadlineUtc - _timeProvider.GetUtcNow();
            if (enteringBudget <= TimeSpan.Zero)
            {
                return;
            }

            using var enteringDeadline = new CancellationTokenSource(enteringBudget, _timeProvider);
            var entering = _enqueueLock.WaitAsync(enteringDeadline.Token);
            try
            {
                await entering.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (enteringDeadline.IsCancellationRequested)
            {
                // That acceptance still owns the lock and the signal. Leave both alive and the
                // state non-terminal; cancellation removes this waiter, so a later DisposeAsync
                // can acquire the lock once the acceptance returns.
                await ObserveAsync(entering).ConfigureAwait(false);
                return;
            }

            _enqueueLock.Release();
            Task pump;
            lock (_gate)
            {
                if (_pump.IsCompleted && !_processorStopping && !_lifetime.IsCancellationRequested)
                {
                    _pump = StartPump();
                }

                _signal.Release();
                pump = _pump;
            }

            if (!await CompletesBeforeDeadlineAsync(pump, stopDeadlineUtc).ConfigureAwait(false))
            {
                var cancelling = ObserveAsync(_lifetime.CancelAsync());
                if (!await CompletesBeforeDeadlineAsync(Task.WhenAll(pump, cancelling), stopDeadlineUtc)
                        .ConfigureAwait(false))
                {
                    // A handler that ignores cancellation still owns the pump and its semaphores.
                    return;
                }
            }

            await _processor.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _processorStopping = true;
            }

            if (!await CompletesBeforeDeadlineAsync(_processor.StopCompletion, stopDeadlineUtc).ConfigureAwait(false))
            {
                // A handler or cancellation callback still owns its bounded processor state and
                // store lease. Leave this outbox non-terminal and all dependencies alive so a
                // later disposal can finish once that exact invocation settles.
                lock (_gate)
                {
                    _pumpState = OutboxPumpState.Stopping;
                }

                PublishChanged();
                return;
            }

            lock (_gate)
            {
                _disposed = true;
                _pumpState = OutboxPumpState.Stopped;
            }

            PublishChanged();
            // Keep the admission semaphore alive after terminal disposal. A caller can have passed
            // the optimistic pre-check just before shutdown closed admission and still be queued;
            // it must be able to acquire, observe _disposed, and release without racing disposal of
            // the semaphore itself. SemaphoreSlim owns no unmanaged resource.
            _signal.Dispose();
            _lifetime.Dispose();
        }
        finally
        {
            lock (_gate)
            {
                _disposing = false;
            }
        }
    }

    private Task StartPump() =>
        Task.Factory.StartNew(
                PumpAsync,
                _lifetime.Token,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                DrainSignals();
                while (true)
                {
                    DateTimeOffset? nextDue;
                    try
                    {
                        SetPumpState(OutboxPumpState.Running);
                        var result = await _processor.ProcessBatchAsync(BatchSize, LeaseDuration, _lifetime.Token)
                            .ConfigureAwait(false);
                        await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
                        if (result.DeadLettered > 0)
                        {
                            _logger?.LogWarning(
                                "Raid history moved {Count} command(s) to the visible dead-letter state.",
                                result.DeadLettered);
                        }

                        nextDue = result.Leased > 0
                            ? _timeProvider.GetUtcNow()
                            : await FindNextDueUtcAsync(_lifetime.Token).ConfigureAwait(false);
                        if (result.Leased == 0
                            && result.InFlight > 0
                            && nextDue <= _timeProvider.GetUtcNow())
                        {
                            // Every processor slot may be retained by work that has already
                            // reported. Due store rows cannot start until one settles, so poll at a
                            // bounded cadence instead of spinning the pump at full speed.
                            nextDue = AddBounded(_timeProvider.GetUtcNow(), InitialPumpBackoff);
                        }

                        // A timed-out handler or uncertain acknowledgement is degraded delivery
                        // health, not a failed pump pass. Keep it visible while still calculating
                        // the next due work and allowing shutdown to observe normal pump progress.
                        MarkPassCompleted(result.LastFault);
                        if (result.LastFault is not null)
                        {
                            bool stopForDeliveryFault;
                            lock (_gate)
                            {
                                stopForDeliveryFault = !_accepting;
                            }

                            if (stopForDeliveryFault)
                            {
                                SetPumpState(OutboxPumpState.Stopped);
                                return;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        var backoff = RecordPumpFault(exception);
                        // #786. Shutting down, a store that cannot be read will not be readable in
                        // the next backoff either. Retrying here kept the final drain going for the
                        // whole ten-second stop deadline, which is longer than the application's
                        // entire teardown budget: closing during startup (the database still being
                        // migrated) took eight seconds and the process was then killed. Accepted
                        // commands are in the durable store and are delivered on the next launch.
                        bool stopForStoreFault;
                        lock (_gate)
                        {
                            stopForStoreFault = !_accepting;
                        }

                        if (stopForStoreFault)
                        {
                            return;
                        }

                        _logger?.LogWarning(
                            "Raid-history delivery paused after a sanitized store or processor failure and resumes in {Backoff}.",
                            backoff);
                        await WaitForSignalOrDelayAsync(backoff).ConfigureAwait(false);
                        continue;
                    }

                    if (nextDue is null)
                    {
                        bool accepting;
                        lock (_gate)
                        {
                            accepting = _accepting;
                        }

                        if (!accepting)
                        {
                            SetPumpState(OutboxPumpState.Stopped);
                            return;
                        }

                        SetPumpState(OutboxPumpState.Idle);
                        break;
                    }

                    var delay = nextDue.Value - _timeProvider.GetUtcNow();
                    if (delay > TimeSpan.Zero)
                    {
                        SetPumpState(OutboxPumpState.Idle);
                        await WaitForSignalOrDelayAsync(delay).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            SetPumpState(OutboxPumpState.Stopped);
        }
        catch (Exception exception)
        {
            // Only the wake signal can fail out here. The pump ends visibly faulted, and the next
            // acceptance or a recovery request starts a new one.
            RecordPumpFault(exception);
        }
    }

    /// <summary>Waits for a wake signal or the delay, whichever comes first, without losing a signal.</summary>
    private async Task WaitForSignalOrDelayAsync(TimeSpan delay)
    {
        using var wake = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var signalled = _signal.WaitAsync(wake.Token);
        var elapsed = Task.Delay(delay, _timeProvider, wake.Token);
        await Task.WhenAny(signalled, elapsed).ConfigureAwait(false);
        await wake.CancelAsync().ConfigureAwait(false);
        await ObserveAsync(signalled).ConfigureAwait(false);
        await ObserveAsync(elapsed).ConfigureAwait(false);
        _lifetime.Token.ThrowIfCancellationRequested();
        DrainSignals();
    }

    /// <summary>Coalesces queued wake signals; one pass serves every acceptance before it.</summary>
    private void DrainSignals()
    {
        while (_signal.CurrentCount > 0 && _signal.Wait(0))
        {
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<OperationId, TaskCompletionSource<OutboxDeliveryState>>[] awaiting;
        lock (_gate)
        {
            awaiting = [.. _accepted];
        }

        if (awaiting.Length > 0)
        {
            var rows = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            var states = rows.ToDictionary(row => row.Item.OperationId, row => row.State);
            lock (_gate)
            {
                foreach (var (operationId, completion) in awaiting)
                {
                    // Every awaited command was stored before it was awaited, and stores keep
                    // dead letters until retried, so one no longer listed was completed and then
                    // released by retention.
                    var state = states.TryGetValue(operationId, out var stored)
                        ? stored
                        : OutboxDeliveryState.Completed;
                    if (state is OutboxDeliveryState.Completed or OutboxDeliveryState.DeadLetter
                        && _accepted.TryGetValue(operationId, out var current)
                        && ReferenceEquals(current, completion))
                    {
                        _accepted.Remove(operationId);
                        completion.TrySetResult(state);
                    }
                }
            }
        }

        var snapshot = await _store.GetSnapshotAsync(_timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        lock (_gate)
        {
            _storeSnapshot = snapshot ?? OutboxSnapshot.Empty;
        }
    }

    /// <summary>When the pump next has something to do, or null when nothing can progress.</summary>
    /// <remarks>
    /// A head held by an expired lease is due when its lease expires. Ignoring those let a pump
    /// that crashed mid-batch go idle with the head still leased, and nothing woke it to recover.
    /// </remarks>
    private async Task<DateTimeOffset?> FindNextDueUtcAsync(CancellationToken cancellationToken)
    {
        var items = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return items
            .Where(item => item.State != OutboxDeliveryState.Completed)
            .GroupBy(item => item.Item.AggregateId)
            .Select(group => group.OrderBy(item => item.Item.AggregateSequence).First())
            .Select(head => head.State switch
            {
                OutboxDeliveryState.Pending or OutboxDeliveryState.Retrying => (DateTimeOffset?)head.NextAttemptUtc,
                OutboxDeliveryState.Processing => head.LeaseExpiresUtc,
                _ => null,
            })
            .Min();
    }

    private void MarkPassCompleted(RuntimeFault? deliveryFault)
    {
        lock (_gate)
        {
            _lastSuccessfulPumpUtc = _timeProvider.GetUtcNow();
            _lastPumpFault = deliveryFault;
            _consecutivePumpFaults = 0;
            _pumpState = deliveryFault is null
                ? OutboxPumpState.Running
                : OutboxPumpState.Faulted;
        }

        PublishChanged();
    }

    private TimeSpan RecordPumpFault(Exception exception)
    {
        TimeSpan backoff;
        lock (_gate)
        {
            if (_consecutivePumpFaults < int.MaxValue)
            {
                _consecutivePumpFaults++;
            }

            _lastPumpFault = exception is RuntimeFaultException classified
                ? classified.Fault
                : RuntimeFault.FromException(exception, _timeProvider, OutboxReference);
            _pumpState = OutboxPumpState.Faulted;
            var exponent = Math.Min(_consecutivePumpFaults - 1, 16);
            var ticks = Math.Min(
                MaximumPumpBackoff.Ticks,
                InitialPumpBackoff.Ticks * Math.Pow(2, exponent));
            backoff = TimeSpan.FromTicks((long)ticks);
        }

        PublishChanged();
        return backoff;
    }

    private void RecordAcceptanceFault(Exception exception)
    {
        var fault = exception is OutboxCapacityException
            ? new RuntimeFault(
                RuntimeFailureKind.Transient,
                new("outbox-admission-full"),
                RuntimeRecoveryAction.RetryManually,
                OutboxReference,
                _timeProvider.GetUtcNow())
            : RuntimeFault.FromException(exception, _timeProvider, OutboxReference);
        lock (_gate)
        {
            _lastAcceptanceFault = fault;
        }

        PublishChanged();
    }

    private void SetPumpState(OutboxPumpState state)
    {
        lock (_gate)
        {
            if (_pumpState == state)
            {
                return;
            }

            _pumpState = state;
        }

        PublishChanged();
    }

    private void WakeUnsafe()
    {
        if (_disposed)
        {
            return;
        }

        if (_accepting && _pump.IsCompleted)
        {
            _pump = StartPump();
        }

        _signal.Release();
    }

    private void SignalProcessorProgress()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _signal.Release();
            }
        }
    }

    private OutboxSnapshot ComposeUnsafe() => _storeSnapshot with
    {
        PumpState = _pumpState,
        LastPumpFault = _lastPumpFault,
        ConsecutivePumpFaults = _consecutivePumpFaults,
        LastSuccessfulPumpUtc = _lastSuccessfulPumpUtc,
        LastAcceptanceFault = _lastAcceptanceFault,
    };

    private async Task<bool> CompletesBeforeDeadlineAsync(Task task, DateTimeOffset deadlineUtc)
    {
        var remaining = deadlineUtc - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return task.IsCompleted;
        }

        try
        {
            await task.WaitAsync(remaining, _timeProvider).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

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
                // Runtime publication is observational. A broken subscriber cannot stop durable
                // delivery or prevent other subscribers from seeing the health transition.
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
            // The awaited wait was cancelled on purpose, or a cancellation callback belonging to
            // a handler threw; the handler's own result reports that failure.
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

    private static EncodedCommand Encode(RaidHistoryCommand command) => command switch
    {
        RaidHistoryCommand.Started started => new EncodedCommand(
            started.RaidId,
            OutboxCommandKind.RaidStarted,
            OutboxPayload.FromTypedJson(StartedPayload.From(started.Raid))),
        RaidHistoryCommand.StateRecorded state => new EncodedCommand(
            state.RaidId,
            OutboxCommandKind.RaidStateRecorded,
            OutboxPayload.FromTypedJson(StatePayload.From(state.RaidId, state.Evidence))),
        RaidHistoryCommand.ExtractsRecorded extracts => new EncodedCommand(
            extracts.RaidId,
            OutboxCommandKind.RaidExtractsRecorded,
            OutboxPayload.FromTypedJson(ExtractsPayload.From(extracts.RaidId, extracts.ObservedUtc, extracts.Extracts))),
        RaidHistoryCommand.ScanRecorded scan => new EncodedCommand(
            scan.RaidId,
            OutboxCommandKind.RaidScanRecorded,
            OutboxPayload.FromTypedJson(ScanPayload.From(scan.RaidId, scan.Result))),
        RaidHistoryCommand.SaleRecorded sale => new EncodedCommand(
            sale.RaidId,
            OutboxCommandKind.RaidSaleRecorded,
            OutboxPayload.FromTypedJson(SalePayload.From(sale.RaidId, sale.Sale))),
        RaidHistoryCommand.QuestRecorded quest => new EncodedCommand(
            quest.RaidId,
            OutboxCommandKind.RaidQuestRecorded,
            OutboxPayload.FromTypedJson(QuestPayload.From(quest.RaidId, quest.Quest))),
        RaidHistoryCommand.PositionRecorded position => new EncodedCommand(
            position.RaidId,
            OutboxCommandKind.RaidPositionRecorded,
            OutboxPayload.FromTypedJson(PositionPayload.From(position.RaidId, position.Position))),
        RaidHistoryCommand.Ended ended => new EncodedCommand(
            ended.RaidId,
            OutboxCommandKind.RaidEnded,
            OutboxPayload.FromTypedJson(new EndedPayload(ended.RaidId, ended.EndUtc, Text(ended.Outcome), Text(ended.Notes))
            {
                StartUtc = ended.RebasedStartUtc,
            })),
        _ => throw new ArgumentException("The raid history command is not part of the closed outbox contract.", nameof(command)),
    };

    private static RaidHistoryCommand Decode(OutboxItem item, OperationId operationId) => item.Command switch
    {
        OutboxCommandKind.RaidStarted => item.Payload.ReadTypedJson<StartedPayload>().ToCommand(),
        OutboxCommandKind.RaidStateRecorded => item.Payload.ReadTypedJson<StatePayload>().ToCommand(),
        OutboxCommandKind.RaidExtractsRecorded => item.Payload.ReadTypedJson<ExtractsPayload>().ToCommand(),
        OutboxCommandKind.RaidScanRecorded => item.Payload.ReadTypedJson<ScanPayload>().ToCommand(),
        OutboxCommandKind.RaidSaleRecorded => item.Payload.ReadTypedJson<SalePayload>().ToCommand(),
        OutboxCommandKind.RaidQuestRecorded => item.Payload.ReadTypedJson<QuestPayload>().ToCommand(),
        OutboxCommandKind.RaidPositionRecorded => item.Payload.ReadTypedJson<PositionPayload>().ToCommand(operationId),
        OutboxCommandKind.RaidEnded => item.Payload.ReadTypedJson<EndedPayload>().ToCommand(),
        _ => throw new RuntimeFaultException(new(
            RuntimeFailureKind.Unsupported,
            new("outbox-command-unsupported"),
            RuntimeRecoveryAction.Upgrade,
            new($"operation:{operationId}"),
            item.CreatedUtc)),
    };

    /// <summary>Free text as a command may carry it: never something named like a screenshot.</summary>
    /// <remarks>
    /// No producer writes one today. This is the boundary that keeps it that way, because a
    /// screenshot's name is its coordinates.
    /// </remarks>
    private static string? Text(string? value)
    {
        if (value is null)
        {
            return null;
        }

        foreach (var marker in ScreenshotNameMarkers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Raid history text cannot carry a screenshot filename.", nameof(value));
            }
        }

        return value;
    }

    private static string RequiredText(string? value) =>
        Text(value ?? throw new ArgumentNullException(nameof(value)))!;

    private static TEnum Defined<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));

    private readonly record struct EncodedCommand(Guid RaidId, OutboxCommandKind Kind, OutboxPayload Payload);

    private sealed record StartedPayload(
        Guid RaidId,
        Guid ProfileId,
        string? MapId,
        string Mode,
        DateTimeOffset? StartedUtc,
        DateTimeOffset? EndedUtc,
        string? Outcome,
        string? Notes)
    {
        public static StartedPayload From(RaidHistoryEntry raid) => new(
            raid.Id,
            raid.ProfileId,
            Text(raid.MapId),
            RequiredText(raid.Mode),
            raid.StartedUtc,
            raid.EndedUtc,
            Text(raid.Outcome),
            Text(raid.Notes));

        public RaidHistoryCommand ToCommand() =>
            RaidHistoryCommand.StartRaid(new(RaidId, ProfileId, MapId, Mode, StartedUtc, EndedUtc, Outcome, Notes));
    }

    private sealed record StatePayload(
        Guid RaidId,
        RaidEvidenceKind Kind,
        DateTimeOffset ObservedUtc,
        string? MapId,
        RaidLifecycleState? SuggestedState,
        double ConfidenceValue,
        string Summary,
        string? Side,
        bool StartsNewRaid,
        string? EventId,
        bool ResumesSession,
        string? SideBasis,
        double? LoadSeconds = null)
    {
        public static StatePayload From(Guid raidId, RaidEvidence evidence) => new(
            raidId,
            Defined(evidence.Kind),
            evidence.ObservedUtc,
            Text(evidence.MapId),
            evidence.SuggestedState is { } suggested ? Defined(suggested) : (RaidLifecycleState?)null,
            evidence.Confidence.Value,
            RequiredText(evidence.Summary),
            Text(evidence.Side),
            evidence.StartsNewRaid,
            Text(evidence.EventId),
            evidence.ResumesSession,
            Text(evidence.SideBasis),
            evidence.LoadSeconds is { } load and > 0 and < 86400 ? load : null);

        public RaidHistoryCommand ToCommand() => RaidHistoryCommand.RecordState(
            RaidId,
            new RaidEvidence(
                Defined(Kind),
                ObservedUtc,
                MapId,
                SuggestedState is { } suggested ? Defined(suggested) : (RaidLifecycleState?)null,
                new Confidence(ConfidenceValue),
                Summary)
            {
                Side = Side,
                StartsNewRaid = StartsNewRaid,
                EventId = EventId,
                ResumesSession = ResumesSession,
                SideBasis = SideBasis,
                LoadSeconds = LoadSeconds,
            });
    }

    private sealed record ExtractPayload(string ExtractId, string Name, double ConfidenceValue, string Source);

    private sealed record ExtractsPayload(Guid RaidId, DateTimeOffset ObservedUtc, ExtractPayload[] Extracts)
    {
        public static ExtractsPayload From(Guid raidId, DateTimeOffset observedUtc, IReadOnlyList<ActiveExtract> extracts) => new(
            raidId,
            observedUtc,
            [.. extracts.Select(extract => new ExtractPayload(
                RequiredText(extract.ExtractId),
                RequiredText(extract.Name),
                extract.Confidence.Value,
                RequiredText(extract.Source)))]);

        public RaidHistoryCommand ToCommand() => RaidHistoryCommand.RecordExtracts(
            RaidId,
            ObservedUtc,
            [.. Extracts.Select(extract => new ActiveExtract(
                extract.ExtractId,
                extract.Name,
                new Confidence(extract.ConfidenceValue),
                extract.Source))]);
    }

    private sealed record ScanPayload(
        Guid RaidId,
        bool IsAvailable,
        bool Succeeded,
        string? CanonicalItemId,
        string? ItemName,
        long? ValueRoubles,
        long? ValuePerSlotRoubles,
        string? Recommendation,
        double ConfidenceValue,
        DateTimeOffset ObservedUtc,
        string Source,
        string Detail)
    {
        public static ScanPayload From(Guid raidId, ScanExecutionResult result) => new(
            raidId,
            result.IsAvailable,
            result.Succeeded,
            Text(result.CanonicalItemId),
            Text(result.ItemName),
            result.ValueRoubles,
            result.ValuePerSlotRoubles,
            Text(result.Recommendation),
            result.Confidence.Value,
            result.ObservedUtc,
            RequiredText(result.Source),
            RequiredText(result.Detail));

        public RaidHistoryCommand ToCommand() => RaidHistoryCommand.RecordScan(
            RaidId,
            new ScanExecutionResult(
                IsAvailable,
                Succeeded,
                CanonicalItemId,
                ItemName,
                ValueRoubles,
                ValuePerSlotRoubles,
                Recommendation,
                new Confidence(ConfidenceValue),
                ObservedUtc,
                Source,
                Detail));
    }

    private sealed record SalePayload(
        Guid RaidId,
        string OfferId,
        string? HandbookItemId,
        int Count,
        DateTimeOffset ObservedUtc)
    {
        public static SalePayload From(Guid raidId, FleaSaleObservation sale) => new(
            raidId,
            RequiredText(sale.OfferId),
            Text(sale.HandbookItemId),
            sale.Count,
            sale.ObservedUtc);

        public RaidHistoryCommand ToCommand() => RaidHistoryCommand.RecordSale(
            RaidId,
            new FleaSaleObservation(OfferId, HandbookItemId, Count, ObservedUtc));
    }

    private sealed record QuestPayload(
        Guid RaidId,
        string EventId,
        string TaskId,
        RecordedTaskState State,
        DateTimeOffset ObservedUtc)
    {
        public static QuestPayload From(Guid raidId, QuestStatusObservation quest) => new(
            raidId,
            RequiredText(quest.EventId),
            RequiredText(quest.TaskId),
            Defined(quest.State),
            quest.ObservedUtc);

        public RaidHistoryCommand ToCommand() => RaidHistoryCommand.RecordQuest(
            RaidId,
            new QuestStatusObservation(EventId, TaskId, Defined(State), ObservedUtc));
    }

    /// <summary>A position without the screenshot it was read from.</summary>
    /// <remarks>
    /// The filename is the coordinates and the time over again, and the stored trail needs
    /// neither twice. Delivery writes a stable per-command token in its place, which keeps the
    /// row readable as a trail point and distinct from its neighbours.
    /// </remarks>
    private sealed record PositionPayload(
        Guid RaidId,
        DateTimeOffset TimestampUtc,
        double X,
        double Y,
        double Z,
        double OrientationX,
        double OrientationY,
        double OrientationZ,
        double OrientationW,
        double HeadingDegrees,
        TimeSpan? InGameTime,
        int? DuplicateIndex)
    {
        public static PositionPayload From(Guid raidId, ScreenshotPosition position) => new(
            raidId,
            position.Timestamp.ToUniversalTime(),
            position.Position.X,
            position.Position.Y,
            position.Position.Z,
            position.Orientation.X,
            position.Orientation.Y,
            position.Orientation.Z,
            position.Orientation.W,
            position.HeadingDegrees,
            position.InGameTime,
            position.DuplicateIndex);

        public RaidHistoryCommand ToCommand(OperationId operationId) => RaidHistoryCommand.RecordPosition(
            RaidId,
            new ScreenshotPosition(
                TimestampUtc,
                new WorldPosition(X, Y, Z),
                new QuaternionOrientation(OrientationX, OrientationY, OrientationZ, OrientationW),
                HeadingDegrees,
                InGameTime,
                DuplicateIndex,
                $"recorded-position-{operationId.Value:N}"));
    }

    private sealed record EndedPayload(Guid RaidId, DateTimeOffset EndUtc, string? Outcome, string? Notes)
    {
        /// <summary>[#891] Absent from every payload written before it, which then moves nothing.</summary>
        public DateTimeOffset? StartUtc { get; init; }

        public RaidHistoryCommand ToCommand() => RaidHistoryCommand.EndRaid(RaidId, EndUtc, Outcome, Notes, StartUtc);
    }

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

            RaidHistoryCommand command;
            try
            {
                command = Decode(item, context.OperationId);
            }
            catch (Exception exception) when (
                exception is JsonException or ArgumentException or InvalidDataException or NotSupportedException)
            {
                // A payload the closed codec cannot read will never become readable by retrying.
                throw new RuntimeFaultException(new(
                    RuntimeFailureKind.Validation,
                    new("outbox-payload-invalid"),
                    RuntimeRecoveryAction.None,
                    new($"operation:{context.OperationId}"),
                    item.CreatedUtc));
            }

            var expectedAggregateId = new OutboxAggregateId($"raid:{command.RaidId:N}");
            if (item.AggregateId != expectedAggregateId)
            {
                // The durable row is the sequencing authority. Never let a corrupted row lease
                // under aggregate A while its payload performs target I/O against raid B.
                throw new RuntimeFaultException(new(
                    RuntimeFailureKind.Validation,
                    new("outbox-aggregate-mismatch"),
                    RuntimeRecoveryAction.None,
                    new($"operation:{context.OperationId}"),
                    item.CreatedUtc));
            }

            if (inner is IRaidHistoryOperationStore operationStore)
            {
                await operationStore.ApplyOnceAsync(
                    context.OperationId,
                    item.Command,
                    command.RaidId,
                    token => WriteAsync(command, context.OperationId, token),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteAsync(command, context.OperationId, cancellationToken).ConfigureAwait(false);

            async Task WriteAsync(
                RaidHistoryCommand decoded,
                OperationId operationId,
                CancellationToken token)
            {
                if (decoded is RaidHistoryCommand.Started started)
                {
                    var id = await inner.StartAsync(started.Raid, token).ConfigureAwait(false);
                    if (id != started.Raid.Id)
                    {
                        throw new RuntimeFaultException(new(
                            RuntimeFailureKind.Conflict,
                            new("raid-start-id-conflict"),
                            RuntimeRecoveryAction.ResolveConflict,
                            new($"operation:{operationId}"),
                            item.CreatedUtc));
                    }

                    return;
                }

                await decoded.WriteAsync(inner, token).ConfigureAwait(false);
            }
        }
    }
}
