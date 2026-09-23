using System.Diagnostics;
using Xunit.Abstractions;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.GroupServer;
using TarkovCompanion.Platform.Windows.Watching;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// How long a squadmate's screenshot takes to move their marker on somebody else's map.
/// </summary>
/// <remarks>
/// The number, not the claim. Two members exchange through a relay running in this process,
/// over a client that adds sixty milliseconds of round trip, and one of them writes real
/// screenshot filenames into a folder the real watcher is watching. What is timed is the whole
/// distance a player cares about: the moment the file appears, to the moment the other member's
/// runtime snapshot carries the new position.
///
/// Both tests run the same measurement. They differ only in the wiring between the two ends —
/// <see cref="Wiring.Now"/> is what this branch does, and <see cref="Wiring.Before"/> is the
/// fixed waits it replaced: a watcher that polls once a second and reports a file only once its
/// pixels have stopped changing, a sender that publishes on a five-second tick, and a receiver
/// that learns about it on a tick of its own. The second is here so the first is a comparison
/// rather than an assertion, and so a regression back to any one of them is visible as a number.
/// </remarks>
public sealed class TeammatePositionLatencyTests(ITestOutputHelper output)
{
    /// <summary>A whole, tiny PNG, because the settled path insists on a complete envelope.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static readonly TimeSpan RoundTrip = TimeSpan.FromMilliseconds(60);

    private const string Key = "a-key-long-enough-for-a-room";

    /// <summary>How the two ends are joined together.</summary>
    private enum Wiring
    {
        /// <summary>Position on sight, published on change, collected on a held exchange.</summary>
        Now,

        /// <summary>Position once the pixels settle, published on a tick, collected on a tick.</summary>
        Before,
    }

    /// <summary>
    /// The budget, and a margin for a shared build box rather than for the design.
    /// </summary>
    /// <remarks>
    /// The measurement settles at 0.40 s median and about 0.45 s at p95, which is a quarter of a
    /// second of folder poll plus sixty milliseconds of round trip and very little else. The
    /// numbers asserted are well above that on purpose: this runs on a box other builds are
    /// using, and a test that fails when the machine is busy teaches nobody anything.
    /// </remarks>
    [Fact]
    public async Task ASquadmatesScreenshotMovesTheirMarkerInUnderASecond()
    {
        var measured = await MeasureAsync(Wiring.Now, samples: 9, budget: TimeSpan.FromSeconds(20));
        output.WriteLine($"Now: {measured}");

        Assert.True(
            measured.Median < TimeSpan.FromSeconds(1),
            $"Median was {measured.Median.TotalSeconds:0.00}s over {measured.Count} deliveries: {measured}");
        Assert.True(
            measured.Slowest95 < TimeSpan.FromSeconds(1.5),
            $"p95 was {measured.Slowest95.TotalSeconds:0.00}s over {measured.Count} deliveries: {measured}");
    }

    /// <summary>
    /// The same measurement, against the waits this replaced.
    /// </summary>
    /// <remarks>
    /// Three samples rather than nine, because each one costs about five seconds and that is the
    /// finding. If this ever passes the budget above, the delays are gone from the comparison
    /// rather than from the product, and the test above has stopped proving anything.
    /// </remarks>
    [Fact]
    public async Task TheThreeWaitsThisReplacedDoNotMeetTheSameBudget()
    {
        var measured = await MeasureAsync(Wiring.Before, samples: 3, budget: TimeSpan.FromSeconds(30));
        output.WriteLine($"Before: {measured}");

        Assert.True(
            measured.Median > TimeSpan.FromSeconds(3),
            $"Median was {measured.Median.TotalSeconds:0.00}s over {measured.Count} deliveries: {measured}");
    }

    /// <summary>
    /// [#707] The same budget when the player watching has already left their raid.
    /// </summary>
    /// <remarks>
    /// Clayton, out of a raid with a squadmate still in it: "pings and positions dont seem to sync
    /// now". The reading side has no in-raid pace of its own — the relay holds the exchange and a
    /// squadmate's change ends the hold — so a player in PostRaid should see the marker move as
    /// fast as one in the raid. This measures that, and that the one who left stops publishing
    /// the last screenshot of a raid that is over.
    /// </remarks>
    [Fact]
    public async Task APlayerWhoHasLeftTheRaidStillSeesTheirSquadmateMoveInUnderASecond()
    {
        var measured = await MeasureAsync(Wiring.Now, samples: 7, budget: TimeSpan.FromSeconds(20), receiverLeft: true);
        output.WriteLine($"Receiver out of raid: {measured}");

        Assert.True(
            measured.Median < TimeSpan.FromSeconds(1),
            $"Median was {measured.Median.TotalSeconds:0.00}s over {measured.Count} deliveries: {measured}");
        Assert.True(
            measured.Slowest95 < TimeSpan.FromSeconds(1.5),
            $"p95 was {measured.Slowest95.TotalSeconds:0.00}s over {measured.Count} deliveries: {measured}");
    }

