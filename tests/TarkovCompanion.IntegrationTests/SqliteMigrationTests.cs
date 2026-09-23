using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests;

[Collection(SqliteCollection.Name)]
public sealed class SqliteMigrationTests
{
    /// <summary>The tables migration 0007 drops, named here so the list is asserted not assumed.</summary>
    private static readonly string[] Superseded =
    [
        "app_meta",
        "map_labels",
        "map_render_configs",
        "map_floor_layers",
        "item_icon_fingerprints",
        "profile_trader_levels",
        "profile_hideout_progress",
        "profile_wishlist",
        "profile_item_counts",
        "profile_overrides",
        "event_definitions",
        "event_items",
        "profile_event_item_state",
        "key_intelligence_overrides",
        "raid_positions",
        "raid_extracts",
        // 0010. The Quests page answers "what progress does the catalog no longer carry" live,
        // off the profile and the loaded catalog, and covers item holdings and pins as well;
        // this table was a staler, narrower copy written on every sync and read by nothing.
        "quest_catalog_orphans",
    ];

    [Fact]
    public async Task EmptyDatabaseMigratesIdempotently()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tarkov-companion-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            var runner = new SqliteMigrationRunner(factory);

            var first = await runner.ApplyAsync(CancellationToken.None);
            var second = await runner.ApplyAsync(CancellationToken.None);

            Assert.Equal(19, first.Applied.Count);
            Assert.Empty(second.Applied);
            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table', 'view') AND name = 'items';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'http_response_cache';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_catalog_snapshots';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_objective_zones';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_progress_journal';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_progress_imports';";
            Assert.Equal(1L, await command.ExecuteScalarAsync());

