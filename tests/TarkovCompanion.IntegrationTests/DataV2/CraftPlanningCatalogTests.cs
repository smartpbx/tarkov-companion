using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests.DataV2;

[Collection(SqliteCollection.Name)]
public sealed class CraftPlanningCatalogTests
{
    [Fact]
    public async Task ProductionCatalogReadsNormalizedCraftsWithBoundedNullableEconomics()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var refresh = new SqliteDataRefreshRepository(database.Factory);
        await refresh.RefreshCraftsAsync(
        [
            new TarkovDevCraft
            {
                Id = "craft-a",
                Station = "workbench",
                Level = 2,
                RequiredItems =
                [
                    new TarkovDevItemRequirement
                    {
                        Item = "input-a",
                        Count = 3,
                        AdditionalData = FutureData("futureRequirement", "retained"),
                    },
                ],
                ProductItem = new TarkovDevItemRequirement { Item = "output-a", Count = 2 },
                AdditionalData = FutureData("futureCraft", "retained"),
            },
            new TarkovDevCraft
            {
                Id = "craft-with-unknown-level",
                Station = "workbench",
                Level = null,
                ProductItem = new TarkovDevItemRequirement { Item = "output-b", Count = 1 },
            },
            new TarkovDevCraft
            {
                Id = "other-station-craft",
                Station = "medstation",
                Level = 1,
                ProductItem = new TarkovDevItemRequirement { Item = "output-c", Count = 1 },
            },
        ], TestContext.Current.CancellationToken);

        var now = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        var historyStore = new SqliteV2DataStore(database.Factory);
        var oldest = new CraftHistoryRecord(
            Guid.NewGuid(), "craft-a", "workbench", 2, null, now,
            "output-a", null, null, null, "fixture://planner", "{\"futureModel\":1}");
        var previous = new CraftHistoryRecord(
            Guid.NewGuid(), "craft-a", "workbench", 2, now.AddMinutes(1), now.AddMinutes(1),
            "output-a", 2, 30_000, null, "fixture://planner", "{\"sample\":2}");
        var latest = new CraftHistoryRecord(
            Guid.NewGuid(), "craft-a", "workbench", 2, now.AddMinutes(2), now.AddMinutes(2),
            "output-a", 2, null, 80_000, "fixture://planner", "{\"sample\":3}");
        var unknownEconomics = new CraftHistoryRecord(
            Guid.NewGuid(), "craft-with-unknown-level", "workbench", null, null, now.AddMinutes(3),
            "output-b", null, null, null, "fixture://planner", "{\"sample\":4}");
        await historyStore.AppendCraftHistoryAsync(oldest, TestContext.Current.CancellationToken);
        await historyStore.AppendCraftHistoryAsync(previous, TestContext.Current.CancellationToken);
        await historyStore.AppendCraftHistoryAsync(latest, TestContext.Current.CancellationToken);
        await historyStore.AppendCraftHistoryAsync(unknownEconomics, TestContext.Current.CancellationToken);

        ICraftPlanningCatalog catalog = new SqliteCraftPlanningCatalog(database.Factory);
        var station = await catalog.GetByStationAsync(
            "workbench",
            2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, station.Count);
        Assert.DoesNotContain(station, entry => entry.CraftId == "other-station-craft");
        var craft = Assert.Single(station, entry => entry.CraftId == "craft-a");
        Assert.Equal("workbench", craft.StationId);
        Assert.Equal<int?>(2, craft.StationLevel);
        Assert.Contains("futureCraft", craft.SourceJson, StringComparison.Ordinal);
        var requirement = Assert.Single(craft.Requirements);
        Assert.Equal("input-a", requirement.ItemId);
        Assert.Equal<decimal?>(3, requirement.Count);
        Assert.Contains("futureRequirement", requirement.SourceJson, StringComparison.Ordinal);
        var output = Assert.Single(craft.Outputs);
        Assert.Equal("output-a", output.ItemId);
        Assert.Equal<decimal?>(2, output.Count);

        Assert.Equal(2, craft.History.Count);
        Assert.Equal(latest.HistoryId, craft.History[0].HistoryId);
        Assert.Null(craft.History[0].EstimatedCostRoubles);
        Assert.Equal<long?>(80_000, craft.History[0].EstimatedYieldRoubles);
        Assert.Equal(latest.PayloadJson, craft.History[0].PayloadJson);
        Assert.Equal(previous.HistoryId, craft.History[1].HistoryId);
        Assert.Equal<long?>(30_000, craft.History[1].EstimatedCostRoubles);
        Assert.Null(craft.History[1].EstimatedYieldRoubles);
        Assert.DoesNotContain(craft.History, observation => observation.HistoryId == oldest.HistoryId);