    private static async Task<Measured> MeasureAsync(Wiring wiring, int samples, TimeSpan budget, bool receiverLeft = false)
    {
        var holds = wiring == Wiring.Now;
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-latency-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using var relay = await Relay.StartAsync(holds);
        using var stopping = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var sender = NewClient();
        using var receiver = NewClient();
        var sending = new RuntimeStateStore(Options);
        var receiving = new RuntimeStateStore(Options);
        var latencies = new List<TimeSpan>(samples);
        await using var ends = new Ends();
        try
        {
            _ = Task.Run(() => Positions(root, wiring, sending, stopping.Token), stopping.Token);
            if (receiverLeft)
            {
                // Extracted a moment ago: the raid is over, its last screenshot still in the snapshot.
                receiving.Update(current => current with
                {
                    Raid = current.Raid with
                    {
                        State = RaidLifecycleState.PostRaid,
                        MapId = "customs",
                        LastKnownPosition = new(
                            DateTimeOffset.UtcNow.AddSeconds(-20),
                            new(900, 0, 900),
                            default,
                            0,
                            null,
                            null,
                            "extract.png"),
                    },
                });
            }

            ends.Add(Member(wiring, relay.Address, "Alpha", sending, sender, receives: receiverLeft, stopping.Token));
            ends.Add(Member(wiring, relay.Address, "Bravo", receiving, receiver, receives: true, stopping.Token));
            if (holds)
            {
                // The game's log says a raid has started at the loading screen, well before
                // anybody can take a screenshot in it, so on a real installation the watcher is
                // already watching closely when the first one lands. There is no log watcher in
                // this harness, so the raid is stated directly and the first sample is measured
                // under the same conditions as the rest.
                sending.Update(current => current with
                {
                    Raid = current.Raid with { State = RaidLifecycleState.InRaid },
                });
                Assert.True(
                    await UntilAsync(
                        () => sending.Current.Group.IsSharing && receiving.Current.Group.IsSharing,
                        TimeSpan.FromSeconds(20),
                        stopping.Token),
                    "Both members should have reached the relay before anything is timed.");
                // The pace is chosen when each wait starts, so it takes hold within one idle
                // interval of the raid beginning. On a real installation that interval passes
                // during the loading screen — the log says the raid has started well before
                // anybody can photograph anything in it — so it is waited out here rather than
                // charged to the first screenshot, which no player could have taken that soon.
                await Task.Delay(TimeSpan.FromSeconds(1), stopping.Token);
            }

            for (var sample = 0; sample < samples; sample++)
            {
                // A different X each time, so "the marker moved" is a fact about this
                // screenshot rather than about any screenshot having arrived.
                var x = 100 + sample;
                var started = Stopwatch.GetTimestamp();
                Write(root, x);
                Assert.True(
                    await UntilAsync(() => Placed(receiving, x), budget, stopping.Token),
                    $"Sample {sample} never reached the other member within {budget.TotalSeconds:0}s.");
                latencies.Add(Stopwatch.GetElapsedTime(started));
                await Task.Delay(200, stopping.Token);
            }

            if (receiverLeft)
            {
                // The squadmate still inside sees the one who left, but not at their extract.
                Assert.True(
                    await UntilAsync(
                        () => sending.Current.Group.Members.Any(member => member.Name == "Bravo"),
                        TimeSpan.FromSeconds(10),
                        stopping.Token),
                    "Alpha should have heard from Bravo.");
                var bravo = sending.Current.Group.Members.Single(member => member.Name == "Bravo");
                Assert.Equal(RaidLifecycleState.PostRaid, bravo.RaidState);
                Assert.Null(bravo.Position);
            }
        }
        finally
        {
            await stopping.CancelAsync();
            Directory.Delete(root, recursive: true);
        }

        return Measured.Of(latencies);
    }

    /// <summary>Whether this member's map now carries Alpha at the position just written.</summary>
    private static bool Placed(RuntimeStateStore store, int x) =>
        store.Current.Group.Members.Any(member =>
            string.Equals(member.Name, "Alpha", StringComparison.Ordinal) &&
            member.Position is { } position &&
            Math.Abs(position.X - x) < 0.001);

