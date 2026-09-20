using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// Migration 0013 deleted every <c>task_objective_items</c> row on upgrade; 0016 puts them back
/// from <c>quest_objective_item_targets</c>, written the way a tasks sync writes them.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class TaskObjectiveItemsRestoreMigrationTests
{
    private const string Restore = "0016_restore_task_objective_items";

    [Fact]
    public async Task UpgradeThrough0013EndsWithTheQuestItemNeedsPutBack()
    {
        var path = NewPath();
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await using (var connection = await factory.OpenAsync(TestContext.Current.CancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE schema_migrations (version TEXT PRIMARY KEY, applied_utc TEXT NOT NULL);";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                foreach (var entry in SqliteMigrationLedger.Entries.TakeWhile(entry =>
                             !entry.Id.StartsWith("0013_", StringComparison.Ordinal)))
                {
                    command.CommandText = SqliteMigrationRunner.ReadFixture(entry.Id).UpgradeSql;
                    await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                    command.CommandText =
                        $"INSERT INTO schema_migrations(version, applied_utc) VALUES ('{entry.Id}', '2026-09-14T00:00:00Z');";
                    await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }

                command.CommandText = CatalogRows + """
                    INSERT INTO task_objective_items(objective_id, item_id, count, found_in_raid_required) VALUES
                        ('hand-over', 'salewa', 3, 1),
                        ('hand-over', 'car-kit', 3, 1),
                        ('plain', 'bolts', 1, 0);
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var applied = await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);

            Assert.Equal(Restore, applied.Applied[^1]);
            Assert.Equal(
                [
                    "task-a|hand-over|car-kit|3|1",
                    "task-a|hand-over|salewa|3|1",
                    "task-a|plain|bolts|1|0",
                ],
                await RowsAsync(factory));
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact]
    public async Task FreshDatabaseAndOneASyncAlreadyFilledAreLeftAlone()
    {
        var path = NewPath();
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            var runner = new SqliteMigrationRunner(factory);
            await runner.ApplyAsync(TestContext.Current.CancellationToken);
            Assert.Empty(await RowsAsync(factory));

            await using (var connection = await factory.OpenAsync(TestContext.Current.CancellationToken))
            await using (var command = connection.CreateCommand())
            {
                // What a sync after 0013 left: one of the three rows the catalog would give.
                command.CommandText = CatalogRows + $"""
                    INSERT INTO task_objective_items(task_id, objective_id, item_id, count, found_in_raid_required)
                    VALUES ('task-a', 'plain', 'bolts', 7, 0);
                    DELETE FROM schema_migrations WHERE version = '{Restore}';
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var again = await runner.ApplyAsync(TestContext.Current.CancellationToken);

            Assert.Equal([Restore], again.Applied);
            Assert.Equal(["task-a|plain|bolts|7|0"], await RowsAsync(factory));
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>
    /// One task in two snapshots. The newer one (regular) is what the legacy tables were filled
    /// from; the older one asks for a different count and must not be read. The task also carries
    /// a failure condition and a quest-item target, neither of which a sync copies across.
    /// </summary>
    private const string CatalogRows = """
        INSERT INTO tasks(id, name, source_json) VALUES ('task-a', 'Task A', '{}');
        INSERT INTO task_objectives(id, task_id, type, description) VALUES
            ('hand-over', 'task-a', 'giveItem', 'Hand over'),
            ('plain', 'task-a', 'giveItem', 'Hand over bolts'),
            ('pick-up', 'task-a', 'findQuestItem', 'Find the case');
        INSERT INTO quest_catalog_snapshots(
            source_key, source_uri, local_game_mode, source_mode, language, payload_sha256,
            translated_payload_sha256, fetched_utc, validated_utc, raw_json, translated_json)
        VALUES
            ('src', 'uri', 'Regular', 'regular', 'en', 'a', 'a', '2026-09-14T10:00:00Z', '2026-09-14T10:00:01Z', '{}', '{}'),
            ('src', 'uri', 'Pve', 'pve', 'en', 'b', 'b', '2026-09-13T10:00:00Z', '2026-09-13T10:00:01Z', '{}', '{}');
        INSERT INTO quest_catalog_tasks(source_key, source_mode, language, id, name, source_game_modes_json, raw_json)
        VALUES ('src', 'regular', 'en', 'task-a', 'Task A', '[]', '{}'), ('src', 'pve', 'en', 'task-a', 'Task A', '[]', '{}');
        INSERT INTO quest_catalog_objectives(
            source_key, source_mode, language, task_id, id, is_failure_condition, source_ordinal,
            source_type, normalized_kind, is_unsupported, description, target_count,
            found_in_raid_required, subtype_json, raw_json)
        VALUES
            ('src', 'regular', 'en', 'task-a', 'hand-over', 0, 0, 'giveItem', 'GiveItem', 0, 'Hand over', '3', 1, '{}', '{}'),
            ('src', 'regular', 'en', 'task-a', 'plain', 0, 1, 'giveItem', 'GiveItem', 0, 'Hand over bolts', NULL, NULL, '{}', '{}'),
            ('src', 'regular', 'en', 'task-a', 'pick-up', 0, 2, 'findQuestItem', 'FindQuestItem', 0, 'Find the case', '1', NULL, '{}', '{}'),
            ('src', 'regular', 'en', 'task-a', 'hand-over', 1, 0, 'giveItem', 'GiveItem', 0, 'Do not hand over', '1', 0, '{}', '{}'),
            ('src', 'pve', 'en', 'task-a', 'hand-over', 0, 0, 'giveItem', 'GiveItem', 0, 'Hand over', '9', 0, '{}', '{}');
        INSERT INTO quest_objective_item_targets(
            source_key, source_mode, language, task_id, objective_id, is_failure_condition,
            source_field, alternative_group, source_ordinal, item_id, target_count, found_in_raid_required)
        VALUES
            ('src', 'regular', 'en', 'task-a', 'hand-over', 0, 'items', 0, 0, 'salewa', '3', 1),
            ('src', 'regular', 'en', 'task-a', 'hand-over', 0, 'items', 1, 0, 'car-kit', '3', 1),
            ('src', 'regular', 'en', 'task-a', 'hand-over', 0, 'items', 2, 0, 'salewa', '3', 1),
            ('src', 'regular', 'en', 'task-a', 'plain', 0, 'items', 0, 0, 'bolts', NULL, NULL),
            ('src', 'regular', 'en', 'task-a', 'pick-up', 0, 'questItem', 0, 0, 'case', '1', NULL),
            ('src', 'regular', 'en', 'task-a', 'hand-over', 1, 'items', 0, 0, 'grizzly', '1', 0),
            ('src', 'pve', 'en', 'task-a', 'hand-over', 0, 'items', 0, 0, 'pve-only', '9', 0);

        """;

    private static async Task<List<string>> RowsAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT task_id || '|' || objective_id || '|' || item_id || '|' || count || '|' || found_in_raid_required
            FROM task_objective_items
            WHERE typeof(count) = 'integer'
            ORDER BY 1;
            """;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"tarkov-companion-restore-items-{Guid.NewGuid():N}.db");

    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
        {
            File.Delete(file);
        }
    }
}
