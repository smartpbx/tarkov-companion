using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>[#802] A quest picked in Plan, with no game log, has to reach the Raid objectives layer.</summary>
public sealed class PlanQuestOnItTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T10:00:00Z");

    [Fact]
    public async Task A_quest_found_with_no_game_log_reaches_the_raid_objectives_only_after_on_it()
    {
        var profile = QuestReadServiceTests.Profile();
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var store = new MemoryProgressStore(scope);
        var catalog = new FixedCatalog(Catalog(CustomsTask("delivery", "Delivery From the Past", "delivery-folder")));
        var profiles = new FixedProfileService(profile);
        var read = new QuestReadService(profiles, catalog, store, new QuestEligibilityEvaluator(), new(), TimeProvider.System);
        var commands = new QuestProgressCommandService(profiles, catalog, store, new(), TimeProvider.System);

        // What Clayton had: the quest is on the board, state Unknown, and the Raid layer is empty.
        var found = Assert.Single((await read.GetQuestBoardAsync(scope, CancellationToken.None)).Tasks);
        Assert.Equal(RecordedTaskState.Unknown, found.RecordedState);
        Assert.False(PlanQuestOnIt.ShowsInRaid(found));
        Assert.Empty((await read.GetActiveMapObjectivesAsync(scope, ["customs"], CancellationToken.None)).Objectives);

        await PlanQuestOnIt.ApplyAsync(commands, scope, found, CancellationToken.None);

        var onIt = Assert.Single((await read.GetQuestBoardAsync(scope, CancellationToken.None)).Tasks);
        Assert.Equal(RecordedTaskState.Active, onIt.RecordedState);
        Assert.True(onIt.IsPinned);
        Assert.True(PlanQuestOnIt.ShowsInRaid(onIt));
        Assert.False(PlanQuestOnIt.CanPutOnIt(onIt));
        var drawn = Assert.Single((await read.GetActiveMapObjectivesAsync(scope, ["customs"], CancellationToken.None)).Objectives);
        Assert.Equal("delivery-folder", drawn.ObjectiveId);
    }

    [Fact]
    public async Task On_it_twice_writes_nothing_the_second_time()
    {
        var profile = QuestReadServiceTests.Profile();
        var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
        var store = new MemoryProgressStore(scope);
        var catalog = new FixedCatalog(Catalog(CustomsTask("delivery", "Delivery From the Past", "delivery-folder")));
        var profiles = new FixedProfileService(profile);
        var read = new QuestReadService(profiles, catalog, store, new QuestEligibilityEvaluator(), new(), TimeProvider.System);
        var commands = new QuestProgressCommandService(profiles, catalog, store, new(), TimeProvider.System);

        await PlanQuestOnIt.ApplyAsync(commands, scope, Assert.Single((await read.GetQuestBoardAsync(scope, CancellationToken.None)).Tasks), CancellationToken.None);
        var writes = store.Writes;
        await PlanQuestOnIt.ApplyAsync(commands, scope, Assert.Single((await read.GetQuestBoardAsync(scope, CancellationToken.None)).Tasks), CancellationToken.None);

        Assert.Equal(2, writes);
        Assert.Equal(writes, store.Writes);
    }

    [Fact]
    public void Open_in_raid_puts_on_a_searched_handful_but_never_a_whole_filter()
    {
        var one = Summary("a", RecordedTaskState.Unknown);
        Assert.Equal(["a"], PlanQuestOnIt.ForOpenInRaid([one, one]).Select(task => task.TaskId));

        var tracked = Summary("b", RecordedTaskState.Active);
        var done = Summary("c", RecordedTaskState.Completed);
        Assert.Equal(["a"], PlanQuestOnIt.ForOpenInRaid([one, tracked, done]).Select(task => task.TaskId));

        var many = Enumerable.Range(0, PlanQuestOnIt.OpenInRaidLimit + 1)
            .Select(index => Summary($"q{index}", RecordedTaskState.NotStarted));
        Assert.Empty(PlanQuestOnIt.ForOpenInRaid(many));
    }

    [Theory]
    [InlineData(RecordedTaskState.Unknown, false, false, true)]
    [InlineData(RecordedTaskState.NotStarted, false, false, true)]
    [InlineData(RecordedTaskState.Active, false, true, false)]
    [InlineData(RecordedTaskState.Unknown, true, true, false)]
    [InlineData(RecordedTaskState.Failed, true, false, true)]
    [InlineData(RecordedTaskState.Completed, true, false, false)]
    public void Shows_in_raid_follows_the_rule_the_objectives_layer_reads(
        RecordedTaskState state, bool pinned, bool showsInRaid, bool canPutOnIt)
    {
        var task = Summary("t", state) with { IsPinned = pinned };

        Assert.Equal(showsInRaid, PlanQuestOnIt.ShowsInRaid(task));
        Assert.Equal(canPutOnIt, PlanQuestOnIt.CanPutOnIt(task));
    }

    private static QuestSummaryReadModel Summary(string id, RecordedTaskState state) => new(
        id,
        id,
        null,
        null,
        state,
        "Manual",
        null,
        new(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfaction.Indeterminate,
        false,
        null,
        false,
        [],
        [],
        []);

    private static QuestTaskDefinition CustomsTask(string id, string name, string objectiveId) => new(
        id,
        name,
        name.ToLowerInvariant(),
        null,
        1,
        "Usec",
        "customs",
        false,
        false,
        false,
        null,
        null,
        null,
        ["regular"],
        [],
        [
            new QuestObjectiveDefinition(
                objectiveId,
                id,
                "visit",
                QuestObjectiveKind.Visit,
                false,
                0,
                "Locate the folder",
                null,
                false,
                null,
                null,
                [],
                [],
                [],
                [],
                "{}",
                "{}") with
            {
                MapAssociations = [new(QuestMapAssociationKind.Declared, 0, "customs")],
            },
        ],
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
            Now,
            Now),
        tasks,
        "{}",
        "{}");

    private sealed class FixedProfileService(PlayerProfile profile) : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(profile);

        public Task SaveAsync(PlayerProfile value, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedCatalog(QuestCatalogSnapshot catalog) : IQuestCatalog
    {
        public Task<QuestCatalogSnapshot?> GetAsync(GameMode gameMode, string language, CancellationToken cancellationToken) =>
            Task.FromResult<QuestCatalogSnapshot?>(catalog);
    }

    /// <summary>Task states and pins in memory: the two things "On it" writes.</summary>
    private sealed class MemoryProgressStore(QuestProfileScope scope) : IQuestProgressStore
    {
        private readonly Dictionary<string, RecordedTaskProgress> _tasks = new(StringComparer.Ordinal);
        private readonly List<RecordedQuestPin> _pins = [];
        private long _revision;

        public int Writes { get; private set; }

        public Task<QuestProgressSnapshot> GetAsync(QuestProfileScope requested, CancellationToken cancellationToken) =>
            Task.FromResult(new QuestProgressSnapshot(
                scope,
                _revision,
                new Dictionary<string, RecordedTaskProgress>(_tasks, StringComparer.Ordinal),
                new Dictionary<string, RecordedObjectiveProgress>(StringComparer.Ordinal),
                [],
                [.. _pins]));

        public Task<QuestProgressCommandResult> ApplyAsync(QuestProgressMutation mutation, CancellationToken cancellationToken)
        {
            Writes++;
            _revision++;
            switch (mutation)
            {
                case SetTaskStateMutation task:
                    _tasks[task.TaskId] = new(task.TaskId, task.State, task.Source, _revision, task.RecordedUtc);
                    break;
                case SetQuestPinMutation pin:
                    _pins.RemoveAll(existing => existing.TargetKind == pin.TargetKind && existing.TargetId == pin.TargetId);
                    if (pin.IsPinned)
                    {
                        _pins.Add(new(pin.TargetKind, pin.TargetId, pin.SortOrder, pin.Note, pin.Source, _revision, pin.RecordedUtc));
                    }

                    break;
                default:
                    throw new NotSupportedException(mutation.GetType().Name);
            }

            return Task.FromResult(new QuestProgressCommandResult(mutation.CorrelationId, _revision, true));
        }

        public Task<IReadOnlyList<QuestProgressChange>> GetJournalAsync(QuestProfileScope requested, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuestProgressChange>>([]);
    }
}
