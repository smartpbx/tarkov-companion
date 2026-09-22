using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

public sealed class QuestHistoryInferenceTests
{
    private static readonly QuestProfileScope Scope = new(Guid.NewGuid(), GameMode.Regular, "0.16");
    private readonly QuestHistoryInference _inference = new();

    [Fact]
    public void ConfirmedQuestBecomesActiveAndCompletedPrerequisitesAreTransitive()
    {
        var catalog = Catalog(
            Task("debut", "Debut"),
            Task("search", "Search Mission", Complete("debut")),
            Task("shootout", "Shootout Picnic", Complete("search")));

        var preview = _inference.Preview(["shootout"], catalog, Progress());

        Assert.Collection(
            preview.Changes,
            change => Assert.Equal(("shootout", RecordedTaskState.Active), (change.TaskId, change.NewState)),
            change => Assert.Equal(("search", RecordedTaskState.Completed), (change.TaskId, change.NewState)),
            change => Assert.Equal(("debut", RecordedTaskState.Completed), (change.TaskId, change.NewState)));
        Assert.Equal(1, preview.ActiveChanges);
        Assert.Equal(2, preview.EarlierQuestChanges);
        Assert.DoesNotContain(preview.Changes, change => change.NewState == RecordedTaskState.Failed);
    }

    [Fact]
    public void ExistingCompletedAndFailedStatesNeverMoveBackwards()
    {
        var catalog = Catalog(
            Task("done", "Already Done"),
            Task("failed", "Already Failed"),
            Task("current", "Current Task", Complete("done"), Complete("failed")));
        var progress = Progress(
            ("done", RecordedTaskState.Completed),
            ("failed", RecordedTaskState.Failed));

        var preview = _inference.Preview(["done", "failed", "current"], catalog, progress);

        var change = Assert.Single(preview.Changes);
        Assert.Equal("current", change.TaskId);
        Assert.Equal(RecordedTaskState.Active, change.NewState);
    }

    [Fact]
    public void NonCompletionRequirementDoesNotCreateHistory()
    {
        var catalog = Catalog(
            Task("branch", "Branch Quest"),
            Task("current", "Current Task", Required("branch", "active")));

        var preview = _inference.Preview(["current"], catalog, Progress());

        Assert.DoesNotContain(preview.Changes, change => change.TaskId == "branch");
        Assert.Equal(0, preview.EarlierQuestChanges);
    }

    [Fact]
    public void ConfirmedActiveEvidenceWinsOverContradictoryPrerequisiteEdge()
    {
        var catalog = Catalog(
            Task("first", "First Task"),
            Task("second", "Second Task", Complete("first")));

        var preview = _inference.Preview(["first", "second"], catalog, Progress());

        Assert.Equal(2, preview.Changes.Count);
        Assert.All(preview.Changes, change => Assert.Equal(RecordedTaskState.Active, change.NewState));
    }

    [Fact]
    public void CyclesTerminateAndEachQuestChangesAtMostOnce()
    {
        var catalog = Catalog(
            Task("one", "One", Complete("two")),
            Task("two", "Two", Complete("one")));

        var preview = _inference.Preview(["one"], catalog, Progress());

        Assert.Equal(2, preview.Changes.Count);
        Assert.Equal(2, preview.Changes.Select(change => change.TaskId).Distinct().Count());
    }

    [Fact]
    public void SharedPrerequisiteAppearsOnceInPreview()
    {
        var catalog = Catalog(
            Task("earlier", "Earlier"),
            Task("one", "One", Complete("earlier")),
            Task("two", "Two", Complete("earlier")));

        var preview = _inference.Preview(["one", "two"], catalog, Progress());

        Assert.Single(preview.Changes, change => change.TaskId == "earlier");
    }

    [Fact]
    public void DuplicateAndUnknownConfirmedIdsAreReportedWithoutDuplicateChanges()
    {
        var catalog = Catalog(Task("known", "Known Task"));

        var preview = _inference.Preview(["known", "known", "missing"], catalog, Progress());

        Assert.Single(preview.Changes);
        Assert.Equal(["missing"], preview.UnknownTaskIds);
    }

    private static QuestCatalogSnapshot Catalog(params QuestTaskDefinition[] tasks) => new(
        new(
            "fixture",
            "https://example.invalid/tasks",
            GameMode.Regular,
            "regular",
            "en",
            new('a', 64),
            new('b', 64),
            null,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch),
        tasks,
        "{}",
        "{}");

    private static QuestProgressSnapshot Progress(params (string Id, RecordedTaskState State)[] tasks) => new(
        Scope,
        0,
        tasks.ToDictionary(
            task => task.Id,
            task => new RecordedTaskProgress(task.Id, task.State, "fixture", 1, DateTimeOffset.UnixEpoch),
            StringComparer.Ordinal),
        new Dictionary<string, RecordedObjectiveProgress>(StringComparer.Ordinal),
        [],
        []);

    private static QuestTaskRequirement Complete(string taskId) => Required(taskId, "complete");

    private static QuestTaskRequirement Required(string taskId, params string[] statuses) =>
        new(0, taskId, statuses, "{}");

    private static QuestTaskDefinition Task(
        string id,
        string name,
        params QuestTaskRequirement[] requirements) => new(
        id,
        name,
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
        null,
        [],
        requirements,
        [],
        [],
        "{}");
}