    /// <summary>
    /// The sending member's own position pipeline: the real watcher, the real filename parser.
    /// </summary>
    /// <remarks>
    /// The one line that differs between the two wirings is which sighting the position is read
    /// from. That is the whole of the first delay.
    /// </remarks>
    private static async Task Positions(
        string root,
        Wiring wiring,
        RuntimeStateStore store,
        CancellationToken cancellationToken)
    {
        var raid = new RaidStateService();
        var parser = new ScreenshotFilenameParser();
        // The pace is half the point of the Now wiring: a raid with the group sharing is the
        // case where the folder is worth looking at four times a second. Before had one pace
        // and one second.
        var watcher = wiring == Wiring.Now
            ? new WindowsScreenshotWatcher(pacer: new ScreenshotWatchPacer(store))
            : new WindowsScreenshotWatcher();
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);
        await foreach (var sighting in watcher.WatchAsync(root, cancellationToken).ConfigureAwait(false))
        {
            if (sighting.IsSettled != (wiring == Wiring.Before))
            {
                continue;
            }

            if (parser.TryParseFile(sighting.Path, offset, out var position) && position is not null)
            {
                store.Update(current => current with { Raid = raid.ApplyPosition(position) });
            }
        }
    }

    /// <summary>One member of the group, exchanging the way this wiring exchanges.</summary>
    private static IAsyncDisposable? Member(
        Wiring wiring,
        string address,
        string name,
        RuntimeStateStore store,
        HttpClient client,
        bool receives,
        CancellationToken cancellationToken)
    {
        if (wiring == Wiring.Before)
        {
            _ = Task.Run(
                () => TickAsync(address, name, store, client, receives, cancellationToken),
                cancellationToken);
            return null;
        }

        var session = new GroupSessionService(
            new FixedSettings(address, name), store, client, NullLogger<GroupSessionService>.Instance);
        session.Start();
        return session;
    }

    /// <summary>
    /// One member on a five-second tick, which is what both ends used to do.
    /// </summary>
    /// <remarks>
    /// Written out here rather than borrowed from the session service, because the session
    /// service no longer behaves this way and the point of the comparison is the cadence. It
    /// sends the same field names the relay has always read and reads the same ones back.
    /// </remarks>
    private static async Task TickAsync(
        string address,
        string name,
        RuntimeStateStore store,
        HttpClient client,
        bool receives,
        CancellationToken cancellationToken)
    {
        var state = new Uri(new Uri(address), "state");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var raid = store.Current.Raid;
                var position = raid.LastKnownPosition;
                using var request = new HttpRequestMessage(HttpMethod.Post, state)
                {
                    Content = JsonContent.Create(new
                    {
                        name,
                        mapId = raid.MapId,
                        raidState = raid.State.ToString(),
                        x = position?.Position.X,
                        y = position?.Position.Y,
                        z = position?.Position.Z,
                        heading = position?.HeadingDegrees,
                        positionAge = position is null
                            ? null
                            : (double?)(DateTimeOffset.UtcNow - position.Timestamp.ToUniversalTime()).TotalSeconds,
                    }),
                };
                request.Headers.Add("X-Group-Key", Key);
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (receives && response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    store.Update(current => current with { Group = ReadRoom(body) });
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The same thing the real loop does with a bad exchange: nothing.
            }

            try
            {
                await Task.Delay(GroupPublishing.Interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Just enough of the room to know where somebody is.</summary>
    private static GroupSnapshot ReadRoom(string body)
    {
        using var document = JsonDocument.Parse(body);
        var members = new List<GroupMemberView>();
        if (document.RootElement.TryGetProperty("members", out var listed))
        {
            foreach (var member in listed.EnumerateArray())
            {
                var x = member.TryGetProperty("x", out var left) && left.ValueKind == JsonValueKind.Number
                    ? left.GetDouble()
                    : (double?)null;
                var z = member.TryGetProperty("z", out var down) && down.ValueKind == JsonValueKind.Number
                    ? down.GetDouble()
                    : (double?)null;
                members.Add(new(
                    member.GetProperty("name").GetString() ?? string.Empty,
                    null,
                    RaidLifecycleState.InRaid,
                    null,
                    x is { } placed && z is { } depth ? new(placed, 0, depth) : null,
                    null,
                    null,
                    [],
                    []));
            }
        }

        return new(true, members, "Sharing", DateTimeOffset.UtcNow);
    }

    /// <summary>The game's own filename shape, with the coordinates this sample is testing.</summary>
    private static void Write(string root, int x)
    {
        var now = DateTime.Now;
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd}[{now:HH-mm}]_{x}, 2, 3_0, 0, 0, 1 ({x}).png");
        File.WriteAllBytes(Path.Combine(root, name), Png);
    }

    private static async Task<bool> UntilAsync(Func<bool> ready, TimeSpan budget, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(deadline) < budget)
        {
            if (ready())
            {
                return true;
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }

        return ready();
    }

    private static HttpClient NewClient() =>
        new(new SlowLink(new SocketsHttpHandler())) { Timeout = Timeout.InfiniteTimeSpan };

    private static RuntimeOptions Options { get; } = new(
        false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(9),
        TimeSpan.FromMinutes(5));

    /// <summary>Half the round trip on the way out and half on the way back.</summary>
    private sealed class SlowLink(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(RoundTrip / 2, cancellationToken).ConfigureAwait(false);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await Task.Delay(RoundTrip / 2, cancellationToken).ConfigureAwait(false);
            return response;
        }
    }

    /// <summary>
    /// A relay in this process, serving the route the deployed one serves.
    /// </summary>
    /// <remarks>
    /// <paramref name="holds"/> false stands in for a relay that predates this work: the same
    /// publish-and-read exchange, ignoring the hold and answering with no revision. It is here
    /// to measure the wait it had, and it doubles as the compatibility case — a current client
    /// against it must keep to its own tick rather than spin.
    /// </remarks>
    private sealed class Relay(WebApplication app, string address) : IAsyncDisposable
    {
        public string Address { get; } = address;

        public static async Task<Relay> StartAsync(bool holds)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<GroupRooms>();
            builder.Services.AddSingleton<GroupRoomChanges>();
            builder.Services.AddSingleton(provider => new GroupMarks(provider.GetRequiredService<TimeProvider>()));
            var app = builder.Build();
            var rooms = app.Services.GetRequiredService<GroupRooms>();
            var marks = app.Services.GetRequiredService<GroupMarks>();
            if (holds)
            {
                app.MapGroupRoomState(rooms, marks, app.Services.GetRequiredService<GroupRoomChanges>());
            }
            else
            {
                app.MapPost("/state", (GroupMemberState state, HttpRequest request) =>
                {
                    if (!GroupKey.TryRead(request, out var key) || state.Validate() is not null)
                    {
                        return Results.BadRequest();
                    }

                    var room = GroupKey.RoomFor(key);
                    rooms.Publish(room, state.Name, state);
                    var (waypoints, pings) = marks.Read(room);
                    return Results.Ok(rooms.Read(room, state.Name) with
                    {
                        Waypoints = waypoints,
                        Pings = pings,
                    });
                });
            }

            await app.StartAsync();
            return new(app, app.Urls.First() + "/");
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>Settings that never change, pointing at the relay this test started.</summary>
    private sealed class FixedSettings(string address, string name) : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(true, address, name, Key, false, false));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>Whatever the wiring started, stopped at the end of the measurement.</summary>
    private sealed class Ends : IAsyncDisposable
    {
        private readonly List<IAsyncDisposable> _started = [];

        public void Add(IAsyncDisposable? started)
        {
            if (started is not null)
            {
                _started.Add(started);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var started in _started)
            {
                await started.DisposeAsync();
            }
        }
    }

    /// <summary>What the samples came to.</summary>
    private readonly record struct Measured(
        int Count,
        TimeSpan Median,
        TimeSpan Slowest95,
        TimeSpan Fastest,
        string Samples)
    {
        public static Measured Of(IReadOnlyList<TimeSpan> samples)
        {
            var ordered = samples.OrderBy(sample => sample).ToArray();
            return new(
                ordered.Length,
                At(ordered, 0.50),
                At(ordered, 0.95),
                ordered[0],
                string.Join(
                    " ",
                    samples.Select(one => one.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture))));
        }

        private static TimeSpan At(IReadOnlyList<TimeSpan> ordered, double fraction) =>
            ordered[Math.Clamp((int)Math.Ceiling(fraction * ordered.Count) - 1, 0, ordered.Count - 1)];

        public override string ToString() => string.Create(
            CultureInfo.InvariantCulture,
            $"fastest {Fastest.TotalSeconds:0.00}s, median {Median.TotalSeconds:0.00}s, p95 {Slowest95.TotalSeconds:0.00}s [{Samples}]");
    }
}
