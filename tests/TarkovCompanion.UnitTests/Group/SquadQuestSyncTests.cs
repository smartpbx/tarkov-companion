using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// [#780] Quest sync across the squad: a member's active quests and open objectives reach the
/// others by id, through the relay's own exchange route, and a change goes out at once.
/// </summary>
public sealed partial class SquadQuestSyncTests
{
    [Fact]
    public async Task OpenObjectivesTravelThroughTheRelayToASquadmate()
    {
        var relay = new InProcessRelay();
        var board = new Board(Quest("debut", RecordedTaskState.Active,
            Objective("shoot-scavs", RecordedObjectiveState.Unknown, count: 3),
            Objective("find-bottles", RecordedObjectiveState.Completed)));
        var clayShare = new GroupQuestShare(new StubProfiles(), board);
        await using var clay = Session(relay, "Clay", clayShare, out _);
        await using var geo = Session(relay, "Geo", null, out var geoStore);

        clay.Start();
        geo.Start();
        var seen = await WaitAsync(() => geoStore.Current.Group.Members.FirstOrDefault(member => member.Name == "Clay") is { Objectives.Count: > 0 });

        Assert.True(seen, "Geo never received Clay's objectives.");
        var member = geoStore.Current.Group.Members.Single(item => item.Name == "Clay");
        Assert.Equal(["debut"], member.QuestIds);
        var objective = Assert.Single(member.Objectives);
        Assert.Equal(new GroupObjectiveView("debut", "shoot-scavs", 3), objective);
    }

