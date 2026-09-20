using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.Planning;

/// <summary>
/// Same-named quests are alternatives everywhere on the real catalog except Neuanfang, whose
/// three tasks are one per prestige. Only the first tier is the player's until he is on a later one.
/// </summary>
public sealed class KeepListPrestigeTierTests
{
    [Fact]
    public void EveryPrestigeTierAfterTheFirstIsLeftOut()
    {
        QuestSummaryReadModel[] tasks =
        [
            Task("tier-3", "Neuanfang", 40, prestige: true),
            Task("tier-1", "Neuanfang", 30, prestige: true),
            Task("tier-2", "Neuanfang", 35, prestige: true),
        ];

        Assert.Equal(["tier-2", "tier-3"], KeepListPlanner.LaterPrestigeTiers(tasks).Order());
    }

    [Fact]
    public void ACompletedTierMakesTheNextOneTheFirst()
    {
        QuestSummaryReadModel[] tasks =
        [
            Task("tier-1", "Neuanfang", 30, prestige: true, RecordedTaskState.Completed),
            Task("tier-2", "Neuanfang", 35, prestige: true),
            Task("tier-3", "Neuanfang", 40, prestige: true),
        ];

        Assert.Equal(["tier-3"], KeepListPlanner.LaterPrestigeTiers(tasks));
    }

    [Fact]
    public void FactionCopiesAndBranchesAreNotTiers()
    {
        QuestSummaryReadModel[] tasks =
        [
            Task("bear", "Textile - Part 1", 1, prestige: false),
            Task("usec", "Textile - Part 1", 1, prestige: false),
            Task("alone", "Collector", 1, prestige: true),
            Task("mixed-a", "Mixed", 1, prestige: true),
            Task("mixed-b", "Mixed", 1, prestige: false),
        ];

        Assert.Empty(KeepListPlanner.LaterPrestigeTiers(tasks));
    }

    private static QuestSummaryReadModel Task(
        string id,
        string name,
        int level,
        bool prestige,
        RecordedTaskState state = RecordedTaskState.NotStarted) => new(
        id,
        name,
        null,
        null,
        state,
        "None",
        null,
        new(
            prestige ? QuestEligibilityState.Indeterminate : QuestEligibilityState.Available,
            prestige ? [new("unknown-profile-prestige", "The task requires a prestige.")] : []),
        default,
        false,
        null,
        false,
        [],
        [],
        [])
    {
        MinimumPlayerLevel = level,
    };
}
