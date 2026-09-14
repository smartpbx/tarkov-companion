using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Ranking maps by where the group's quests overlap.
/// </summary>
/// <remarks>
/// The decision a squad makes before any other one, and the one thing the companion had
/// nothing to say about. <c>GroupQuestShare</c> sent five names and the receiving end printed
/// them; the catalog on both machines knows every task's map and every objective's map, so the
/// overlap was already answerable and nobody was asking it.
/// </remarks>
public sealed class TonightMapsTests
{
    [Fact]
    public void The_map_the_group_has_most_to_do_on_comes_first()
    {
        var rows = TonightMaps.Rank(
            [Quest("one", "customs"), Quest("two", "woods")],
            [Member("Geo", "two")]);

        Assert.Equal("woods", rows[0].MapId);
    }

    [Fact]
    public void Your_own_quests_count_without_anybody_else_in_the_group()
    {
        // It works alone. A player deciding what to queue on their own is the same decision
        // with one list instead of four.
        var rows = TonightMaps.Rank([Quest("one", "customs"), Quest("two", "customs")], []);

        Assert.Equal(2, rows.Single().Yours);
    }

    [Fact]
    public void A_squadmate_is_named_and_counted_separately_from_you()
    {
        // A map where one person has five and nobody else has any is a different night from
        // one where two people have two each, and a total cannot tell them apart.
        var rows = TonightMaps.Rank(
            [Quest("one", "customs"), Quest("two", "customs")],
            [Member("Geo", "one")]);

        var row = rows.Single();
        Assert.Equal(2, row.Yours);
        Assert.Equal(new TonightMemberQuests("Geo", 1), row.Others.Single());
        Assert.Equal(3, row.Total);
    }

    [Fact]
    public void A_pinned_quest_is_counted_and_also_called_out()
    {
        var rows = TonightMaps.Rank([Quest("one", "customs", isPinned: true)], []);

        Assert.Equal(1, rows.Single().Yours);
        Assert.Equal(1, rows.Single().YoursPinned);
    }

    /// <summary>The same filter the group is sent: what you are on, not what you have done.</summary>
    [Theory]
    [InlineData(RecordedTaskState.Completed)]
    [InlineData(RecordedTaskState.Failed)]
    [InlineData(RecordedTaskState.NotStarted)]
    public void A_quest_you_are_not_on_ranks_nothing(RecordedTaskState state)
    {
        Assert.Empty(TonightMaps.Rank([Quest("one", "customs", state: state)], []));
    }

    [Fact]
    public void A_quest_with_four_things_to_do_on_one_map_is_one_reason_to_go_there()
    {
        // Per quest rather than per objective. Counting objectives would rank a map by how
        // finely its quests happen to be broken up.
        var task = Quest("one", "customs") with
        {
            Objectives =
            [
                Objective("customs"),
                Objective("customs"),
                Objective("customs"),
                Objective("customs"),
            ],
        };

        Assert.Equal(1, TonightMaps.Rank([task], []).Single().Yours);
    }

    [Fact]
    public void A_quest_that_spans_two_maps_counts_on_both()
    {
        var task = Quest("one", "customs") with
        {
            Objectives = [Objective("customs"), Objective("woods")],
        };

        Assert.Equal(["customs", "woods"], TonightMaps.Rank([task], []).Select(row => row.MapId).Order());
    }

    [Fact]
    public void An_objective_that_names_no_map_falls_back_to_the_quest_own_map()
    {
        // The rule the map layer places them by, so a row's count is what clicking it lights.
        var task = Quest("one", "customs") with { Objectives = [Objective()] };

        Assert.Equal("customs", TonightMaps.Rank([task], []).Single().MapId);
    }

    [Fact]
    public void A_quest_that_names_no_map_anywhere_ranks_nothing()
    {
        // There is nothing to go and do, so there is nowhere to go and do it.
        var task = Quest("one", null) with { Objectives = [Objective()] };

        Assert.Empty(TonightMaps.Rank([task], []));
    }

    [Fact]
    public void A_task_id_this_catalog_does_not_know_is_passed_over()
    {
        // A squadmate on a newer catalog contributes what this machine can place and is silent
        // about the rest, rather than ranking a map by a quest nobody here has heard of.
        var rows = TonightMaps.Rank([Quest("one", "customs")], [Member("Geo", "one", "not-in-this-catalog")]);

        Assert.Equal(1, rows.Single().Others.Single().Count);
    }

    [Fact]
    public void A_squadmate_who_sent_the_same_quest_twice_wants_the_map_once()
    {
        var rows = TonightMaps.Rank([Quest("one", "customs")], [Member("Geo", "one", "one")]);

        Assert.Equal(1, rows.Single().Others.Single().Count);
    }

    /// <summary>
    /// A squadmate's own progress is not consulted, because it was never sent.
    /// </summary>
    /// <remarks>
    /// What arrives is "these are the quests I am on", already filtered by the sender. Reading
    /// the receiver's recorded state for a squadmate's id would answer with the reader's own
    /// progress on it, which is a different question and usually a wrong answer.
    /// </remarks>
    [Fact]
    public void A_squadmate_counts_a_quest_the_reader_has_already_finished()
    {
        var rows = TonightMaps.Rank(
            [Quest("one", "customs", state: RecordedTaskState.Completed)],
            [Member("Geo", "one")]);

        var row = rows.Single();
        Assert.Equal(0, row.Yours);
        Assert.Equal(1, row.Others.Single().Count);
    }

    [Fact]
    public void The_busiest_squadmate_on_a_map_is_named_first()
    {
        var rows = TonightMaps.Rank(
            [Quest("one", "customs"), Quest("two", "customs")],
            [Member("Geo", "one"), Member("Max", "one", "two")]);

        Assert.Equal(["Max", "Geo"], rows.Single().Others.Select(other => other.Name));
    }

    [Fact]
    public void Only_as_many_maps_as_are_worth_offering()
    {
        var board = Enumerable.Range(0, 12)
            .Select(index => Quest($"task-{index}", $"map-{index}"))
            .ToArray();

        Assert.Equal(TonightMaps.Limit, TonightMaps.Rank(board, []).Count);
    }

    [Fact]
    public void Nothing_to_do_anywhere_is_no_rows_rather_than_empty_ones()
    {
        Assert.Empty(TonightMaps.Rank([], []));
    }

    private static GroupMemberView Member(string name, params string[] questIds) => new(
        name,
        null,
        RaidLifecycleState.Menu,
        null,
        null,
        null,
        null,
        [],
        [])
    {
        QuestIds = questIds,
    };

    private static QuestSummaryReadModel Quest(
        string taskId,
        string? mapId,
        RecordedTaskState state = RecordedTaskState.Active,
        bool isPinned = false) => new(
        taskId,
        taskId,
        null,
        mapId,
        state,
        "Manual",
        null,
        new QuestEligibility(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfaction.Indeterminate,
        isPinned,
        null,
        false,
        [],
        [],
        [Objective()]);

    /// <summary>One objective, on the maps named or on none at all.</summary>
    private static QuestObjectiveReadModel Objective(params string[] mapIds) => new(
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
        mapIds,
        []);
}
