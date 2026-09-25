using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// [#707] One player has left the raid and a squadmate has not: what each end publishes, and who
/// the player who left should be watching.
/// </summary>
public sealed class SquadAfterRaidTests
{
    /// <summary>
    /// Out of the raid, the last screenshot is where the player was, not where they are.
    /// </summary>
    /// <remarks>
    /// Fails on main: Describe published raid.LastKnownPosition in every state, so the squadmate
    /// still inside saw a live-looking "you" marker at the extract for the rest of their raid.
    /// </remarks>
    [Theory]
    [InlineData(RaidLifecycleState.PostRaid)]
    [InlineData(RaidLifecycleState.Menu)]
    public async Task APlayerWhoHasLeftTheRaidPublishesNoPositionOrTrail(RaidLifecycleState left)
    {
        var bodies = new ConcurrentQueue<string>();
        await using var service = Service(bodies, _ => Json("""{"members":[],"revision":1}"""), out var store);
        store.Update(current => current with
        {
            Raid = current.Raid with
            {
                State = left,
                MapId = "customs",
                LastKnownPosition = Somewhere(),
                PositionTrail = [Somewhere(-2), Somewhere(-1), Somewhere()],
                RaidClock = TimeSpan.FromSeconds(1777),
                RaidClockReadUtc = DateTimeOffset.UtcNow.AddMinutes(-3),
            },
        });

        service.Start();
        await WaitUntilAsync(() => !bodies.IsEmpty);

        var body = Assert.Single(bodies.Take(1));
        // #886: nor the raid clock, which PostRaid keeps and whose growing age made every
        // publish after the raid a room change.
        Assert.DoesNotContain("1777", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"raidClockAge\":1", body, StringComparison.Ordinal);
        Assert.Contains($"\"raidState\":\"{left}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"x\":null", body, StringComparison.Ordinal);
        Assert.DoesNotContain("12.5", body, StringComparison.Ordinal);
        Assert.Contains("\"trail\":[]", body, StringComparison.Ordinal);
    }