    /// <summary>
    /// #786: a running session with quest sharing on, mid-hold at the relay, closes in under a second.
    /// </summary>
    [Fact]
    public async Task ASharingSessionHeldAtTheRelayClosesWithinASecond()
    {
        var relay = new InProcessRelay();
        var board = new Board(Quest("debut", RecordedTaskState.Active, Objective("shoot-scavs", RecordedObjectiveState.Unknown)));
        var clay = Session(relay, "Clay", new GroupQuestShare(new StubProfiles(), board), out _);
        await using var geo = Session(relay, "Geo", null, out var geoStore);
        clay.Start();
        geo.Start();
        Assert.True(await WaitAsync(() => geoStore.Current.Group.Members.Any(member => member.Name == "Clay")));
        // Into the relay's hold: nothing changes in the room now, so the next exchange waits.
        await System.Threading.Tasks.Task.Delay(500);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await clay.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"Closing took {watch.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// #786: a relay that stops answering cannot hold the application's close on the goodbye.
    /// </summary>
    [Fact]
    public async Task ARelayThatStopsAnsweringDoesNotHoldTheClose()
    {
        var answering = true;
        var calls = 0;
        var counting = new StubHandler(async (request, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            if (!Volatile.Read(ref answering) || request.RequestUri!.Query.Contains("wait=", StringComparison.Ordinal))
            {
                // A relay holding the answer, and then one that has gone away altogether.
                await System.Threading.Tasks.Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"members":[],"revision":1}""", Encoding.UTF8, "application/json"),
            };
        });
        var board = new Board(Quest("debut", RecordedTaskState.Active, Objective("shoot-scavs", RecordedObjectiveState.Unknown)));
        var service = new GroupSessionService(
            new StubSettings("Clay"),
            Store(),
            new HttpClient(counting) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance,
            new GroupQuestShare(new StubProfiles(), board));
        service.Start();
        Assert.True(await WaitAsync(() => Volatile.Read(ref calls) >= 2), "The held exchange never started.");
        Volatile.Write(ref answering, false);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        // The goodbye gets 500 ms; 1.5 s leaves room for a loaded CI runner (1,059 ms was seen) and
        // still fails the old 2 s allowance this test exists to keep out.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"Closing took {watch.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// [#269] A PvE squadmate's quests are not this PvP player's quests: they arrive with none, and
    /// with their mode named so the Team page can say why.
    /// </summary>
    [Fact]
    public async Task ASquadmateOnAnotherModeArrivesWithoutQuests()
    {
        var relay = new InProcessRelay();
        var board = new Board(Quest("debut", RecordedTaskState.Active, Objective("shoot-scavs", RecordedObjectiveState.Unknown)));
        await using var clay = Session(relay, "Clay", new GroupQuestShare(new StubProfiles(GameMode.Pve), board), out _);
        await using var geo = Session(relay, "Geo", new GroupQuestShare(new StubProfiles(GameMode.Regular), board), out var geoStore);

        clay.Start();
        geo.Start();
        var seen = await WaitAsync(() => geoStore.Current.Group.Members.FirstOrDefault(member => member.Name == "Clay") is { GameMode: not null });

        Assert.True(seen, "Geo never heard Clay's game mode.");
        var member = geoStore.Current.Group.Members.Single(item => item.Name == "Clay");
        Assert.Equal("pve", member.GameMode);
        Assert.Equal("pvp", geoStore.Current.Group.MyGameMode);
        Assert.Empty(member.QuestIds);
        Assert.Empty(member.Quests);
        Assert.Empty(member.Objectives);
        Assert.Equal(
            "Clay is on PvE; this profile is PvP. Quests are not shared across modes.",
            GroupModeCheck.Warning(geoStore.Current.Group.MyGameMode, geoStore.Current.Group.Members));
    }

    [Fact]
    public void TheRelayRefusesAGameModeLongerThanItsBound()
    {
        var state = new GroupMemberState("Clay", null, "Menu", null, null, null, null, null, [], []) { GameMode = "pve" };

        Assert.Null(state.Validate());
        Assert.NotNull((state with { GameMode = new string('x', 17) }).Validate());
    }

    [Fact]
    public async Task AnOlderCompanionWithoutObjectivesIsStillReadAsNoObjectives()
    {
        var relay = new InProcessRelay();
        // What a build before #780 publishes: quest names and ids, no objectives field at all.
        var older = """{"name":"Riley","mapId":"customs","raidState":"InRaid","loadout":[],"quests":["Debut"],"questIds":["debut"]}""";
        var accepted = relay.Publish(older);
        await using var clay = Session(relay, "Clay", null, out var clayStore);

        clay.Start();
        await WaitAsync(() => clayStore.Current.Group.Members.Count > 0);

        Assert.Null(accepted);
        var member = Assert.Single(clayStore.Current.Group.Members);
        Assert.Equal(["debut"], member.QuestIds);
        Assert.Empty(member.Objectives);
    }

    [Fact]
    public void AnOlderReaderIgnoresTheNewField()
    {
        var state = new GroupMemberState("Clay", "customs", "InRaid", null, null, null, null, null, [], [])
        {
            Objectives = [new GroupObjectiveState("debut", "shoot-scavs") { Count = 2 }],
        };

        var json = JsonSerializer.Serialize(state);
        var older = JsonSerializer.Deserialize<OlderMember>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"objectives\"", json, StringComparison.Ordinal);
        Assert.Equal("Clay", older!.Name);
    }

    [Fact]
    public void TheRelayRefusesMoreObjectivesThanItsBound()
    {
        var state = new GroupMemberState("Clay", null, "Menu", null, null, null, null, null, [], [])
        {
            Objectives = [.. Enumerable.Range(0, 61).Select(index => new GroupObjectiveState("t", $"o{index}"))],
        };

        Assert.NotNull(state.Validate());
        Assert.Null((state with { Objectives = state.Objectives.Take(60).ToArray() }).Validate());
    }

    /// <summary>
    /// A quest change is sent at once, not when the relay's five-second hold runs out.
    /// </summary>
    [Fact]
    public async Task AQuestChangeCutsTheHoldShortAndSendsTheNewObjectives()
    {
        var board = new Board(Quest("debut", RecordedTaskState.Active, Objective("shoot-scavs", RecordedObjectiveState.Unknown)));
        var share = new GroupQuestShare(new StubProfiles(), board);
        var bodies = new List<(DateTimeOffset At, string Body)>();
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (bodies)
            {
                bodies.Add((DateTimeOffset.UtcNow, body));
            }

            if (request.RequestUri!.Query.Contains("wait=", StringComparison.Ordinal))
            {
                // A relay holding the answer: nothing changes in the room, so it never answers.
                await System.Threading.Tasks.Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"members":[],"revision":1}""", Encoding.UTF8, "application/json"),
            };
        });
        await using var service = new GroupSessionService(
            new StubSettings("Clay"),
            Store(),
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance,
            share);

