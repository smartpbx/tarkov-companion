using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Writing raid history behind a queue, so persistence cannot take observation down.
/// </summary>
/// <remarks>
/// Every event was written inline on the thread that observed it. A screenshot is read, a
/// position is recorded, and the recording awaits a SQLite write — so a database busy with the
/// hourly catalog refresh stalls the watcher reading the game's log.
///
/// The observation side is the half that must never stop: a write that arrives late is a row
/// with the right timestamp, and an observation that never happens is gone.
/// </remarks>
public sealed class RaidHistoryOutboxTests
{
    [Fact]
    public async Task Recording_returns_before_the_write_happens()
    {
        // The whole point. The caller is a log watcher, and it has to get back to the file.
        var inner = new BlockingHistory();
        await using var outbox = new RaidHistoryOutbox(inner);

        await outbox.AcceptAsync([RaidHistoryCommand.RecordPosition(Guid.NewGuid(), Position())], CancellationToken.None);

        Assert.Equal(0, inner.Written);
        inner.Release();
    }

    [Fact]
    public async Task Everything_queued_is_written()
    {
        var inner = new BlockingHistory();
        var raidId = Guid.NewGuid();

        await using (var outbox = new RaidHistoryOutbox(inner))
        {
            for (var index = 0; index < 20; index++)
            {
                await outbox.AcceptAsync([RaidHistoryCommand.RecordPosition(raidId, Position(index))], CancellationToken.None);
            }

            inner.Release();
        }

        Assert.Equal(20, inner.Written);
    }

    [Fact]
    public async Task Writes_land_in_the_order_they_were_queued()
    {
        // Start, then events, then End. An event references a raid row that has to exist, and
        // one reader is what keeps that true by construction rather than by luck.
        var inner = new BlockingHistory();
        var raidId = Guid.NewGuid();

        await using (var outbox = new RaidHistoryOutbox(inner))
        {
            await outbox.AcceptAsync([RaidHistoryCommand.RecordState(raidId, State())], CancellationToken.None);
            await outbox.AcceptAsync([RaidHistoryCommand.RecordExtracts(raidId, DateTimeOffset.UnixEpoch, [])], CancellationToken.None);
            await outbox.EndAsync(raidId, DateTimeOffset.UnixEpoch, null, null, CancellationToken.None);
            inner.Release();
        }

        Assert.Equal(["state", "extracts", "end"], inner.Order);
    }

    [Fact]
    public async Task Disposing_drains_what_is_still_queued()
    {
        // A raid that ended as the application closed should still be in the database next
        // time it opens.
        var inner = new BlockingHistory();
        var raidId = Guid.NewGuid();

        await using (var outbox = new RaidHistoryOutbox(inner))
        {
            await outbox.EndAsync(raidId, DateTimeOffset.UnixEpoch, null, null, CancellationToken.None);
            inner.Release();
        }

        Assert.Equal(["end"], inner.Order);
    }

    [Fact]
    public async Task Starting_a_raid_is_not_deferred()
    {
        // It returns the id everything else is keyed by, so it cannot be queued without
        // inventing one — and it happens once per raid rather than once per screenshot.
        var inner = new BlockingHistory();
        await using var outbox = new RaidHistoryOutbox(inner);

        var id = await outbox.StartAsync(Entry(), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, id);
    }

    [Fact]
    public async Task A_write_that_keeps_failing_blocks_later_writes_in_its_aggregate()
    {
        // Aggregate order is a data invariant: delivering a later raid event after its
        // predecessor was rejected would claim a history gap was complete. Other raids remain
        // independent and are covered by the runtime outbox contract tests.
        var inner = new BlockingHistory { FailType = "state" };
        var raidId = Guid.NewGuid();

        await using (var outbox = new RaidHistoryOutbox(inner))
        {
            await outbox.AcceptAsync([RaidHistoryCommand.RecordState(raidId, State())], CancellationToken.None);
            await outbox.AcceptAsync([RaidHistoryCommand.RecordExtracts(raidId, DateTimeOffset.UnixEpoch, [])], CancellationToken.None);
            inner.Release();
        }

        Assert.DoesNotContain("extracts", inner.Order);
    }

