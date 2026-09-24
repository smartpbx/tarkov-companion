using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.GroupServer;

namespace TarkovCompanion.UnitTests.Group;

/// <summary>
/// [#289] The Team workspace's ready check: an extract, a note and ready or not reach a squadmate
/// through the relay's own exchange route, and nothing breaks for a build that predates them.
/// </summary>
public sealed partial class SquadQuestSyncTests
{
    [Fact]
    public async Task ReadyExtractAndNoteTravelThroughTheRelayToASquadmate()
    {
        var relay = new InProcessRelay();
        var status = new GroupSquadStatus();
        status.Set(new SquadStatus(true, "  ZB-1011 ", "customs", " meet at dorms "));
        await using var clay = StatusSession(relay, "Clay", status, out _);
        await using var geo = Session(relay, "Geo", null, out var geoStore);

        clay.Start();
        geo.Start();
        var seen = await WaitAsync(() => geoStore.Current.Group.Members.FirstOrDefault(member => member.Name == "Clay") is { Ready: not null });

        Assert.True(seen, "Geo never received Clay's ready state.");
        var member = geoStore.Current.Group.Members.Single(item => item.Name == "Clay");
        Assert.Equal((true, "ZB-1011", "meet at dorms"), (member.Ready, member.PlannedExtract, member.Note));
        Assert.Equal("Ready · Extract · ZB-1011 · meet at dorms", TeamWorkspaceViewModel.DescribeStatus(member));
    }

