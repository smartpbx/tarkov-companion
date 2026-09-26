using System.Text.Json;
using Avalonia.Media;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// [#712 T7] The shared-task planner and the loadout ready check: which quests overlap, where and
/// in what order; each member's own Loadout check and level through the relay, on by default with
/// the off switch honoured; the relay's bounds; and builds on either side of the change.
/// </summary>
public sealed partial class SquadQuestSyncTests
{
    private const string Customs = "customs-id";
    private const string Woods = "woods-id";

    private static QuestTaskDefinition OnMap(string id, string name, string map, params string[] objectiveIds) =>
        Definition(id, name, objectiveIds) with { PrimaryMapId = map };

    private static SquadQuestPicture PlanPicture(params SquadMemberQuestIds[] members)
    {
        var tasks = new[]
        {
            OnMap("A", "Alpha", Customs, "a1", "a2"),
            OnMap("B", "Bravo", Customs, "b1"),
            OnMap("C", "Charlie", Woods, "c1"),
            OnMap("D", "Delta", Customs, "d1"),
            OnMap("E", "Echo", Customs, "e1", "e2"),
        }.ToDictionary(task => task.Id, StringComparer.Ordinal);
        return SquadQuestResolver.Resolve(members, tasks);
    }

    private static SquadMemberQuestIds Holds(string name, bool self, params (string Task, string[] Open)[] quests) => new(
        name,
        self,
        [.. quests.Select(quest => quest.Task)],
        [.. quests.SelectMany(quest => quest.Open.Select(open => new GroupObjectiveView(quest.Task, open, null)))]);

    [Fact]
    public void ThePlannerKeepsQuestsTwoOrMoreShareGroupedByTheMapTheirOpenObjectivesAreOn()
    {
        var picture = PlanPicture(
            Holds("You", true, ("A", ["a1"]), ("B", ["b1"]), ("D", ["d1"])),
            Holds("Geo", false, ("A", ["a1", "a2"]), ("B", ["b1"]), ("C", ["c1"])),
            Holds("Riley", false, ("A", ["a2"]), ("C", ["c1"])));

        var maps = SquadTaskPlanner.Plan(picture);

        // Customs has two shared quests, Woods one; Delta is only this player's and counts nowhere.
        Assert.Equal([Customs, Woods], maps.Select(map => map.MapId));
        var customs = maps[0];
        Assert.Equal(["Alpha", "Bravo"], customs.Quests.Select(quest => quest.Name));
        Assert.Equal([1, 2], customs.Quests.Select(quest => quest.Order));
        Assert.Equal(["You", "Geo", "Riley"], customs.Quests[0].Members);
        // a1 is open for You and Geo, a2 for Geo and Riley: one trip each does it for two of them.
        Assert.All(customs.Quests[0].Objectives, objective => Assert.True(objective.Together));
        Assert.Equal(["You", "Geo"], customs.Quests[0].Objectives.Single(objective => objective.ObjectiveId == "a1").Members);
        Assert.Equal(["Charlie"], maps[1].Quests.Select(quest => quest.Name));
    }

    [Fact]
    public void TheSuggestedOrderPutsTheWidestQuestFirstThenOneTripForSeveral()
    {
        var picture = PlanPicture(
            Holds("You", true, ("B", ["b1"]), ("E", ["e1"])),
            Holds("Geo", false, ("B", ["b1"]), ("E", ["e2"]), ("A", ["a1"])),
            Holds("Riley", false, ("A", ["a1"])),
            Holds("Sam", false, ("A", ["a2"])));

        var customs = SquadTaskPlanner.ForMap(picture, Customs);

        // Alpha: three hold it. Bravo and Echo: two each, but only Bravo has an objective both have open.
        Assert.Equal(["Alpha", "Bravo", "Echo"], customs.Quests.Select(quest => quest.Name));
        Assert.True(customs.Quests[1].HasTogether);
        Assert.False(customs.Quests[2].HasTogether);
        Assert.Equal(2, customs.Quests[2].Objectives.Count);
    }

    [Fact]
    public void AnOlderCompanionWithoutObjectivesCountsAsAHolderButAddsNoObjective()
    {
        var picture = PlanPicture(
            Holds("You", true, ("A", ["a1"])),
            new SquadMemberQuestIds("Sam", false, ["A"], []));

        var quest = Assert.Single(Assert.Single(SquadTaskPlanner.Plan(picture)).Quests);

        Assert.Equal(["You", "Sam"], quest.Members);
        Assert.Equal(["You"], Assert.Single(quest.Objectives).Members);
        Assert.False(quest.HasTogether);
    }

    [Fact]
    public void NothingIsPlannedWhenNobodySharesAQuest()
    {
        var picture = PlanPicture(Holds("You", true, ("A", ["a1"])), Holds("Geo", false, ("B", ["b1"])));

        Assert.Empty(SquadTaskPlanner.Plan(picture));
    }

