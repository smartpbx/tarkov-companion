using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests;

/// <summary>
/// Debrief's promise, against a real database: a correction is kept as one, the export says who
/// wrote each field, and a scan taken during the raid comes out beside it.
/// </summary>
/// <remarks>
/// A corrected outcome used to overwrite the column and leave no trace, so nothing downstream
/// could tell what the player had typed from what the game had said.
/// </remarks>
[Collection(SqliteCollection.Name)]
public sealed class RaidDebriefExportTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-14T02:00:00Z");

    [Fact]
    public async Task A_correction_is_stored_as_an_event_in_the_same_write_and_shows_in_the_export_as_manual()
    {
        await using var harness = await Harness.CreateAsync();
        var raidId = await harness.StartEndedRaidAsync();

        await harness.History.CorrectAsync(raidId, "Survived", "Found a GPU", CancellationToken.None);

        var stored = Assert.Single(await harness.History.ListEventPayloadsAsync(raidId, RaidCorrection.EventType, CancellationToken.None));
        var correction = Assert.Single(RaidCorrection.ParseAll([stored]));
        Assert.Equal(new RaidFieldChange(null, "Survived"), correction.Outcome);
        Assert.Equal(new RaidFieldChange(null, "Found a GPU"), correction.Notes);

        var row = Assert.Single(await ExportRowsAsync(harness));
        Assert.Equal("Survived", row["outcome"]);
        Assert.Equal("manual", row["outcome_source"]);
        Assert.Equal("manual", row["notes_source"]);
        Assert.Equal("observed", row["end_source"]);
    }

    [Fact]
    public async Task Saving_a_correction_that_changes_nothing_writes_no_event()
    {
        await using var harness = await Harness.CreateAsync();
        var raidId = await harness.StartEndedRaidAsync();
        await harness.History.CorrectAsync(raidId, "Survived", null, CancellationToken.None);

        await harness.History.CorrectAsync(raidId, "Survived", null, CancellationToken.None);

        Assert.Single(await harness.History.ListEventPayloadsAsync(raidId, RaidCorrection.EventType, CancellationToken.None));
    }

    [Fact]
    public async Task A_correction_to_a_raid_that_does_not_exist_fails_and_records_nothing()
    {
        await using var harness = await Harness.CreateAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            harness.History.CorrectAsync(Guid.NewGuid(), "Survived", null, CancellationToken.None));
    }

    [Fact]
    public async Task A_raid_the_companion_closed_on_restart_exports_inferred_until_the_player_overwrites_it()
    {
        await using var harness = await Harness.CreateAsync();
        var raidId = await harness.StartEndedRaidAsync(RaidClosure.ClosedOnRestartOutcome, RaidClosure.ClosedOnRestartNotes);

        var closed = Assert.Single(await ExportRowsAsync(harness));
        Assert.Equal("inferred", closed["outcome_source"]);
        Assert.Equal("inferred", closed["end_source"]);

        await harness.History.CorrectAsync(raidId, "Survived", "Extracted", CancellationToken.None);

        var corrected = Assert.Single(await ExportRowsAsync(harness));
        Assert.Equal("manual", corrected["outcome_source"]);
        // The end time is still when the companion noticed, however the outcome was later corrected.
        Assert.Equal("inferred", corrected["end_source"]);
    }

    [Fact]
    public async Task Scans_taken_during_a_raid_come_out_of_both_exports()
    {
        await using var harness = await Harness.CreateAsync();
        var raidId = await harness.StartEndedRaidAsync();
        await harness.History.RecordEventAsync(
            raidId,
            "scan",
            Start.AddMinutes(5),
            JsonSerializer.Serialize(new ScanExecutionResult(
                true, true, "item-gpu", "Graphics card", 12_000, 12_000, "Take", new(0.93), Start.AddMinutes(5), "screenshot", "detail")),
            CancellationToken.None);

        var row = Assert.Single(await ExportRowsAsync(harness));
        Assert.Equal("1", row["scans"]);
        Assert.Equal("1", row["scans_recognised"]);

        await using var json = new MemoryStream();
        await harness.History.ExportJsonAsync(json, CancellationToken.None);
        using var document = JsonDocument.Parse(json.ToArray());
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
        var scan = document.RootElement.GetProperty("raids")[0].GetProperty("scans")[0];
        Assert.Equal("Graphics card", scan.GetProperty("itemName").GetString());
        Assert.Equal("inferred", scan.GetProperty("itemSource").GetString());
        Assert.Equal("estimated", scan.GetProperty("valueSource").GetString());
        Assert.Equal(0.93, scan.GetProperty("confidence").GetDouble());
    }

    [Fact]
    public async Task A_scan_marked_wrong_keeps_its_stable_event_and_is_omitted_from_totals_and_export()
    {
        await using var harness = await Harness.CreateAsync();
        var raidId = await harness.StartEndedRaidAsync();
        await harness.History.RecordEventAsync(
            raidId,
            "scan",
            Start.AddMinutes(5),
            JsonSerializer.Serialize(new ScanExecutionResult(
                true, true, "item-gpu", "Graphics card", 12_000, 12_000, "Take", new(0.93), Start.AddMinutes(5), "screenshot", "detail")),
            CancellationToken.None);
        var scan = Assert.Single(await harness.History.ListEventsAsync(raidId, "scan", CancellationToken.None));

        await harness.History.RecordEventAsync(
            raidId,
            RaidScanCorrection.EventType,
            Start.AddMinutes(25),
            new RaidScanCorrection(scan.Id, true, Start.AddMinutes(25)).ToPayload(),
            CancellationToken.None);

        Assert.Equal(scan.Id, Assert.Single(await harness.History.ListEventsAsync(raidId, "scan", CancellationToken.None)).Id);
        Assert.Single(await harness.History.ListEventPayloadsAsync(raidId, "scan", CancellationToken.None));
        var row = Assert.Single(await ExportRowsAsync(harness));
        Assert.Equal("0", row["scans"]);
        Assert.Equal("0", row["scans_recognised"]);
        await using var json = new MemoryStream();
        await harness.History.ExportJsonAsync(json, CancellationToken.None);
        using var document = JsonDocument.Parse(json.ToArray());
        Assert.Equal(0, document.RootElement.GetProperty("raids")[0].GetProperty("scans").GetArrayLength());
    }

    [Fact]
    public async Task Manual_kills_and_value_round_trip_through_the_real_database_and_the_export()
    {
        await using var harness = await Harness.CreateAsync();
        var raidId = await harness.StartEndedRaidAsync("Survived", null);
        Assert.Null(await harness.History.GetManualMetadataAsync(raidId, CancellationToken.None));

        await harness.History.SetManualMetadataAsync(raidId, new RaidManualMetadata(2, 1, 0, 450_000), CancellationToken.None);

        var stored = await harness.History.GetManualMetadataAsync(raidId, CancellationToken.None);
        Assert.Equal(new RaidManualMetadata(2, 1, 0, 450_000), stored);

        var row = Assert.Single(await ExportRowsAsync(harness));
        Assert.Equal("2", row["pmc_kills"]);
        Assert.Equal("manual", row["pmc_kills_source"]);
        Assert.Equal("450000", row["value_roubles"]);
        Assert.Equal("manual", row["value_roubles_source"]);

        // Clearing every field back to null removes the row's stored JSON rather than keeping an
        // all-null object: GetManualMetadataAsync and an unset raid must read the same way.
        await harness.History.SetManualMetadataAsync(raidId, RaidManualMetadata.Empty, CancellationToken.None);
        Assert.Null(await harness.History.GetManualMetadataAsync(raidId, CancellationToken.None));
    }

    private static async Task<IReadOnlyList<Dictionary<string, string>>> ExportRowsAsync(Harness harness)
    {
        await using var csv = new MemoryStream();
        await harness.History.ExportCsvAsync(csv, CancellationToken.None);
        var lines = Encoding.UTF8.GetString(csv.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].TrimEnd('\r').Split(',');
        return
        [
            .. lines.Skip(1).Select(line => header
                .Zip(line.TrimEnd('\r').Split(','), (name, value) => (name, value))
                .ToDictionary(pair => pair.name, pair => pair.value)),
        ];
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory;

        private Harness(string directory, SqliteRaidHistoryService history)
        {
            _directory = directory;
            History = history;
        }

        public SqliteRaidHistoryService History { get; }

        public static async Task<Harness> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"tarkov-debrief-export-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var factory = new SqliteConnectionFactory(new(Path.Combine(directory, "debrief.db")));
            await new SqliteMigrationRunner(factory).ApplyAsync(CancellationToken.None);
            return new(directory, new SqliteRaidHistoryService(factory, new ManualTimeProvider(Start.AddHours(1))));
        }

        public async Task<Guid> StartEndedRaidAsync(string? outcome = null, string? notes = null)
        {
            var raidId = Guid.NewGuid();
            await History.StartAsync(
                new RaidHistoryEntry(raidId, Guid.NewGuid(), "customs", "Regular", Start, null, null, null),
                CancellationToken.None);
            await History.EndAsync(raidId, Start.AddMinutes(24), outcome, notes, CancellationToken.None);
            return raidId;
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