    /// <summary>Pressing "Not ready" goes out at once, not when the relay's hold runs out.</summary>
    [Fact]
    public async Task AReadyChangeReachesASquadmateWithoutWaitingOutTheHold()
    {
        var relay = new InProcessRelay();
        var status = new GroupSquadStatus();
        status.Set(new SquadStatus(true, null, null, null));
        await using var clay = StatusSession(relay, "Clay", status, out _);
        await using var geo = Session(relay, "Geo", null, out var geoStore);
        clay.Start();
        geo.Start();
        Assert.True(await WaitAsync(() => geoStore.Current.Group.Members.Any(member => member is { Name: "Clay", Ready: true })));
        // Into the relay's hold: nothing in the room changes, so both exchanges wait.
        await Task.Delay(500);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        status.Set(new SquadStatus(false, null, null, null));

        Assert.True(
            await WaitAsync(() => geoStore.Current.Group.Members.Any(member => member is { Name: "Clay", Ready: false }), attempts: 40),
            "Geo never saw Clay turn not ready.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"The change took {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task AnOlderCompanionWithoutAStatusIsReadAsNotSaid()
    {
        var relay = new InProcessRelay();
        var older = """{"name":"Riley","mapId":"customs","raidState":"InRaid","loadout":[],"quests":[]}""";
        Assert.Null(relay.Publish(older));
        await using var clay = Session(relay, "Clay", null, out var clayStore);

        clay.Start();
        await WaitAsync(() => clayStore.Current.Group.Members.Count > 0);

        var member = Assert.Single(clayStore.Current.Group.Members);
        Assert.Equal((null, null, null), (member.Ready, member.PlannedExtract, member.Note));
        // Said nothing, so no row claims "not ready" for them; the count still has them not ready.
        Assert.Empty(TeamWorkspaceViewModel.BuildSquadStatusRows(clayStore.Current.Group.Members));
        Assert.Equal("1 of 2 ready", TeamWorkspaceViewModel.DescribeReadiness(true, clayStore.Current.Group.Members));
    }

    [Fact]
    public void AnOlderReaderIgnoresTheStatusFields()
    {
        var state = new GroupMemberState("Clay", "customs", "InRaid", null, null, null, null, null, [], [])
        {
            Ready = true,
            PlannedExtract = "ZB-1011",
            Note = "meet at dorms",
        };

        var json = JsonSerializer.Serialize(state);
        var older = JsonSerializer.Deserialize<OlderMember>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"plannedExtract\":\"ZB-1011\"", json, StringComparison.Ordinal);
        Assert.Equal("Clay", older!.Name);
    }

    [Fact]
    public void TheRelayRefusesANoteOrExtractLongerThanItsBound()
    {
        var state = new GroupMemberState("Clay", null, "Menu", null, null, null, null, null, [], [])
        {
            Ready = false,
            PlannedExtract = new string('e', 64),
            Note = new string('n', SquadStatus.NoteLimit),
        };

        Assert.Null(state.Validate());
        Assert.NotNull((state with { Note = new string('n', SquadStatus.NoteLimit + 1) }).Validate());
        Assert.NotNull((state with { PlannedExtract = new string('e', 65) }).Validate());
    }

    [Fact]
    public void AStatusIsTrimmedAndCutToTheRelaysBounds()
    {
        var status = new GroupSquadStatus();
        var changes = 0;
        status.Changed += () => changes++;

        status.Set(new SquadStatus(null, "   ", "customs", new string('n', 200)));
        status.Set(new SquadStatus(null, "   ", "customs", new string('n', 200)));

        Assert.Equal(1, changes);
        Assert.Null(status.Current.Extract);
        Assert.Null(status.Current.ExtractMapId);
        Assert.Equal(SquadStatus.NoteLimit, status.Current.Note!.Length);
    }

    /// <summary>A plan for Customs is not said while the player is on Woods; in the menu it is.</summary>
    [Fact]
    public void AnExtractTravelsOnlyWithItsOwnMap()
    {
        var status = new SquadStatus(true, "ZB-1011", "customs", null);

        Assert.Equal("ZB-1011", status.ExtractFor("customs"));
        Assert.Equal("ZB-1011", status.ExtractFor("Customs"));
        Assert.Equal("ZB-1011", status.ExtractFor(null));
        Assert.Null(status.ExtractFor("woods"));
    }

    [Fact]
    public void OnlySquadmatesWhoSaidSomethingGetARow()
    {
        GroupMemberView Member(string name) => new(name, "customs", RaidLifecycleState.Menu, "PMC", null, null, null, [], []);
        IReadOnlyList<GroupMemberView> members =
        [
            Member("Geo") with { Ready = true, PlannedExtract = "ZB-1011" },
            Member("Riley") with { Note = "2 min" },
            Member("Sam"),
        ];

        var rows = TeamWorkspaceViewModel.BuildSquadStatusRows(members);

        Assert.Equal(["Geo", "Riley"], rows.Select(row => row.Name));
        Assert.Equal(("Ready", "Extract · ZB-1011"), (rows[0].ReadyLabel, rows[0].ExtractLabel));
        Assert.False(rows[1].HasReady);
        Assert.Equal("2 of 4 ready", TeamWorkspaceViewModel.DescribeReadiness(true, members));
    }

    [Fact]
    public async Task TheTeamButtonsSetClearAndShareTheStatus()
    {
        var relay = new InProcessRelay();
        var status = new GroupSquadStatus();
        await using var session = Session(relay, "Clay", null, out _);
        var team = new TeamWorkspaceViewModel(session, new StubSettings("Clay"), squadStatus: status);

        team.ReadyCommand.Execute(null);
        Assert.Equal((true, true, false), (status.Current.Ready, team.IsReady, team.IsNotReady));
        team.NotReadyCommand.Execute(null);
        Assert.Equal(false, status.Current.Ready);
        // Pressing the chosen answer again takes it back to "not said".
        team.NotReadyCommand.Execute(null);
        Assert.Null(status.Current.Ready);

        team.NoteDraft = " covering left ";
        Assert.True(team.CanShareNote);
        team.ShareNoteCommand.Execute(null);
        Assert.Equal("covering left", status.Current.Note);
        Assert.False(team.CanShareNote);
        Assert.Equal("Shared · covering left", team.SharedNoteLabel);

        // The picker's null (its list rebuilt without the choice) is not the player clearing it.
        team.SelectedExtract = null;
        Assert.Null(status.Current.Extract);
        Assert.Equal(TeamWorkspaceViewModel.NoExtract, team.SelectedExtract);
    }

    private static GroupSessionService StatusSession(InProcessRelay relay, string name, GroupSquadStatus status, out RuntimeStateStore store)
    {
        store = Store();
        return new(
            new StubSettings(name),
            store,
            new HttpClient(relay) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance,
            status: status);
    }
}
