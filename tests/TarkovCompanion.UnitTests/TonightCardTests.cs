using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The card that answers "what are we queueing", beside the map.
/// </summary>
/// <remarks>
/// The counts are the row. "You: 3 (1 pinned) · Geo: 2" is what somebody would otherwise say
/// out loud after four people had read their quest lists to each other, which is how a group
/// picked a map before this existed.
/// </remarks>
public sealed class TonightCardTests
{
    private static readonly IReadOnlyList<MapLocation> Locations =
    [
        new("customs", "55f2d3fd4bdc2d5f408b4567", "Customs", null, null, []),
        new("woods", null, "Woods", null, null, []),
    ];

    [Fact]
    public void Reads_as_a_sentence_somebody_would_say()
    {
        var card = new TonightMapViewModel(
            "Customs",
            new("customs", 3, 1, [new("Geo", 2), new("Max", 1)]),
            () => Task.CompletedTask);

        Assert.Equal("You: 3 (1 pinned) · Geo: 2 · Max: 1", card.Detail);
        Assert.Equal(6, card.Total);
    }

    [Fact]
    public void Says_nothing_about_pins_when_none_are_pinned()
    {
        var card = new TonightMapViewModel("Customs", new("customs", 3, 0, []), () => Task.CompletedTask);

        Assert.Equal("You: 3", card.Detail);
    }

    [Fact]
    public void Leaves_you_out_of_a_map_you_have_nothing_on()
    {
        // A map two squadmates want and you have nothing on is still worth queueing, and
        // "You: 0" is a line that only says what the absence of a line already said.
        var card = new TonightMapViewModel(
            "Woods",
            new("woods", 0, 0, [new("Geo", 2)]),
            () => Task.CompletedTask);

        Assert.Equal("Geo: 2", card.Detail);
    }

    [Fact]
    public void The_map_is_named_rather_than_slugged()
    {
        var rows = RaidPageViewModel.Choose(
            RaidLifecycleState.Menu,
            [Quest("one", "customs")],
            [],
            Locations,
            _ => Task.CompletedTask);

        Assert.Equal("Customs", rows.Single().MapName);
    }

    /// <summary>
    /// Only in the menu, because after that it is not a decision any more.
    /// </summary>
    /// <remarks>
    /// A card offering to switch the map mid-raid would be in the way of the map the player is
    /// standing on, which is the page's whole point.
    /// </remarks>
    [Theory]
    [InlineData(RaidLifecycleState.InRaid)]
    [InlineData(RaidLifecycleState.LoadingRaid)]
    [InlineData(RaidLifecycleState.PostRaid)]
    [InlineData(RaidLifecycleState.Unknown)]
    public void Says_nothing_once_the_map_has_been_chosen(RaidLifecycleState state) =>
        Assert.Empty(RaidPageViewModel.Choose(
            state,
            [Quest("one", "customs")],
            [],
            Locations,
            _ => Task.CompletedTask));

    [Fact]
    public void A_map_this_companion_cannot_show_is_not_offered()
    {
        // A row that does nothing when clicked is worse than a row that is not there.
        var rows = RaidPageViewModel.Choose(
            RaidLifecycleState.Menu,
            [Quest("one", "terminal")],
            [],
            Locations,
            _ => Task.CompletedTask);

        Assert.Empty(rows);
    }

    /// <summary>
    /// The quest catalog and the map catalog do not always name a map the same way.
    /// </summary>
    [Fact]
    public void A_map_known_by_its_source_id_is_still_offered()
    {
        var rows = RaidPageViewModel.Choose(
            RaidLifecycleState.Menu,
            [Quest("one", "55f2d3fd4bdc2d5f408b4567")],
            [],
            Locations,
            _ => Task.CompletedTask);

        Assert.Equal("Customs", rows.Single().MapName);
    }

    [Fact]
    public async Task Clicking_a_row_asks_for_that_map()
    {
        var asked = string.Empty;
        var rows = RaidPageViewModel.Choose(
            RaidLifecycleState.Menu,
            [Quest("one", "woods")],
            [],
            Locations,
            mapId =>
            {
                asked = mapId;
                return Task.CompletedTask;
            });

        rows.Single().ShowCommand.Execute(null);
        await Task.Yield();

        Assert.Equal("woods", asked);
    }

    private static QuestSummaryReadModel Quest(string taskId, string mapId) => new(
        taskId,
        taskId,
        null,
        mapId,
        RecordedTaskState.Active,
        "Manual",
        null,
        new QuestEligibility(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfaction.Indeterminate,
        false,
        null,
        false,
        [],
        [],
        [
            new QuestObjectiveReadModel(
                "objective",
                "Do the thing",
                QuestObjectiveKind.Unsupported,
                false,
                false,
                RecordedObjectiveState.Unknown,
                null,
                null,
                null,
                "Manual",
                null,
                false,
                [mapId],
                []),
        ]);
}