    [Fact]
    public void ThePlanShowsThePickedMapThenTheRaidMapOnScreenThenTheBusiest()
    {
        var maps = new[] { new SquadPlanMap(Customs, []), new SquadPlanMap(Woods, []) };
        string Name(string id) => id == Woods ? "Woods" : "Customs";

        Assert.Equal(Customs, TeamWorkspaceViewModel.ChoosePlanMap(maps, null, null, Name)!.MapId);
        Assert.Equal(Woods, TeamWorkspaceViewModel.ChoosePlanMap(maps, null, "woods", Name)!.MapId);
        Assert.Equal(Customs, TeamWorkspaceViewModel.ChoosePlanMap(maps, Customs, "Woods", Name)!.MapId);
        Assert.Equal(Customs, TeamWorkspaceViewModel.ChoosePlanMap(maps, null, "Lighthouse", Name)!.MapId);
        Assert.Null(TeamWorkspaceViewModel.ChoosePlanMap([], null, "Woods", Name));
    }

    private static LoadoutSuggestionRow Row(LoadoutSuggestionKind kind, string title, string have, bool owned) => new(
        new LoadoutSuggestion(kind, title, "reason", ["Quest"], ["item-" + title], 1, false, "test"),
        have,
        owned,
        string.Empty,
        false);

    private static LoadoutSuggestionPlan CheckPlan(params LoadoutSuggestionRow[] rows) => new([], Customs, rows, "test");

    [Fact]
    public void ALoadoutCheckSaysMissingOnlyWhereAScanCountedNone()
    {
        var plan = CheckPlan(
            Row(LoadoutSuggestionKind.Key, "Bring Dorm room 314 marked key", "You own it", true),
            Row(LoadoutSuggestionKind.Carry, "Bring MS2000 Marker ×2", "None owned", false),
            Row(LoadoutSuggestionKind.Weapon, "Bring an AKM", "Owned: not scanned", false),
            Row(LoadoutSuggestionKind.Range, "Kill from 100 m", string.Empty, false));

        var check = SharedLoadoutCheck.From(plan, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            [new LoadoutCheckItem("keys", true), new LoadoutCheckItem("items", false, "MS2000 Marker"), new LoadoutCheckItem("weapon", null)],
            check.Items);
        Assert.Equal(1, check.MissingCount);
        Assert.Equal(Customs, check.MapId);
    }

    [Fact]
    public async Task AMembersOwnLoadoutCheckAndLevelReachASquadmateThroughTheRelay()
    {
        var relay = new InProcessRelay();
        var share = new GroupReadyCheckShare(new StubProfiles(), (_, _) => Task.FromResult(CheckPlan(
            Row(LoadoutSuggestionKind.Key, "Bring Dorm room 314 marked key", "You own it", true),
            Row(LoadoutSuggestionKind.Carry, "Bring MS2000 Marker", "None owned", false))));
        await using var clay = ReadyCheckSession(relay, "Clay", share, new ReadySettings("Clay", true), out _);
        await using var geo = Session(relay, "Geo", null, out var geoStore);

        clay.Start();
        geo.Start();
        var seen = await WaitAsync(() => geoStore.Current.Group.Members.Any(member => member is { Name: "Clay", LoadoutCheck: not null }));

        Assert.True(seen, "Geo never received Clay's loadout check.");
        var member = geoStore.Current.Group.Members.Single(item => item.Name == "Clay");
        Assert.Equal(1, member.Level);
        Assert.Equal(Customs, member.LoadoutCheck!.MapId);
        Assert.Equal([new LoadoutCheckItem("keys", true), new LoadoutCheckItem("items", false, "MS2000 Marker")], member.LoadoutCheck.Items);
        var rows = TeamWorkspaceViewModel.BuildReadyRows("You", true, null, DateTimeOffset.UtcNow, [member], _ => Brushes.White);
        Assert.Equal(["keys", "no MS2000 Marker"], rows[1].Checks.Select(chip => chip.Label));
        Assert.StartsWith("shared by Clay's companion · ", rows[1].Source, StringComparison.Ordinal);
    }

    /// <summary>Decision 5 on #712: on by default in a squad, and the off switch sends neither the check nor the level.</summary>
    [Fact]
    public async Task TheReadyCheckSwitchOffSendsNeitherTheCheckNorTheLevel()
    {
        Assert.True(GroupSharingSettings.Off.SharesReadyCheck);
        var relay = new InProcessRelay();
        var asked = 0;
        var share = new GroupReadyCheckShare(new StubProfiles(), (_, _) =>
        {
            Interlocked.Increment(ref asked);
            return Task.FromResult(CheckPlan(Row(LoadoutSuggestionKind.Key, "Bring a key", "You own it", true)));
        });
        await using var clay = ReadyCheckSession(relay, "Clay", share, new ReadySettings("Clay", false), out _);
        await using var geo = Session(relay, "Geo", null, out var geoStore);

        clay.Start();
        geo.Start();
        Assert.True(await WaitAsync(() => geoStore.Current.Group.Members.Any(member => member.Name == "Clay")));
        await Task.Delay(300);

        var member = geoStore.Current.Group.Members.Single(item => item.Name == "Clay");
        Assert.Null(member.LoadoutCheck);
        Assert.Null(member.Level);
        Assert.Equal(0, Volatile.Read(ref asked));
    }

