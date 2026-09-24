using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.Compatibility;

/// <summary>
/// [#294] The group exchange across builds: older desktops against the current relay, and the
/// current desktop against older and newer relays.
/// </summary>
/// <remarks>
/// The relay is redeployed on its own schedule and every player updates when they update, so on
/// any evening one squad can hold three generations. Everything on this wire is meant to be
/// additive (docs/GROUP_RELAY.md, "Which version everything speaks"); these tests hold it to that
/// with the messages the older builds actually wrote, rather than with today's types.
/// </remarks>
public sealed class GroupRelayVersionMatrixTests
{
    // What ASP.NET's minimal APIs bind and write with, and what every desktop build has used.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("group-publish-v2-rough-1.json", "OldClay")]
    [InlineData("group-publish-53a3b743.json", "MidClay")]
    public async Task AnOlderDesktopsPublishIsAcceptedAndReachesACurrentSquadmate(string fixture, string name)
    {
        var relay = new InProcessRelay();
        var old = CompatibilityFixtures.Node(fixture);

        var refused = await relay.PublishAsync(CompatibilityFixtures.Read(fixture));
        var reply = await relay.PublishAsync(CurrentMember("Geo"));

        Assert.Null(refused.Refusal);
        var member = Assert.Single(reply.Room!.Members);
        Assert.Equal(name, member.Name);
        Assert.Equal(old["x"]!.GetValue<double>(), member.X);
        Assert.Equal(old["z"]!.GetValue<double>(), member.Z);
        Assert.Equal(["quest-debut"], member.QuestIds);
        // Fields that client never sent read as "not said", never as a refusal or a default claim.
        Assert.Null(member.Ready);
        Assert.Null(member.PlannedExtract);
        Assert.Null(member.Note);
        Assert.Equal(old["gameMode"]?.GetValue<string>(), member.GameMode);
    }

    /// <summary>
    /// The current relay's reply, read with the oldest desktop's own reader, still carries
    /// everything that reader binds.
    /// </summary>
    [Fact]
    public async Task ACurrentRelayReplyStillParsesInTheOldestDesktop()
    {
        var relay = new InProcessRelay();
        await relay.PublishAsync(CompatibilityFixtures.Read("group-publish-v2-rough-1.json"));
        var mark = JsonSerializer.Deserialize<MarkRequest>(CompatibilityFixtures.Read("group-mark-v2-rough-1.json"), Web)!;
        Assert.Null(mark.Validate());
        relay.AddWaypoint(mark);
        relay.AddPing(mark with { Label = null });
        await relay.PublishAsync(CurrentMember("Geo"));

        var reply = await relay.PublishAsync(CompatibilityFixtures.Read("group-publish-v2-rough-1.json"));
        var room = JsonSerializer.Deserialize<Rough1RoomStateDto>(reply.Json!, Web)!;

        var geo = Assert.Single(room.Members);
        Assert.Equal("Geo", geo.Name);
        Assert.Equal("InRaid", geo.RaidState);
        Assert.Equal("customs", geo.MapId);
        Assert.Equal(12.5, geo.X);
        Assert.Equal(44.0, geo.Z);
        Assert.Equal(["Debut"], geo.Quests);
        Assert.Equal(["quest-debut"], geo.QuestIds);
        Assert.NotNull(geo.SinceSeconds);
        var waypoint = Assert.Single(room.Waypoints);
        Assert.Equal(("OldClay", "customs", "Dorms"), (waypoint.By, waypoint.MapId, waypoint.Label));
        Assert.NotEqual(default, waypoint.CreatedUtc);
        Assert.Equal("OldClay", Assert.Single(room.Pings).By);
        Assert.Equal(1, room.Protocol);
    }

