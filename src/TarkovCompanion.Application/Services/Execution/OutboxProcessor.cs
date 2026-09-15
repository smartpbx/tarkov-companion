namespace TarkovCompanion.Application.Services.Execution;

public sealed record OutboxProcessResult(
    int Leased,
    int Completed,
    int Retrying,
    int DeadLettered,
    int LostLeaseRaces)
{
    /// <summary>Handlers still owned after reporting their attempt deadline.</summary>
    public int TimedOut { get; init; }

    /// <summary>Successful handlers whose store acknowledgement is being retried.</summary>
    public int AcknowledgementsPending { get; init; }

    /// <summary>
    /// Successful or possibly successful handlers whose lease ownership could not be proven.
    /// A durable target operation ledger must reconcile these before replay.
    /// </summary>
    public int AcknowledgementsUnknown { get; init; }

    /// <summary>All handler, cancellation-callback, and acknowledgement work still owned here.</summary>
    public int InFlight { get; init; }

    /// <summary>The highest-priority safe delivery fault visible during this pass.</summary>
    public RuntimeFault? LastFault { get; init; }
}

/// <summary>Runs deterministic, bounded, at-least-once delivery batches.</summary>
/// <remarks>
/// A batch deadline reports promptly; it never abandons the handler. The processor retains a
/// bounded operation-keyed owner that observes the exact invocation, renews its lease, and fences
/// that operation from an in-process replay until the invocation settles. Handler success enters
/// a separate acknowledgement phase: store acknowledgement is retried with the same lease and
/// delivery token, never by invoking the handler again. An unknowable acknowledgement leaves a
/// bounded lightweight fence after active work settles; #270 supplies its durable counterpart.
/// </remarks>
public sealed class OutboxProcessor : IAsyncDisposable
{
    public const int DefaultMaximumConcurrentAttempts = 32;
    public const int MaximumConcurrentAttemptsLimit = 1024;

    private static readonly TimeSpan MaximumAcknowledgementRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumAcknowledgementRetryDelay = TimeSpan.FromMilliseconds(10);
    private readonly IOutboxStore _store;
    private readonly IOutboxCommandHandler _handler;
    private readonly TimeProvider _timeProvider;
    private readonly IRetryJitter _jitter;
    private readonly Action? _progress;
    private readonly int _maximumConcurrentAttempts;
    private readonly SemaphoreSlim _batchGate = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<OperationId, OwnedDelivery> _owned = [];
    private readonly Dictionary<OperationId, RuntimeFault> _fences = [];
    private readonly Queue<OutboxProcessResult> _settledReports = [];
    private readonly TaskCompletionSource _stopCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeBatchCalls;
    private bool _resourcesDisposed;
    private bool _stopping;

