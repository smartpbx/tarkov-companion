using Microsoft.Data.Sqlite;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.IntegrationTests.DataV2;

[Collection(SqliteCollection.Name)]
public sealed class MigrationRecoveryTests
{
    [Fact]
    public async Task LedgerHasPairedUpgradeAndRollbackFixturesAndFreshDatabaseAppliesAll()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(14, SqliteMigrationLedger.Entries.Count);
        Assert.Equal("0014_quest_progress_game_log_actor", SqliteMigrationLedger.Entries[^1].Id);
        Assert.All(SqliteMigrationLedger.Entries, entry =>
        {
            var fixture = SqliteMigrationRunner.ReadFixture(entry.Id);
            Assert.False(string.IsNullOrWhiteSpace(fixture.UpgradeSql));
            Assert.False(string.IsNullOrWhiteSpace(fixture.RollbackSql));
            Assert.EndsWith(";", fixture.UpgradeSql.TrimEnd(), StringComparison.Ordinal);
            Assert.EndsWith(";", fixture.RollbackSql.TrimEnd(), StringComparison.Ordinal);
        });
        Assert.Equal(14, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM schema_migrations;"));

        var rollback = SqliteMigrationRunner.ReadFixture("0014_quest_progress_game_log_actor").RollbackSql;
        await using var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = rollback;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'quest_progress_journal';";
        var schema = Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("GameLog", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRollbackFixtureExecutesAgainstARepresentativePopulatedUpgrade()
    {
        for (var targetIndex = 0; targetIndex < SqliteMigrationLedger.Entries.Count; targetIndex++)
        {
            var target = SqliteMigrationLedger.Entries[targetIndex];
            var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-rollback-{target.Id}-{Guid.NewGuid():N}.db");
            try
            {
                var factory = new SqliteConnectionFactory(new(path));
                await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                for (var upgradeIndex = 0; upgradeIndex <= targetIndex; upgradeIndex++)
                {
                    var upgrade = SqliteMigrationLedger.Entries[upgradeIndex];
                    if (upgradeIndex == targetIndex)
                    {
                        command.CommandText = RepresentativePreUpgradeRows(target.Id);
                        if (!string.IsNullOrWhiteSpace(command.CommandText))
                        {
                            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                            command.CommandText = RepresentativePreUpgradeProbe(target.Id);
                            Assert.True(Convert.ToInt64(
                                await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
                                System.Globalization.CultureInfo.InvariantCulture) > 0);
                        }
                    }

                    command.CommandText = SqliteMigrationRunner.ReadFixture(upgrade.Id).UpgradeSql;
                    await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                    if (upgradeIndex == 0)
                    {
                        command.CommandText = RepresentativeV1Rows;
                        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                    }
                }

                command.CommandText = RepresentativeRowFor(target.Id);
                if (!string.IsNullOrWhiteSpace(command.CommandText))
                {
                    await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                }

                command.CommandText = SqliteMigrationRunner.ReadFixture(target.Id).RollbackSql;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                command.CommandText = "PRAGMA integrity_check;";
                Assert.Equal("ok", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));

                if (targetIndex > 0)
                {
                    command.CommandText = "SELECT name FROM items WHERE id = 'rollback-item';";
                    Assert.Equal("Rollback item", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
                }

                command.CommandText = RollbackArtifactProbe(target.Id);
                Assert.Equal(
                    RollbackArtifactExpectedCount(target.Id),
                    await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                File.Delete(path);
                File.Delete(path + "-shm");
                File.Delete(path + "-wal");
            }
        }
    }

    [Fact]
    public async Task DestructiveFailureRestoresVerifiedV10DatabaseAndLegacyCache()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-v2-upgrade-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await CreateAtV10Async(factory);
            await using (var connection = await factory.OpenAsync(TestContext.Current.CancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO http_response_cache(cache_key, body_json, cached_utc) VALUES ('legacy', '{\"data\":{}}', '2026-09-15T00:00:00Z');";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var runner = new SqliteMigrationRunner(factory, new ThrowAtFault(SqliteMigrationFaultPoint.AfterMigrationSql, new IOException("disk-full")));
            var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() => runner.ApplyAsync(TestContext.Current.CancellationToken));
            Assert.Equal(MigrationRecoveryState.RestoredVerifiedBackup, failure.RecoveryState);
            Assert.NotNull(failure.LastRecoverableBackupPath);
            Assert.Equal(10, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal(1, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM http_response_cache WHERE cache_key = 'legacy' AND body_json = '{\"data\":{}}';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*")) File.Delete(file);
        }
    }

    [Fact]
    public async Task LedgerlessExistingDatabaseIsBackedUpBeforeTheLedgerOrMigrationSqlCanMutateIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-ledgerless-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await using (var connection = await factory.OpenAsync(TestContext.Current.CancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TABLE legacy_marker(value TEXT NOT NULL);
                    INSERT INTO legacy_marker(value) VALUES ('preserve-me');
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var runner = new SqliteMigrationRunner(
                factory,
                new ThrowAtFault(
                    SqliteMigrationFaultPoint.AfterMigrationSql,
                    new IOException("ledgerless-migration-failed")));
            var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() =>
                runner.ApplyAsync(TestContext.Current.CancellationToken));

            Assert.Equal(MigrationRecoveryState.RestoredVerifiedBackup, failure.RecoveryState);
            Assert.NotNull(failure.LastRecoverableBackupPath);
            Assert.Equal(1, await V2TestDatabase.ScalarAsync(factory,
                "SELECT COUNT(*) FROM legacy_marker WHERE value = 'preserve-me';"));
            Assert.Equal(0, await V2TestDatabase.ScalarAsync(factory,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(
                         Path.GetDirectoryName(path)!,
                         $"{Path.GetFileNameWithoutExtension(path)}*"))
            {
                File.Delete(file);
            }
        }
    }

    [Theory]
    [InlineData(SqliteMigrationFaultPoint.BeforeBackup, "disk-full")]
    [InlineData(SqliteMigrationFaultPoint.BeforeMigration, "database-locked")]
    [InlineData(SqliteMigrationFaultPoint.BeforeMigrationCommit, "interrupted")]
    public async Task BackupMigrationAndRollbackFaultsAlwaysReportRecoverableState(SqliteMigrationFaultPoint point, string code)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-v2-fault-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await CreateAtV10Async(factory);
            Exception injected = code == "interrupted" ? new OperationCanceledException(code) : new IOException(code);
            var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() => new SqliteMigrationRunner(factory, new ThrowAtFault(point, injected)).ApplyAsync(TestContext.Current.CancellationToken));
            Assert.Contains(failure.RecoveryState, new[] { MigrationRecoveryState.OriginalIntact, MigrationRecoveryState.RestoredVerifiedBackup });
            Assert.Equal(10, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM schema_migrations;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*")) File.Delete(file);
        }
    }

    [Fact]
    public async Task RestoreFailureLeavesVerifiedBackupPathForManualRecovery()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-v2-restore-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await CreateAtV10Async(factory);
            var injector = new MultiFault(
                (SqliteMigrationFaultPoint.AfterMigrationSql, new IOException("migration-failed")),
                (SqliteMigrationFaultPoint.AfterRestoreCopy, new IOException("restore-failed")));
            var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() => new SqliteMigrationRunner(factory, injector).ApplyAsync(TestContext.Current.CancellationToken));
            Assert.Equal(MigrationRecoveryState.RecoverableBackupAvailable, failure.RecoveryState);
            Assert.True(File.Exists(failure.LastRecoverableBackupPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*")) File.Delete(file);
        }
    }

    [Fact]
    public async Task MigrationAndTransactionRollbackFailureStillRestoreTheVerifiedBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-v2-rollback-fault-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await CreateAtV10Async(factory);
            await using (var connection = await factory.OpenAsync(TestContext.Current.CancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "INSERT INTO http_response_cache(cache_key, body_json, cached_utc) VALUES ('legacy', '{\"data\":{}}', '2026-09-15T00:00:00Z');";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            var injector = new MultiFault(
                (SqliteMigrationFaultPoint.AfterMigrationSql, new IOException("migration-failed")),
                (SqliteMigrationFaultPoint.BeforeMigrationRollback, new IOException("rollback-failed")));
            var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() =>
                new SqliteMigrationRunner(factory, injector).ApplyAsync(TestContext.Current.CancellationToken));

            Assert.Equal(MigrationRecoveryState.RestoredVerifiedBackup, failure.RecoveryState);
            var aggregate = Assert.IsType<AggregateException>(failure.InnerException);
            Assert.Contains(aggregate.InnerExceptions, value => value.Message == "migration-failed");
            Assert.Contains(aggregate.InnerExceptions, value => value.Message == "rollback-failed");
            Assert.Equal(10, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal(1, await V2TestDatabase.ScalarAsync(factory,
                "SELECT COUNT(*) FROM http_response_cache WHERE cache_key = 'legacy' AND body_json = '{\"data\":{}}';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*")) File.Delete(file);
        }
    }

    [Fact]
    public async Task BackupCorruptionIsDetectedBeforeAnyDestructiveSqlRuns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tarkov-companion-v2-corrupt-{Guid.NewGuid():N}.db");
        try
        {
            var factory = new SqliteConnectionFactory(new(path));
            await CreateAtV10Async(factory);
            var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() =>
                new SqliteMigrationRunner(factory, new CorruptVerifiedBackup()).ApplyAsync(TestContext.Current.CancellationToken));
            Assert.Equal(MigrationRecoveryState.OriginalIntact, failure.RecoveryState);
            Assert.Equal(10, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal(1, await V2TestDatabase.ScalarAsync(factory, "SELECT COUNT(*) FROM pragma_table_info('http_response_cache') WHERE name = 'body_json';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*")) File.Delete(file);
        }
    }

