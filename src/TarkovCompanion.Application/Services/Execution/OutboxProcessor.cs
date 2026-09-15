namespace TarkovCompanion.Application.Services.Execution;

public sealed record OutboxProcessResult(
    int Leased,
    int Completed,
    int Retrying,
    int DeadLettered,
    int LostLeaseRaces);

/// <summary>Runs one deterministic, bounded at-least-once delivery batch.</summary>
/// <remarks>
/// A handler is deliberately started away from the pump continuation: a synchronous prefix must
/// not prevent its attempt deadline from arming or hold unrelated aggregate heads hostage. Once
/// an attempt times out, its lease and cancellation source remain owned here until that exact
/// handler returns. Retrying earlier would let the same aggregate overtake a late side effect.
/// </remarks>
public sealed class OutboxProcessor(
    IOutboxStore store,
    IOutboxCommandHandler handler,
    TimeProvider timeProvider,
    IRetryJitter? jitter = null)
{
    private readonly IOutboxStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IOutboxCommandHandler _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IRetryJitter _jitter = jitter ?? new DeterministicRetryJitter();

    public async Task<OutboxProcessResult> ProcessBatchAsync(
        int maximumCount,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        await _store.RecoverExpiredLeasesAsync(_timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var leased = await _store.LeaseNextAsync(
                _timeProvider.GetUtcNow(),
                leaseDuration,
                maximumCount,
                cancellationToken)
            .ConfigureAwait(false);

        // LeaseNextAsync returns at most one ordered head per aggregate. Running those independent
        // heads together preserves aggregate order while one hostile synchronous handler cannot
        // serialize every other aggregate in this batch.
        var outcomes = await Task.WhenAll(leased.Select(stored =>
                ProcessOneAsync(stored, leaseDuration, cancellationToken)))
            .ConfigureAwait(false);
        return new(
            leased.Length,
            outcomes.Sum(outcome => outcome.Completed),
            outcomes.Sum(outcome => outcome.Retrying),
            outcomes.Sum(outcome => outcome.DeadLettered),
            outcomes.Sum(outcome => outcome.LostLeaseRaces));
    }

    private async Task<OutboxProcessResult> ProcessOneAsync(
        OutboxStoredItem stored,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var item = stored.Item;
        var leaseToken = stored.LeaseToken
            ?? throw new InvalidOperationException("A processing outbox item must have a lease token.");
        using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var handling = StartHandling(item, stored.AttemptCount, attemptCancellation.Token);
        RuntimeFault? fault = null;

        try
        {
            try
            {
                await handling
                    .WaitAsync(item.AttemptPolicy.AttemptTimeout, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                TryCancel(attemptCancellation);
                await KeepLeaseAndObserveAsync(handling, item, leaseToken, leaseDuration).ConfigureAwait(false);
                fault = RuntimeFault.FromException(
                    exception,
                    _timeProvider,
                    new($"operation:{item.OperationId}"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancellation may have interrupted the wait before the handler saw its linked
                // token. Keep both ownership objects alive until it has actually stopped.
                TryCancel(attemptCancellation);
                await KeepLeaseAndObserveAsync(handling, item, leaseToken, leaseDuration).ConfigureAwait(false);
                throw;
            }
            catch (RuntimeFaultException exception)
            {
                fault = exception.Fault;
            }
            catch (Exception exception)
            {
                fault = RuntimeFault.FromException(
                    exception,
                    _timeProvider,
                    new($"operation:{item.OperationId}"));
            }

            if (fault is null)
            {
                // An acknowledgement failure is a store/pump failure, never a handler failure.
                // In particular, do not call RetryAsync or DeadLetterAsync after the handler may
                // already have produced its side effect.
                if (await _store.CompleteAsync(
                        item.OperationId,
                        leaseToken,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return new(0, 1, 0, 0, 0);
                }

                return new(0, 0, 0, 0, 1);
            }

            return await SettleHandlerFaultAsync(item, stored.AttemptCount, leaseToken, fault, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Every exceptional path above waits for an unfinished handler before arriving here.
            // Keeping the source alive is required because handlers can retain and observe it.
            if (!handling.IsCompleted)
            {
                await KeepLeaseAndObserveAsync(handling, item, leaseToken, leaseDuration).ConfigureAwait(false);
            }
        }
    }

    private Task StartHandling(OutboxItem item, int attempt, CancellationToken attemptCancellation) =>
        Task.Factory.StartNew(
                () => _handler.HandleAsync(
                        item,
                        new(
                            item.OperationId,
                            item.IdempotencyKey,
                            item.CorrelationId,
                            item.FeatureId,
                            attempt),
                        attemptCancellation)
                    ?? throw new InvalidOperationException("The outbox handler returned no task."),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default)
            .Unwrap();

    private async Task KeepLeaseAndObserveAsync(
        Task handling,
        OutboxItem item,
        OutboxLeaseToken leaseToken,
        TimeSpan leaseDuration)
    {
        using var leaseLifetime = new CancellationTokenSource();
        var renewal = KeepLeaseUntilSettledAsync(handling, item.OperationId, leaseToken, leaseDuration, leaseLifetime.Token);
        await ObserveAsync(handling).ConfigureAwait(false);
        TryCancel(leaseLifetime);
        if (!await renewal.ConfigureAwait(false))
        {
            throw new InvalidOperationException("The outbox lost a timed-out handler lease before the handler returned.");
        }
    }

    private async Task<bool> KeepLeaseUntilSettledAsync(
        Task handling,
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var cadence = TimeSpan.FromTicks(Math.Max(1, leaseDuration.Ticks / 2));
        while (!handling.IsCompleted)
        {
            if (!await _store.RenewLeaseAsync(
                    operationId,
                    leaseToken,
                    _timeProvider.GetUtcNow(),
                    leaseDuration,
                    CancellationToken.None)
                .ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                await Task.Delay(cadence, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return true;
            }
        }

        return true;
    }

    private async Task<OutboxProcessResult> SettleHandlerFaultAsync(
        OutboxItem item,
        int attemptCount,
        OutboxLeaseToken leaseToken,
        RuntimeFault fault,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var mayRetry = fault.IsRetryable
            && attemptCount < item.AttemptPolicy.MaxAttempts
            && now < item.ExpiresUtc;
        if (mayRetry)
        {
            var retryAt = AddBounded(now, RetryDelay(item, attemptCount));
            if (retryAt < item.ExpiresUtc
                && await _store.RetryAsync(item.OperationId, leaseToken, retryAt, fault, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new(0, 0, 1, 0, 0);
            }

            if (retryAt < item.ExpiresUtc)
            {
                return new(0, 0, 0, 0, 1);
            }
        }

        return await _store.DeadLetterAsync(item.OperationId, leaseToken, fault, now, cancellationToken)
            .ConfigureAwait(false)
            ? new(0, 0, 0, 1, 0)
            : new(0, 0, 0, 0, 1);
    }

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

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException)
        {
            // Handler cancellation callbacks are untrusted. The handler task is still observed.
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
            // A late handler fault belongs to the timed-out attempt. It must be observed, but it
            // cannot replace the timeout fault or trigger a second delivery decision.
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
}
