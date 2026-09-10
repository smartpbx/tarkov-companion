using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

public sealed class QuestEligibilityEvaluatorTests
{
    [Fact]
    public void MissingRecordedPrerequisiteIsIndeterminateRatherThanLocked()
    {
        var prerequisite = Task("prerequisite");
        var task = Task("task", [new(0, prerequisite.Id, ["complete"], "{}")]);
        var catalog = Catalog(task, prerequisite);
        var progress = Progress(Profile(), tasks: []);

        var result = new QuestEligibilityEvaluator().Evaluate(
            task,
            catalog,
            progress,
            Profile(),
            DateTimeOffset.Parse("2026-09-10T12:00:00Z"));

        Assert.Equal(QuestEligibilityState.Indeterminate, result.State);
        Assert.Contains(result.Reasons, reason => reason.Code == "unknown-prerequisite-state");
    }

    [Fact]
    public void PrerequisiteCycleIsReportedWithoutRecursingForever()
    {
        var taskA = Task("a", [new(0, "b", ["complete"], "{}")]);
        var taskB = Task("b", [new(0, "a", ["complete"], "{}")]);

        var result = new QuestEligibilityEvaluator().Evaluate(
            taskA,
            Catalog(taskA, taskB),
            Progress(Profile(), tasks: []),
            Profile(),
            DateTimeOffset.Parse("2026-09-10T12:00:00Z"));

        Assert.Equal(QuestEligibilityState.Indeterminate, result.State);
        Assert.Equal("prerequisite-cycle", Assert.Single(result.Reasons).Code);
    }

    [Fact]
    public void ExplicitlyUnmetKnownPrerequisiteIsLocked()
    {
        var prerequisite = Task("prerequisite");
        var task = Task("task", [new(0, prerequisite.Id, ["complete"], "{}")]);
        var profile = Profile();
        var progress = Progress(
            profile,
            [new(
                prerequisite.Id,
                RecordedTaskState.NotStarted,
                "Manual",
                1,
                DateTimeOffset.Parse("2026-09-10T10:00:00Z"))]);

        var result = new QuestEligibilityEvaluator().Evaluate(
            task,
            Catalog(task, prerequisite),
            progress,
            profile,
            DateTimeOffset.Parse("2026-09-10T12:00:00Z"));

        Assert.Equal(QuestEligibilityState.Locked, result.State);
        Assert.Contains(result.Reasons, reason => reason.Code == "prerequisite-state");
    }

    [Fact]
    public void RecordedCompletionTimeProducesDeterministicDelay()
    {
        var prerequisite = Task("prerequisite");
        var task = Task("task", [new(0, prerequisite.Id, ["complete"], "{}")]) with
        {
            AvailableDelaySecondsMinimum = 3600,
            AvailableDelaySecondsMaximum = 3600,
        };
        var profile = Profile();
        var completedUtc = DateTimeOffset.Parse("2026-09-10T10:00:00Z");
        var progress = Progress(
            profile,
            [new(prerequisite.Id, RecordedTaskState.Completed, "Manual", 1, completedUtc)]);

        var result = new QuestEligibilityEvaluator().Evaluate(
            task,
            Catalog(task, prerequisite),
            progress,
            profile,
            completedUtc.AddMinutes(30));

        Assert.Equal(QuestEligibilityState.Delayed, result.State);
        Assert.Equal(completedUtc.AddHours(1), result.AvailableUtc);
    }

    [Fact]
    public void RequiredPrestigeWithoutProfileStateIsIndeterminate()
    {
        var task = Task("prestige-task") with { RequiredPrestigeId = "prestige-001" };
        var profile = Profile();

        var result = new QuestEligibilityEvaluator().Evaluate(
            task,
            Catalog(task),
            Progress(profile, tasks: []),
            profile,
            DateTimeOffset.Parse("2026-09-10T12:00:00Z"));

        Assert.Equal(QuestEligibilityState.Indeterminate, result.State);
        Assert.Contains(result.Reasons, reason => reason.Code == "unknown-profile-prestige");
    }

    private static QuestTaskDefinition Task(
        string id,
        IReadOnlyList<QuestTaskRequirement>? requirements = null) => new(
        id,
        id,
        id,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        ["regular"],
        requirements ?? [],
        [],
        [],
        "{}");

    private static QuestCatalogSnapshot Catalog(params QuestTaskDefinition[] tasks) => new(
        new(
            "fixture",
            "https://fixture.invalid/tasks",
            GameMode.Regular,
            "regular",
            "en",
            "hash",
            "translated-hash",
            null,
            null,
            DateTimeOffset.Parse("2026-09-10T10:00:00Z"),
            DateTimeOffset.Parse("2026-09-10T10:00:00Z")),
        tasks,
        "{}",
        "{}");

    private static PlayerProfile Profile() => QuestReadServiceTests.Profile();

    private static QuestProgressSnapshot Progress(
        PlayerProfile profile,
        IReadOnlyList<RecordedTaskProgress> tasks) => new(
        new(profile.Id, profile.GameMode, profile.ProfileGeneration),
        0,
        tasks.ToDictionary(value => value.TaskId, StringComparer.Ordinal),
        new Dictionary<string, RecordedObjectiveProgress>(StringComparer.Ordinal),
        [],
        []);
}

public sealed class QuestReadServiceTests
{
    private static readonly DateTimeOffset RecordedUtc = DateTimeOffset.Parse("2026-09-10T10:00:00Z");

