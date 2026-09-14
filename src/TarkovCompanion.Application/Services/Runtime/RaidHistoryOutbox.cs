using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Runtime;

/// <summary>
/// Writes raid history behind a queue, so persistence can never take observation down.
/// </summary>
/// <remarks>
/// <para>
/// Every event was written inline on the thread that observed it. A screenshot is read, a
/// position is recorded, and the recording awaits a SQLite write — so a database busy with the
/// hourly catalog refresh stalls the watcher that is reading the game's log. The observation
/// side is the half that must never stop: a write that arrives late is a row with the right
/// timestamp, and an observation that never happens is gone.
/// </para>
/// <para>
/// One reader, so order is kept by construction. Start then events then End is the order they
/// were queued in and therefore the order they are written in, which matters because an event
/// references a raid row that has to exist.
/// </para>
/// <para>
/// Starting a raid is deliberately not queued. It returns the id everything else is keyed by,
/// so it cannot be deferred without inventing one — and it happens once per raid rather than
/// once per screenshot, which is the cost this exists to move off the hot path.
/// </para>
/// </remarks>
public sealed class RaidHistoryOutbox : IRaidHistoryService, IAsyncDisposable
{
    /// <summary>
    /// How many writes may wait.
    /// </summary>
    /// <remarks>
    /// A raid produces a few dozen: one per screenshot, one per scan, one per state change. A
    /// thousand is room for a long raid several times over while still being a bound rather
    /// than a memory leak with a queue in front of it.
    /// </remarks>
    private const int Capacity = 1000;

    /// <summary>How many times a busy database is retried before the write is given up on.</summary>
    /// <remarks>
    /// The waits double from 50 ms, so five attempts spans about a second and a half. The
    /// refresh transactions this contends with are shorter than that, and a write still failing
    /// after them is failing for a reason waiting will not fix.
    /// </remarks>
    private const int Attempts = 5;

    private readonly IRaidHistoryService _inner;
    private readonly ILogger<RaidHistoryOutbox>? _logger;
    private readonly Channel<Func<CancellationToken, Task>> _queue;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;
    private bool _reportedFull;
    private bool _reportedGaveUp;

    public RaidHistoryOutbox(IRaidHistoryService inner, ILogger<RaidHistoryOutbox>? logger = null)
    {
        _inner = inner;
        _logger = logger;
        _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true,
            // Waiting rather than dropping. A dropped write is a hole in a record that nothing
            // anywhere would report, and the queue only fills if the database has been busy for
            // longer than a raid — at which point one observation waiting is the smaller harm.
            FullMode = BoundedChannelFullMode.Wait,
        });
        _pump = Task.Run(PumpAsync);
    }

    public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken) =>
        _inner.StartAsync(raid, cancellationToken);

    public Task RecordEventAsync(
        Guid raidId,
        string type,
        DateTimeOffset timestampUtc,
        string payloadJson,
        CancellationToken cancellationToken) =>
        EnqueueAsync(
            token => _inner.RecordEventAsync(raidId, type, timestampUtc, payloadJson, token),
            $"{type} event for raid {raidId}",
            cancellationToken);

    public Task EndAsync(
        Guid raidId,
        DateTimeOffset endUtc,
        string? outcome,
        string? notes,
        CancellationToken cancellationToken) =>
        EnqueueAsync(
            token => _inner.EndAsync(raidId, endUtc, outcome, notes, token),
            $"end of raid {raidId}",
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

    /// <summary>
    /// Waits until everything queued so far has been written.
    /// </summary>
    /// <remarks>
    /// The honest cost of moving writes off the observation thread: a caller that writes and
    /// then immediately reads is now racing its own write. Almost nothing does that — the
    /// application writes as it observes and reads when somebody opens a page — but a test
    /// that asserts a row landed does, and so would a shutdown that wanted to be sure.
    ///
    /// A marker through the same queue rather than a flag. One reader means the marker cannot
    /// be reached until everything queued before it has been, which is the same property the
    /// ordering relies on rather than a second mechanism that could disagree with it.
    /// </remarks>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(
            _ =>
            {
                drained.TrySetResult();
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        await drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Drains what is queued, then stops.
    /// </summary>
    /// <remarks>
    /// Completing the channel before waiting means the pump writes everything already queued
    /// rather than abandoning it. A raid that ended as the application closed should still be
    /// in the database next time it opens.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    private async Task EnqueueAsync(
        Func<CancellationToken, Task> write,
        string what,
        CancellationToken cancellationToken)
    {
        if (_queue.Writer.TryWrite(write))
        {
            return;
        }

        // Said once. A queue that has filled says something about the database, and saying it
        // per write would fill the log with the symptom of the thing already reported.
        if (!_reportedFull)
        {
            _reportedFull = true;
            _logger?.LogWarning(
                "The raid history queue is full at {Capacity} writes; observation is waiting on the database.",
                Capacity);
        }

        await _queue.Writer.WriteAsync(write, cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        await foreach (var write in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
        {
            await AttemptAsync(write).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes, retrying a busy database and giving up rather than blocking the queue for ever.
    /// </summary>
    /// <remarks>
    /// A write that will not go is dropped after its attempts, and that is the right trade in
    /// this direction: the queue behind it holds a raid's whole record, and stalling all of it
    /// on one row would turn one lost event into every lost event.
    ///
    /// The failure is reported once rather than per write, for the same reason the full queue
    /// is. What matters is that somebody knows history stopped being written, not how many
    /// times it stopped.
    /// </remarks>
    private async Task AttemptAsync(Func<CancellationToken, Task> write)
    {
        var wait = TimeSpan.FromMilliseconds(50);
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                await write(_stopping.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                if (attempt == Attempts)
                {
                    if (!_reportedGaveUp)
                    {
                        _reportedGaveUp = true;
                        _logger?.LogWarning(
                            exception,
                            "Gave up writing raid history after {Attempts} attempts. Observation is unaffected.",
                            Attempts);
                    }

                    return;
                }

                await Task.Delay(wait, _stopping.Token).ConfigureAwait(false);
                wait *= 2;
            }
        }
    }
}