        var unknownLevel = Assert.Single(station, entry => entry.CraftId == "craft-with-unknown-level");
        Assert.Null(unknownLevel.StationLevel);
        var unknownObservation = Assert.Single(unknownLevel.History);
        Assert.Equal(unknownEconomics.HistoryId, unknownObservation.HistoryId);
        Assert.Null(unknownObservation.ObservedUtc);
        Assert.Null(unknownObservation.OutputCount);
        Assert.Null(unknownObservation.EstimatedCostRoubles);
        Assert.Null(unknownObservation.EstimatedYieldRoubles);

        var exact = await catalog.GetByCraftAsync(
            "craft-a",
            1,
            TestContext.Current.CancellationToken);
        Assert.NotNull(exact);
        Assert.Equal(latest.HistoryId, Assert.Single(exact.History).HistoryId);
        Assert.Null(await catalog.GetByCraftAsync(
            "missing",
            1,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProductionCatalogEnforcesHistoryBoundsAndUsesEveryCraftIndex()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var catalog = new SqliteCraftPlanningCatalog(database.Factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => catalog.GetByStationAsync(
            "workbench",
            -1,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => catalog.GetByCraftAsync(
            "craft-a",
            ICraftPlanningCatalog.MaximumHistoryLimitPerCraft + 1,
            TestContext.Current.CancellationToken));

        var plan = Assert.Single(
            await new SqliteQueryPlanAuditor(database.Factory)
                .CaptureAsync(TestContext.Current.CancellationToken),
            candidate => candidate.Path == "craft");
        var steps = string.Join(" | ", plan.Steps);
        Assert.Contains("idx_crafts_station_level", steps, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idx_craft_requirements_craft", steps, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idx_craft_outputs_craft", steps, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idx_craft_history_lookup", steps, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StationPlannerBoundsDatabaseWorkAndRejectsDynamicTypePoison()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await new SqliteDataRefreshRepository(database.Factory).RefreshCraftsAsync(
        [
            new TarkovDevCraft
            {
                Id = "large-tail-craft",
                Station = "workbench",
                Level = 1,
                ProductItem = new TarkovDevItemRequirement { Item = "output", Count = 1 },
            },
        ], TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(
            database.Factory,
            """
            WITH RECURSIVE sequence(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM sequence WHERE value < 5000
            )
            INSERT INTO craft_history(
                history_id, craft_id, station_id, station_level, observed_utc, recorded_utc,
                output_item_id, output_count, estimated_cost_roubles, estimated_yield_roubles,
                source, payload_json)
            SELECT printf('10000000-0000-0000-0000-%012x', value), 'large-tail-craft',
                   'workbench', 1, '2026-09-15T00:00:00.0000000+00:00',
                   '2026-09-15T00:00:00.0000000+00:00', 'output', 1, 1, 1,
                   'fixture', '{"sample":1}'
            FROM sequence;
            """);

        var catalog = new SqliteCraftPlanningCatalog(database.Factory);
        var entry = Assert.Single(await catalog.GetByStationAsync(
            "workbench", 3, TestContext.Current.CancellationToken));
        Assert.Equal(3, entry.History.Count);

        await ExecuteSqlAsync(
            database.Factory,
            "PRAGMA ignore_check_constraints = ON; UPDATE craft_history SET station_level = 1.5 WHERE craft_id = 'large-tail-craft'; PRAGMA ignore_check_constraints = OFF;");
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetByStationAsync(
            "workbench", 3, TestContext.Current.CancellationToken));

        await ExecuteSqlAsync(
            database.Factory,
            """
            WITH RECURSIVE sequence(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM sequence WHERE value < 1025
            )
            INSERT INTO crafts(id, station_id, level, source_json)
            SELECT 'overflow-' || value, 'overflow-station', 1, '{}' FROM sequence;
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.GetByStationAsync(
            "overflow-station", 0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ProductionCompositionRegistersTheCraftPlanningCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-craft-catalog-{Guid.NewGuid():N}");
        try
        {
            using var services = AppComposition.Build(
                new AppCommandLine(false, false, true, false, null, null, null),
                new(DataRoot: root, Offline: true));

            Assert.Same(
                services.GetRequiredService<SqliteCraftPlanningCatalog>(),
                services.GetRequiredService<ICraftPlanningCatalog>());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Dictionary<string, JsonElement> FutureData(string name, string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return new() { [name] = document.RootElement.Clone() };
    }

    private static async Task ExecuteSqlAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
