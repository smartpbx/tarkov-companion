using System.Diagnostics;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Quests;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.Performance;

/// <summary>
/// What one keystroke in the Plan workspace's quest search costs, over a board the size of the real
/// one, held to numbers that were measured and are re-measured by
/// <c>tools/RaidPerfHarness --scenarios plansearch</c> (see docs/PERFORMANCE.md).
/// </summary>
/// <remarks>
/// <para>
/// The fault these budgets exist for: the filter used to join a quest's searchable text — its name,
/// its trader, every objective's description and the name of every map its objectives name — inside
/// the loop, for every quest the filter kept, on every character typed, and then build every group
/// and row view model again. Over the synced catalog that was about 3 KB and a fresh view model per
/// quest per keystroke, so the budget is written per quest: anything that reads all of them again
/// cannot stay inside it, however fast the machine is.
/// </para>
/// <para>
/// Allocation budgets are in bytes on one thread and do not move with the machine, so they are set
/// at about twice what was measured. The one time budget is for joining the whole index, which
/// happens once per board read, and is set wide enough for a shared CI runner.
/// </para>
/// <para>
/// A budget that fails is a prompt to run the harness and find out why, not to raise the number.
/// </para>
/// </remarks>
public sealed class PlanSearchBudgetTests(ITestOutputHelper output)
{
    /// <summary>The synced catalog's size, so the measurement is the one the player is typing over.</summary>
    private const int Quests = 515;
    private const int ObjectivesPerQuest = 3;

    // Measured 2026-09-18 on the dev box (8 cores, shared with other work), Release, .NET 10.
    //
    //   one keystroke, result set unchanged   13,822 B    26 B per quest
    //   one keystroke, result set narrowed    17,912 B    34 B per quest
    //   joining the whole index               415 KB, 0.5 ms — once per board read, not per keystroke
    //
    // What the old behaviour gave, measured by the harness over the synced catalog rather than from
    // here: 1,868 KB for one keystroke over 503 quests, about 3.7 KB per quest, and 18.3 MB to type
    // "graphics card".
    private const long KeystrokeBytesPerQuest = 64;
    private const long IndexBytes = 1_000_000;
    private const double IndexMilliseconds = 50;

    [Fact]
    public void A_keystroke_that_changes_nothing_does_not_read_every_quest_again()
    {
        var tasks = Board();
        var index = PlanSearchIndex.Build(tasks, MapName);
        IReadOnlyList<PlanMapGroupViewModel> groups = [];

        var bytes = Measure(200, () => groups = Keystroke(tasks, index, "graphics card", groups));

        var perQuest = bytes / Quests;
        output.WriteLine($"keystroke over an unchanged result set: {bytes} B, {perQuest} B per quest");
        Assert.NotEmpty(groups);
        Assert.True(
            perQuest <= KeystrokeBytesPerQuest,
            $"A keystroke allocated {perQuest} B per quest; the budget is {KeystrokeBytesPerQuest} B.");
    }

    [Fact]
    public void A_keystroke_that_narrows_the_result_set_does_not_read_every_quest_again()
    {
        // Alternating between two queries with different results, which is the case that cannot
        // reuse the last pass's rows wholesale and still may not read every quest's text again.
        var tasks = Board();
        var index = PlanSearchIndex.Build(tasks, MapName);
        IReadOnlyList<PlanMapGroupViewModel> groups = [];
        var wide = false;

        var bytes = Measure(200, () =>
        {
            wide = !wide;
            groups = Keystroke(tasks, index, wide ? "graphics" : "graphics card", groups);
        });

        var perQuest = bytes / Quests;
        output.WriteLine($"keystroke that narrows the result set: {bytes} B, {perQuest} B per quest");
        Assert.NotEmpty(groups);
        Assert.True(
            perQuest <= KeystrokeBytesPerQuest,
            $"A narrowing keystroke allocated {perQuest} B per quest; the budget is {KeystrokeBytesPerQuest} B.");
    }

