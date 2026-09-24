using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.IntegrationTests.DataV2;

/// <summary>
/// #270: the query plans behind the hot reads, held to something stricter than "the word USING
/// appears somewhere". <see cref="SqliteQueryPlanAuditor"/> already runs EXPLAIN QUERY PLAN against
/// the SQL the real repositories execute; its <c>UsesIndex</c> only asks that some step mentions an
/// index, so a statement that searches one table and scans another still passes. This pins what
/// each statement is allowed to scan, so a new full scan in a read that is supposed to be indexed
/// fails here and has to be justified in writing.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class QueryPlanEvidenceTests
{
    [Fact]
    public async Task IndexedReadsSearchTheirMainTableAndScanOnlyWhatIsWrittenDownHere()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var plans = await new SqliteQueryPlanAuditor(database.Factory).CaptureAsync(TestContext.Current.CancellationToken);
        var required = plans
            .SelectMany(plan => plan.Statements
                .Where(statement => statement.IndexRequired)
                .Select(statement => (Name: $"{plan.Path}/{statement.Name}", statement.Steps)))
            .ToArray();

        // Every one of them looks something up by key. None of them builds a throwaway index to do it.
        Assert.All(required, read =>
        {
            Assert.Contains(read.Steps, step => step.StartsWith("SEARCH ", StringComparison.Ordinal));
            Assert.DoesNotContain(read.Steps, step => step.Contains("AUTOMATIC", StringComparison.Ordinal));
        });

        // The one full scan inside a read that is otherwise indexed: the trail query picks the raids on
        // a map with `map_id = ?` and there is no index on raids(map_id). It reads the raid table once
        // per map opened; that table is one row per raid the player has played, so it is left as it is,
        // and named here so it cannot grow a sibling unnoticed.
        var scans = required
            .SelectMany(read => read.Steps
                .Where(step => step.StartsWith("SCAN ", StringComparison.Ordinal) && !step.Contains(" USING ", StringComparison.Ordinal))
                .Select(step => $"{read.Name}: {step}"))
            .ToArray();
        Assert.Equal(["history/map-trails: SCAN raids"], scans);
    }

    [Fact]
    public async Task TheReadsThatAreWholeTableByDesignAreExactlyTheOnesTheDocumentationNames()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var plans = await new SqliteQueryPlanAuditor(database.Factory).CaptureAsync(TestContext.Current.CancellationToken);

        var wholeTable = plans
            .SelectMany(plan => plan.Statements
                .Where(statement => !statement.IndexRequired)
                .Select(statement => $"{plan.Path}/{statement.Name}: {string.Join(" | ", statement.Steps)}"))
            .ToArray();

        Assert.Equal(
            [
                "map/catalog-scan: SCAN maps",
                "history/history-list: SCAN raids | USE TEMP B-TREE FOR ORDER BY",
                "profile/profile-contexts: SCAN profile_contexts USING INDEX sqlite_autoindex_profile_contexts_1",
                // Loaded whole once per sync and cached. The join to the objective is still a key lookup.
                "requirements/quest-items: SCAN item USING INDEX sqlite_autoindex_task_objective_items_1 | " +
                    "SEARCH objective USING COVERING INDEX sqlite_autoindex_task_objectives_1 (task_id=? AND id=?)",
                "requirements/hideout-items: SCAN hideout_requirements",
                // FTS5 reports every lookup as a SCAN of the virtual table; ":M" is the MATCH constraint
                // going to its own index, which is the part that matters.
                "item-search/full-text: SCAN item_search VIRTUAL TABLE INDEX 0:M5 | USE TEMP B-TREE FOR ORDER BY",
                // Typo tolerance scores every name in memory: about 5,300 short rows, read once per search.
                "item-search/fuzzy-names: SCAN items",
            ],
            wholeTable);
    }

    [Fact]
    public async Task QuestProgressReadsFindOneProfileByItsWholeScopeAndExactSearchUsesBothNameIndexes()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var plans = await new SqliteQueryPlanAuditor(database.Factory).CaptureAsync(TestContext.Current.CancellationToken);

        // The quest page reads five tables per open. Each grows by one row per quest, objective, item
        // or pin per profile and generation, so each read must seek on the full scope, not a prefix of it.
        var questProgress = Assert.Single(plans, plan => plan.Path == "quest-progress").Statements;
        Assert.Equal(["profile-revision", "task-states", "objective-states", "item-holdings", "pins"],
            questProgress.Select(statement => statement.Name));
        Assert.All(questProgress, statement => Assert.StartsWith(
            "SEARCH ", statement.Steps[0], StringComparison.Ordinal));
        Assert.All(questProgress, statement => Assert.EndsWith(
            "(profile_id=? AND game_mode=? AND generation=?)", statement.Steps[0], StringComparison.Ordinal));

        // An exact name or short name: an OR that falls back to a scan when either index is missing.
        var exact = Assert.Single(
            Assert.Single(plans, plan => plan.Path == "item-search").Statements,
            statement => statement.Name == "exact-name");
        Assert.Equal(
            [
                "MULTI-INDEX OR",
                "INDEX 1",
                "SEARCH items USING INDEX idx_items_normalized_name (normalized_name=?)",
                "INDEX 2",
                "SEARCH items USING INDEX idx_items_normalized_short_name (normalized_short_name=?)",
            ],
            exact.Steps.ToArray());
    }
}