    /// <summary>The control: in a raid, the same snapshot publishes its position.</summary>
    [Fact]
    public async Task APlayerInARaidStillPublishesTheirPosition()
    {
        var bodies = new ConcurrentQueue<string>();
        await using var service = Service(bodies, _ => Json("""{"members":[],"revision":1}"""), out var store);
        store.Update(current => current with
        {
            Raid = current.Raid with
            {
                State = RaidLifecycleState.InRaid,
                MapId = "customs",
                LastKnownPosition = Somewhere(),
            },
        });

        service.Start();
        await WaitUntilAsync(() => !bodies.IsEmpty);

        Assert.Contains("\"x\":12.5", bodies.First(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A mark sent to the relay comes back with the id the relay gave it, so it can be taken off
    /// again when it is taken off the map.
    /// </summary>
    [Fact]
    public async Task AMarkSentToTheGroupAnswersWithTheRelaysId()
    {
        var bodies = new ConcurrentQueue<string>();
        await using var service = Service(
            bodies,
            request => request.RequestUri!.AbsolutePath.EndsWith("/pings", StringComparison.Ordinal)
                ? Json("""{"id":42,"by":"Clay","mapId":"customs","x":1,"y":2,"z":3,"label":null,"createdUtc":"2026-09-23T00:00:00Z"}""")
                : Json("""{"members":[]}"""),
            out _);

        var id = await service.SendMarkAsync("customs", new WorldPosition(1, 2, 3), null, isPing: true, CancellationToken.None);

        Assert.Equal(42, id);
        Assert.Contains(bodies, body => body.Contains("\"mapId\":\"customs\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMarkTheRelayRefusesAnswersWithNoId()
    {
        await using var service = Service(
            new ConcurrentQueue<string>(),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            out _);

        Assert.Null(await service.SendMarkAsync("customs", new WorldPosition(1, 2, 3), null, isPing: false, CancellationToken.None));
    }

    [Fact]
    public void AnExtractedPlayerWatchesTheSquadmateStillInside()
    {
        var watched = SquadRaidPresence.StillInRaid(
            RaidLifecycleState.PostRaid,
            [Member("Riley", "customs", RaidLifecycleState.PostRaid), Member("Geo", "customs", RaidLifecycleState.InRaid)]);

        Assert.Equal("Geo", watched?.Name);
    }

    [Fact]
    public void APlayerInTheirOwnRaidWatchesNobodyElse()
    {
        Assert.Null(SquadRaidPresence.StillInRaid(
            RaidLifecycleState.InRaid,
            [Member("Geo", "woods", RaidLifecycleState.InRaid)]));
        Assert.Null(SquadRaidPresence.StillInRaid(
            RaidLifecycleState.LoadingRaid,
            [Member("Geo", "woods", RaidLifecycleState.InRaid)]));
    }

    [Fact]
    public void WhenTheLastSquadmateLeavesThereIsNobodyToWatch()
    {
        Assert.Null(SquadRaidPresence.StillInRaid(
            RaidLifecycleState.Menu,
            [Member("Geo", "customs", RaidLifecycleState.PostRaid), Member("Sam", null, RaidLifecycleState.Menu)]));
    }

    [Fact]
    public void TheSquadmateHeardFromMostRecentlyIsPreferred()
    {
        var watched = SquadRaidPresence.StillInRaid(
            RaidLifecycleState.Menu,
            [
                Member("Geo", "customs", RaidLifecycleState.InRaid) with { Since = TimeSpan.FromSeconds(40) },
                Member("Sam", "woods", RaidLifecycleState.InRaid) with { Since = TimeSpan.FromSeconds(1) },
            ]);

        Assert.Equal("Sam", watched?.Name);
    }

    [Theory]
    [InlineData(RaidLifecycleState.InRaid, true)]
    [InlineData(RaidLifecycleState.Unknown, true)]
    [InlineData(RaidLifecycleState.LauncherOrGameDetected, true)]
    [InlineData(RaidLifecycleState.PostRaid, false)]
    [InlineData(RaidLifecycleState.Menu, false)]
    public void OnlyASquadmateWhoHasSaidTheyLeftIsTakenOffTheMap(RaidLifecycleState state, bool placed) =>
        Assert.Equal(placed, SquadRaidPresence.IsPlaced(Member("Geo", "customs", state)));

    private static GroupMemberView Member(string name, string? mapId, RaidLifecycleState state) =>
        new(name, mapId, state, "PMC", new WorldPosition(1, 0, 1), 0, TimeSpan.FromSeconds(3), [], []);

    private static ScreenshotPosition Somewhere(int step = 0) => new(
        DateTimeOffset.UtcNow.AddSeconds(step),
        new WorldPosition(12.5 + (step * 0.25), 0, 3.5),
        new QuaternionOrientation(0, 0, 0, 1),
        0,
        null,
        null,
        $"shot-{step}.png");

    private static GroupSessionService Service(
        ConcurrentQueue<string> bodies,
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        out RuntimeStateStore store)
    {
        store = new(new(
            false,
            Offline: true,
            GameMode.Regular,
            "en",
            TimeSpan.FromHours(9),
            TimeSpan.FromMinutes(5)));
        return new(
            new FixedSettings(),
            store,
            new HttpClient(new RecordingHandler(bodies, respond)) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 200 && !ready(); attempt++)
        {
            await Task.Delay(25);
        }
    }

    private sealed class RecordingHandler(
        ConcurrentQueue<string> bodies,
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is { } content)
            {
                bodies.Enqueue(await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            return respond(request);
        }
    }

    private sealed class FixedSettings : IGroupSettingsStore
    {
        private static readonly GroupSharingSettings Settings = new(
            true,
            "https://relay.example.test/",
            "Clay",
            "a-key-long-enough",
            false,
            false);

        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Settings);

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