    [Fact]
    public async Task ItemNeedsKeepAlternativesAndFirHoldingsSeparate()
    {
        var profile = Profile();
        var objective = Objective(
            "objective",
            targetCount: 3,
            foundInRaid: true,
            [
                new("item-a", "items", 0, 0, 3, true),
                new("item-b", "items", 0, 1, 3, true),
            ]);
        var task = TaskDefinition("task", [objective]);
        var progress = new QuestProgressSnapshot(
            new(profile.Id, profile.GameMode, profile.ProfileGeneration),
            4,
            new Dictionary<string, RecordedTaskProgress>
            {
                [task.Id] = new(task.Id, RecordedTaskState.Active, "Manual", 1, RecordedUtc),
            },
            new Dictionary<string, RecordedObjectiveProgress>
            {
                [objective.Id] = new(
                    objective.Id,
                    RecordedObjectiveState.InProgress,
                    1,
                    "Manual",
                    2,
                    RecordedUtc),
            },
            [
                new("item-a", false, 7, "Manual", 3, RecordedUtc),
                new("item-a", true, 2, "Manual", 4, RecordedUtc),
            ],
            []);
        var service = Service(profile, Catalog(task), progress);

        var result = await service.GetItemNeedsAsync(progress.Scope, "item-a", CancellationToken.None);

        var requirement = Assert.Single(result.Requirements);
        Assert.Equal(["item-a", "item-b"], requirement.AcceptableItemIds);
        Assert.True(requirement.FoundInRaidRequired);
        Assert.Equal(3, requirement.TargetCount);
        Assert.Equal(1, requirement.RecordedCount);
        Assert.Equal(2, requirement.RemainingCount);
        Assert.Equal(2, result.FoundInRaidHeldCount);
        Assert.Equal(7, result.NonFoundInRaidHeldCount);
    }

    [Fact]
    public async Task CompletedObjectivesDoNotInferTaskCompletion()
    {
        var profile = Profile();
        var objective = Objective("objective", 1, false, []);
        var task = TaskDefinition("task", [objective]);
        var progress = new QuestProgressSnapshot(
            new(profile.Id, profile.GameMode, profile.ProfileGeneration),
            2,
            new Dictionary<string, RecordedTaskProgress>
            {
                [task.Id] = new(task.Id, RecordedTaskState.Active, "Manual", 1, RecordedUtc),
            },
            new Dictionary<string, RecordedObjectiveProgress>
            {
                [objective.Id] = new(
                    objective.Id,
                    RecordedObjectiveState.Completed,
                    1,
                    "Manual",
                    2,
                    RecordedUtc),
            },
            [],
            []);

        var board = await Service(profile, Catalog(task), progress)
            .GetQuestBoardAsync(progress.Scope, CancellationToken.None);

        var summary = Assert.Single(board.Tasks);
        Assert.Equal(RecordedTaskState.Active, summary.RecordedState);
        Assert.Equal(RecordedObjectivesSatisfaction.Satisfied, summary.RecordedObjectivesSatisfied);
    }

    internal static PlayerProfile Profile() => new(
        Guid.Parse("46bc28fe-1554-4b16-884f-fe725285877b"),
        "Quest profile",
        GameMode.Regular,
        20,
        Faction.Usec,
        null,
        new Dictionary<string, int>(),
        new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, int>(),
        new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, EventItemState>(),
        new Dictionary<string, string>(),
        DateTimeOffset.Parse("2026-09-10T10:00:00Z"),
        "generation-a");

    private static QuestReadService Service(
        PlayerProfile profile,
        QuestCatalogSnapshot catalog,
        QuestProgressSnapshot progress) => new(
        new StubProfileService(profile),
        new StubCatalog(catalog),
        new StubProgressStore(progress),
        new QuestEligibilityEvaluator(),
        new(),
        TimeProvider.System);

    private static QuestTaskDefinition TaskDefinition(
        string id,
        IReadOnlyList<QuestObjectiveDefinition> objectives) => new(
        id,
        "Task name",
        "task name",
        null,
        1,
        "Usec",
        null,
        false,
        false,
        false,
        null,
        null,
        null,
        ["regular"],
        [],
        objectives,
        [],
        "{}");

    private static QuestObjectiveDefinition Objective(
        string id,
        decimal targetCount,
        bool foundInRaid,
        IReadOnlyList<QuestObjectiveItemTarget> itemTargets) => new(
        id,
        "task",
        "findItem",
        QuestObjectiveKind.FindItem,
        false,
        0,
        "Find items",
        targetCount,
        false,
        foundInRaid,
        null,
        [],
        itemTargets,
        [],
        [],
        "{}",
        "{}");

    private static QuestCatalogSnapshot Catalog(params QuestTaskDefinition[] tasks) => new(
        new(
            "fixture",
            "https://fixture.invalid/tasks",
            GameMode.Regular,
            "regular",
            "en",
            "hash",
            "translated-hash",
            null,
            null,
            DateTimeOffset.Parse("2026-09-10T10:00:00Z"),
            DateTimeOffset.Parse("2026-09-10T10:00:00Z")),
        tasks,
        "{}",
        "{}");

    private sealed class StubProfileService(PlayerProfile profile) : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(profile);

        public Task SaveAsync(PlayerProfile value, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubCatalog(QuestCatalogSnapshot catalog) : IQuestCatalog
    {
        public Task<QuestCatalogSnapshot?> GetAsync(
            GameMode gameMode,
            string language,
            CancellationToken cancellationToken) => Task.FromResult<QuestCatalogSnapshot?>(catalog);
    }

    private sealed class StubProgressStore(QuestProgressSnapshot progress) : IQuestProgressStore
    {
        public Task<QuestProgressSnapshot> GetAsync(
            QuestProfileScope scope,
            CancellationToken cancellationToken) => Task.FromResult(progress);

        public Task<QuestProgressCommandResult> ApplyAsync(
            QuestProgressMutation mutation,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<QuestProgressChange>> GetJournalAsync(
            QuestProfileScope scope,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