            // Sixteen tables the first migration created for designs that were settled some
            // other way, and which nothing has ever read or written. A fresh database should
            // not carry them: an empty table that looks authoritative is a trap, and this one
            // cost a night twice.
            foreach (var dropped in Superseded)
            {
                command.CommandText =
                    $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{dropped}';";
                Assert.Equal(0L, await command.ExecuteScalarAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task ExistingInitialSchemaUpgradesWithoutLosingItems()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"tarkov-companion-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                var assembly = typeof(SqliteMigrationRunner).Assembly;
                var resourceName = Assert.Single(
                    assembly.GetManifestResourceNames(),
                    name => name.EndsWith("Persistence.Migrations.0001_initial.sql", StringComparison.Ordinal));
                await using var stream = assembly.GetManifestResourceStream(resourceName);
                Assert.NotNull(stream);
                using var reader = new StreamReader(stream);
                var initialSql = await reader.ReadToEndAsync();

                await using var command = connection.CreateCommand();
                command.CommandText = initialSql;
                await command.ExecuteNonQueryAsync();
                command.CommandText = """
                    CREATE TABLE schema_migrations (version TEXT PRIMARY KEY, applied_utc TEXT NOT NULL);
                    INSERT INTO schema_migrations(version, applied_utc) VALUES ('0001_initial', '2026-09-09T00:00:00Z');
                    INSERT INTO items(
                        id, name, short_name, normalized_name, description, category_type,
                        width, height, slots, flea_eligible, source_updated_utc)
                    VALUES ('existing', 'Existing item', 'Existing', 'existing item', '', 'Barter', 1, 1, 1, 1, '2026-09-09T00:00:00Z');
                    INSERT INTO player_profiles(
                        id, name, game_mode, faction, level, created_utc, updated_utc)
                    VALUES (
                        '940d35d5-47a2-4a25-afb9-94145166d65b', 'Legacy profile', 'Regular', 'Usec', 12,
                        '2026-09-09T00:00:00Z', '2026-09-09T01:00:00Z');
                    INSERT INTO profile_task_progress(profile_id, task_id, status)
                    VALUES ('940d35d5-47a2-4a25-afb9-94145166d65b', 'legacy-task', 'active');
                    INSERT INTO profile_objective_progress(profile_id, objective_id, count)
                    VALUES ('940d35d5-47a2-4a25-afb9-94145166d65b', 'legacy-objective', 2);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var applied = await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);

            Assert.Equal(
                [
                    "0002_data_cache",
                    "0003_recognition_scan_metadata",
                    "0004_quest_catalog_fidelity",
                    "0005_local_quest_progress",
                    "0006_quest_progress_exchange",
                    "0007_drop_superseded_tables",
                    "0008_loot_containers",
                    "0009_drop_unread_map_tables",
                    "0010_drop_quest_catalog_orphans",
                    "0011_v2_data_platform",
                    "0012_task_wiki_link",
                    "0013_task_objective_task_scoped_keys",
                    "0014_quest_progress_game_log_actor",
                    "0015_flea_market_settings",
                    "0016_restore_task_objective_items",
                    "0017_raid_soft_delete",
                    "0018_stash_review_commands",
                    "0019_loot_scan_history",
                ],
                applied.Applied);
            await using var verification = await factory.OpenAsync(CancellationToken.None);
            await using var verifyCommand = verification.CreateCommand();
            verifyCommand.CommandText = "SELECT name, normalized_short_name FROM items WHERE id = 'existing';";
            await using var result = await verifyCommand.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            Assert.Equal("Existing item", result.GetString(0));
            Assert.Equal(string.Empty, result.GetString(1));

            await result.DisposeAsync();
            verifyCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('scan_history') WHERE name = 'diagnostic_code';";
            Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync());


            // An upgraded database loses them too, and the profile rows above survived it,
            // which is the half that would matter if any of them had ever held anything.
            foreach (var dropped in Superseded)
            {
                verifyCommand.CommandText =
                    $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{dropped}';";
                Assert.Equal(0L, await verifyCommand.ExecuteScalarAsync());
            }

            verifyCommand.CommandText = """
                SELECT game_mode, generation, revision
                FROM quest_progress_profiles
                WHERE profile_id = '940d35d5-47a2-4a25-afb9-94145166d65b';
                """;
            await using var profileResult = await verifyCommand.ExecuteReaderAsync();
            Assert.True(await profileResult.ReadAsync());
            Assert.Equal("Regular", profileResult.GetString(0));
            Assert.Equal("legacy-940d35d547a24a25afb994145166d65b", profileResult.GetString(1));
            Assert.Equal(0, profileResult.GetInt64(2));

            await profileResult.DisposeAsync();
            verifyCommand.CommandText = """
                SELECT state FROM quest_profile_task_states WHERE task_id = 'legacy-task';
                """;
            Assert.Equal("Active", await verifyCommand.ExecuteScalarAsync());
            verifyCommand.CommandText = """
                SELECT state || ':' || progress_count
                FROM quest_profile_objective_states
                WHERE objective_id = 'legacy-objective';
                """;
            Assert.Equal("InProgress:2", await verifyCommand.ExecuteScalarAsync());
            verifyCommand.CommandText = "SELECT COUNT(*) FROM quest_progress_journal WHERE actor = 'SystemMigration';";
            Assert.Equal(2L, await verifyCommand.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task GameLogActorWritesSucceedAfterUpgradingPast0005()
    {
        // Clayton's install was created when 0005's CHECK still omitted GameLog. A fresh schema
        // would hide that: this builds a real 0005-era journal, proves GameLog is refused, migrates
        // forward, and only then asks the store to write one.
        var databasePath = Path.Combine(Path.GetTempPath(), $"tarkov-companion-gamelog-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_migrations (
                        version TEXT PRIMARY KEY,
                        applied_utc TEXT NOT NULL
                    );
                    """;
                await command.ExecuteNonQueryAsync();

                foreach (var entry in SqliteMigrationLedger.Entries.Take(5))
                {
                    command.CommandText = SqliteMigrationRunner.ReadFixture(entry.Id).UpgradeSql;
                    await command.ExecuteNonQueryAsync();
                    command.CommandText =
                        "INSERT INTO schema_migrations(version, applied_utc) VALUES ($id, '2026-09-10T00:00:00Z');";
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("$id", entry.Id);
                    await command.ExecuteNonQueryAsync();
                    command.Parameters.Clear();
                }

                Assert.Equal("0005_local_quest_progress", SqliteMigrationLedger.Entries[4].Id);

                command.CommandText = """
                    INSERT INTO quest_progress_profiles(
                        profile_id, game_mode, generation, display_name, created_utc, modified_utc, revision)
                    VALUES (
                        '11111111-1111-4111-8111-111111111111', 'Regular', 'wipe', 'Upgrade profile',
                        '2026-09-10T00:00:00Z', '2026-09-10T00:00:00Z', 1);
                    INSERT INTO quest_progress_journal(
                        profile_id, game_mode, generation, correlation_id, entity_kind, entity_id,
                        field_name, previous_value_json, new_value_json, inverse_value_json,
                        actor, assertion_source, revision, recorded_utc)
                    VALUES (
                        '11111111-1111-4111-8111-111111111111', 'Regular', 'wipe',
                        '22222222-2222-4222-8222-222222222222', 'Task', 'kept-task', 'state',
                        'null', json_object('state', 'Active'), 'null',
                        'User', 'Manual', 1, '2026-09-10T00:00:00Z');
                    """;
                await command.ExecuteNonQueryAsync();

                command.CommandText = """
                    INSERT INTO quest_progress_journal(
                        profile_id, game_mode, generation, correlation_id, entity_kind, entity_id,
                        field_name, previous_value_json, new_value_json, inverse_value_json,
                        actor, assertion_source, revision, recorded_utc)
                    VALUES (
                        '11111111-1111-4111-8111-111111111111', 'Regular', 'wipe',
                        '33333333-3333-4333-8333-333333333333', 'Task', 'refused-task', 'state',
                        'null', json_object('state', 'Active'), 'null',
                        'GameLog', 'GameLog', 2, '2026-09-10T00:00:00Z');
                    """;
                var refused = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
                Assert.Contains("CHECK constraint failed", refused.Message, StringComparison.Ordinal);
            }

            var applied = await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            Assert.Contains("0014_quest_progress_game_log_actor", applied.Applied);

            await using (var connection = await factory.OpenAsync(CancellationToken.None))
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*) FROM quest_progress_journal
                    WHERE entity_id = 'kept-task' AND actor = 'User';
                    """;
                Assert.Equal(1L, await command.ExecuteScalarAsync());

                command.CommandText =
                    "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'quest_progress_journal';";
                var schema = Assert.IsType<string>(await command.ExecuteScalarAsync());
                Assert.Contains("GameLog", schema, StringComparison.Ordinal);
            }

            var scope = new QuestProfileScope(
                Guid.Parse("11111111-1111-4111-8111-111111111111"),
                GameMode.Regular,
                "wipe");
            var store = new SqliteQuestProgressStore(factory);
            var result = await store.ApplyAsync(
                new SetTaskStateMutation(
                    scope,
                    "Upgrade profile",
                    "log-task",
                    RecordedTaskState.Active,
                    QuestProgressActor.GameLog,
                    QuestProgressSources.GameLog,
                    Guid.Parse("44444444-4444-4444-8444-444444444444"),
                    DateTimeOffset.Parse("2026-09-10T12:00:00Z")),
                CancellationToken.None);

            Assert.True(result.Changed);
            var journal = await store.GetJournalAsync(scope, CancellationToken.None);
            Assert.Contains(journal, change =>
                change.EntityId == "log-task" && change.Actor == QuestProgressActor.GameLog);
            Assert.Contains(journal, change =>
                change.EntityId == "kept-task" && change.Actor == QuestProgressActor.User);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }
}