    public OutboxProcessor(
        IOutboxStore store,
        IOutboxCommandHandler handler,
        TimeProvider timeProvider,
        IRetryJitter? jitter = null,
        int maximumConcurrentAttempts = DefaultMaximumConcurrentAttempts,
        Action? progress = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _jitter = jitter ?? new DeterministicRetryJitter();
        _progress = progress;
        if (maximumConcurrentAttempts is < 1 or > MaximumConcurrentAttemptsLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrentAttempts));
        }

        _maximumConcurrentAttempts = maximumConcurrentAttempts;
    }

    public int InFlightCount
    {
        get
        {
            lock (_gate)
            {
                return ActiveOwnerCountUnsafe();
            }
        }
    }

    /// <summary>
    /// Completes after stop was requested and every admitted batch and retained owner settled.
    /// </summary>
    public Task StopCompletion => _stopCompletion.Task;

    /// <summary>
    /// Releases a local acknowledgement fence after the store has durably recorded an operator's
    /// retry or terminal-resolution decision. Active work is still observed before removal.
    /// </summary>
    internal void NotifyReconciled(OperationId operationId)
    {
        if (!operationId.IsDefined)
        {
            throw new ArgumentException("An operation id is required.", nameof(operationId));
        }

        lock (_gate)
        {
            if (_fences.Remove(operationId))
            {
                TryCompleteStopUnsafe();
                return;
            }

            if (!_owned.TryGetValue(operationId, out var delivery))
            {
                return;
            }

            delivery.RequestFenceResolution();
        }
    }

    public async Task<OutboxProcessResult> ProcessBatchAsync(
        int maximumCount,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > MaximumConcurrentAttemptsLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        if (leaseDuration <= TimeSpan.Zero || leaseDuration > OperationPolicy.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _activeBatchCalls++;
        }

        var enteredBatchGate = false;
        var started = new List<OwnedDelivery>();
        try
        {
            await _batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredBatchGate = true;
            OutboxProcessResult accumulated;
            int available;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                accumulated = DrainSettledReportsUnsafe();
                available = Math.Min(
                    _maximumConcurrentAttempts - ActiveOwnerCountUnsafe(),
                    MaximumConcurrentAttemptsLimit - _owned.Count - _fences.Count);
            }

            await _store.RecoverExpiredLeasesAsync(_timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            var remaining = Math.Min(maximumCount, Math.Max(0, available));
            var firstReports = new List<Task<OutboxProcessResult>>(remaining);
            var leasePasses = 0;
            while (remaining > 0 && leasePasses++ < MaximumConcurrentAttemptsLimit)
            {
                var leased = await _store.LeaseNextAsync(
                        _timeProvider.GetUtcNow(),
                        leaseDuration,
                        remaining,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (leased.IsEmpty)
                {
                    break;
                }

                accumulated = Merge(accumulated, new(leased.Length, 0, 0, 0, 0));
                var sawUnavailable = false;
                var stopObserved = false;
                foreach (var stored in leased)
                {
                    OwnedDelivery? delivery = null;
                    var duplicate = false;
                    var leaseUnavailable = stored.LeaseToken is null
                        || stored.LeaseExpiresUtc is not { } leaseExpiresUtc
                        || leaseExpiresUtc <= _timeProvider.GetUtcNow();
                    lock (_gate)
                    {
                        if (_stopping)
                        {
                            stopObserved = true;
                        }
                        else if (!leaseUnavailable
                            && (_owned.ContainsKey(stored.Item.OperationId)
                                || _fences.ContainsKey(stored.Item.OperationId)))
                        {
                            duplicate = true;
                        }
                        else if (!leaseUnavailable)
                        {
                            delivery = new(stored, leaseDuration);
                            _owned.Add(stored.Item.OperationId, delivery);
                        }
                    }

                    if (leaseUnavailable)
                    {
                        // A store call may return after the lease it acquired has already elapsed.
                        // No handler crossed the boundary, so this is a lost race rather than an
                        // uncertain side effect. A later recovery pass may issue a fresh token.
                        sawUnavailable = true;
                        accumulated = Merge(accumulated, LeaseUnavailableBeforeLaunch(stored.Item));
                        continue;
                    }

                    if (duplicate)
                    {
                        // A failed heartbeat can make the store offer an operation whose original
                        // invocation is still alive or locally fenced. Never invoke it twice. This
                        // lease remains store-owned for durable-ledger reconciliation, and another
                        // lease pass may still find unrelated aggregate heads.
                        sawUnavailable = true;
                        accumulated = Merge(accumulated, AcknowledgementUnknown(stored.Item));
                        continue;
                    }

                    if (delivery is null)
                    {
                        accumulated = Merge(accumulated, AcknowledgementUnknown(stored.Item));
                        continue;
                    }

                    remaining--;
                    started.Add(delivery);
                    firstReports.Add(delivery.FirstReport.Task);
                    StartOwnedDelivery(delivery);
                }

                if (stopObserved || !sawUnavailable)
                {
                    break;
                }
            }

            if (firstReports.Count > 0)
            {
                var reports = await Task.WhenAll(firstReports)
                    .WaitAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var report in reports)
                {
                    accumulated = Merge(accumulated, report);
                }
            }

            lock (_gate)
            {
                accumulated = Merge(accumulated, DrainSettledReportsUnsafe());
                return WithOwnedState(accumulated);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var delivery in started)
            {
                delivery.RequestHandlerCancellation(abandonAfterSettlement: true);
            }

            throw;
        }
        finally
        {
            if (enteredBatchGate)
            {
                _batchGate.Release();
            }

            lock (_gate)
            {
                _activeBatchCalls--;
                TryCompleteStopUnsafe();
            }
        }
    }

    /// <summary>
    /// Stops admission and requests every handler's cancellation without waiting for hostile
    /// callbacks or handlers that ignore cancellation. Their bounded owner records remain alive
    /// until the underlying work actually settles.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        OwnedDelivery[] active;
        lock (_gate)
        {
            if (_stopping)
            {
                return ValueTask.CompletedTask;
            }

            _stopping = true;
            _fences.Clear();
            active = [.. _owned.Values];
            TryCompleteStopUnsafe();
        }

        foreach (var delivery in active)
        {
            // Request cancellation even when StartOwnedDelivery has not yet assigned OwnerTask.
            // CancelAsync itself cannot run a hostile registration on this caller.
            delivery.RequestHandlerCancellation(abandonAfterSettlement: true);
        }

        return ValueTask.CompletedTask;
    }

    private void StartOwnedDelivery(OwnedDelivery delivery)
    {
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerTask = Task.Factory.StartNew(
                () => InvokeHandlerAfterLaunchAsync(delivery, launch.Task),
                delivery.HandlerCancellation.Token,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();
        delivery.HandlerTask = handlerTask;
        delivery.RenewalTask = RenewLeaseUntilTerminalAsync(delivery);
        delivery.OwnerTask = ObserveAsync(OwnDeliveryAsync(delivery));

        // The handler cannot cross this gate until its timeout and renewal owner exist.
        launch.TrySetResult();
    }

    private async Task InvokeHandlerAfterLaunchAsync(OwnedDelivery delivery, Task launch)
    {
        await launch.ConfigureAwait(false);
        if (delivery.LeaseLost.Task.IsCompleted || delivery.IsTerminal)
        {
            return;
        }

        if (delivery.LeaseExpiresUtc <= _timeProvider.GetUtcNow())
        {
            delivery.MarkLeaseLost(AcknowledgementUnknownFault(delivery.Stored.Item));
            return;
        }

        var item = delivery.Stored.Item;
        var handling = _handler.HandleAsync(
                item,
                new(
                    item.OperationId,
                    item.IdempotencyKey,
                    item.CorrelationId,
                    item.FeatureId,
                    delivery.Stored.AttemptCount),
                delivery.HandlerCancellation.Token)
            ?? throw new InvalidOperationException("The outbox handler returned no task.");
        await handling.ConfigureAwait(false);
    }

    private async Task OwnDeliveryAsync(OwnedDelivery delivery)
    {
        var item = delivery.Stored.Item;
        using var deadlineCancellation = new CancellationTokenSource();
        var deadline = Task.Delay(
            item.AttemptPolicy.AttemptTimeout,
            _timeProvider,
            deadlineCancellation.Token);
        try
        {
            var first = await Task.WhenAny(delivery.HandlerTask, delivery.LeaseLost.Task, deadline)
                .ConfigureAwait(false);
            if (delivery.LeaseLost.Task.IsCompleted)
            {
                PublishOutcome(delivery, AcknowledgementUnknown(item));
                delivery.RequestHandlerCancellation(abandonAfterSettlement: false);
                await ObserveAsync(delivery.HandlerTask).ConfigureAwait(false);
                return;
            }

            if (first == deadline && !delivery.HandlerTask.IsCompleted)
            {
                delivery.MarkTimedOut(TimeoutFault(item));
                PublishOutcome(delivery, new(0, 0, 0, 0, 0)
                {
                    TimedOut = 1,
                    InFlight = 1,
                    LastFault = delivery.VisibleFault,
                });
                delivery.RequestHandlerCancellation(abandonAfterSettlement: false);

                await Task.WhenAny(delivery.HandlerTask, delivery.LeaseLost.Task).ConfigureAwait(false);
                if (delivery.LeaseLost.Task.IsCompleted)
                {
                    PublishOutcome(delivery, AcknowledgementUnknown(item));
                    await ObserveAsync(delivery.HandlerTask).ConfigureAwait(false);
                    return;
                }
            }

            var handler = await ReadHandlerOutcomeAsync(delivery).ConfigureAwait(false);
            if (delivery.LeaseLost.Task.IsCompleted)
            {
                PublishOutcome(delivery, AcknowledgementUnknown(item));
                return;
            }

            if (handler.Succeeded)
            {
                await AcknowledgeSuccessAsync(delivery).ConfigureAwait(false);
                return;
            }

            if (delivery.AbandonAfterSettlement)
            {
                // Shutdown/caller cancellation leaves the still-owned row Processing. Once this
                // invocation is gone, normal expired-lease recovery may safely retry it.
                delivery.MarkTerminal();
                PublishOutcome(delivery, new(0, 0, 0, 0, 0)
                {
                    LastFault = handler.Fault,
                });
                return;
            }

            var fault = delivery.TimedOut
                ? TimeoutFault(item)
                : handler.Fault ?? RuntimeFault.FromException(
                    new InvalidOperationException("The handler failed without a classified fault."),
                    _timeProvider,
                    Reference(item));
            await SettleHandlerFaultAsync(delivery, fault).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Any unexpected processor-path failure leaves the handler's side effect uncertain.
            // Keep the same conservative fence as an explicit lease/acknowledgement loss and
            // observe the exact handler before allowing this owner to settle.
            delivery.MarkLeaseLost(AcknowledgementUnknownFault(item));
            PublishOutcome(delivery, AcknowledgementUnknown(item));
            delivery.RequestHandlerCancellation(abandonAfterSettlement: false);
            await ObserveAsync(delivery.HandlerTask).ConfigureAwait(false);
        }
        finally
        {
            var cancellingDeadline = deadlineCancellation.CancelAsync();
            await ObserveAsync(cancellingDeadline).ConfigureAwait(false);
            await ObserveAsync(deadline).ConfigureAwait(false);
            delivery.StopRenewal();
            await ObserveAsync(delivery.RenewalTask).ConfigureAwait(false);
            await delivery.SealObserveAndDisposeAsync().ConfigureAwait(false);
            var ownerRemoved = false;
            lock (_gate)
            {
                if (_owned.TryGetValue(item.OperationId, out var current)
                    && ReferenceEquals(current, delivery))
                {
                    if (!_stopping
                        && delivery.State == OwnedDeliveryState.AcknowledgementUnknown
                        && !delivery.FenceResolutionRequested)
                    {
                        // The invocation is fully observed, but successful delivery cannot be
                        // disproved. Keep a lightweight local fence so an expired store lease can
                        // never replay this exact operation in the same processor. #270 persists
                        // the equivalent state across processor and process lifetimes.
                        _owned.Remove(item.OperationId);
                        _fences[item.OperationId] = delivery.VisibleFault
                            ?? AcknowledgementUnknownFault(item);
                        ownerRemoved = true;
                    }
                    else
                    {
                        delivery.MarkTerminal();
                        _owned.Remove(item.OperationId);
                        TryCompleteStopUnsafe();
                        ownerRemoved = true;
                    }
                }
            }

            if (ownerRemoved)
            {
                NotifyProgress();
            }
        }
    }

    private async Task RenewLeaseUntilTerminalAsync(OwnedDelivery delivery)
    {
        var cadence = TimeSpan.FromTicks(Math.Max(1, delivery.LeaseDuration.Ticks / 2));
        try
        {
            while (!delivery.IsTerminal)
            {
                var renewIn = delivery.LeaseExpiresUtc - _timeProvider.GetUtcNow() - cadence;
                if (renewIn < TimeSpan.Zero)
                {
                    renewIn = TimeSpan.Zero;
                }

                await Task.Delay(renewIn, _timeProvider, delivery.RenewalCancellation.Token)
                    .ConfigureAwait(false);
                if (delivery.IsTerminal)
                {
                    return;
                }

                var item = delivery.Stored.Item;
                var renewedAt = default(DateTimeOffset);
                var result = await InvokeBeforeLeaseExpiryAsync(
                        delivery,
                        storeCancellation =>
                        {
                            renewedAt = _timeProvider.GetUtcNow();
                            return _store.RenewLeaseAsync(
                                item.OperationId,
                                delivery.LeaseToken,
                                renewedAt,
                                delivery.LeaseDuration,
                                storeCancellation);
                        },
                        terminalOnSuccess: false,
                        onSuccess: () => delivery.SetLeaseExpiry(
                            AddBounded(renewedAt, delivery.LeaseDuration)))
                    .ConfigureAwait(false);
                if (result.Exception is not null || result.TimedOut || result.Value != true)
                {
                    delivery.MarkLeaseLost(AcknowledgementUnknownFault(item));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (delivery.RenewalCancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            delivery.MarkLeaseLost(AcknowledgementUnknownFault(delivery.Stored.Item));
        }
    }

    private async Task AcknowledgeSuccessAsync(OwnedDelivery delivery)
    {
        var item = delivery.Stored.Item;
        var pendingReported = false;
        while (!delivery.LeaseLost.Task.IsCompleted)
        {
            var result = await InvokeBeforeLeaseExpiryAsync(
                    delivery,
                    storeCancellation => _store.CompleteAsync(
                        item.OperationId,
                        delivery.LeaseToken,
                        _timeProvider.GetUtcNow(),
                        storeCancellation),
                    terminalOnSuccess: true)
                .ConfigureAwait(false);
            if (result.Value == true)
            {
                PublishOutcome(delivery, new(0, 1, 0, 0, 0));
                return;
            }

            if (result.TimedOut || result.Value == false)
            {
                delivery.MarkLeaseLost(AcknowledgementUnknownFault(item));
                PublishOutcome(delivery, AcknowledgementUnknown(item));
                return;
            }

            if (delivery.LeaseLost.Task.IsCompleted || delivery.AbandonAfterSettlement)
            {
                delivery.MarkLeaseLost(AcknowledgementUnknownFault(item));
                PublishOutcome(delivery, AcknowledgementUnknown(item));
                return;
            }

            if (!pendingReported)
            {
                pendingReported = true;
                delivery.MarkAcknowledgementPending(AcknowledgementPendingFault(item));
                PublishOutcome(delivery, new(0, 0, 0, 0, 0)
                {
                    AcknowledgementsPending = 1,
                    InFlight = 1,
                    LastFault = delivery.VisibleFault,
                });
            }

            if (!await WaitForAcknowledgementRetryAsync(delivery).ConfigureAwait(false))
            {
                delivery.MarkLeaseLost(AcknowledgementUnknownFault(item));
                PublishOutcome(delivery, AcknowledgementUnknown(item));
                return;
            }
        }

        PublishOutcome(delivery, AcknowledgementUnknown(item));
    }

    private async Task SettleHandlerFaultAsync(OwnedDelivery delivery, RuntimeFault fault)
    {
        var item = delivery.Stored.Item;
        var now = _timeProvider.GetUtcNow();
        var mayRetry = fault.IsRetryable
            && delivery.Stored.AttemptCount < item.AttemptPolicy.MaxAttempts
            && now < item.ExpiresUtc;
        Func<CancellationToken, Task<bool>> mutation;
        OutboxProcessResult success;
        if (mayRetry)
        {
            var retryAt = AddBounded(now, RetryDelay(item, delivery.Stored.AttemptCount));
            if (retryAt < item.ExpiresUtc)
            {
                mutation = storeCancellation =>
                {
                    var retryingAt = _timeProvider.GetUtcNow();
                    var eligibleAt = retryAt < retryingAt ? retryingAt : retryAt;
                    return _store.RetryAsync(
                        item.OperationId,
                        delivery.LeaseToken,
                        retryingAt,
                        eligibleAt,
                        fault,
                        storeCancellation);
                };
                success = new(0, 0, 1, 0, 0);
            }
            else
            {
                mutation = DeadLetterMutation;
                success = new(0, 0, 0, 1, 0);
            }
        }
        else
        {
            mutation = DeadLetterMutation;
            success = new(0, 0, 0, 1, 0);
        }

        var result = await InvokeBeforeLeaseExpiryAsync(delivery, mutation, terminalOnSuccess: true)
            .ConfigureAwait(false);
        if (result.Value == true)
        {
            PublishOutcome(delivery, success);
            return;
        }

        if (result.TimedOut || result.Value == false)
        {
            delivery.MarkLeaseLost(AcknowledgementUnknownFault(item));
            PublishOutcome(delivery, AcknowledgementUnknown(item));
            return;
        }

        delivery.MarkTerminal();
        PublishOutcome(delivery, new(0, 0, 0, 0, 0)
        {
            InFlight = 1,
            LastFault = RuntimeFault.FromException(result.Exception!, _timeProvider, Reference(item)),
        });

        Task<bool> DeadLetterMutation(CancellationToken storeCancellation) => _store.DeadLetterAsync(
            item.OperationId,
            delivery.LeaseToken,
            fault,
            _timeProvider.GetUtcNow(),
            storeCancellation);
    }

    private async Task<LeaseMutationResult> InvokeBeforeLeaseExpiryAsync(
        OwnedDelivery delivery,
        Func<CancellationToken, Task<bool>> mutation,
        bool terminalOnSuccess,
        Action? onSuccess = null)
    {
        if (delivery.LeaseLost.Task.IsCompleted || delivery.IsTerminal)
        {
            return new(false, null, false);
        }

        var initialWindow = delivery.CaptureLeaseWindow();
        if (initialWindow.ExpiresUtc <= _timeProvider.GetUtcNow())
        {
            return new(null, null, true);
        }

        var storeCancellation = new CancellationTokenSource();
        var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeCall = Task.Factory.StartNew(
                () => InvokeStoreAfterLaunchAsync(
                    mutation,
                    launch.Task,
                    storeCancellation.Token),
                storeCancellation.Token,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();
        launch.TrySetResult();

        while (true)
        {
            var window = delivery.CaptureLeaseWindow();
            var remaining = window.ExpiresUtc - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                var cancellingStore = storeCancellation.CancelAsync();
                delivery.RetainForObservation(ObserveCancelledStoreCallAsync(
                    storeCall,
                    cancellingStore,
                    storeCancellation));
                return new(null, null, true);
            }

            using var expiryCancellation = new CancellationTokenSource();
            var expiry = Task.Delay(remaining, _timeProvider, expiryCancellation.Token);
            var first = await Task.WhenAny(
                    storeCall,
                    expiry,
                    window.Changed,
                    delivery.LeaseLost.Task,
                    delivery.Terminal.Task)
                .ConfigureAwait(false);

            var cancellingExpiry = expiryCancellation.CancelAsync();
            await ObserveAsync(cancellingExpiry).ConfigureAwait(false);
            await ObserveAsync(expiry).ConfigureAwait(false);
            if (first == window.Changed)
            {
                continue;
            }

            if (first != storeCall)
            {
                var cancellingStore = storeCancellation.CancelAsync();
                delivery.RetainForObservation(ObserveCancelledStoreCallAsync(
                    storeCall,
                    cancellingStore,
                    storeCancellation));
                return first == expiry
                    ? new(null, null, true)
                    : new(false, null, false);
            }

            try
            {
                var value = await storeCall.ConfigureAwait(false);
                if (value)
                {
                    onSuccess?.Invoke();
                    if (terminalOnSuccess)
                    {
                        // The exact-token terminal CAS is authoritative. Marking it here wakes a
                        // concurrent heartbeat without allowing that heartbeat to downgrade the
                        // successful delivery into an ownership loss.
                        delivery.MarkTerminal();
                    }
                }

                return new(value, null, false);
            }
            catch (Exception exception)
            {
                return new(null, exception, false);
            }
            finally
            {
                storeCancellation.Dispose();
            }
        }
    }

    private static async Task<bool> InvokeStoreAfterLaunchAsync(
        Func<CancellationToken, Task<bool>> mutation,
        Task launch,
        CancellationToken cancellationToken)
    {
        await launch.ConfigureAwait(false);
        var task = mutation(cancellationToken)
            ?? throw new InvalidOperationException("The outbox store returned no task.");
        return await task.ConfigureAwait(false);
    }

    private async Task<bool> WaitForAcknowledgementRetryAsync(OwnedDelivery delivery)
    {
        var quarterLeaseTicks = Math.Max(1, delivery.LeaseDuration.Ticks / 4);
        var delay = TimeSpan.FromTicks(Math.Clamp(
            quarterLeaseTicks,
            MinimumAcknowledgementRetryDelay.Ticks,
            MaximumAcknowledgementRetryDelay.Ticks));
        using var delayCancellation = new CancellationTokenSource();
        var elapsed = Task.Delay(delay, _timeProvider, delayCancellation.Token);
        var first = await Task.WhenAny(
                elapsed,
                delivery.LeaseLost.Task,
                delivery.ShutdownRequested.Task)
            .ConfigureAwait(false);
        if (first == elapsed
            && !delivery.LeaseLost.Task.IsCompleted
            && !delivery.ShutdownRequested.Task.IsCompleted)
        {
            return true;
        }

        var cancellingDelay = delayCancellation.CancelAsync();
        await ObserveAsync(cancellingDelay).ConfigureAwait(false);
        await ObserveAsync(elapsed).ConfigureAwait(false);
        return false;
    }

    private async Task<HandlerOutcome> ReadHandlerOutcomeAsync(OwnedDelivery delivery)
    {
        try
        {
            await delivery.HandlerTask.ConfigureAwait(false);
            return new(true, null);
        }
        catch (RuntimeFaultException exception)
        {
            return new(false, exception.Fault);
        }
        catch (Exception exception)
        {
            return new(false, RuntimeFault.FromException(
                exception,
                _timeProvider,
                Reference(delivery.Stored.Item)));
        }
    }

    private void PublishOutcome(OwnedDelivery delivery, OutboxProcessResult outcome)
    {
        if (delivery.FirstReport.TrySetResult(outcome))
        {
            return;
        }

        lock (_gate)
        {
            _settledReports.Enqueue(outcome);
        }

        NotifyProgress();
    }

    private OutboxProcessResult DrainSettledReportsUnsafe()
    {
        var result = new OutboxProcessResult(0, 0, 0, 0, 0);
        while (_settledReports.TryDequeue(out var settled))
        {
            result = Merge(result, settled);
        }

        return result;
    }

    private int ActiveOwnerCountUnsafe() => _owned.Count;

    private void TryCompleteStopUnsafe()
    {
        if (_stopping
            && _activeBatchCalls == 0
            && _owned.Count == 0
            && _fences.Count == 0
            && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _batchGate.Dispose();
            _stopCompletion.TrySetResult();
        }
    }

    private OutboxProcessResult WithOwnedState(OutboxProcessResult result)
    {
        var timedOut = _owned.Values.Count(delivery => delivery.State == OwnedDeliveryState.TimedOut);
        var acknowledgementPending = _owned.Values.Count(
            delivery => delivery.State == OwnedDeliveryState.AcknowledgementPending);
        var acknowledgementUnknown = _owned.Values.Count(
            delivery => delivery.State == OwnedDeliveryState.AcknowledgementUnknown)
            + _fences.Count;
        var visible = _owned.Values
            .Select(delivery => delivery.VisibleFault)
            .Concat(_fences.Values)
            .OrderByDescending(fault => fault is null ? -1 : FaultPriority(fault))
            .FirstOrDefault(fault => fault is not null);
        return result with
        {
            TimedOut = Math.Max(result.TimedOut, timedOut),
            AcknowledgementsPending = Math.Max(result.AcknowledgementsPending, acknowledgementPending),
            AcknowledgementsUnknown = Math.Max(result.AcknowledgementsUnknown, acknowledgementUnknown),
            InFlight = ActiveOwnerCountUnsafe(),
            LastFault = HigherPriority(result.LastFault, visible),
        };
    }

    private static OutboxProcessResult Merge(OutboxProcessResult left, OutboxProcessResult right) => new(
        checked(left.Leased + right.Leased),
        checked(left.Completed + right.Completed),
        checked(left.Retrying + right.Retrying),
        checked(left.DeadLettered + right.DeadLettered),
        checked(left.LostLeaseRaces + right.LostLeaseRaces))
    {
        TimedOut = checked(left.TimedOut + right.TimedOut),
        AcknowledgementsPending = checked(left.AcknowledgementsPending + right.AcknowledgementsPending),
        AcknowledgementsUnknown = checked(left.AcknowledgementsUnknown + right.AcknowledgementsUnknown),
        InFlight = Math.Max(left.InFlight, right.InFlight),
        LastFault = HigherPriority(left.LastFault, right.LastFault),
    };

    private static RuntimeFault? HigherPriority(RuntimeFault? left, RuntimeFault? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return FaultPriority(right) > FaultPriority(left) ? right : left;
    }

    private static int FaultPriority(RuntimeFault fault) => fault.Code.Value switch
    {
        "outbox-acknowledgement-unknown" => 3,
        "outbox-acknowledgement-pending" => 2,
        "outbox-handler-timeout" => 1,
        _ => 0,
    };

    private OutboxProcessResult AcknowledgementUnknown(OutboxItem item) => new(0, 0, 0, 0, 1)
    {
        AcknowledgementsUnknown = 1,
        InFlight = 1,
        LastFault = AcknowledgementUnknownFault(item),
    };

    private OutboxProcessResult LeaseUnavailableBeforeLaunch(OutboxItem item) => new(0, 0, 0, 0, 1)
    {
        LastFault = new(
            RuntimeFailureKind.Transient,
            new("outbox-lease-unavailable-before-launch"),
            RuntimeRecoveryAction.RetryAutomatically,
            Reference(item),
            _timeProvider.GetUtcNow()),
    };

    private RuntimeFault TimeoutFault(OutboxItem item) => new(
        RuntimeFailureKind.Timeout,
        new("outbox-handler-timeout"),
        RuntimeRecoveryAction.RetryAutomatically,
        Reference(item),
        _timeProvider.GetUtcNow());

    private RuntimeFault AcknowledgementPendingFault(OutboxItem item) => new(
        RuntimeFailureKind.Transient,
        new("outbox-acknowledgement-pending"),
        RuntimeRecoveryAction.RetryAutomatically,
        Reference(item),
        _timeProvider.GetUtcNow());

    private RuntimeFault AcknowledgementUnknownFault(OutboxItem item) => new(
        RuntimeFailureKind.Unexpected,
        new("outbox-acknowledgement-unknown"),
        RuntimeRecoveryAction.RetryManually,
        Reference(item),
        _timeProvider.GetUtcNow());

    private static DiagnosticReference Reference(OutboxItem item) => new($"operation:{item.OperationId}");

    private TimeSpan RetryDelay(OutboxItem item, int completedAttempt)
    {
        var multiplier = Math.Pow(item.AttemptPolicy.BackoffFactor, completedAttempt - 1);
        var ticks = Math.Min(
            item.AttemptPolicy.MaxRetryDelay.Ticks,
            item.AttemptPolicy.InitialRetryDelay.Ticks * multiplier);
        var baseDelay = TimeSpan.FromTicks((long)Math.Round(ticks, MidpointRounding.AwayFromZero));
        var jittered = _jitter.Apply(new(
            item.OperationId,
            completedAttempt + 1,
            baseDelay,
            0.2));
        if (jittered < TimeSpan.Zero || jittered > OperationPolicy.MaximumDuration)
        {
            throw new InvalidOperationException("The outbox retry jitter returned an out-of-policy delay.");
        }

        return jittered > item.AttemptPolicy.MaxRetryDelay
            ? item.AttemptPolicy.MaxRetryDelay
            : jittered;
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ownership requires observation, but late callback/store/handler details never cross
            // the sanitized runtime boundary or replace the already-published delivery result.
        }
    }

    private void NotifyProgress()
    {
        try
        {
            _progress?.Invoke();
        }
        catch (Exception)
        {
            // Progress is only a wake hint. A consumer cannot take delivery ownership down.
        }
    }

    private static async Task ObserveCancelledStoreCallAsync(
        Task storeCall,
        Task cancellation,
        CancellationTokenSource source)
    {
        try
        {
            await ObserveAsync(Task.WhenAll(storeCall, cancellation)).ConfigureAwait(false);
        }
        finally
        {
            source.Dispose();
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

    private readonly record struct HandlerOutcome(bool Succeeded, RuntimeFault? Fault);

    private readonly record struct LeaseMutationResult(bool? Value, Exception? Exception, bool TimedOut);

    private readonly record struct LeaseWindow(DateTimeOffset ExpiresUtc, Task Changed);

    private enum OwnedDeliveryState
    {
        Running = 0,
        TimedOut = 1,
        AcknowledgementPending = 2,
        AcknowledgementUnknown = 3,
        Terminal = 4,
    }

    private sealed class OwnedDelivery(OutboxStoredItem stored, TimeSpan leaseDuration)
    {
        private readonly object _gate = new();
        private readonly List<Task> _retainedTasks = [];
        private Task? _cancellationTask;
        private Task? _renewalStopTask;
        private DateTimeOffset _leaseExpiresUtc = stored.LeaseExpiresUtc
            ?? throw new InvalidOperationException("A processing outbox item must have a lease expiry.");
        private TaskCompletionSource _leaseChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private OwnedDeliveryState _state;
        private RuntimeFault? _visibleFault;
        private bool _abandonAfterSettlement;
        private bool _fenceResolutionRequested;
        private bool _resourcesDisposed;

        public OutboxStoredItem Stored { get; } = stored;
        public TimeSpan LeaseDuration { get; } = leaseDuration;
        public OutboxLeaseToken LeaseToken { get; } = stored.LeaseToken
            ?? throw new InvalidOperationException("A processing outbox item must have a lease token.");
        public CancellationTokenSource HandlerCancellation { get; } = new();
        public CancellationTokenSource RenewalCancellation { get; } = new();
        public TaskCompletionSource<OutboxProcessResult> FirstReport { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LeaseLost { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Terminal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ShutdownRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task HandlerTask { get; set; } = Task.CompletedTask;
        public Task RenewalTask { get; set; } = Task.CompletedTask;
        public Task OwnerTask { get; set; } = Task.CompletedTask;

        public DateTimeOffset LeaseExpiresUtc
        {
            get
            {
                lock (_gate)
                {
                    return _leaseExpiresUtc;
                }
            }
        }

        public OwnedDeliveryState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        public RuntimeFault? VisibleFault
        {
            get
            {
                lock (_gate)
                {
                    return _visibleFault;
                }
            }
        }

        public bool TimedOut => State == OwnedDeliveryState.TimedOut;

        public bool IsTerminal => State == OwnedDeliveryState.Terminal;

        public bool FenceResolutionRequested
        {
            get
            {
                lock (_gate)
                {
                    return _fenceResolutionRequested;
                }
            }
        }

        public bool AbandonAfterSettlement
        {
            get
            {
                lock (_gate)
                {
                    return _abandonAfterSettlement;
                }
            }
        }

        public void SetLeaseExpiry(DateTimeOffset leaseExpiresUtc)
        {
            TaskCompletionSource changed;
            lock (_gate)
            {
                _leaseExpiresUtc = leaseExpiresUtc;
                changed = _leaseChanged;
                _leaseChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            changed.TrySetResult();
        }

        public LeaseWindow CaptureLeaseWindow()
        {
            lock (_gate)
            {
                return new(_leaseExpiresUtc, _leaseChanged.Task);
            }
        }

        public void MarkTimedOut(RuntimeFault fault)
        {
            lock (_gate)
            {
                if (_state == OwnedDeliveryState.Running)
                {
                    _state = OwnedDeliveryState.TimedOut;
                    _visibleFault = fault;
                }
            }
        }

        public void MarkAcknowledgementPending(RuntimeFault fault)
        {
            lock (_gate)
            {
                if (_state is OwnedDeliveryState.Running or OwnedDeliveryState.TimedOut)
                {
                    _state = OwnedDeliveryState.AcknowledgementPending;
                    _visibleFault = fault;
                }
            }
        }

        public void MarkLeaseLost(RuntimeFault fault)
        {
            lock (_gate)
            {
                if (_state == OwnedDeliveryState.Terminal)
                {
                    return;
                }

                _state = OwnedDeliveryState.AcknowledgementUnknown;
                _visibleFault = fault;
            }

            LeaseLost.TrySetResult();
        }

        public void MarkTerminal()
        {
            var changed = false;
            lock (_gate)
            {
                if (_state == OwnedDeliveryState.Terminal)
                {
                    return;
                }

                _state = OwnedDeliveryState.Terminal;
                _renewalStopTask ??= RenewalCancellation.CancelAsync();
                changed = true;
            }

            if (changed)
            {
                Terminal.TrySetResult();
            }
        }

        public void RequestFenceResolution()
        {
            lock (_gate)
            {
                _fenceResolutionRequested = true;
            }
        }

        public void StopRenewal()
        {
            lock (_gate)
            {
                if (_resourcesDisposed)
                {
                    return;
                }

                _renewalStopTask ??= RenewalCancellation.CancelAsync();
            }
        }

        public void RequestHandlerCancellation(bool abandonAfterSettlement)
        {
            var signalShutdown = false;
            lock (_gate)
            {
                _abandonAfterSettlement |= abandonAfterSettlement;
                signalShutdown = abandonAfterSettlement;
                if (_resourcesDisposed)
                {
                    signalShutdown = false;
                }
                else
                {
                    _cancellationTask ??= HandlerCancellation.CancelAsync();
                }
            }

            if (signalShutdown)
            {
                ShutdownRequested.TrySetResult();
            }
        }

        public void RetainForObservation(Task task)
        {
            lock (_gate)
            {
                _retainedTasks.Add(task);
            }
        }

        public async Task SealObserveAndDisposeAsync()
        {
            Task[] tasks;
            Task? cancellation;
            Task? renewalStop;
            lock (_gate)
            {
                if (_resourcesDisposed)
                {
                    return;
                }

                // Seal cancellation registration and observation in one critical section. A
                // concurrent DisposeAsync can either contribute its cancellation task to this
                // snapshot or observe the sealed state; it cannot start an unowned callback in
                // between the snapshot and source disposal.
                _resourcesDisposed = true;
                tasks = [.. _retainedTasks];
                cancellation = _cancellationTask;
                renewalStop = _renewalStopTask;
            }

            foreach (var task in tasks)
            {
                await ObserveAsync(task).ConfigureAwait(false);
            }

            if (renewalStop is not null)
            {
                await ObserveAsync(renewalStop).ConfigureAwait(false);
            }

            if (cancellation is not null)
            {
                await ObserveAsync(cancellation).ConfigureAwait(false);
            }

            HandlerCancellation.Dispose();
            RenewalCancellation.Dispose();
        }
    }
}
