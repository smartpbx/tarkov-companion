using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.GroupServer;
using TarkovCompanion.Infrastructure.Maps;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// One ping placed on the desktop, followed from the click to the moment the squad stops seeing it.
/// </summary>
/// <remarks>
/// #589/#785 say a ping lasts 45 seconds. Clayton's log (2.0.1533) had each desktop ping taken off
/// the relay about 16 s after "Marked a ping". Every hop is real here except the network: a relay
/// in this process serving the relay's own /state route plus the same /pings and DELETE handlers
/// Program.cs maps, the real JSON mark store, the real <see cref="GroupMarkForwarder"/>, and two
/// real <see cref="GroupSessionService"/>s, Alpha placing and Bravo watching. On this path the
/// ping lives its 45 s: the forwarder takes it off the relay when the local store expires it,
/// 45.0 s after the click, and Bravo loses it within a moment of that. The whole app (the V2
/// Raid cockpit placing it, against a real relay process) measured the same on 2026-09-24, so the
/// 16 s was not reproduced here. The only removal the forwarder makes on its own is at the local
/// expiry, so a "Marked" line 16 s before it means the send finished about 29 s after the click;
/// the send time and the removal's age that #808 added to those lines will say which.
///
/// What following it did find is the second test below.
///
/// The mark store, the forwarder and the relay's marks run on one clock three times faster than
/// the wall, so the 45 s take 15; the two sessions' holds and timeouts stay on the real one.
/// </remarks>
public sealed class PingLifetimeEndToEndTests(ITestOutputHelper output)
{
    private const string Key = "a-key-long-enough-for-a-room";
    private const double Speed = 3;

