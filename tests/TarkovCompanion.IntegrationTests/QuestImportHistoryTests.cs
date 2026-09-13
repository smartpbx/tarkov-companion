using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// Reading back what an import refused to do.
/// </summary>
/// <remarks>
/// Both tables have been written on every import since the exchange was built and neither had a
/// reader, so an import that half-worked reported "kept 3 local · 2 unresolved" once, in a
/// status line, and could never say which three or which two.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class QuestImportHistoryTests
{
    private static readonly Guid ProfileId = new("940d35d5-47a2-4a25-afb9-94145166d65b");
    private static readonly Guid OtherProfileId = new("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task AnImportReportsEveryChangeItRefusedAndWhy()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tarkov-imports-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);
            await SeedAsync(factory);

            var history = new SqliteQuestProgressImportHistory(factory);
            var records = await history.GetRecentAsync(
                new(ProfileId, GameMode.Regular, "gen-1"),
                5,
                TestContext.Current.CancellationToken);

            // Newest first, and the other profile's import is not this profile's business.
            Assert.Equal(2, records.Count);
            Assert.Equal("Second", records[0].ProfileName);
            Assert.Equal("First", records[1].ProfileName);

            var second = records[0];
            Assert.Equal(7, second.AppliedChangeCount);
            Assert.Equal(2, second.KeptLocalCount);

            var conflict = Assert.Single(second.Conflicts);
            Assert.Equal("Task", conflict.EntityKind);
            Assert.Equal("task-a", conflict.EntityId);
            Assert.Equal("Local is further along", conflict.Reason);
            Assert.Equal(QuestImportResolution.KeepLocal, conflict.Resolution);

            var unresolved = Assert.Single(second.Unresolved);
            Assert.Equal("Objective", unresolved.EntityKind);
            Assert.Equal("objective-z", unresolved.EntityId);
            Assert.Equal("Unknown id", unresolved.Reason);

            // An import that refused nothing still comes back, with empty lists rather than
            // being left out: "it went through cleanly" is an answer.
            Assert.Empty(records[1].Conflicts);
            Assert.Empty(records[1].Unresolved);
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
    public async Task AProfileWithNoImportsReportsNoneRatherThanFailing()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tarkov-imports-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(TestContext.Current.CancellationToken);

            var history = new SqliteQuestProgressImportHistory(factory);

            Assert.Empty(await history.GetRecentAsync(
                new(ProfileId, GameMode.Regular, "gen-1"),
                5,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    private static async Task SeedAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO quest_progress_profiles(
                profile_id, game_mode, generation, display_name, revision, created_utc, modified_utc)
            VALUES
                ('940d35d5-47a2-4a25-afb9-94145166d65b', 'Regular', 'gen-1', 'Mine', 4,
                 '2026-09-10T00:00:00Z', '2026-09-12T00:00:00Z'),
                ('11111111-2222-3333-4444-555555555555', 'Regular', 'gen-1', 'Somebody else', 1,
                 '2026-09-10T00:00:00Z', '2026-09-12T00:00:00Z');

            INSERT INTO quest_progress_imports(
                import_id, profile_id, game_mode, generation, profile_name, payload_sha256,
                preview_sha256, base_revision, applied_revision, source_app_version,
                source_exported_utc, provenance_summary, imported_utc, applied_change_count,
                kept_local_count, unresolved_count)
            VALUES
                ('aaaaaaaa-0000-0000-0000-000000000001', '940d35d5-47a2-4a25-afb9-94145166d65b',
                 'Regular', 'gen-1', 'First', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 0, 1, '1.0.100',
                 '2026-09-11T10:00:00Z', 'Exported by a friend', '2026-09-11T11:00:00Z', 3, 0, 0),
                ('aaaaaaaa-0000-0000-0000-000000000002', '940d35d5-47a2-4a25-afb9-94145166d65b',
                 'Regular', 'gen-1', 'Second', 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc', 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd', 1, 2, '1.0.200',
                 '2026-09-12T10:00:00Z', 'Exported by a friend', '2026-09-12T11:00:00Z', 7, 2, 1),
                ('aaaaaaaa-0000-0000-0000-000000000003', '11111111-2222-3333-4444-555555555555',
                 'Regular', 'gen-1', 'Not mine', 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee', 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff', 0, 1, '1.0.200',
                 '2026-09-12T10:00:00Z', 'Exported by a friend', '2026-09-12T12:00:00Z', 1, 0, 0);

            INSERT INTO quest_progress_import_conflicts(
                import_id, proposal_key, entity_kind, entity_id, local_value_json,
                incoming_value_json, reason, resolution)
            VALUES ('aaaaaaaa-0000-0000-0000-000000000002', 'task:task-a', 'Task', 'task-a',
                    '{"state":"Completed"}', '{"state":"Active"}', 'Local is further along', 'KeepLocal');

            INSERT INTO quest_progress_import_unresolved(
                import_id, proposal_key, entity_kind, entity_id, incoming_value_json, reason)
            VALUES ('aaaaaaaa-0000-0000-0000-000000000002', 'objective:objective-z', 'Objective',
                    'objective-z', '{"count":2}', 'Unknown id');
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
