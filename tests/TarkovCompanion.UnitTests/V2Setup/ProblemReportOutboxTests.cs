using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Feedback;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests.V2Setup;

/// <summary>
/// #314: a consented report that meets an unreachable relay is queued and retried with back-off, a bounded
/// number of times, for at most seven days, and the relay never receives the same report twice.
/// </summary>
public sealed class ProblemReportOutboxTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnUnreachableRelayQueuesTheReportAndARetryAfterTheBackoffDeliversItOnce()
    {
        var clock = new Clock(Start);
        var relay = new Relay(ReportDelivery.Unreachable);
        var outbox = new ProblemReportOutbox(new MemoryStore(), relay.SendAsync, clock);

        var status = await outbox.SendAsync("report A", CancellationToken.None);
        Assert.StartsWith("Offline · queued", status, StringComparison.Ordinal);
        Assert.Single(outbox.Queued);
        Assert.Equal(Start + ProblemReportOutbox.Backoff(1), outbox.Queued[0].NextAttemptUtc);

        // Not due yet: nothing is sent.
        relay.Next = ReportDelivery.Sent;
        clock.Now = Start + TimeSpan.FromMinutes(1);
        Assert.Equal(0, await outbox.RetryDueAsync(CancellationToken.None));
        Assert.Equal(1, relay.Calls);

        clock.Now = Start + ProblemReportOutbox.Backoff(1);
        Assert.Equal(1, await outbox.RetryDueAsync(CancellationToken.None));
        Assert.Empty(outbox.Queued);
        Assert.Equal(0, await outbox.RetryDueAsync(CancellationToken.None));
        Assert.Equal(["report A", "report A"], relay.Received);
        Assert.Equal(1, relay.Delivered);
    }

    [Fact]
    public async Task TheSameReportIsNeverSentTwiceAndIsNeverQueuedTwice()
    {
        var clock = new Clock(Start);
        var relay = new Relay(ReportDelivery.Unreachable);
        var outbox = new ProblemReportOutbox(new MemoryStore(), relay.SendAsync, clock);

        await outbox.SendAsync("report A", CancellationToken.None);
        await outbox.SendAsync("report A", CancellationToken.None);
        Assert.Single(outbox.Queued);

        relay.Next = ReportDelivery.Sent;
        await outbox.SendAsync("report A", CancellationToken.None);
        Assert.Empty(outbox.Queued);

        var again = await outbox.SendAsync("report A", CancellationToken.None);
        Assert.StartsWith("Already sent", again, StringComparison.Ordinal);
        Assert.Equal(1, relay.Delivered);
        Assert.Equal(3, relay.Calls);
    }

    [Fact]
    public async Task RetriesStopAfterTheMaximumAttemptsWithGrowingWaits()
    {
        var clock = new Clock(Start);
        var relay = new Relay(ReportDelivery.Unreachable);
        var outbox = new ProblemReportOutbox(new MemoryStore(), relay.SendAsync, clock);

        await outbox.SendAsync("report A", CancellationToken.None);
        var waits = new List<TimeSpan>();
        while (outbox.Queued.Count > 0)
        {
            var next = outbox.Queued[0].NextAttemptUtc;
            waits.Add(next - clock.Now);
            clock.Now = next;
            await outbox.RetryDueAsync(CancellationToken.None);
        }

        Assert.Equal(ProblemReportOutbox.MaximumAttempts, relay.Calls);
        Assert.Equal(
            [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(16)],
            waits);
        Assert.Equal(ProblemReportOutbox.LongestRetry, ProblemReportOutbox.Backoff(20));
    }

    [Fact]
    public async Task AReportOlderThanSevenDaysIsDroppedUnsent()
    {
        var clock = new Clock(Start);
        var relay = new Relay(ReportDelivery.Unreachable);
        var store = new MemoryStore();
        var outbox = new ProblemReportOutbox(store, relay.SendAsync, clock);
        await outbox.SendAsync("report A", CancellationToken.None);

        relay.Next = ReportDelivery.Sent;
        clock.Now = Start + ProblemReportOutbox.Lifetime;
        Assert.Equal(0, await outbox.RetryDueAsync(CancellationToken.None));
        Assert.Empty(outbox.Queued);
        Assert.Empty(store.State.Queued);
        Assert.Equal(1, relay.Calls);
    }

    [Fact]
    public async Task ARefusalIsNotRetriedAndTheQueueIsBounded()
    {
        var clock = new Clock(Start);
        var relay = new Relay(ReportDelivery.Refused);
        var outbox = new ProblemReportOutbox(new MemoryStore(), relay.SendAsync, clock);

        Assert.Equal("refused", await outbox.SendAsync("report A", CancellationToken.None));
        Assert.Empty(outbox.Queued);

        relay.Next = ReportDelivery.Unreachable;
        for (var index = 0; index < ProblemReportOutbox.MaximumQueued + 2; index++)
        {
            clock.Now = Start + TimeSpan.FromSeconds(index);
            await outbox.SendAsync($"report {index}", CancellationToken.None);
        }

        Assert.Equal(ProblemReportOutbox.MaximumQueued, outbox.Queued.Count);
        Assert.DoesNotContain(outbox.Queued, queued => queued.Text == "report 0");
    }

    [Fact]
    public async Task TheQueueSurvivesARestartThroughTheJsonFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-outbox-{Guid.NewGuid():N}.json");
        try
        {
            var clock = new Clock(Start);
            var relay = new Relay(ReportDelivery.Unreachable);
            await new ProblemReportOutbox(new JsonFileProblemReportOutboxStore(path), relay.SendAsync, clock)
                .SendAsync("report A", CancellationToken.None);

            relay.Next = ReportDelivery.Sent;
            clock.Now = Start + TimeSpan.FromHours(1);
            var restarted = new ProblemReportOutbox(new JsonFileProblemReportOutboxStore(path), relay.SendAsync, clock);
            Assert.Equal(1, await restarted.RetryDueAsync(CancellationToken.None));

            var third = new ProblemReportOutbox(new JsonFileProblemReportOutboxStore(path), relay.SendAsync, clock);
            Assert.StartsWith("Already sent", await third.SendAsync("report A", CancellationToken.None), StringComparison.Ordinal);
            Assert.Equal(1, relay.Delivered);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThePendingLineCountsReportsAndNamesTheNextTryInLocalTime()
    {
        using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
        Assert.Equal(string.Empty, ProblemReportPendingText.Describe([]));
        Assert.Equal(
            "2 reports queued · next try 12:02",
            ProblemReportPendingText.Describe(
                [
                    new("a", "A", Start, 1, Start + TimeSpan.FromMinutes(4)),
                    new("b", "B", Start, 1, Start + TimeSpan.FromMinutes(2)),
                ],
                CultureInfo.GetCultureInfo("en-GB")));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class MemoryStore : IProblemReportOutboxStore
    {
        public ProblemReportOutboxState State { get; private set; } = ProblemReportOutboxState.Empty;

        public Task<ProblemReportOutboxState> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(State);

        public Task SaveAsync(ProblemReportOutboxState state, CancellationToken cancellationToken)
        {
            State = state;
            return Task.CompletedTask;
        }
    }

    private sealed class Relay(ReportDelivery next)
    {
        public ReportDelivery Next { get; set; } = next;

        public List<string> Received { get; } = [];

        public int Calls => Received.Count;

        public int Delivered { get; private set; }

        public Task<ReportSendResult> SendAsync(string report, CancellationToken cancellationToken)
        {
            Received.Add(report);
            Delivered += Next == ReportDelivery.Sent ? 1 : 0;
            return Task.FromResult(new ReportSendResult(Next, Next.ToString().ToLowerInvariant()));
        }
    }
}