    [Fact]
    public async Task MixedNewerSchemaIsLeftIntactInsteadOfApplyingMissingOlderMigration()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using (var connection = await database.Factory.OpenAsync(TestContext.Current.CancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM schema_migrations WHERE version = '0011_v2_data_platform';
                INSERT INTO schema_migrations(version, applied_utc)
                VALUES ('0042_from_the_future', '2030-01-01T00:00:00Z');
                CREATE TABLE future_schema_marker(value TEXT NOT NULL);
                INSERT INTO future_schema_marker(value) VALUES ('preserve-me');
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var failure = await Assert.ThrowsAsync<SqliteMigrationException>(() =>
            new SqliteMigrationRunner(database.Factory).ApplyAsync(TestContext.Current.CancellationToken));

        Assert.Equal(MigrationRecoveryState.OriginalIntact, failure.RecoveryState);
        Assert.Contains("newer build", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = '0011_v2_data_platform';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM schema_migrations WHERE version = '0042_from_the_future';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT COUNT(*) FROM future_schema_marker WHERE value = 'preserve-me';"));
    }

    private static async Task CreateAtV10Async(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE schema_migrations(version TEXT PRIMARY KEY, applied_utc TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        foreach (var entry in SqliteMigrationLedger.Entries.Take(10))
        {
            command.CommandText = SqliteMigrationRunner.ReadFixture(entry.Id).UpgradeSql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            command.CommandText = "INSERT INTO schema_migrations(version, applied_utc) VALUES ($id, '2026-09-15T00:00:00Z');";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", entry.Id);
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            command.Parameters.Clear();
        }
    }

    private const string RepresentativeV1Rows = """
        INSERT INTO items(
            id, name, short_name, normalized_name, description, category_type,
            width, height, slots, flea_eligible, source_updated_utc)
        VALUES ('rollback-item', 'Rollback item', 'Rollback', 'rollback item', '', 'Unknown',
                1, 1, 1, 0, '2026-09-15T00:00:00Z');
        INSERT INTO player_profiles(id, name, game_mode, faction, level, created_utc, updated_utc)
        VALUES ('rollback-profile', 'Rollback profile', 'Regular', 'Usec', 1,
                '2026-09-15T00:00:00Z', '2026-09-15T00:00:00Z');
        INSERT INTO maps(id, name, normalized_name, source_json)
        VALUES ('rollback-map', 'Rollback map', 'rollback map', '{}');
        INSERT INTO raids(id, profile_id, map_id, mode, start_utc)
        VALUES ('rollback-raid', 'rollback-profile', 'rollback-map', 'Regular', '2026-09-15T00:00:00Z');
        """;

    private static string RepresentativePreUpgradeRows(string migrationId) => migrationId switch
    {
        "0007_drop_superseded_tables" => """
            INSERT INTO app_meta(key, value) VALUES ('fixture', 'preserved-by-backup');
            INSERT INTO map_labels(id, map_id, name, source_json) VALUES ('label', 'rollback-map', 'Label', '{}');
            INSERT INTO map_render_configs(map_id, transform_json, bounds_json) VALUES ('rollback-map', '{}', '{}');
            INSERT INTO map_floor_layers(map_id, layer_id, name, min_height, max_height) VALUES ('rollback-map', 'ground', 'Ground', 0, 1);
            INSERT INTO item_icon_fingerprints(item_id, hash_type, hash, width, height, updated_utc)
            VALUES ('rollback-item', 'fixture', 'hash', 1, 1, '2026-09-15T00:00:00Z');
            INSERT INTO profile_trader_levels(profile_id, trader_id, level) VALUES ('rollback-profile', 'trader', 1);
            INSERT INTO profile_hideout_progress(profile_id, station_id, level) VALUES ('rollback-profile', 'station', 1);
            INSERT INTO profile_wishlist(profile_id, item_id) VALUES ('rollback-profile', 'rollback-item');
            INSERT INTO profile_item_counts(profile_id, item_id, count) VALUES ('rollback-profile', 'rollback-item', 1);
            INSERT INTO profile_overrides(profile_id, item_id, action, note) VALUES ('rollback-profile', 'rollback-item', 'keep', 'fixture');
            INSERT INTO event_definitions(event_id, name, rules_json, active, source_json) VALUES ('event', 'Event', '{}', 1, '{}');
            INSERT INTO event_items(event_id, item_id, metadata_json) VALUES ('event', 'rollback-item', '{}');
            INSERT INTO profile_event_item_state(profile_id, event_id, item_id, state, updated_utc)
            VALUES ('rollback-profile', 'event', 'rollback-item', 'needed', '2026-09-15T00:00:00Z');
            INSERT INTO key_intelligence_overrides(key_item_id, score_json, notes, source, source_date, confidence)
            VALUES ('rollback-item', '{}', 'fixture', 'fixture', '2026-09-15', 0.5);
            INSERT INTO raid_positions(raid_id, timestamp_utc, x, y, z, heading, screenshot_filename)
            VALUES ('rollback-raid', '2026-09-15T00:00:00Z', 1, 2, 3, 4, 'fixture.png');
            INSERT INTO raid_extracts(raid_id, extract_id, name, confidence, source)
            VALUES ('rollback-raid', 'extract', 'Extract', 0.5, 'fixture');
            """,
        "0009_drop_unread_map_tables" => """
            INSERT INTO map_spawns(id, map_id, type, source_json) VALUES ('spawn', 'rollback-map', 'pmc', '{}');
            INSERT INTO map_transits(id, map_id, source_json) VALUES ('transit', 'rollback-map', '{}');
            INSERT INTO map_hazards(id, map_id, source_json) VALUES ('hazard', 'rollback-map', '{}');
            INSERT INTO map_loot_positions(id, map_id, source_json) VALUES ('loot', 'rollback-map', '{}');
            """,
        "0010_drop_quest_catalog_orphans" => """
            INSERT INTO quest_catalog_orphans(source_mode, profile_id, entity_kind, external_id, recorded_value, detected_utc)
            VALUES ('regular', 'rollback-profile', 'task', 'lost-task', 'completed', '2026-09-15T00:00:00Z');
            """,
        "0011_v2_data_platform" => """
            INSERT INTO http_response_cache(cache_key, body_json, cached_utc, etag, last_modified)
            VALUES ('legacy-before-v2', '{"data":{"fixture":true}}', '2026-09-15T00:00:00Z', '"fixture"', '2026-09-15T00:00:00Z');
            """,
        "0013_task_objective_task_scoped_keys" => """
            INSERT INTO tasks(id, name, source_json) VALUES ('rollback-task', 'Rollback task', '{}');
            INSERT INTO task_objectives(id, task_id, type, description) VALUES ('rollback-objective', 'rollback-task', 'giveItem', 'Fixture');
            INSERT INTO task_objective_items(objective_id, item_id, count, found_in_raid_required)
            VALUES ('rollback-objective', 'rollback-item', 1, 0);
            """,
        "0014_quest_progress_game_log_actor" => """
            INSERT INTO quest_progress_profiles(profile_id, game_mode, generation, display_name, created_utc, modified_utc)
            VALUES ('rollback-profile', 'Regular', 'wipe', 'Rollback profile', '2026-09-15T00:00:00Z', '2026-09-15T00:00:00Z');
            INSERT INTO quest_progress_journal(
                profile_id, game_mode, generation, correlation_id, entity_kind, entity_id, field_name,
                previous_value_json, new_value_json, inverse_value_json, actor, assertion_source,
                revision, recorded_utc)
            VALUES (
                'rollback-profile', 'Regular', 'wipe', '00000000-0000-4000-8000-000000000014',
                'Task', 'rollback-task', 'state', 'null', json_object('state', 'Active'), 'null',
                'User', 'Manual', 1, '2026-09-15T00:00:00Z');
            """,
        _ => string.Empty,
    };

    private static string RepresentativePreUpgradeProbe(string migrationId) => migrationId switch
    {
        "0007_drop_superseded_tables" => "SELECT COUNT(*) FROM app_meta WHERE key = 'fixture';",
        "0009_drop_unread_map_tables" => "SELECT COUNT(*) FROM map_spawns WHERE id = 'spawn';",
        "0010_drop_quest_catalog_orphans" => "SELECT COUNT(*) FROM quest_catalog_orphans WHERE external_id = 'lost-task';",
        "0011_v2_data_platform" => "SELECT COUNT(*) FROM http_response_cache WHERE cache_key = 'legacy-before-v2';",
        "0013_task_objective_task_scoped_keys" => "SELECT COUNT(*) FROM task_objective_items WHERE objective_id = 'rollback-objective';",
        "0014_quest_progress_game_log_actor" => "SELECT COUNT(*) FROM quest_progress_journal WHERE entity_id = 'rollback-task';",
        _ => throw new ArgumentOutOfRangeException(nameof(migrationId)),
    };

    private static string RepresentativeRowFor(string migrationId) => migrationId switch
    {
        "0001_initial" => string.Empty,
        "0002_data_cache" => "INSERT INTO http_response_cache(cache_key, body_json, cached_utc) VALUES ('rollback', '{\"data\":{}}', '2026-09-15T00:00:00Z');",
        "0003_recognition_scan_metadata" => "INSERT INTO scan_history(id, timestamp_utc, scan_context, confidence, candidate_json, diagnostic_code) VALUES ('rollback-scan', '2026-09-15T00:00:00Z', 'fixture', 0.5, '[]', 'fixture');",
        "0004_quest_catalog_fidelity" => "INSERT INTO quest_catalog_snapshots(source_key, source_uri, local_game_mode, source_mode, language, payload_sha256, translated_payload_sha256, fetched_utc, validated_utc, raw_json, translated_json) VALUES ('fixture', 'https://fixture.invalid', 'Regular', 'regular', 'en', 'hash', 'hash', '2026-09-15T00:00:00Z', '2026-09-15T00:00:00Z', '{}', '{}');",
        "0005_local_quest_progress" => "INSERT INTO quest_progress_profiles(profile_id, game_mode, generation, display_name, created_utc, modified_utc) VALUES ('rollback-profile', 'Regular', 'wipe', 'Rollback profile', '2026-09-15T00:00:00Z', '2026-09-15T00:00:00Z');",
        "0006_quest_progress_exchange" => """
            INSERT INTO quest_progress_profiles(profile_id, game_mode, generation, display_name, created_utc, modified_utc)
            VALUES ('rollback-profile', 'Regular', 'wipe', 'Rollback profile', '2026-09-15T00:00:00Z', '2026-09-15T00:00:00Z');
            INSERT INTO quest_progress_imports(
                import_id, profile_id, game_mode, generation, profile_name, payload_sha256,
                preview_sha256, base_revision, applied_revision, source_app_version,
                source_exported_utc, provenance_summary, imported_utc, applied_change_count,
                kept_local_count, unresolved_count)
            VALUES ('rollback-import', 'rollback-profile', 'Regular', 'wipe', 'Rollback profile',
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                    0, 0, 'fixture', '2026-09-15T00:00:00Z', 'fixture',
                    '2026-09-15T00:00:00Z', 0, 0, 0);
            """,
        "0008_loot_containers" => "INSERT INTO loot_containers(id, normalized_name) VALUES ('rollback-container', 'rollback container');",
        "0011_v2_data_platform" => """
            INSERT INTO dataset_sync_runs(
                run_id, game_mode, language, started_utc, completed_utc, state,
                endpoint_count, successful_count, stale_count, refused_count, failure_count)
            VALUES ('rollback-run', 'regular', 'en', '2026-09-15T00:00:00Z',
                    '2026-09-15T00:01:00Z', 'current', 1, 1, 0, 0, 0);
            INSERT INTO dataset_publications(
                publication_id, run_id, source_key, game_mode, language, state,
                content_sha256, record_count, attempted_utc, published_utc)
            VALUES ('rollback-publication', 'rollback-run', 'items', 'regular', 'en', 'current',
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 1,
                    '2026-09-15T00:00:00Z', '2026-09-15T00:00:00Z');
            INSERT INTO dataset_heads(
                source_key, game_mode, language, visible_publication_id,
                last_known_good_publication_id, state, updated_utc)
            VALUES ('items', 'regular', 'en', 'rollback-publication',
                    'rollback-publication', 'current', '2026-09-15T00:00:00Z');
            INSERT INTO dataset_endpoint_materializations(
                source_key, claimed_publication_order, claimed_run_id,
                materialized_publication_order, materialized_run_id,
                materialized_game_mode, materialized_language, materialized_publication_id)
            SELECT 'items', publication_order, run_id, publication_order, run_id,
                   'regular', 'en', 'rollback-publication'
            FROM dataset_sync_runs
            WHERE run_id = 'rollback-run';
            INSERT INTO retention_policies(
                policy_key, screenshot_retention_enabled, screenshot_retention_hours,
                debug_capture_enabled, data_retention_days, updated_utc)
            VALUES ('rollback', 0, NULL, 0, 30, '2026-09-15T00:00:00Z');
            """,
        "0013_task_objective_task_scoped_keys" =>
            "INSERT INTO task_objective_items(task_id, objective_id, item_id, count, found_in_raid_required) " +
            "VALUES ('rollback-task', 'rollback-objective', 'rollback-item-2', 2, 0);",
        "0014_quest_progress_game_log_actor" => """
            INSERT INTO quest_progress_journal(
                profile_id, game_mode, generation, correlation_id, entity_kind, entity_id, field_name,
                previous_value_json, new_value_json, inverse_value_json, actor, assertion_source,
                revision, recorded_utc)
            VALUES (
                'rollback-profile', 'Regular', 'wipe', '00000000-0000-4000-8000-000000000114',
                'Task', 'rollback-task-2', 'state', 'null', json_object('state', 'Completed'), 'null',
                'User', 'Manual', 2, '2026-09-15T00:00:00Z');
            """,
        _ => string.Empty,
    };

    private static string RollbackArtifactProbe(string migrationId) => migrationId switch
    {
        "0001_initial" => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'items';",
        "0002_data_cache" => "SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = 'normalized_short_name';",
        "0003_recognition_scan_metadata" => "SELECT COUNT(*) FROM pragma_table_info('scan_history') WHERE name = 'diagnostic_code';",
        "0004_quest_catalog_fidelity" => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_catalog_snapshots';",
        "0005_local_quest_progress" => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_progress_profiles';",
        "0006_quest_progress_exchange" => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_progress_imports';",
        "0007_drop_superseded_tables" => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_meta';",
        "0008_loot_containers" => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'loot_containers';",
        "0009_drop_unread_map_tables" => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'map_spawns';",
        "0010_drop_quest_catalog_orphans" => "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_catalog_orphans';",
        "0011_v2_data_platform" => "SELECT COUNT(*) FROM pragma_table_info('http_response_cache') WHERE name = 'body_json';",
        "0012_task_wiki_link" => "SELECT COUNT(*) FROM pragma_table_info('quest_catalog_tasks') WHERE name = 'wiki_url';",
        "0013_task_objective_task_scoped_keys" => "SELECT COUNT(*) FROM pragma_table_info('task_objective_items') WHERE name = 'task_id';",
        "0014_quest_progress_game_log_actor" =>
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'quest_progress_journal' AND sql LIKE '%GameLog%';",
        _ => throw new ArgumentOutOfRangeException(nameof(migrationId)),
    };

    private static long RollbackArtifactExpectedCount(string migrationId) => migrationId switch
    {
        "0007_drop_superseded_tables" or
        "0009_drop_unread_map_tables" or
        "0010_drop_quest_catalog_orphans" or
        "0011_v2_data_platform" => 1L,
        _ => 0L,
    };

    private sealed class ThrowAtFault(SqliteMigrationFaultPoint point, Exception exception) : ISqliteMigrationFaultInjector
    {
        // BeforeBackup fires once per run, named for the *last* pending destructive migration
        // (the backup filename it verifies) — that was 0011 until 0013/0014 became pending too.
        // BeforeMigration/BeforeMigrationCommit fire per applied migration and still hit 0011
        // first, since it stays earliest in ledger order.
        public ValueTask InjectAsync(SqliteMigrationFaultPoint actual, string? migrationId, string databasePath, CancellationToken cancellationToken)
        {
            if (actual == point && (migrationId is null ||
                migrationId is "0011_v2_data_platform"
                    or "0013_task_objective_task_scoped_keys"
                    or "0014_quest_progress_game_log_actor")) throw exception;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MultiFault(params (SqliteMigrationFaultPoint Point, Exception Exception)[] faults) : ISqliteMigrationFaultInjector
    {
        public ValueTask InjectAsync(SqliteMigrationFaultPoint actual, string? migrationId, string databasePath, CancellationToken cancellationToken)
        {
            var fault = faults.FirstOrDefault(candidate => candidate.Point == actual);
            if (fault.Exception is not null) throw fault.Exception;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CorruptVerifiedBackup : ISqliteMigrationFaultInjector
    {
        public ValueTask InjectAsync(SqliteMigrationFaultPoint point, string? migrationId, string databasePath, CancellationToken cancellationToken)
        {
            if (point == SqliteMigrationFaultPoint.AfterBackupVerified)
            {
                var directory = Path.GetDirectoryName(databasePath)!;
                var stem = Path.GetFileNameWithoutExtension(databasePath);
                var backup = Directory.GetFiles(directory, $"{stem}.pre-0011_v2_data_platform-*.db").Single();
                File.WriteAllBytes(backup, [0x00, 0x01, 0x02]);
            }
            return ValueTask.CompletedTask;
        }
    }
}