    [Fact]
    public void TheRelayBoundsALoadoutCheckAndALevel()
    {
        var item = new GroupLoadoutCheckItemState("items", false) { Missing = new string('m', 64) };
        var state = new GroupMemberState("Clay", null, "Menu", null, null, null, null, null, [], [])
        {
            Level = 79,
            LoadoutCheck = new GroupLoadoutCheckState([.. Enumerable.Repeat(item, GroupMemberState.MaximumLoadoutChecks)])
            {
                MapId = Customs,
                AgeSeconds = 12,
            },
        };

        Assert.Null(state.Validate());
        Assert.NotNull((state with { Level = 80 }).Validate());
        Assert.NotNull((state with { Level = 0 }).Validate());
        Assert.NotNull((state with { LoadoutCheck = new([.. Enumerable.Repeat(item, 9)]) }).Validate());
        Assert.NotNull((state with { LoadoutCheck = new([item with { Missing = new string('m', 65) }]) }).Validate());
        Assert.NotNull((state with { LoadoutCheck = new([new GroupLoadoutCheckItemState(new string('k', 17), true)]) }).Validate());
        Assert.NotNull((state with { LoadoutCheck = new([item]) { AgeSeconds = -1 } }).Validate());
        Assert.NotNull((state with { LoadoutCheck = new(null!) }).Validate());
    }

    /// <summary>An older reader ignores the fields; a publish without a check is what it was before.</summary>
    [Fact]
    public void OlderBuildsIgnoreTheReadyCheckFieldsAndAPublishWithoutOneOmitsThem()
    {
        var state = new GroupMemberState("Clay", "customs", "Menu", null, null, null, null, null, [], [])
        {
            Level = 42,
            LoadoutCheck = new([new GroupLoadoutCheckItemState("keys", true)]) { AgeSeconds = 5 },
        };

        var json = JsonSerializer.Serialize(state);
        var older = JsonSerializer.Deserialize<OlderMember>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var without = JsonSerializer.Serialize(state with { Level = null, LoadoutCheck = null });

        Assert.Contains("\"loadoutCheck\":{", json, StringComparison.Ordinal);
        Assert.Equal("Clay", older!.Name);
        Assert.DoesNotContain("loadoutCheck", without, StringComparison.Ordinal);
        Assert.DoesNotContain("\"level\"", without, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOlderCompanionShowsNothingSharedRatherThanMissing()
    {
        var relay = new InProcessRelay();
        Assert.Null(relay.Publish("""{"name":"Riley","mapId":"customs","raidState":"Menu","loadout":[],"quests":[],"ready":true}"""));
        await using var clay = Session(relay, "Clay", null, out var clayStore);

        clay.Start();
        await WaitAsync(() => clayStore.Current.Group.Members.Count > 0);

        var member = Assert.Single(clayStore.Current.Group.Members);
        Assert.Null(member.LoadoutCheck);
        var rows = TeamWorkspaceViewModel.BuildReadyRows("You", null, null, DateTimeOffset.UtcNow, [member], _ => Brushes.White);
        Assert.Equal(("nothing shared", 0), (rows[1].Empty, rows[1].MissingCount));
        Assert.Equal("1 of 2 ready", TeamWorkspaceViewModel.DescribeReadyCheck(1, rows));
    }

    [Fact]
    public void TheSummaryNamesWhoIsMissingWhat()
    {
        GroupMemberView Member(string name, params LoadoutCheckItem[] items) =>
            new(name, null, RaidLifecycleState.Menu, null, null, null, null, [], [])
            {
                LoadoutCheck = new(Customs, items, TimeSpan.FromMinutes(2)),
                Ready = true,
            };
        var own = new SharedReadiness(new SharedLoadoutCheck(Customs, [new("keys", true)], DateTimeOffset.UnixEpoch), 30);

        var rows = TeamWorkspaceViewModel.BuildReadyRows(
            "Clay",
            true,
            own,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            [Member("Geo", new LoadoutCheckItem("keys", true)), Member("Riley", new LoadoutCheckItem("keys", true), new LoadoutCheckItem("items", false, "MS2000 Marker"))],
            _ => Brushes.White);

        Assert.Equal("Level 30", rows[0].Level);
        Assert.StartsWith("checked here · ", rows[0].Source, StringComparison.Ordinal);
        Assert.Equal("3 of 3 ready · Riley is missing 1 item", TeamWorkspaceViewModel.DescribeReadyCheck(3, rows));
        Assert.Equal("2 of 2 ready · Nothing missing", TeamWorkspaceViewModel.DescribeReadyCheck(2, rows.Take(2).ToArray()));
    }

    private static GroupSessionService ReadyCheckSession(
        InProcessRelay relay,
        string name,
        GroupReadyCheckShare share,
        IGroupSettingsStore settings,
        out RuntimeStateStore store)
    {
        store = Store();
        return new(
            settings,
            store,
            new HttpClient(relay) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance,
            readyCheck: share);
    }

    private sealed class ReadySettings(string name, bool sharesReadyCheck) : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(true, "https://relay.example.test/", name, "a-key-long-enough", false, true)
            {
                SharesReadyCheck = sharesReadyCheck,
            });

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