        service.Start();
        Assert.True(await WaitAsync(() => Count(bodies) >= 2), "The second, held exchange never started.");
        board.Replace(Quest("debut", RecordedTaskState.Active,
            Objective("shoot-scavs", RecordedObjectiveState.Completed),
            Objective("find-bottles", RecordedObjectiveState.Unknown)));
        var changed = DateTimeOffset.UtcNow;
        share.Invalidate();

        Assert.True(await WaitAsync(() => Count(bodies) >= 3, attempts: 20), "The change waited out the hold.");
        string latest;
        DateTimeOffset at;
        lock (bodies)
        {
            (at, latest) = bodies[2];
        }

        Assert.True(at - changed < TimeSpan.FromSeconds(2));
        Assert.Contains("find-bottles", latest, StringComparison.Ordinal);
        Assert.DoesNotContain("shoot-scavs", latest, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectivesAreOnlyTheOpenOnesOfActiveQuests()
    {
        var shared = GroupQuestShare.Choose(
            [
                Quest("debut", RecordedTaskState.Active,
                    Objective("a", RecordedObjectiveState.Unknown),
                    Objective("b", RecordedObjectiveState.Completed),
                    Objective("c", RecordedObjectiveState.Unknown)),
                Quest("pinned", RecordedTaskState.NotStarted, isPinned: true, objectives: Objective("d", RecordedObjectiveState.Unknown)),
            ],
            new HashSet<string>(["c"], StringComparer.Ordinal));

        Assert.Equal(["a"], shared.Objectives.Select(objective => objective.ObjectiveId));
    }

    /// <summary>Names come from this catalog; progress from what is open; "shared" from overlap.</summary>
    [Fact]
    public void TheSquadIsResolvedFromThisCatalogWithProgressAndOverlap()
    {
        var tasks = new Dictionary<string, QuestTaskDefinition>(StringComparer.Ordinal)
        {
            ["debut"] = Definition("debut", "Debut", "o1", "o2", "o3"),
            ["shortage"] = Definition("shortage", "Shortage", "s1"),
        };

        var picture = SquadQuestResolver.Resolve(
            [
                new("You", true, ["debut"], [new("debut", "o1", null)]),
                new("Geo", false, ["debut", "shortage", "unknown-to-this-catalog"], [new("debut", "o3", 1), new("shortage", "s1", null)]),
                new("Sam", false, ["shortage"], []),
            ],
            tasks);

        Assert.Equal(["debut", "shortage"], picture.SharedTaskIds.Order(StringComparer.Ordinal));
        var geo = picture.Members.Single(member => member.Name == "Geo");
        Assert.Equal(["Debut", "Shortage"], geo.Quests.Select(quest => quest.Name));
        var debut = geo.Quests[0];
        Assert.Equal((3, 1, 2), (debut.ObjectiveCount, debut.OpenCount, debut.DoneCount));
        Assert.Equal("2/3 done", TarkovCompanion.App.ViewModels.V2.Team.TeamWorkspaceViewModel.Progress(debut));
        // Sam's companion sent no objectives, so nothing is claimed about his progress.
        Assert.Equal(string.Empty, TarkovCompanion.App.ViewModels.V2.Team.TeamWorkspaceViewModel.Progress(
            picture.Members.Single(member => member.Name == "Sam").Quests[0]));
        Assert.Equal("Geo", picture.MemberFor("o3"));
    }

    private static QuestTaskDefinition Definition(string id, string name, params string[] objectiveIds) => new(
        id, name, null, null, null, null, "customs", null, null, null, null, null, null, [], [],
        [.. objectiveIds.Select((objectiveId, index) => new QuestObjectiveDefinition(
            objectiveId, id, "visit", QuestObjectiveKind.Visit, false, index, objectiveId, null, false, null, null, [], [], [], [], "{}", "{}"))],
        [], "{}");

    private static int Count(List<(DateTimeOffset, string)> bodies)
    {
        lock (bodies)
        {
            return bodies.Count;
        }
    }

    private static GroupSessionService Session(InProcessRelay relay, string name, GroupQuestShare? share, out RuntimeStateStore store)
    {
        store = Store();
        return new(
            new StubSettings(name),
            store,
            new HttpClient(relay) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance,
            share);
    }

    private static RuntimeStateStore Store() => new(new(
        false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(9),
        TimeSpan.FromMinutes(5)));

    private static async Task<bool> WaitAsync(Func<bool> condition, int attempts = 100)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await System.Threading.Tasks.Task.Delay(100);
        }

