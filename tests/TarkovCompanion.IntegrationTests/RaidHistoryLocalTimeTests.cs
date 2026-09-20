using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// The raid-history exports are read by the player, so they carry the player's clock, while the
/// database keeps the exact UTC instant it was given.
/// </summary>
/// <remarks>
/// The two formats are chosen separately on purpose. The CSV is opened in a spreadsheet, which
/// reads "2026-09-18 23:03:39" as a date-time and reads an ISO string ending "+00:00" as text, so
/// it gets the local clock under a header that names it. The JSON is read by programs as well as
/// people, so it gets ISO-8601 with the numeric offset: local for a person, and the very same
/// instant for a program. The zone is pinned to UTC-4 so a UTC-only CI box cannot pass by
/// coincidence, and the "stored values are untouched" half is checked under that same zone.
/// </remarks>
public sealed class RaidHistoryLocalTimeTests
{
    private static readonly Guid ProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // 03:03:39 UTC is 23:03:39 the evening before at UTC-4: the date differs, not just the hour.
    private static readonly DateTimeOffset Started = new(2026, 9, 19, 3, 3, 39, TimeSpan.Zero);
    private static readonly DateTimeOffset Ended = new(2026, 9, 19, 3, 27, 40, TimeSpan.Zero);

    private static readonly TimeZoneInfo UtcMinusFour =
        TimeZoneInfo.CreateCustomTimeZone("Test/UTC-4", TimeSpan.FromHours(-4), "UTC-4", "UTC-4");

    [Fact]
    public async Task Csv_export_is_a_spreadsheet_friendly_local_clock_under_a_header_that_says_so()
    {
        await using var scratch = await Scratch.CreateAsync();
        using var zone = LocalTime.UseZone(UtcMinusFour);

        var lines = (await scratch.ExportCsvAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Version 2 keeps these eight columns first and unchanged, then adds a schema version, a
        // source per field and scan counts (docs/DEBRIEF_EXPORT.md) — a prefix check, not equality.
        Assert.StartsWith(
            "id,profile_id,map_id,mode,start_local,end_local,outcome,notes",
            lines[0].TrimEnd('\r'),
            StringComparison.Ordinal);
        var row = lines[1].TrimEnd('\r').Split(',');
        Assert.Equal("customs", row[2]);
        Assert.Equal("2026-09-18 23:03:39", row[4]);
        Assert.Equal("2026-09-18 23:27:40", row[5]);
        Assert.Equal("Survived", row[6]);
    }

    [Fact]
    public async Task Json_export_is_iso_8601_at_the_players_offset_and_names_the_same_instant()
    {
        await using var scratch = await Scratch.CreateAsync();
        using var zone = LocalTime.UseZone(UtcMinusFour);

        using var document = JsonDocument.Parse(await scratch.ExportJsonAsync());

        // Version 2 is an envelope (schemaVersion, exportedUtc, raids) rather than version 1's bare
        // array — the one documented, deliberate break in docs/DEBRIEF_EXPORT.md. Nothing in the
        // repo reads this file, so nothing but this test needed to move with it.
        var raid = Assert.Single(document.RootElement.GetProperty("raids").EnumerateArray());
        Assert.False(raid.TryGetProperty("startedUtc", out _));
        Assert.False(raid.TryGetProperty("endedUtc", out _));
        var started = raid.GetProperty("started").GetString()!;
        var ended = raid.GetProperty("ended").GetString()!;
        Assert.EndsWith("-04:00", started, StringComparison.Ordinal);
        Assert.EndsWith("-04:00", ended, StringComparison.Ordinal);
        Assert.StartsWith("2026-09-18T23:03:39", started, StringComparison.Ordinal);
        // The offset is what keeps it unambiguous: a program reads back the instant that was stored.
        Assert.Equal(Started, DateTimeOffset.Parse(started, CultureInfo.InvariantCulture));
        Assert.Equal(Ended, DateTimeOffset.Parse(ended, CultureInfo.InvariantCulture));
        Assert.Equal("customs", raid.GetProperty("mapId").GetString());
    }

    /// <summary>
    /// A presentation change must not move a stored value: after both exports, under the non-UTC
    /// zone, the rows are byte-for-byte what was written and the service still returns UTC.
    /// </summary>
    [Fact]
    public async Task Storage_and_the_listed_history_stay_utc_after_exporting_under_another_zone()
    {
        await using var scratch = await Scratch.CreateAsync();
        using var zone = LocalTime.UseZone(UtcMinusFour);

        _ = await scratch.ExportCsvAsync();
        _ = await scratch.ExportJsonAsync();

        var (startUtc, endUtc) = await scratch.StoredTimesAsync();
        Assert.Equal("2026-09-19T03:03:39.0000000+00:00", startUtc);
        Assert.Equal("2026-09-19T03:27:40.0000000+00:00", endUtc);
        var listed = Assert.Single(await scratch.History.ListAsync(CancellationToken.None));
        Assert.Equal(Started, listed.StartedUtc);
        Assert.Equal(Ended, listed.EndedUtc);
        Assert.Equal(TimeSpan.Zero, listed.StartedUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, listed.EndedUtc!.Value.Offset);
    }

    private sealed class Scratch : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly SqliteConnectionFactory _factory;

        private Scratch(string directory, SqliteConnectionFactory factory)
        {
            _directory = directory;
            _factory = factory;
            History = new SqliteRaidHistoryService(factory);
        }

        public SqliteRaidHistoryService History { get; }

        public static async Task<Scratch> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"tarkov-local-time-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var factory = new SqliteConnectionFactory(new(Path.Combine(directory, "local-time.db")));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            var scratch = new Scratch(directory, factory);
            await scratch.ExecuteAsync(
                $"""
                 INSERT INTO player_profiles(id, name, game_mode, faction, level, created_utc, updated_utc)
                 VALUES ('{ProfileId:D}', 'Walker', 'Regular', 'Usec', 20, '2026-09-13T00:00:00Z', '2026-09-13T00:00:00Z');
                 """);
            var raidId = await scratch.History.StartAsync(
                new RaidHistoryEntry(Guid.NewGuid(), ProfileId, "customs", "Regular", Started, null, null, null),
                CancellationToken.None);
            await scratch.History.EndAsync(raidId, Ended, "Survived", null, CancellationToken.None);
            return scratch;
        }

        public async Task<string> ExportCsvAsync()
        {
            await using var stream = new MemoryStream();
            await History.ExportCsvAsync(stream, CancellationToken.None);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public async Task<string> ExportJsonAsync()
        {
            await using var stream = new MemoryStream();
            await History.ExportJsonAsync(stream, CancellationToken.None);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        public async Task<(string StartUtc, string EndUtc)> StoredTimesAsync()
        {
            await using var connection = await _factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT start_utc, end_utc FROM raids;";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetString(0), reader.GetString(1));
        }

        public async ValueTask DisposeAsync()
        {
            await Task.CompletedTask;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                ScratchDirectory.Remove(_directory);
            }
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var connection = await _factory.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }
}
