using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// Reading a raid's path back out of the events it recorded while it ran.
/// </summary>
/// <remarks>
/// Every position has been written on every scan since the first raid and nothing ever read
/// one back, so a raid's path survived a restart on disk and vanished from the screen.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class RaidTrailPersistenceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ThePathComesBackInTheOrderItWasWalked()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tarkov-trail-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            var history = new SqliteRaidHistoryService(factory);
            var profileId = await SeedProfileAsync(factory);
            var raidId = await history.StartAsync(
                new(Guid.NewGuid(), profileId, "woods", "Regular", Moment(0), null, null, null),
                CancellationToken.None);

            // Deliberately out of order, because the read orders by the recorded time rather
            // than by the order the rows happened to be inserted in.
            foreach (var index in new[] { 2, 0, 1 })
            {
                await history.RecordEventAsync(
                    raidId,
                    "position",
                    Moment(index),
                    JsonSerializer.Serialize(Position(index), Json),
                    CancellationToken.None);
            }

            // A scan is not a position and must not join the trail.
            await history.RecordEventAsync(
                raidId,
                "scan",
                Moment(3),
                """{"itemName":"Graphics card"}""",
                CancellationToken.None);

            var positions = await history.ListPositionsAsync(raidId, CancellationToken.None);

            Assert.Equal(3, positions.Count);
            Assert.Equal([0d, 100d, 200d], positions.Select(position => position.Position.X));
        }
        finally
        {
            Cleanup(databasePath);
        }
    }

    /// <summary>
    /// A payload that is not a position does not become one at the origin.
    /// </summary>
    /// <remarks>
    /// Valid JSON of another shape deserialises to a record of defaults rather than throwing:
    /// no filename, a zero timestamp, a position at the origin. Drawn on a map that is a point
    /// somebody never stood on.
    /// </remarks>
    [Fact]
    public async Task AnUnreadablePositionIsSkipped()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tarkov-trail-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            var history = new SqliteRaidHistoryService(factory);
            var profileId = await SeedProfileAsync(factory);
            var raidId = await history.StartAsync(
                new(Guid.NewGuid(), profileId, "woods", "Regular", Moment(0), null, null, null),
                CancellationToken.None);

            await history.RecordEventAsync(
                raidId, "position", Moment(0), JsonSerializer.Serialize(Position(0), Json), CancellationToken.None);
            await history.RecordEventAsync(
                raidId, "position", Moment(1), """{"somethingElse":true}""", CancellationToken.None);
            await history.RecordEventAsync(
                raidId, "position", Moment(2), """{"filename":""}""", CancellationToken.None);

            var positions = await history.ListPositionsAsync(raidId, CancellationToken.None);

            var kept = Assert.Single(positions);
            Assert.Equal("2026-09-13[03-00]_shot.png", kept.Filename);
        }
        finally
        {
            Cleanup(databasePath);
        }
    }

    [Fact]
    public async Task ARaidWithNoScreenshotsHasNoPath()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), "tarkov-trail-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var factory = new SqliteConnectionFactory(new(databasePath));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);

            Assert.Empty(await new SqliteRaidHistoryService(factory)
                .ListPositionsAsync(Guid.NewGuid(), CancellationToken.None));
        }
        finally
        {
            Cleanup(databasePath);
        }
    }

    private static DateTimeOffset Moment(int index) =>
        new DateTimeOffset(2026, 9, 13, 3, 0, 0, TimeSpan.Zero).AddMinutes(index);

    private static ScreenshotPosition Position(int index) => new(
        Moment(index),
        new WorldPosition(index * 100, 2, index * -50),
        new QuaternionOrientation(0, 0, 0, 1),
        90,
        null,
        null,
        $"2026-09-13[03-0{index}]_shot.png");

    private static async Task<Guid> SeedProfileAsync(SqliteConnectionFactory factory)
    {
        var profileId = Guid.NewGuid();
        await using var connection = await factory.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO player_profiles(id, name, game_mode, faction, level, created_utc, updated_utc)
            VALUES ($id, 'Local profile', 'Regular', 'Unknown', 1,
                    '2026-09-13T03:00:00Z', '2026-09-13T03:00:00Z');
            """;
        command.Parameters.AddWithValue("$id", profileId.ToString("D"));
        await command.ExecuteNonQueryAsync(CancellationToken.None);
        return profileId;
    }

    private static void Cleanup(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        File.Delete(databasePath);
        File.Delete(databasePath + "-shm");
        File.Delete(databasePath + "-wal");
    }
}
