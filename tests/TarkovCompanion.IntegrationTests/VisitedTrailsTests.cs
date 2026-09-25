using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// Everywhere this player has been on one map.
/// </summary>
/// <remarks>
/// <c>raid_events</c> has kept every position of every raid since the first one, and the only
/// readers were a single raid's replay and a distance sum. A player who has run Customs two
/// hundred times had two hundred trails in the database and could see one at a time, from the
/// History page, one raid at a time.
///
/// The grouping is the part worth guarding. The points of one raid are a path and the points of
/// two are not: joining the last position of Tuesday to the first of Wednesday would draw a line
/// across the map that nobody walked.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class VisitedTrailsTests
{
    private static readonly DateTimeOffset Evening = DateTimeOffset.Parse("2026-09-13T20:00:00Z");

    [Fact]
    public async Task Every_raid_on_the_map_comes_back_newest_first()
    {
        await using var scratch = await Scratch.CreateAsync();
        await scratch.RaidAsync("customs", Evening, ["a-1", "a-2"]);
        await scratch.RaidAsync("customs", Evening.AddHours(1), ["b-1"]);

        var trails = await scratch.History.ListTrailsForMapAsync("customs", 10, CancellationToken.None);

        Assert.Equal(2, trails.Count);
        Assert.Equal(["b-1"], trails[0].Positions.Select(position => position.Filename));
        Assert.Equal(["a-1", "a-2"], trails[1].Positions.Select(position => position.Filename));
    }

    [Fact]
    public async Task One_raid_is_one_path_rather_than_two_joined_together()
    {
        // The whole reason the answer is grouped. Flattened, the last position of one raid and
        // the first of the next become a line across the map that nobody walked.
        await using var scratch = await Scratch.CreateAsync();
        await scratch.RaidAsync("customs", Evening, ["a-1", "a-2"]);
        await scratch.RaidAsync("customs", Evening.AddHours(1), ["b-1", "b-2"]);

        var trails = await scratch.History.ListTrailsForMapAsync("customs", 10, CancellationToken.None);

        Assert.All(trails, trail => Assert.Equal(2, trail.Positions.Count));
    }

    [Fact]
    public async Task A_raid_on_another_map_is_not_on_this_one()
    {
        await using var scratch = await Scratch.CreateAsync();
        await scratch.RaidAsync("customs", Evening, ["here"]);
        await scratch.RaidAsync("woods", Evening.AddHours(1), ["elsewhere"]);

        var trails = await scratch.History.ListTrailsForMapAsync("customs", 10, CancellationToken.None);

        Assert.Equal(["here"], Assert.Single(trails).Positions.Select(position => position.Filename));
    }

    [Fact]
    public async Task The_limit_counts_raids_rather_than_points()
    {
        // Which is what the caller is choosing between: past a few dozen the lines stop being
        // distinguishable from each other.
        await using var scratch = await Scratch.CreateAsync();
        for (var index = 0; index < 5; index++)
        {
            await scratch.RaidAsync("customs", Evening.AddHours(index), [$"raid-{index}-1", $"raid-{index}-2"]);
        }

        var trails = await scratch.History.ListTrailsForMapAsync("customs", 2, CancellationToken.None);

        Assert.Equal(2, trails.Count);
        Assert.All(trails, trail => Assert.Equal(2, trail.Positions.Count));
    }

    [Fact]
    public async Task The_limit_keeps_the_newest_raids()
    {
        await using var scratch = await Scratch.CreateAsync();
        await scratch.RaidAsync("customs", Evening, ["oldest"]);
        await scratch.RaidAsync("customs", Evening.AddHours(5), ["newest"]);

        var trails = await scratch.History.ListTrailsForMapAsync("customs", 1, CancellationToken.None);

        Assert.Equal(["newest"], Assert.Single(trails).Positions.Select(position => position.Filename));
    }

    [Fact]
    public async Task A_deleted_raid_is_not_drawn_and_does_not_take_a_slot_in_the_limit()
    {
        // #889: the Debrief undo window keeps a deleted raid soft-deleted, and the trails query
        // still drew it and let it push the oldest real raid out of the limit.
        await using var scratch = await Scratch.CreateAsync();
        await scratch.RaidAsync("customs", Evening, ["kept"]);
        var deleted = await scratch.RaidAsync("customs", Evening.AddHours(5), ["deleted"]);
        await scratch.History.SoftDeleteAsync([deleted], Evening.AddHours(6), CancellationToken.None);

        var trails = await scratch.History.ListTrailsForMapAsync("customs", 1, CancellationToken.None);

        Assert.Equal(["kept"], Assert.Single(trails).Positions.Select(position => position.Filename));
    }

    [Fact]
    public async Task A_payload_that_is_not_a_position_does_not_put_a_point_at_the_origin()
    {
        // The same refusal the single-raid read makes. Valid JSON that is not one of these
        // deserialises to a record of defaults: no filename, and a position at (0, 0, 0), which
        // drawn on a map is somewhere nobody stood.
        await using var scratch = await Scratch.CreateAsync();
        await scratch.RaidAsync("customs", Evening, ["real"]);
        await scratch.RawEventAsync("customs", Evening, """{"somethingElse":true}""");

        var trail = Assert.Single(await scratch.History.ListTrailsForMapAsync("customs", 10, CancellationToken.None));

        Assert.Equal(["real"], trail.Positions.Select(position => position.Filename));
    }

    [Fact]
    public async Task A_map_nobody_has_run_is_an_empty_list_rather_than_a_failure()
    {
        await using var scratch = await Scratch.CreateAsync();

        Assert.Empty(await scratch.History.ListTrailsForMapAsync("lighthouse", 10, CancellationToken.None));
    }

    [Fact]
    public async Task Asking_for_no_raids_reads_nothing()
    {
        await using var scratch = await Scratch.CreateAsync();
        await scratch.RaidAsync("customs", Evening, ["a-1"]);

        Assert.Empty(await scratch.History.ListTrailsForMapAsync("customs", 0, CancellationToken.None));
    }

    private sealed class Scratch : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly string _directory;

        private Scratch(string directory, SqliteConnectionFactory factory)
        {
            _directory = directory;
            Factory = factory;
            History = new SqliteRaidHistoryService(factory);
        }

        public SqliteConnectionFactory Factory { get; }

        public SqliteRaidHistoryService History { get; }

        public static async Task<Scratch> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"tarkov-visited-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var factory = new SqliteConnectionFactory(new(Path.Combine(directory, "visited.db")));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);

            var scratch = new Scratch(directory, factory);
            await scratch.ExecuteAsync(
                """
                INSERT INTO player_profiles(id, name, game_mode, faction, level, created_utc, updated_utc)
                VALUES ('11111111-1111-1111-1111-111111111111', 'Walker', 'Regular', 'Usec', 20, '2026-09-13T00:00:00Z', '2026-09-13T00:00:00Z');
                """);
            return scratch;
        }

        /// <summary>Records one raid with one position event per name given.</summary>
        public async Task<Guid> RaidAsync(string mapId, DateTimeOffset started, IReadOnlyList<string> filenames)
        {
            var raidId = Guid.NewGuid();
            await ExecuteAsync(
                $"""
                 INSERT INTO raids(id, profile_id, map_id, mode, start_utc)
                 VALUES ('{raidId:D}', '11111111-1111-1111-1111-111111111111', '{mapId}', 'Regular', '{started:O}');
                 """);

            for (var index = 0; index < filenames.Count; index++)
            {
                var position = new ScreenshotPosition(
                    started.AddMinutes(index),
                    new(index * 10, 0, index * 10),
                    new(0, 0, 0, 1),
                    90,
                    null,
                    null,
                    filenames[index]);
                await EventAsync(raidId, started.AddMinutes(index), JsonSerializer.Serialize(position, Json));
            }

            return raidId;
        }

        /// <summary>Records a position event whose payload is not a position.</summary>
        public async Task RawEventAsync(string mapId, DateTimeOffset started, string payload)
        {
            var raidId = Guid.NewGuid();
            await ExecuteAsync(
                $"""
                 INSERT INTO raids(id, profile_id, map_id, mode, start_utc)
                 VALUES ('{raidId:D}', '11111111-1111-1111-1111-111111111111', '{mapId}', 'Regular', '{started:O}');
                 """);
            await EventAsync(raidId, started, payload);
        }

        private Task EventAsync(Guid raidId, DateTimeOffset at, string payload) => ExecuteAsync(
            $"""
             INSERT INTO raid_events(raid_id, timestamp_utc, type, payload_json)
             VALUES ('{raidId:D}', '{at:O}', 'position', '{payload.Replace("'", "''")}');
             """);

        private async Task ExecuteAsync(string sql)
        {
            await using var connection = await Factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                ScratchDirectory.Remove(_directory);
            }

            return ValueTask.CompletedTask;
        }
    }
}
