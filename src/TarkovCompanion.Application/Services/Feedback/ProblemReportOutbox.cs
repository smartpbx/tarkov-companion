using System.Security.Cryptography;
using System.Text;

namespace TarkovCompanion.Application.Services.Feedback;

/// <summary>What happened to one attempt to send a problem report.</summary>
public enum ReportDelivery
{
    /// <summary>The relay has it.</summary>
    Sent,

    /// <summary>The relay could not be reached; trying again later may work.</summary>
    Unreachable,

    /// <summary>The relay (or the settings) said no; trying again will not change that.</summary>
    Refused,
}

/// <summary>One send attempt's outcome and the sentence the player reads about it.</summary>
public sealed record ReportSendResult(ReportDelivery Delivery, string Message);

/// <summary>A consented report waiting for the relay to come back.</summary>
public sealed record QueuedProblemReport(
    string Key,
    string Text,
    DateTimeOffset QueuedUtc,
    int Attempts,
    DateTimeOffset NextAttemptUtc);

/// <summary>A report the relay took, remembered by key only so the same text is never sent twice.</summary>
public sealed record SentProblemReport(string Key, DateTimeOffset SentUtc);

public sealed record ProblemReportOutboxState(
    IReadOnlyList<QueuedProblemReport> Queued,
    IReadOnlyList<SentProblemReport> Sent)
{
    public static ProblemReportOutboxState Empty { get; } = new([], []);
}

/// <summary>Where the outbox keeps its queue between runs.</summary>
public interface IProblemReportOutboxStore
{
    Task<ProblemReportOutboxState> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ProblemReportOutboxState state, CancellationToken cancellationToken);
}

/// <summary>
/// Sends a problem report the player agreed to, and keeps it for later when the relay is not there (#314).
/// </summary>
/// <remarks>
/// A report is most often sent when something is wrong, and "the relay is unreachable" is one of those
/// things, so a failed send used to lose the report. Now an unreachable relay queues it and it is retried
/// with back-off. The bounds are the point: at most <see cref="MaximumAttempts"/> tries, at most
/// <see cref="MaximumQueued"/> reports, and nothing older than <see cref="Lifetime"/>, so a consent given
/// once does not turn into a report arriving a month later.
///
/// Never twice: a report is keyed by the hash of its text. The same text queued again joins the entry
/// already there, and a text the relay took is remembered (hash only, for the same seven days) so a
/// second press of Send says "already sent" instead of filing a duplicate. A refusal is not retried.
/// One gate serialises every send, so a retry pass and a player's press cannot both send one entry.
/// </remarks>
public sealed class ProblemReportOutbox
{
    public const int MaximumAttempts = 5;
    public const int MaximumQueued = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan FirstRetry = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan LongestRetry = TimeSpan.FromHours(6);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    private readonly IProblemReportOutboxStore _store;
    private readonly Func<string, CancellationToken, Task<ReportSendResult>> _send;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProblemReportOutboxState? _state;

