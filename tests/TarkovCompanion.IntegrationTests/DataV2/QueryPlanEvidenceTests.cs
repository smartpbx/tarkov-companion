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
            ],
            wholeTable);
    }
}