    [Fact]
    public async Task A_desktop_ping_lives_its_45_seconds_for_the_squad()
    {
        await using var run = await Run.StartAsync(output);
        var placed = await run.PlaceAsync();
        Assert.True(await run.UntilAsync(() => run.BravoSeesPing, TimeSpan.FromSeconds(5)), "Bravo should see the ping.");
        var seen = run.Clock.GetUtcNow();
        run.Events.Add("bravo sees the ping");

        Assert.True(await run.UntilAsync(() => !run.BravoSeesPing, TimeSpan.FromSeconds(30)), "Bravo should stop seeing the ping.");
        var lost = run.Clock.GetUtcNow();
        var lostAt = Stopwatch.GetTimestamp();
        run.Events.Add("bravo lost the ping");

        var lived = lost - placed.CreatedUtc;
        var shown = lost - seen;
        run.Events.Add(string.Create(CultureInfo.InvariantCulture, $"lived {lived.TotalSeconds:0.0} s, Bravo saw it {shown.TotalSeconds:0.0} s"));
        // Not taken early: the #589/#785 fault was a ping gone from the squad's maps at 16 s.
        Assert.True(lived.TotalSeconds >= 43, $"The ping lived {lived.TotalSeconds:0.0} s.");
        Assert.True(shown.TotalSeconds >= 43, $"Bravo saw the ping for {shown.TotalSeconds:0.0} s.");
        Assert.Contains(run.Events.Lines, line => line.Contains("forwarder remove", StringComparison.Ordinal) && line.Contains("expired", StringComparison.Ordinal));

        // Not kept late, in two halves measured on the clock each one runs on. The upper bound
        // used to be on the whole of it on the fast clock, which multiplied the relay round trip,
        // Bravo's exchange and the test's own polling by three: 0.67 s of a busy runner read as a
        // ping that lived 47.008 s. The removal is timed on the fast clock, where its timer runs;
        // the squad hearing of it is timed on the wall, where the network and the holds run.
        var (removedUtc, removedAt) = run.Removed ?? throw new InvalidOperationException("The forwarder never removed the ping.");
        var removedAfter = removedUtc - placed.CreatedUtc;
        Assert.InRange(removedAfter.TotalSeconds, 44.9, 46.5);
        var heard = Stopwatch.GetElapsedTime(removedAt, lostAt);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"removed at {removedAfter.TotalSeconds:0.00} s, Bravo lost it {heard.TotalMilliseconds:0} ms of wall time later"));
        Assert.True(heard < TimeSpan.FromSeconds(1.5), $"Bravo lost the ping {heard.TotalMilliseconds:0} ms after the forwarder removed it.");
    }

    /// <summary>
    /// Placing or removing a ping while an exchange is held is not a failed publish.
    /// </summary>
    /// <remarks>
    /// Fails without <c>MarksChangedHere</c>: the mark's own interrupt cut the held exchange short
    /// with no local change counted, the loop read that as a relay that did not answer, logged
    /// "Group publish failed", marked the squad stale and waited a whole tick. Against a relay
    /// that ends holds on a new mark that only happens when the interrupt wins the race with the
    /// relay's own answer, so this relay does not end them, which makes it happen every time.
    /// </remarks>
    [Fact]
    public async Task Placing_and_removing_a_ping_during_a_hold_is_not_a_failed_publish()
    {
        await using var run = await Run.StartAsync(output, marksEndHolds: false);
        Assert.True(await run.UntilAsync(() => run.Changes.WaitingCount >= 2, TimeSpan.FromSeconds(10)), "Both members should be holding.");
        var placed = await run.PlaceAsync();
        Assert.True(await run.UntilAsync(() => run.AlphaSeesPing, TimeSpan.FromSeconds(3)), "Alpha should see its ping at once.");
        await Task.Delay(600);
        Assert.True(await run.UntilAsync(() => run.Changes.WaitingCount >= 2, TimeSpan.FromSeconds(10)), "Both members should be holding again.");
        await run.Store.RemoveAsync(placed.Id);
        Assert.True(await run.UntilAsync(() => !run.AlphaSeesPing, TimeSpan.FromSeconds(3)), "Alpha should lose its ping at once.");
        await Task.Delay(500);

        Assert.DoesNotContain(run.Events.Lines, line => line.Contains("publish failed", StringComparison.OrdinalIgnoreCase));
        Assert.Null(run.AlphaState.Current.Group.StaleSince);

        // #886: the forwarder's own removal is scoped to its sender, so an id the relay reissued
        // after a restart cannot take a squadmate's mark with it, and scoped to Alpha it still
        // removed Alpha's ping.
        Assert.Contains(run.Events.Lines, line => line.Contains("deleted, scoped to Alpha", StringComparison.Ordinal));
    }

    /// <summary>
    /// #929: dead in a scav raid, or extracted from one, with the squad still inside on the same
    /// map; a ping and a waypoint placed then reach the squad.
    /// </summary>
    /// <remarks>
    /// Guards the half of the report after the click: nothing on the way from the mark store to a
    /// squadmate's snapshot (the forwarder, the session, the relay) looks at the sender's raid
    /// state or side. The half before the click, where the press was swallowed, is
    /// RaidMarkGestureTests.A_right_click_inside_a_traffic_circle_or_on_an_extract_places_a_mark.
    /// </remarks>
    [Theory]
    [InlineData("scav", "killed")]
    [InlineData("scav", "extracted")]
    [InlineData("PMC", "killed")]
    public async Task A_player_out_of_their_raid_still_marks_the_map_for_the_squad_inside(string side, string how)
    {
        await using var run = await Run.StartAsync(output);
        run.AlphaState.Update(snapshot => snapshot with
        {
            Raid = snapshot.Raid with { State = TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.InRaid, MapId = "lighthouse", Side = side },
        });
        run.BravoState.Update(snapshot => snapshot with
        {
            Raid = snapshot.Raid with { State = TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.InRaid, MapId = "lighthouse", Side = side },
        });
        // Killed and extracted end the same way in the log (userMatchOver); a scav's may carry
        // Transfer. Either is PostRaid, and the menu after it is Menu.
        run.AlphaState.Update(snapshot => snapshot with
        {
            Raid = snapshot.Raid with
            {
                State = how == "killed"
                    ? TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.PostRaid
                    : TarkovCompanion.Core.Domain.Raids.RaidLifecycleState.Menu,
            },
        });
        Assert.True(
            await run.UntilAsync(
                () => run.BravoState.Current.Group.Members.Any(member =>
                    member.Name == "Alpha" && TarkovCompanion.Application.Services.Group.SquadRaidPresence.HasLeftRaid(member.RaidState)),
                TimeSpan.FromSeconds(10)),
            "Bravo should see Alpha out of the raid.");

        await run.PlaceAsync();
        await run.Store.PlaceAsync("lighthouse", null, 30, 40, null, RaidMarkScope.Squad, RaidMarkLifetime.UntilRemoved);

        Assert.True(await run.UntilAsync(() => run.BravoSeesPing, TimeSpan.FromSeconds(10)), "Bravo should see the ping.");
        Assert.True(
            await run.UntilAsync(() => run.BravoState.Current.Group.Waypoints.Count > 0, TimeSpan.FromSeconds(10)),
            "Bravo should see the waypoint.");
        var ping = Assert.Single(run.BravoState.Current.Group.Pings);
        Assert.Equal("lighthouse", ping.MapId);
        Assert.Equal("Alpha", ping.By);
        Assert.Equal("lighthouse", Assert.Single(run.BravoState.Current.Group.Waypoints).MapId);
    }

    /// <summary>Everything one ping passes through, started and wired together.</summary>
    private sealed class Run : IAsyncDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _root;
        private readonly Relay _relay;
        private readonly HttpClient _alphaClient = new() { Timeout = Timeout.InfiniteTimeSpan };
        private readonly HttpClient _bravoClient = new() { Timeout = Timeout.InfiniteTimeSpan };
        private readonly GroupSessionService _alpha;
        private readonly GroupSessionService _bravo;
        private readonly GroupMarkForwarder _forwarder;

        private Run(ITestOutputHelper output, string root, Relay relay, FastClock clock, Timeline events)
        {
            _output = output;
            _root = root;
            _relay = relay;
            Clock = clock;
            Events = events;
            _alpha = new(new FixedSettings(relay.Address, "Alpha"), AlphaState, _alphaClient, new TimelineLogger<GroupSessionService>(events, "alpha"));
            _bravo = new(new FixedSettings(relay.Address, "Bravo"), BravoState, _bravoClient, new TimelineLogger<GroupSessionService>(events, "bravo"));
            Store = new JsonFileRaidMarkStore(Path.Combine(root, "raid-marks.json"), clock);
            _forwarder = new GroupMarkForwarder(
                Store,
                _ => new WorldPosition(10, 0, 20),
                (mapId, position, isPing, cancellationToken) => _alpha.SendMarkAsync(mapId, position, label: null, isPing, cancellationToken),
                (id, why) =>
                {
                    Removed ??= (clock.GetUtcNow(), Stopwatch.GetTimestamp());
                    events.Add($"forwarder remove {id}: {why}");
                    return _alpha.RemoveMarkAsync(id, CancellationToken.None, why);
                },
                clock);
        }

        public FastClock Clock { get; }

        /// <summary>When the forwarder first took a mark off the relay, on the fast clock and the wall.</summary>
        public (DateTimeOffset Utc, long At)? Removed { get; private set; }

        public Timeline Events { get; }

        public JsonFileRaidMarkStore Store { get; }

        public RuntimeStateStore AlphaState { get; } = new(Options);

        public RuntimeStateStore BravoState { get; } = new(Options);

        public GroupRoomChanges Changes => _relay.Changes;

        public bool BravoSeesPing => BravoState.Current.Group.Pings.Count > 0;

        public bool AlphaSeesPing => AlphaState.Current.Group.Pings.Count > 0;

        public static async Task<Run> StartAsync(ITestOutputHelper output, bool marksEndHolds = true)
        {
            var clock = new FastClock(Speed);
            var events = new Timeline(clock);
            var root = Path.Combine(Path.GetTempPath(), $"tarkov-ping-life-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var relay = await Relay.StartAsync(clock, events, marksEndHolds);
            var run = new Run(output, root, relay, clock, events);
            await run.Store.LoadAsync();
            run._alpha.Start();
            run._bravo.Start();
            Assert.True(
                await run.UntilAsync(() => run.AlphaState.Current.Group.IsSharing && run.BravoState.Current.Group.IsSharing, TimeSpan.FromSeconds(20)),
                "Both members should reach the relay.");
            return run;
        }

        public async Task<RaidMark> PlaceAsync()
        {
            var placed = await Store.PlaceAsync("lighthouse", null, 10, 20, null, RaidMarkScope.Squad, RaidMarkLifetime.Ping);
            Events.Add($"placed, expires {(placed.State.ExpiresUtc!.Value - placed.CreatedUtc).TotalSeconds:0.0} s after");
            return placed;
        }

        public async Task<bool> UntilAsync(Func<bool> ready, TimeSpan budget)
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < budget)
            {
                if (ready())
                {
                    return true;
                }

                await Task.Delay(10);
            }

            return ready();
        }

        public async ValueTask DisposeAsync()
        {
            _forwarder.Dispose();
            await _alpha.DisposeAsync();
            await _bravo.DisposeAsync();
            Store.Dispose();
            _alphaClient.Dispose();
            _bravoClient.Dispose();
            await _relay.DisposeAsync();
            foreach (var line in Events.Lines)
            {
                _output.WriteLine(line);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private static RuntimeOptions Options { get; } = new(
        false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(9),
        TimeSpan.FromMinutes(5));

    /// <summary>Wall time running <paramref name="speed"/> times faster than the real one, timers included.</summary>
    private sealed class FastClock(double speed) : TimeProvider
    {
        private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
        private readonly long _started = Stopwatch.GetTimestamp();

        public override DateTimeOffset GetUtcNow() => _start + Stopwatch.GetElapsedTime(_started) * speed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, Scale(dueTime), Scale(period));

        private TimeSpan Scale(TimeSpan span) => span < TimeSpan.Zero ? span : span / speed;
    }

    /// <summary>What happened and when, in the fast clock's seconds.</summary>
    private sealed class Timeline(TimeProvider clock)
    {
        private readonly DateTimeOffset _started = clock.GetUtcNow();
        private readonly ConcurrentQueue<string> _lines = new();

        public IEnumerable<string> Lines => _lines;

        public void Add(string what) => _lines.Enqueue(string.Create(
            CultureInfo.InvariantCulture,
            $"{(clock.GetUtcNow() - _started).TotalSeconds,7:0.00} {what}"));
    }

    private sealed class TimelineLogger<T>(Timeline events, string who) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                events.Add($"{who}: {formatter(state, exception)}");
            }
        }
    }

    /// <summary>
    /// A relay in this process: its own /state route, and the /pings and DELETE handlers
    /// Program.cs maps, with the marks on the fast clock.
    /// </summary>
    private sealed class Relay(WebApplication app, string address, GroupRoomChanges changes) : IAsyncDisposable
    {
        public string Address { get; } = address;

        public GroupRoomChanges Changes { get; } = changes;

        public static async Task<Relay> StartAsync(TimeProvider marksClock, Timeline events, bool marksEndHolds)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var rooms = new GroupRooms(TimeProvider.System);
            var changes = new GroupRoomChanges(TimeProvider.System);
            var marks = new GroupMarks(marksClock);
            var app = builder.Build();
            app.MapGroupRoomState(rooms, marks, changes);
            app.MapPost("/pings", (MarkRequest request, HttpRequest http) =>
            {
                if (!GroupKey.TryRead(http, out var key) || request.Validate() is not null)
                {
                    return Results.BadRequest();
                }

                var room = GroupKey.RoomFor(key);
                var added = marks.AddPing(room, request.By, request.MapId, request.X, request.Y, request.Z, request.Label)!;
                if (marksEndHolds)
                {
                    changes.Record(room, null);
                }

                events.Add($"relay: ping {added.Id} added");
                return Results.Ok(added);
            });
            // #929: the waypoint route as Program.cs maps it, for Shift+right-click.
            app.MapPost("/waypoints", (MarkRequest request, HttpRequest http) =>
            {
                if (!GroupKey.TryRead(http, out var key) || request.Validate() is not null)
                {
                    return Results.BadRequest();
                }

                var room = GroupKey.RoomFor(key);
                var added = marks.AddWaypoint(room, request.By, request.MapId, request.X, request.Y, request.Z, request.Label)!;
                if (marksEndHolds)
                {
                    changes.Record(room, null);
                }

                events.Add($"relay: waypoint {added.Id} added");
                return Results.Ok(added);
            });
            app.MapDelete("/waypoints/{id:long}", (long id, string? by, HttpRequest http) =>
            {
                if (!GroupKey.TryRead(http, out var key))
                {
                    return Results.Unauthorized();
                }

                var room = GroupKey.RoomFor(key);
                if (!marks.Remove(room, id, by))
                {
                    return Results.NotFound();
                }

                if (marksEndHolds)
                {
                    changes.Record(room, null);
                }

                events.Add($"relay: mark {id} deleted, scoped to {by ?? "nobody"}");
                return Results.Ok();
            });
            await app.StartAsync();
            return new(app, app.Urls.First() + "/", changes);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class FixedSettings(string address, string name) : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(true, address, name, Key, false, false));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
