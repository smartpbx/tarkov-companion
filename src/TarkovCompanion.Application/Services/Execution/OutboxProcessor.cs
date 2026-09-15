namespace TarkovCompanion.Application.Services.Execution;

public sealed record OutboxProcessResult(
    int Leased,
    int Completed,
    int Retrying,
    int DeadLettered,
    int LostLeaseRaces);

/// <summary>Runs one deterministic, bounded at-least-once delivery batch.</summary>
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
        var completed = 0;
        var retrying = 0;
        var deadLettered = 0;
        var lostLeaseRaces = 0;
        foreach (var stored in leased)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = stored.Item;
            var leaseToken = stored.LeaseToken
                ?? throw new InvalidOperationException("A processing outbox item must have a lease token.");
            RuntimeFault? fault = null;
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var handling = _handler.HandleAsync(
                        item,
                        new(
                            item.OperationId,
                            item.IdempotencyKey,
                            item.CorrelationId,
                            item.FeatureId,
                            stored.AttemptCount),
                        attemptCancellation.Token)
                    ?? throw new InvalidOperationException("The outbox handler returned no task.");
                await handling
                    .WaitAsync(item.AttemptPolicy.AttemptTimeout, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
                if (await _store.CompleteAsync(
                        item.OperationId,
                        leaseToken,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    completed++;
                }
                else
                {
                    lostLeaseRaces++;
                }

                continue;
            }
            catch (TimeoutException exception)
            {
                TryCancel(attemptCancellation);
                fault = RuntimeFault.FromException(
                    exception,
                    _timeProvider,
                    new($"operation:{item.OperationId}"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancelled explicitly before the attempt source is disposed on the way out:
                // relying on the token link lost the cancellation whenever this wait observed it
                // first, and a handler still running would never be told to stop.
                TryCancel(attemptCancellation);
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

            var now = _timeProvider.GetUtcNow();
            var mayRetry = fault.IsRetryable
                && stored.AttemptCount < item.AttemptPolicy.MaxAttempts
                && now < item.ExpiresUtc;
            if (mayRetry)
            {
                var retryAt = AddBounded(now, RetryDelay(item, stored.AttemptCount));
                if (retryAt < item.ExpiresUtc
                    && await _store.RetryAsync(
                            item.OperationId,
                            leaseToken,
                            retryAt,
                            fault,
                            cancellationToken)
                        .ConfigureAwait(false))
                {
                    retrying++;
                }
                else if (retryAt < item.ExpiresUtc)
                {
                    lostLeaseRaces++;
                }
                else if (await _store.DeadLetterAsync(
                             item.OperationId,
                             leaseToken,
                             fault,
                             now,
                             cancellationToken)
                         .ConfigureAwait(false))
                {
                    deadLettered++;
                }
                else
                {
                    lostLeaseRaces++;
                }
            }
            else if (await _store.DeadLetterAsync(
                         item.OperationId,
                         leaseToken,
                         fault,
                         now,
                         cancellationToken)
                     .ConfigureAwait(false))
            {
                deadLettered++;
            }
            else
            {
                lostLeaseRaces++;
            }
        }

        return new(leased.Length, completed, retrying, deadLettered, lostLeaseRaces);
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
            // A callback registered by the handler threw; the attempt's own fault reports it.
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
