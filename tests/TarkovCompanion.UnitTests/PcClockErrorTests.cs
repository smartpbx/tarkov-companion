using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// [#891] A PC clock hours out, measured against the relay, corrected everywhere the PC's time
/// meets another machine's, with nothing asked of the player.
/// </summary>
/// <remarks>
/// The owner's Windows PC started four hours fast after most boots (a dual-boot RTC). The game
/// names screenshots by the real time, the relay stamps by its own, and the PC read both four
/// hours off. The clocks here move only the wall time, as Windows' does when it is set; timers and
/// the monotonic timestamp keep the system's.
/// </remarks>
public sealed class PcClockErrorTests
{
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);

    [Fact]
    public void AnOffsetMeasuredOverHttpsCorrectsAndOneOverPlainHttpOnlyReports()
    {
        var pc = new SettableWallClock { Offset = FourHours };
        var tracker = new RelayClockOffsetTracker(clock: pc);
        var available = 0;
        tracker.CorrectionAvailable += () => available++;

        tracker.Observe(TimeProvider.System.GetUtcNow(), pc.GetUtcNow(), overTls: false);
        Assert.True(tracker.Current?.IsSkewed);
        Assert.Equal(TimeSpan.Zero, tracker.CorrectionAt(pc.GetUtcNow()));
        Assert.Equal(0, available);

        tracker.Observe(TimeProvider.System.GetUtcNow(), pc.GetUtcNow(), overTls: true);
        Assert.InRange((tracker.CorrectionAt(pc.GetUtcNow()) + FourHours).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
        Assert.InRange((tracker.ToRelayTime(pc.GetUtcNow()) - TimeProvider.System.GetUtcNow()).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(2));

        // Measured again a second apart, as every relay response does: said once, not per response.
        tracker.Observe(TimeProvider.System.GetUtcNow().AddSeconds(1), pc.GetUtcNow(), overTls: true);
        Assert.Equal(1, available);
        Assert.True(RelayClockOffsetTracker.IsTrustedTransport(new Uri("https://relay.example.test/")));
        Assert.True(RelayClockOffsetTracker.IsTrustedTransport(new Uri("http://127.0.0.1:5000/")));
        Assert.False(RelayClockOffsetTracker.IsTrustedTransport(new Uri("http://relay.example.test/")));
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(4)]
    public void AMeasurementIsNotAppliedToAClockThatHasBeenSetSince(int hours)
    {
        var pc = new SettableWallClock { Offset = TimeSpan.FromHours(hours) };
        var tracker = new RelayClockOffsetTracker(clock: pc);
        tracker.Observe(TimeProvider.System.GetUtcNow(), pc.GetUtcNow(), overTls: true);
        Assert.NotEqual(TimeSpan.Zero, tracker.CorrectionAt(pc.GetUtcNow()));

        pc.Offset = TimeSpan.Zero;

        // Applying the old offset now would put the right clock hours wrong.
        Assert.Equal(TimeSpan.Zero, tracker.CorrectionAt(pc.GetUtcNow()));
    }

    [Theory]
    [InlineData(-4, true)]
    [InlineData(4, true)]
    [InlineData(-4, false)]
    [InlineData(4, false)]
    public void AScreenshotTakenNowIsNowOnAPcHoursOut(int hours, bool relayMeasured)
    {
        var pc = new SettableWallClock { Offset = TimeSpan.FromHours(hours) };
        var tracker = new RelayClockOffsetTracker(clock: pc);
        if (relayMeasured)
        {
            tracker.Observe(TimeProvider.System.GetUtcNow(), pc.GetUtcNow(), overTls: true);
        }

        var parser = new ScreenshotFilenameParser(pc, tracker);

        Assert.True(parser.TryParseFile(NameTakenAt(TimeProvider.System.GetUtcNow()), EasternDaylight, out var position));

        // The name has minutes only, so up to a minute old; never four hours.
        Assert.InRange(pc.GetUtcNow() - position!.Timestamp, TimeSpan.Zero, TimeSpan.FromSeconds(61));
    }

    /// <remarks>
    /// Without the relay's measurement only a gap within minutes of whole hours is read as a clock
    /// error; a watcher reports a shot within seconds, so 4 h 7 min is left alone.
    /// </remarks>
    [Theory]
    [InlineData(4, true)]
    [InlineData(-4, true)]
    [InlineData(0, false)]
    public void AScreenshotsRealAgeIsKept(int hours, bool relayMeasured)
    {
        var pc = new SettableWallClock { Offset = TimeSpan.FromHours(hours) };
        var tracker = new RelayClockOffsetTracker(clock: pc);
        if (relayMeasured)
        {
            tracker.Observe(TimeProvider.System.GetUtcNow(), pc.GetUtcNow(), overTls: true);
        }

        var parser = new ScreenshotFilenameParser(pc, tracker);

        Assert.True(parser.TryParseFile(
            NameTakenAt(TimeProvider.System.GetUtcNow().AddMinutes(-7)),
            EasternDaylight,
            out var position));

        Assert.InRange(pc.GetUtcNow() - position!.Timestamp, TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(8).Add(TimeSpan.FromSeconds(1)));
    }

    /// <summary>What the squad receives: the age of a screenshot taken now, from a PC four hours fast.</summary>
    [Fact]
    public async Task AScreenshotTakenNowIsPublishedToTheSquadAsSecondsOld()
    {
        var pc = new SettableWallClock { Offset = FourHours };
        var tracker = new RelayClockOffsetTracker(clock: pc);
        tracker.Observe(TimeProvider.System.GetUtcNow(), pc.GetUtcNow(), overTls: true);
        var parser = new ScreenshotFilenameParser(pc, tracker);
        Assert.True(parser.TryParseFile(NameTakenAt(TimeProvider.System.GetUtcNow()), EasternDaylight, out var position));

        var bodies = new List<string>();
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (bodies)
            {
                bodies.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"room":"r","members":[]}""", Encoding.UTF8, "application/json"),
            };
        });
        var store = new RuntimeStateStore(Options);
        store.Update(current => current with
        {
            Raid = current.Raid with
            {
                State = RaidLifecycleState.InRaid,
                MapId = "lighthouse",
                LastKnownPosition = position,
            },
        });
        await using var service = new GroupSessionService(
            new StubSettings(),
            store,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance,
            clock: pc);

        service.Start();
        Assert.True(await UntilAsync(() => Latest(bodies) is { Length: > 0 }), "Nothing was published.");

        using var sent = JsonDocument.Parse(Latest(bodies)!);
        // Before #891: 14,400 seconds, and a squadmate's map drew the marker faded and stale.
        Assert.InRange(sent.RootElement.GetProperty("positionAge").GetDouble(), 0, 65);
    }

    private static readonly TimeSpan EasternDaylight = TimeSpan.FromHours(-4);

    private static string NameTakenAt(DateTimeOffset realUtc)
    {
        var local = realUtc.ToOffset(EasternDaylight);
        return local.ToString("yyyy-MM-dd'['HH'-'mm']'", CultureInfo.InvariantCulture) +
            "_80.02, 1.39, -51.06_-0.00242, 0.84404, 0.00393, 0.53626_9.91 (0).png";
    }

    private static string? Latest(List<string> bodies)
    {
        lock (bodies)
        {
            return bodies.Count == 0 ? null : bodies[^1];
        }
    }

    private static async Task<bool> UntilAsync(Func<bool> ready)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            if (ready())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return ready();
    }

    private static RuntimeOptions Options { get; } = new(
        false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(9),
        TimeSpan.FromMinutes(5));

    /// <summary>The system clock, with a wall time that can be set like Windows' can.</summary>
    private sealed class SettableWallClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + Offset;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class StubSettings : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(
                true, "https://relay.example.test/", "Clay", "a-key-long-enough", false, true));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class UiActivityCollection
{
    public const string Name = "UiActivity static state";
}

/// <summary>[#891] Hang reports' ages, on the one static state every view shares.</summary>
[Collection(UiActivityCollection.Name)]
public sealed class UiActivityMonotonicTests
{
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);

    [Fact]
    public void AHangReportsAgesSurviveTheClockBeingSetBack()
    {
        var clock = new SteppingTimestampClock();
        UiActivity.Reset();
        var previous = UiActivity.Clock;
        UiActivity.Clock = clock;
        try
        {
            UiActivity.LoadStarted("startup/map");
            UiActivity.LoadFinished("startup/map");
            // Four hours of wall clock taken away; only five seconds of real time pass.
            clock.Elapsed = TimeSpan.FromSeconds(5);

            var described = UiActivity.Describe();

            Assert.Contains("'startup/map' (finished, started 5.0s ago)", described, StringComparison.Ordinal);
        }
        finally
        {
            UiActivity.Clock = previous;
            UiActivity.Reset();
        }
    }

    /// <summary>A monotonic clock that moves only when told; its wall time is irrelevant here.</summary>
    private sealed class SteppingTimestampClock : TimeProvider
    {
        public TimeSpan Elapsed { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Elapsed.Ticks;

        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() - FourHours;
    }
}