    /// <summary>A 53a3b743 desktop gets back the quest sync and game mode it reads.</summary>
    [Fact]
    public async Task ACurrentRelayHandsA53a3b743DesktopItsQuestSyncAndGameMode()
    {
        var relay = new InProcessRelay();
        await relay.PublishAsync(CompatibilityFixtures.Read("group-publish-53a3b743.json"));
        await relay.PublishAsync(CurrentMember("Geo"));

        var reply = await relay.PublishAsync(CompatibilityFixtures.Read("group-publish-53a3b743.json"));
        var geo = JsonNode.Parse(reply.Json!)!["members"]![0]!;

        Assert.Equal("pvp", geo["gameMode"]!.GetValue<string>());
        Assert.Equal("quest-debut", geo["objectives"]![0]!["task"]!.GetValue<string>());
        Assert.Equal("objective-shoot", geo["objectives"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(2, geo["objectives"]![0]!["count"]!.GetValue<decimal>());
        Assert.NotNull(JsonNode.Parse(reply.Json!)!["revision"]);
    }

    [Theory]
    [InlineData("group-reply-v2-rough-1.json", "OldGeo")]
    [InlineData("group-reply-53a3b743.json", "MidGeo")]
    [InlineData("group-reply-newer.json", "NewGeo")]
    public async Task TheCurrentDesktopReadsEveryRelayGeneration(string fixture, string name)
    {
        var reply = CompatibilityFixtures.Node(fixture);
        var handler = new FixedRelay(CompatibilityFixtures.Read(fixture));
        await using var session = Session(handler, null, out var store);

        session.Start();
        Assert.True(await WaitAsync(() => store.Current.Group.Members.Any(member => member.Name == name)), $"{name} never appeared.");

        var group = store.Current.Group;
        var member = group.Members.Single(item => item.Name == name);
        var sent = reply["members"]![0]!;
        Assert.True(group.IsSharing);
        Assert.Equal("customs", member.MapId);
        Assert.Equal(12.5, member.Position!.Value.X);
        Assert.Equal(44.0, member.Position.Value.Z);
        Assert.Equal(["quest-debut"], member.QuestIds);
        Assert.Equal(sent["ready"]?.GetValue<bool>(), member.Ready);
        Assert.Equal(sent["plannedExtract"]?.GetValue<string>(), member.PlannedExtract);
        Assert.Equal(sent["note"]?.GetValue<string>(), member.Note);
        Assert.Equal(sent["gameMode"]?.GetValue<string>(), member.GameMode);
        Assert.Equal(sent["objectives"]?.AsArray().Count ?? 0, member.Objectives.Count);
        Assert.Equal(reply["waypoints"]!.AsArray().Count, group.Waypoints.Count);
        Assert.Equal(reply["pings"]!.AsArray().Count, group.Pings.Count);
        Assert.DoesNotContain("Relay speaks", group.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A relay that raised the protocol number is named on the group line, and the squad stays on
    /// the map: a skew is something to say, not a reason to stop sharing.
    /// </summary>
    [Fact]
    public async Task ARelayOnANewerProtocolKeepsTheSquadVisibleAndSaysSo()
    {
        var reply = CompatibilityFixtures.Node("group-reply-newer.json");
        reply["protocol"] = 2;
        await using var session = Session(new FixedRelay(reply.ToJsonString()), null, out var store);

        session.Start();
        Assert.True(await WaitAsync(() => store.Current.Group.Members.Any(member => member.Name == "NewGeo")));

        Assert.True(store.Current.Group.IsSharing);
        Assert.Contains("Relay speaks 2", store.Current.Group.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// What the current desktop publishes still binds in an older relay: every field the older
    /// desktop sent is still sent, as the same kind of JSON value, so the older relay's binder reads
    /// it; the fields added since are extra, which System.Text.Json ignores by default.
    /// </summary>
    [Theory]
    [InlineData("group-publish-v2-rough-1.json")]
    [InlineData("group-publish-53a3b743.json")]
    public async Task TheCurrentDesktopsPublishCarriesEverythingAnOlderRelayBinds(string fixture)
    {
        var handler = new FixedRelay(CompatibilityFixtures.Read("group-reply-53a3b743.json"));
        var status = new GroupSquadStatus();
        status.Set(new SquadStatus(true, "ZB-1011", "customs", "rotating north"));
        await using var session = Session(handler, status, out _);

        session.Start();
        Assert.True(await WaitAsync(() => !handler.Published.IsEmpty));

        var current = JsonNode.Parse(handler.Published.First())!.AsObject();
        var old = CompatibilityFixtures.Node(fixture).AsObject();
        foreach (var (key, value) in old)
        {
            Assert.True(current.TryGetPropertyValue(key, out var now), $"The current desktop no longer sends \"{key}\".");
            if (value is not null && now is not null)
            {
                Assert.Equal(value.GetValueKind(), now.GetValueKind());
            }
        }

        // Within the older relay's own bounds, which it refuses a publish for breaking.
        Assert.InRange(current["name"]!.GetValue<string>().Length, 1, 48);
        Assert.NotNull(current["raidState"]);
        Assert.True(current["ready"]!.GetValue<bool>());
    }

    private static string CurrentMember(string name) => JsonSerializer.Serialize(
        new GroupMemberState(name, "customs", "InRaid", "pmc", 12.5, 44.0, 90, 4, [], ["Debut"])
        {
            QuestIds = ["quest-debut"],
            GameMode = "pvp",
            Objectives = [new GroupObjectiveState("quest-debut", "objective-shoot") { Count = 2 }],
            Ready = true,
            PlannedExtract = "ZB-1011",
            Note = "rotating north",
        },
        Web);

    private static GroupSessionService Session(HttpMessageHandler handler, GroupSquadStatus? status, out RuntimeStateStore store)
    {
        store = new(new(false, Offline: true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5)));
        return new(
            new StubSettings(),
            store,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance,
            status: status);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(100);
        }

        return condition();
    }

    private sealed record Published(GroupRoomState? Room, string? Json, string? Refusal);

    /// <summary>The current relay's own exchange and mark code, in process, against one set of rooms.</summary>
    private sealed class InProcessRelay
    {
        private const string Key = "a-key-long-enough";
        private readonly GroupRooms _rooms = new(TimeProvider.System);
        private readonly GroupMarks _marks = new(TimeProvider.System);
        private readonly GroupRoomChanges _changes = new(TimeProvider.System);
        private readonly string _room;

        public InProcessRelay()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Group-Key"] = Key;
            Assert.True(GroupKey.TryRead(context.Request, out var key));
            _room = GroupKey.RoomFor(key);
        }

        public async Task<Published> PublishAsync(string body)
        {
            var state = JsonSerializer.Deserialize<GroupMemberState>(body, Web)!;
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Group-Key"] = Key;
            var result = await RelayRoomStateRoutes.PublishAndReadAsync(_rooms, _marks, _changes, state, context.Request, CancellationToken.None);
            return result.Result switch
            {
                Ok<GroupRoomState> ok => new(ok.Value, JsonSerializer.Serialize(ok.Value, Web), null),
                BadRequest<string> refused => new(null, null, refused.Value),
                _ => new(null, null, "unauthorized"),
            };
        }

        // The two mark routes are inline in Program.cs; these are the calls they make once a body validates.
        public void AddWaypoint(MarkRequest mark) =>
            _marks.AddWaypoint(_room, mark.By, mark.MapId, mark.X, mark.Y, mark.Z, mark.Label);

        public void AddPing(MarkRequest mark) =>
            _marks.AddPing(_room, mark.By, mark.MapId, mark.X, mark.Y, mark.Z, mark.Label);
    }

    /// <summary>A relay of another build: answers every exchange with one recorded reply.</summary>
    private sealed class FixedRelay(string reply) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Published { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/state")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            Published.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubSettings : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(true, "https://relay.example.test/", "Clay", "a-key-long-enough", false, true));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // The v2-rough-1 desktop's reader (GroupSessionService.RoomStateDto and the records under it at
    // that tag), reproduced so the current relay's reply is parsed exactly as that build parses it.
    private sealed record Rough1RoomStateDto(
        [property: JsonPropertyName("room")] string Room,
        [property: JsonPropertyName("members")] IReadOnlyList<Rough1MemberStateDto> Members)
    {
        [JsonPropertyName("waypoints")]
        public IReadOnlyList<Rough1WaypointDto> Waypoints { get; init; } = [];

        [JsonPropertyName("pings")]
        public IReadOnlyList<Rough1PingDto> Pings { get; init; } = [];

        [JsonPropertyName("protocol")]
        public int? Protocol { get; init; }
    }

    private sealed record Rough1MemberStateDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("mapId")] string? MapId,
        [property: JsonPropertyName("raidState")] string RaidState,
        [property: JsonPropertyName("side")] string? Side,
        [property: JsonPropertyName("x")] double? X,
        [property: JsonPropertyName("z")] double? Z,
        [property: JsonPropertyName("heading")] double? Heading,
        [property: JsonPropertyName("positionAge")] double? PositionAge,
        [property: JsonPropertyName("loadout")] IReadOnlyList<string>? Loadout,
        [property: JsonPropertyName("quests")] IReadOnlyList<string>? Quests)
    {
        [JsonPropertyName("sinceSeconds")]
        public double? SinceSeconds { get; init; }

        [JsonPropertyName("questIds")]
        public IReadOnlyList<string>? QuestIds { get; init; }

        [JsonPropertyName("y")]
        public double? Y { get; init; }

        [JsonPropertyName("observed")]
        public IReadOnlyList<Rough1ObservedKitDto>? Observed { get; init; }

        [JsonPropertyName("trail")]
        public IReadOnlyList<Rough1TrailPointDto>? Trail { get; init; }

        [JsonPropertyName("extracts")]
        public IReadOnlyList<string>? Extracts { get; init; }

        [JsonPropertyName("transits")]
        public IReadOnlyList<string>? Transits { get; init; }

        [JsonPropertyName("raidClockSeconds")]
        public double? RaidClockSeconds { get; init; }

        [JsonPropertyName("raidClockAge")]
        public double? RaidClockAgeSeconds { get; init; }
    }

    private sealed record Rough1TrailPointDto(
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("age")] double AgeSeconds)
    {
        [JsonPropertyName("y")]
        public double? Y { get; init; }
    }

    private sealed record Rough1ObservedKitDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("loadout")] IReadOnlyList<string>? Loadout)
    {
        [JsonPropertyName("level")]
        public int? Level { get; init; }

        [JsonPropertyName("side")]
        public string? Side { get; init; }

        [JsonPropertyName("scavLockedUntil")]
        public long? ScavLockedUntilUnix { get; init; }
    }

    private sealed record Rough1WaypointDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("by")] string By,
        [property: JsonPropertyName("mapId")] string MapId,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("completedBy")] string? CompletedBy)
    {
        [JsonPropertyName("createdUtc")]
        public DateTimeOffset CreatedUtc { get; init; }
    }

    private sealed record Rough1PingDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("by")] string By,
        [property: JsonPropertyName("mapId")] string MapId,
        [property: JsonPropertyName("x")] double X,
        [property: JsonPropertyName("y")] double Y,
        [property: JsonPropertyName("z")] double Z,
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc);
}