    [Fact]
    public void Joining_what_the_search_reads_costs_one_board_read_not_one_keystroke()
    {
        var tasks = Board();
        PlanSearchIndex.Build(tasks.Take(10).ToArray(), MapName);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();

        var index = PlanSearchIndex.Build(tasks, MapName);

        var milliseconds = watch.Elapsed.TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"index build: {bytes} B, {milliseconds:F1} ms");
        Assert.Equal(Quests, index.Count);
        Assert.True(bytes <= IndexBytes, $"Joining the index allocated {bytes} B; the budget is {IndexBytes} B.");
        Assert.True(milliseconds <= IndexMilliseconds, $"Joining the index took {milliseconds:F0} ms; the budget is {IndexMilliseconds:F0} ms.");
    }

    /// <summary>Everything one character typed into the search box sets off, and nothing else.</summary>
    private static IReadOnlyList<PlanMapGroupViewModel> Keystroke(
        IReadOnlyList<QuestSummaryReadModel> tasks,
        PlanSearchIndex index,
        string query,
        IReadOnlyList<PlanMapGroupViewModel> groups) =>
        PlanWorkspaceViewModel.ComposeGroups(
            PlanWorkspaceViewModel.Bucket(
                tasks,
                PlanQuestFilter.All,
                QuestsPageViewModel.SearchTerms(query),
                traderId: null,
                index.TextFor),
            groups,
            MapName);

    /// <summary>Bytes allocated per call on this thread, after a warm-up that pays for the JIT.</summary>
    private static long Measure(int iterations, Action work, int warmup = 50)
    {
        for (var index = 0; index < warmup; index++)
        {
            work();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++)
        {
            work();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
    }

    private static string MapName(string mapId) => mapId.Length == 0 ? "Any map" : $"Map {mapId}";

    /// <summary>
    /// A board the shape of the synced one: 515 quests, three objectives each, most of them on one
    /// of a dozen maps, and one quest the test's query actually finds.
    /// </summary>
    private static QuestSummaryReadModel[] Board()
    {
        var maps = new[] { "customs", "woods", "shoreline", "factory", "interchange", "reserve", "lighthouse", "streets", "labs", "ground-zero", "any", "lab" };
        var tasks = new QuestSummaryReadModel[Quests];
        for (var index = 0; index < Quests; index++)
        {
            var objectives = new QuestObjectiveReadModel[ObjectivesPerQuest];
            for (var step = 0; step < ObjectivesPerQuest; step++)
            {
                var mapId = maps[(index + step) % maps.Length];
                objectives[step] = new(
                    ObjectiveId: $"objective-{index}-{step}",
                    Description: index == 42 && step == 0
                        ? "Hand over one graphics card found in raid to Mechanic"
                        : $"Stash the marked container in the dormitory basement, step {step} of quest {index}",
                    Kind: QuestObjectiveKind.FindItem,
                    IsOptional: false,
                    IsUnsupported: false,
                    RecordedState: RecordedObjectiveState.InProgress,
                    RecordedCount: null,
                    TargetCount: null,
                    FoundInRaidRequired: step == 0,
                    ProgressSource: "test",
                    ProgressModifiedUtc: null,
                    IsPinned: false,
                    MapIds: step == 2 ? [] : [mapId],
                    ItemTargets: []);
            }

            tasks[index] = new(
                TaskId: $"task-{index}",
                Name: $"Quest number {index}",
                TraderId: $"trader-{index % 8}",
                PrimaryMapId: null,
                RecordedState: RecordedTaskState.Active,
                ProgressSource: "test",
                ProgressModifiedUtc: null,
                Eligibility: new(QuestEligibilityState.Available, []),
                RecordedObjectivesSatisfied: RecordedObjectivesSatisfaction.Indeterminate,
                IsPinned: false,
                Restartable: false,
                HasFailureConditions: false,
                FailureConditionNotes: [],
                Prerequisites: [],
                Objectives: objectives)
            {
                TraderName = $"Trader {index % 8}",
            };
        }

        return tasks;
    }
}
