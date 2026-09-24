using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// [#799] The PC's clock set by four hours, either way, while the companion runs.
/// </summary>
/// <remarks>
/// The clock here moves only the wall time; timers and the monotonic timestamp keep running as
/// the system's do, which is exactly what Windows does when its time is set.
/// </remarks>
public sealed class ClockJumpTests : IDisposable
{
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"clock-jump-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Theory]
    [InlineData(-4)]
    [InlineData(4)]
    public void TheDetectorReportsAJumpOnceAndNotOrdinaryTime(int hours)
    {
        var clock = new SettableWallClock();
        using var detector = new WallClockJumpDetector(clock);
        var reported = new List<TimeSpan>();
        detector.Jumped += reported.Add;

        Assert.Null(detector.Check());
        clock.Offset = TimeSpan.FromHours(hours);
        var jump = detector.Check();
        Assert.Null(detector.Check());

        Assert.NotNull(jump);
        Assert.InRange((jump.Value - TimeSpan.FromHours(hours)).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Single(reported);
    }

    /// <summary>
    /// The one that took the squad away: the rate bound was a wall-clock difference, so four
    /// hours back became a four-hour wait before the next exchange.
    /// </summary>
    [Theory]
    [InlineData(-4)]
    [InlineData(4)]
    public async Task GroupPublishingCarriesOnAndTheSquadStaysAfterTheClockIsSet(int hours)
    {
        var clock = new SettableWallClock();
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
                Content = new StringContent(
                    """{"room":"r","members":[{"name":"Bravo","mapId":"lighthouse","raidState":"InRaid","x":1,"z":2,"positionAge":3}]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var store = new RuntimeStateStore(Options);
        store.Update(current => current with
        {
            Raid = current.Raid with
            {
                State = RaidLifecycleState.InRaid,
                MapId = "lighthouse",
                LastKnownPosition = Position(clock.GetUtcNow(), x: 10),
            },
        });
        await using var service = new GroupSessionService(
            new StubSettings(),
            store,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance,
            clock: clock);

        service.Start();
        Assert.True(await UntilAsync(() => Count(bodies) >= 1 && store.Current.Group.Members.Count == 1), "The first exchange never happened.");
        await Task.Delay(400);
        var before = Count(bodies);

        clock.Offset = TimeSpan.FromHours(hours);
        // A screenshot after the clock was set, stamped by the clock as it now is, which is what
        // the game's filename does: a change, so the loop should publish it at once.
        store.Update(current => current with
        {
            Raid = current.Raid with { LastKnownPosition = Position(clock.GetUtcNow(), x: 20) },
        });

        Assert.True(
            await UntilAsync(() => Count(bodies) > before, TimeSpan.FromSeconds(3)),
            "No exchange after the clock was set: the publish loop is waiting on the old clock.");
        Assert.Contains(store.Current.Group.Members, member => member.Name == "Bravo");
        Assert.True(store.Current.Group.IsSharing);
        string latest;
        lock (bodies)
        {
            latest = bodies[^1];
        }

        using var sent = JsonDocument.Parse(latest);
        Assert.Equal(20, sent.RootElement.GetProperty("x").GetDouble());
        Assert.InRange(sent.RootElement.GetProperty("positionAge").GetDouble(), 0, 5);
    }

    /// <summary>
    /// The player's own marker: after four hours back, every new screenshot was "older" than the
    /// last one and refused; after four hours forward, the gap started a new raid.
    /// </summary>
    [Theory]
    [InlineData(-4)]
    [InlineData(4)]
    public void OwnPositionKeepsMovingInTheSameRaidOnceTheRaidIsRebased(int hours)
    {
        var jump = TimeSpan.FromHours(hours);
        var now = new DateTimeOffset(2026, 9, 24, 4, 19, 52, TimeSpan.Zero);
        var raid = new RaidStateService();
        raid.Apply(new RaidEvidence(
            RaidEvidenceKind.LogLine,
            now,
            "lighthouse",
            RaidLifecycleState.InRaid,
            new Confidence(0.9),
            "test"));
        raid.ApplyPosition(Position(now.AddSeconds(30), x: 10));
        var raidId = raid.Current.RaidId;
        Assert.NotNull(raidId);

        raid.RebaseClock(jump);
        var after = now + jump + TimeSpan.FromSeconds(60);
        raid.ApplyPosition(Position(after, x: 20));

        Assert.Equal(20, raid.Current.LastKnownPosition!.Position.X);
        Assert.Equal(raidId, raid.Current.RaidId);
        Assert.Equal(now + jump, raid.Current.StartedUtc);
        Assert.Equal(2, raid.Current.PositionTrail.Count);
        // The Raid map fades a screenshot older than two minutes: this one is seconds old.
        Assert.InRange(after.AddSeconds(5) - raid.Current.LastKnownPosition.Timestamp, TimeSpan.Zero, TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void WithoutTheRebaseASetBackClockRefusedEveryNewScreenshot()
    {
        // The failure this guards, stated so the test above cannot pass for another reason.
        var now = new DateTimeOffset(2026, 9, 24, 4, 19, 52, TimeSpan.Zero);
        var raid = new RaidStateService();
        raid.ApplyPosition(Position(now, x: 10));

        raid.ApplyPosition(Position(now - FourHours + TimeSpan.FromMinutes(1), x: 20));

        Assert.Equal(10, raid.Current.LastKnownPosition!.Position.X);
    }

    /// <summary>
    /// A ping placed before the clock was set keeps the life it had left, and one placed after
    /// lives its 45 seconds.
    /// </summary>
    [Theory]
    [InlineData(-4)]
    [InlineData(4)]
    public async Task PingsKeepTheirLifeAcrossTheJump(int hours)
    {
        var clock = new SettableWallClock();
        using var store = new JsonFileRaidMarkStore(Path.Combine(_directory, "raid-marks.json"), clock);
        await store.LoadAsync();
        var before = await store.PlaceAsync("lighthouse", null, 10, 10, null, RaidMarkScope.Squad, RaidMarkLifetime.Ping);

        clock.Offset = TimeSpan.FromHours(hours);
        await store.RebaseClockAsync(TimeSpan.FromHours(hours));
        var after = await store.PlaceAsync("lighthouse", null, 20, 20, null, RaidMarkScope.Squad, RaidMarkLifetime.Ping);

        var now = clock.GetUtcNow();
        var marks = store.Marks;
        Assert.Equal(2, marks.Count);
        var moved = Assert.Single(marks, mark => mark.Id == before.Id);
        Assert.InRange(moved.State.ExpiresUtc!.Value - now, TimeSpan.FromSeconds(40), MapMarkPolicy.PingLifetime);
        Assert.InRange(now - moved.CreatedUtc, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var placed = Assert.Single(marks, mark => mark.Id == after.Id);
        Assert.InRange(placed.State.ExpiresUtc!.Value - now, TimeSpan.FromSeconds(44), MapMarkPolicy.PingLifetime);
    }

    private static int Count(List<string> bodies)
    {
        lock (bodies)
        {
            return bodies.Count;
        }
    }

    private static ScreenshotPosition Position(DateTimeOffset taken, double x) =>
        new(taken, new WorldPosition(x, 0, 5), default, 90, null, null, $"shot-{x}.png");

    private static async Task<bool> UntilAsync(Func<bool> ready, TimeSpan? budget = null)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < (budget ?? TimeSpan.FromSeconds(10)))
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