        return condition();
    }

    private static QuestObjectiveReadModel Objective(string id, RecordedObjectiveState state, decimal? count = null) => new(
        id, id, QuestObjectiveKind.Shoot, false, false, state, count, 5, null, "Manual", null, false, ["customs"], []);

    private static QuestSummaryReadModel Quest(string id, RecordedTaskState state, params QuestObjectiveReadModel[] objectives) =>
        Quest(id, state, false, objectives);

    private static QuestSummaryReadModel Quest(string id, RecordedTaskState state, bool isPinned, params QuestObjectiveReadModel[] objectives) => new(
        id, id, null, null, state, "Manual", null,
        new QuestEligibility(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfaction.Indeterminate,
        isPinned, null, false, [], [], objectives);

    private sealed record OlderMember(string Name, IReadOnlyList<string>? QuestIds);

    /// <summary>The relay's own exchange route, run in process against one shared set of rooms.</summary>
    private sealed class InProcessRelay : HttpMessageHandler
    {
        private readonly GroupRooms _rooms = new(TimeProvider.System);
        private readonly GroupMarks _marks = new(TimeProvider.System);
        private readonly GroupRoomChanges _changes = new(TimeProvider.System);
        private const string Key = "a-key-long-enough";

        /// <summary>Publishes a raw JSON body as a member would; returns the relay's refusal, or null.</summary>
        public string? Publish(string json)
        {
            var state = JsonSerializer.Deserialize<GroupMemberState>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var result = Exchange(state, string.Empty, CancellationToken.None).GetAwaiter().GetResult();
            return result.Result is BadRequest<string> refused ? refused.Value : null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var state = JsonSerializer.Deserialize<GroupMemberState>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var result = await Exchange(state, request.RequestUri!.Query, cancellationToken);
            if (result.Result is not Ok<GroupRoomState> ok)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        private Task<Results<Ok<GroupRoomState>, UnauthorizedHttpResult, BadRequest<string>>> Exchange(
            GroupMemberState state,
            string query,
            CancellationToken cancellationToken)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["X-Group-Key"] = Key;
            context.Request.QueryString = new QueryString(query);
            return RelayRoomStateRoutes.PublishAndReadAsync(_rooms, _marks, _changes, state, context.Request, cancellationToken);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class StubSettings(string name) : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new GroupSharingSettings(
                true, "https://relay.example.test/", name, "a-key-long-enough", false, true));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed class Board(params QuestSummaryReadModel[] tasks) : IQuestReadService
    {
        private QuestSummaryReadModel[] _tasks = tasks;

        public void Replace(params QuestSummaryReadModel[] tasks) => _tasks = tasks;

        public Task<QuestBoardReadModel> GetQuestBoardAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new QuestBoardReadModel(scope, 1, null, _tasks, []));

        public Task<QuestItemNeedsReadModel> GetItemNeedsAsync(QuestProfileScope scope, string itemId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(
            QuestProfileScope scope,
            IReadOnlyCollection<string> mapIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubProfiles(GameMode mode = GameMode.Regular) : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(new PlayerProfile(
                Guid.Empty, "Local profile", mode, 1, Faction.Unknown, null,
                new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, EventItemState>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                DateTimeOffset.UnixEpoch));

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => System.Threading.Tasks.Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