    [Fact]
    public async Task A_write_that_fails_once_is_retried_rather_than_lost()
    {
        var inner = new BlockingHistory { FailType = "state", FailTimes = 1 };
        var raidId = Guid.NewGuid();

        await using (var outbox = new RaidHistoryOutbox(inner))
        {
            await outbox.AcceptAsync([RaidHistoryCommand.RecordState(raidId, State())], CancellationToken.None);
            inner.Release();
        }

        Assert.Contains("state", inner.Order);
    }

    [Fact]
    public async Task Reads_are_not_queued_behind_writes()
    {
        // A read has to give the real answer now. Queueing one would mean the summary asked
        // the database a question and got it back after the panel had been drawn.
        var inner = new BlockingHistory();
        await using var outbox = new RaidHistoryOutbox(inner);
        await outbox.AcceptAsync([RaidHistoryCommand.RecordPosition(Guid.NewGuid(), Position())], CancellationToken.None);

        Assert.Empty(await outbox.ListAsync(CancellationToken.None));

        inner.Release();
    }

    private static RaidHistoryEntry Entry() => new(
        Guid.NewGuid(),
        Guid.Parse("2c2f2a0d-6f1e-4a7f-9c5a-1f4f0c6ad2b1"),
        "customs",
        "Regular",
        DateTimeOffset.UnixEpoch,
        null,
        null,
        null);

    private static RaidEvidence State() => new(
        RaidEvidenceKind.LogLine,
        DateTimeOffset.UnixEpoch,
        "bigmap",
        RaidLifecycleState.InRaid,
        Confidence.Certain,
        "fixture raid state");

    private static ScreenshotPosition Position(int index = 0) => new(
        DateTimeOffset.UnixEpoch.AddSeconds(index),
        new(index, 0, index),
        new(0, 0, 0, 1),
        0,
        null,
        index,
        $"fixture-{index}.png");

    /// <summary>An inner service that holds every write until it is told to let them through.</summary>
    /// <remarks>
    /// Holding is what makes the queue observable: without it the pump drains faster than a
    /// test can look, and "returned before the write happened" cannot be distinguished from
    /// "wrote very quickly".
    /// </remarks>
    private sealed class BlockingHistory : IRaidHistoryService
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();
        private readonly List<string> _order = [];
        private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);

        /// <summary>An event type that fails when it is written.</summary>
        public string? FailType { get; init; }

        /// <summary>How many times it fails before succeeding; zero means always.</summary>
        public int FailTimes { get; init; }

        public int Written
        {
            get
            {
                lock (_lock)
                {
                    return _order.Count;
                }
            }
        }

        public IReadOnlyList<string> Order
        {
            get
            {
                lock (_lock)
                {
                    return [.. _order];
                }
            }
        }

        public void Release() => _gate.TrySetResult();

        public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken) =>
            Task.FromResult(raid.Id);

        public async Task RecordEventAsync(
            Guid raidId,
            string type,
            DateTimeOffset timestampUtc,
            string payloadJson,
            CancellationToken cancellationToken)
        {
            await _gate.Task.ConfigureAwait(false);
            if (string.Equals(type, FailType, StringComparison.Ordinal))
            {
                lock (_lock)
                {
                    var already = _failures.GetValueOrDefault(type);
                    if (FailTimes == 0 || already < FailTimes)
                    {
                        _failures[type] = already + 1;
                        throw new InvalidOperationException("the database was busy");
                    }
                }
            }

            lock (_lock)
            {
                _order.Add(type);
            }
        }

        public async Task EndAsync(
            Guid raidId,
            DateTimeOffset endUtc,
            string? outcome,
            string? notes,
            CancellationToken cancellationToken)
        {
            await _gate.Task.ConfigureAwait(false);
            lock (_lock)
            {
                _order.Add("end");
            }
        }

        public Task CorrectAsync(
            Guid raidId,
            string? outcome,
            string? notes,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidHistoryEntry>>([]);

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(
            Guid raidId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ScreenshotPosition>>([]);

        public Task<IReadOnlyList<string>> ListEventPayloadsAsync(
            Guid raidId,
            string type,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(
            string mapId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidTrail>>([]);

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
