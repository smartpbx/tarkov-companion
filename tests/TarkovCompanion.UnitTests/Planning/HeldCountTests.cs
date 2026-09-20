using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.Planning;

/// <summary>Unknown is not zero: a holding nobody recorded is never shown, summed or subtracted as 0.</summary>
public sealed class HeldCountTests
{
    private static readonly IReadOnlyDictionary<string, int> Held = new Dictionary<string, int>
    {
        ["recorded-none"] = 0,
        ["recorded-some"] = 4,
    };

    [Fact]
    public void Not_recorded_is_null_and_a_recorded_zero_is_zero()
    {
        Assert.Null(HeldCount.Of(Held, "never-counted"));
        Assert.Equal(0, HeldCount.Of(Held, "recorded-none"));
        Assert.Equal(4, HeldCount.Of(Held, "recorded-some"));
    }

    [Fact]
    public void Alternatives_are_unknown_only_when_none_of_them_is_recorded()
    {
        Assert.Null(HeldCount.OfAny(Held, ["never-counted", "nor-this"]));
        // One recorded alternative is something known; the unrecorded one adds nothing to it.
        Assert.Equal(4, HeldCount.OfAny(Held, ["never-counted", "recorded-some"]));
        Assert.Equal(0, HeldCount.OfAny(Held, ["recorded-none"]));
    }

    [Fact]
    public void Nothing_is_taken_off_an_unknown_and_an_unknown_meets_nothing()
    {
        Assert.Equal(5, HeldCount.Remaining(5, null));
        Assert.Equal(1, HeldCount.Remaining(5, 4));
        Assert.Equal(0, HeldCount.Remaining(5, 9));
        Assert.False(HeldCount.Meets(5, null));
        Assert.False(HeldCount.Meets(1, 0));
        Assert.True(HeldCount.Meets(5, 5));
    }

    [Fact]
    public void A_quest_requirement_nobody_counted_reads_as_a_question_mark_not_a_zero()
    {
        var unknown = new PlanRequirementRowViewModel("salewa", "Salewa", "Find in raid", 3, null);
        var none = new PlanRequirementRowViewModel("salewa", "Salewa", "Find in raid", 3, 0);
        var some = new PlanRequirementRowViewModel("salewa", "Salewa", "Find in raid", 3, 7);

        Assert.Equal("? / 3", unknown.ProgressLabel);
        Assert.False(unknown.IsSatisfied);
        Assert.Equal("0 / 3", none.ProgressLabel);
        Assert.Equal("3 / 3", some.ProgressLabel);
        Assert.True(some.IsSatisfied);
    }

    [Fact]
    public void The_summary_says_to_check_for_what_is_unknown_and_still_needed_for_what_is_short()
    {
        var shortOne = new PlanRequirementRowViewModel("a", "A", "Hand in", 3, 1);
        var unknownOne = new PlanRequirementRowViewModel("b", "B", "Hand in", 2, null);
        var unknownTwo = new PlanRequirementRowViewModel("c", "C", "Bring", 1, null);

        Assert.Equal(string.Empty, PlanQuestRules.SummariseUnmet([]));
        Assert.Equal("1 still needed", PlanQuestRules.SummariseUnmet([shortOne]));
        Assert.Equal("2 to check", PlanQuestRules.SummariseUnmet([unknownOne, unknownTwo]));
        Assert.Equal("1 still needed · 2 to check", PlanQuestRules.SummariseUnmet([shortOne, unknownOne, unknownTwo]));
    }

    [Fact]
    public void The_planner_hands_an_unknown_holding_through_rather_than_summing_it_as_zero()
    {
        var objective = new QuestObjectiveReadModel(
            "give",
            Description: "Hand over",
            Kind: QuestObjectiveKind.GiveItem,
            IsOptional: false,
            IsUnsupported: false,
            RecordedState: RecordedObjectiveState.InProgress,
            RecordedCount: null,
            TargetCount: 3,
            FoundInRaidRequired: false,
            ProgressSource: "test",
            ProgressModifiedUtc: null,
            IsPinned: false,
            MapIds: [],
            ItemTargets: [new("never-counted", "items", 0, 0, null, null)]);

        var requirement = Assert.Single(QuestRequirementPlanner.Build([objective], Held));

        Assert.Null(requirement.Have);
        Assert.Equal(3, requirement.Remaining);
        Assert.False(requirement.IsSatisfied);
    }
}