    public ProblemReportOutbox(
        IProblemReportOutboxStore store,
        Func<string, CancellationToken, Task<ReportSendResult>> send,
        TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after the queue changes, from whichever thread changed it.</summary>
    public event Action? Changed;

    /// <summary>The reports still waiting, oldest first.</summary>
    public IReadOnlyList<QueuedProblemReport> Queued => _state?.Queued ?? [];

    /// <summary>The wait before retry number <paramref name="attempts"/>: 2, 4, 8, 16 minutes…, capped at six hours.</summary>
    public static TimeSpan Backoff(int attempts)
    {
        var minutes = FirstRetry.TotalMinutes * Math.Pow(2, Math.Max(0, attempts - 1));
        return minutes >= LongestRetry.TotalMinutes ? LongestRetry : TimeSpan.FromMinutes(minutes);
    }

    public static string KeyOf(string report) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(report)));

    /// <summary>Sends a report the player has read and agreed to; queues it when the relay is unreachable.</summary>
    public async Task<string> SendAsync(string report, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(report);
        var key = KeyOf(report);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = Prune(await LoadAsync(cancellationToken).ConfigureAwait(false));
            if (state.Sent.Any(sent => sent.Key == key))
            {
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
                return "Already sent · this exact report reached the relay.";
            }

            var existing = state.Queued.FirstOrDefault(queued => queued.Key == key);
            var result = await _send(report, cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            state = result.Delivery switch
            {
                ReportDelivery.Sent => Delivered(state, key, now),
                ReportDelivery.Refused => state with { Queued = [.. state.Queued.Where(queued => queued.Key != key)] },
                _ => Requeue(state, existing ?? new QueuedProblemReport(key, report, now, 0, now), now),
            };
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            if (result.Delivery != ReportDelivery.Unreachable)
            {
                return result.Message;
            }

            return state.Queued.FirstOrDefault(queued => queued.Key == key) is { } kept
                ? $"Offline · queued, retried automatically for up to {Lifetime.TotalDays:N0} days. {result.Message}"
                : result.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Tries every queued report whose back-off has passed; returns how many the relay took.</summary>
    public async Task<int> RetryDueAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = Prune(await LoadAsync(cancellationToken).ConfigureAwait(false));
            var delivered = 0;
            foreach (var entry in state.Queued.ToArray())
            {
                var now = _time.GetUtcNow();
                if (entry.NextAttemptUtc > now)
                {
                    continue;
                }

                var result = await _send(entry.Text, cancellationToken).ConfigureAwait(false);
                now = _time.GetUtcNow();
                state = result.Delivery switch
                {
                    ReportDelivery.Sent => Delivered(state, entry.Key, now),
                    ReportDelivery.Refused => state with { Queued = [.. state.Queued.Where(queued => queued.Key != entry.Key)] },
                    _ => Requeue(state, entry, now),
                };
                delivered += result.Delivery == ReportDelivery.Sent ? 1 : 0;

                // Saved after every send, so a crash between two sends cannot resend the first.
                await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }

            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            return delivered;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Retries due reports once a minute until <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RetryDueAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A store or setting that cannot be read this minute may be readable the next;
                // the loop must outlive it, or one bad minute ends every later retry.
            }

            try
            {
                await Task.Delay(CheckEvery, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static ProblemReportOutboxState Delivered(ProblemReportOutboxState state, string key, DateTimeOffset now) => new(
        [.. state.Queued.Where(queued => queued.Key != key)],
        [.. state.Sent.Where(sent => sent.Key != key), new SentProblemReport(key, now)]);

    /// <summary>Counts the failed attempt and schedules the next, or drops the entry when it has had its tries.</summary>
    private static ProblemReportOutboxState Requeue(ProblemReportOutboxState state, QueuedProblemReport entry, DateTimeOffset now)
    {
        var attempts = entry.Attempts + 1;
        var others = state.Queued.Where(queued => queued.Key != entry.Key).ToList();
        if (attempts >= MaximumAttempts || now - entry.QueuedUtc >= Lifetime)
        {
            return state with { Queued = others };
        }

        others.Add(entry with { Attempts = attempts, NextAttemptUtc = now + Backoff(attempts) });

        // Oldest dropped first when a new report would go over the bound.
        var bounded = others.OrderBy(queued => queued.QueuedUtc).ToList();
        while (bounded.Count > MaximumQueued)
        {
            bounded.RemoveAt(0);
        }

        return state with { Queued = bounded };
    }

    private ProblemReportOutboxState Prune(ProblemReportOutboxState state)
    {
        var now = _time.GetUtcNow();
        return new(
            [.. state.Queued.Where(queued => now - queued.QueuedUtc < Lifetime && queued.Attempts < MaximumAttempts)],
            [.. state.Sent.Where(sent => now - sent.SentUtc < Lifetime)]);
    }

    private async Task<ProblemReportOutboxState> LoadAsync(CancellationToken cancellationToken) =>
        _state ??= await _store.LoadAsync(cancellationToken).ConfigureAwait(false);

    private async Task SaveAsync(ProblemReportOutboxState state, CancellationToken cancellationToken)
    {
        var changed = !ReferenceEquals(_state, state) &&
                      (_state is null || !_state.Queued.SequenceEqual(state.Queued) || !_state.Sent.SequenceEqual(state.Sent));
        _state = state;
        if (changed)
        {
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke();
        }
    }
}
