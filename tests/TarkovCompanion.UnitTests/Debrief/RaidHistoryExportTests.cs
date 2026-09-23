using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Debrief;

/// <summary>
/// The Debrief export's schema, pinned: named columns, a version, and a source beside every fact.
/// </summary>
public sealed class RaidHistoryExportTests
{
    private static readonly Guid RaidId = Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid ProfileId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Start = new(2026, 9, 15, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_csv_keeps_the_original_eight_columns_first_and_names_every_new_one()
    {
        var text = await Csv([Record(outcome: "Survived")]);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "id,profile_id,map_id,mode,start_local,end_local,outcome,notes,schema_version,map_source,mode_source,"
                + "start_source,end_source,outcome_source,notes_source,scans,scans_recognised,pmc_kills,scav_kills,"
                + "boss_kills,value_roubles,pmc_kills_source,scav_kills_source,boss_kills_source,value_roubles_source",
            lines[0].TrimEnd('\r'));
        Assert.Equal(RaidHistoryExport.CsvColumns, lines[0].TrimEnd('\r').Split(','));
        Assert.StartsWith("id,profile_id,map_id,mode,start_local,end_local,outcome,notes,", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_csv_row_says_where_each_fact_came_from_and_leaves_an_absent_one_empty()
    {
        var text = await Csv([Record(outcome: "Survived", notes: null)]);

        var row = Cells(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r'));
        var header = RaidHistoryExport.CsvColumns.ToList();
        string Cell(string column) => row[header.IndexOf(column)];

        Assert.Equal("3", Cell("schema_version"));
        Assert.Equal("customs", Cell("map_id"));
        Assert.Equal("observed", Cell("map_source"));
        Assert.Equal("inferred", Cell("mode_source"));
        Assert.Equal("observed", Cell("end_source"));
        Assert.Equal("Survived", Cell("outcome"));
        Assert.Equal("manual", Cell("outcome_source"));
        Assert.Equal(string.Empty, Cell("notes"));
        Assert.Equal(string.Empty, Cell("notes_source"));
    }

    [Fact]
    public async Task Scans_are_counted_and_only_recognised_ones_count_as_recognised()
    {
        var text = await Csv([Record(scans: [Scan(recognised: true), Scan(recognised: false), Scan(recognised: true)])]);

        var row = Cells(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r'));
        var header = RaidHistoryExport.CsvColumns.ToList();
        Assert.Equal("3", row[header.IndexOf("scans")]);
        Assert.Equal("2", row[header.IndexOf("scans_recognised")]);
    }

    [Fact]
    public async Task Manual_kills_and_value_export_as_manual_and_an_unset_field_stays_empty()
    {
        var text = await Csv([Record(manual: new RaidManualMetadata(2, 1, 0, 450_000))]);

        var row = Cells(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r'));
        var header = RaidHistoryExport.CsvColumns.ToList();
        string Cell(string column) => row[header.IndexOf(column)];

        Assert.Equal("2", Cell("pmc_kills"));
        Assert.Equal("1", Cell("scav_kills"));
        Assert.Equal("0", Cell("boss_kills"));
        Assert.Equal("450000", Cell("value_roubles"));
        Assert.Equal("manual", Cell("pmc_kills_source"));
        Assert.Equal("manual", Cell("scav_kills_source"));
        Assert.Equal("manual", Cell("boss_kills_source"));
        Assert.Equal("manual", Cell("value_roubles_source"));
    }

    [Fact]
    public async Task A_raid_with_no_manual_fields_exports_them_all_empty()
    {
        var text = await Csv([Record()]);

        var row = Cells(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)[1].TrimEnd('\r'));
        var header = RaidHistoryExport.CsvColumns.ToList();
        string Cell(string column) => row[header.IndexOf(column)];

        Assert.Equal(string.Empty, Cell("pmc_kills"));
        Assert.Equal(string.Empty, Cell("value_roubles"));
        Assert.Equal(string.Empty, Cell("pmc_kills_source"));
        Assert.Equal(string.Empty, Cell("value_roubles_source"));
    }

    [Fact]
    public async Task Manual_fields_export_to_json_under_the_raid_and_its_sources()
    {
        await using var stream = new MemoryStream();
        await RaidHistoryExport.WriteJsonAsync(
            stream,
            [Record(manual: new RaidManualMetadata(2, 1, 0, 450_000))],
            Start,
            CancellationToken.None);

        using var document = JsonDocument.Parse(stream.ToArray());
        var raid = document.RootElement.GetProperty("raids")[0];
        Assert.Equal(2, raid.GetProperty("pmcKills").GetInt32());
        Assert.Equal(1, raid.GetProperty("scavKills").GetInt32());
        Assert.Equal(0, raid.GetProperty("bossKills").GetInt32());
        Assert.Equal(450_000, raid.GetProperty("valueRoubles").GetInt64());
        var sources = raid.GetProperty("sources");
        Assert.Equal("manual", sources.GetProperty("pmcKills").GetString());
        Assert.Equal("manual", sources.GetProperty("valueRoubles").GetString());
    }

    [Fact]
    public async Task The_json_is_a_versioned_envelope_with_sources_and_scans_per_raid()
    {
        var exported = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        await using var stream = new MemoryStream();
        await RaidHistoryExport.WriteJsonAsync(
            stream,
            [Record(outcome: "Survived", scans: [Scan(recognised: true)])],
            exported,
            CancellationToken.None);

        using var document = JsonDocument.Parse(stream.ToArray());
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(exported, root.GetProperty("exportedUtc").GetDateTimeOffset());
        var raid = Assert.Single(root.GetProperty("raids").EnumerateArray());
        Assert.Equal("customs", raid.GetProperty("mapId").GetString());
        Assert.Equal("Survived", raid.GetProperty("outcome").GetString());
        var sources = raid.GetProperty("sources");
        Assert.Equal("observed", sources.GetProperty("map").GetString());
        Assert.Equal("inferred", sources.GetProperty("mode").GetString());
        Assert.Equal("manual", sources.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, sources.GetProperty("notes").ValueKind);
        var scan = Assert.Single(raid.GetProperty("scans").EnumerateArray());
        Assert.Equal("inferred", scan.GetProperty("itemSource").GetString());
        Assert.Equal("estimated", scan.GetProperty("valueSource").GetString());
        Assert.Equal(12_000, scan.GetProperty("valueRoubles").GetInt64());
    }

    [Fact]
    public async Task A_scan_that_found_nothing_claims_neither_an_identity_nor_a_value()
    {
        await using var stream = new MemoryStream();
        await RaidHistoryExport.WriteJsonAsync(
            stream,
            [Record(scans: [Scan(recognised: false)])],
            Start,
            CancellationToken.None);

        using var document = JsonDocument.Parse(stream.ToArray());
        var scan = document.RootElement.GetProperty("raids")[0].GetProperty("scans")[0];
        Assert.Equal(JsonValueKind.Null, scan.GetProperty("itemSource").ValueKind);
        Assert.Equal(JsonValueKind.Null, scan.GetProperty("valueSource").ValueKind);
    }

    [Fact]
    public void A_stored_scan_reads_back_with_its_confidence_which_default_deserialization_would_lose()
    {
        var stored = JsonSerializer.Serialize(new ScanExecutionResult(
            true,
            true,
            "item-gpu",
            "Graphics card",
            12_000,
            12_000,
            "Take",
            new Confidence(0.93),
            Start.AddMinutes(5),
            "screenshot",
            "Resolved Graphics card from an in-memory scan; no pixels were persisted."));

        var scan = RaidScanFact.TryParse(stored);

        Assert.NotNull(scan);
        Assert.True(scan.Recognised);
        Assert.Equal("Graphics card", scan.ItemName);
        Assert.Equal(0.93, scan.Confidence);
        Assert.Equal(12_000, scan.ValueRoubles);
        Assert.Equal("Take", scan.Recommendation);
        Assert.Equal(RaidFactKind.Inferred, scan.IdentityKind);
        Assert.Equal(RaidFactKind.Estimated, scan.ValueKind);
    }

    [Fact]
    public void A_stored_scan_keeps_the_event_identity_supplied_by_the_history_store()
    {
        var stored = JsonSerializer.Serialize(new ScanExecutionResult(
            true, true, "item-gpu", "Graphics card", 12_000, 12_000, "Take", new(0.93), Start, "screenshot", "detail"));

        var scan = RaidScanFact.TryParse("raid:42", stored);

        Assert.NotNull(scan);
        Assert.Equal("raid:42", scan.Id);
    }

    [Fact]
    public void The_latest_scan_correction_decides_whether_the_scan_is_wrong()
    {
        var wrong = new RaidScanCorrection("raid:42", true, Start).ToPayload();
        var restored = new RaidScanCorrection("raid:42", false, Start.AddMinutes(1)).ToPayload();
        var other = new RaidScanCorrection("raid:43", true, Start.AddMinutes(2)).ToPayload();

        var wrongIds = RaidScanCorrection.WrongScanIds([wrong, "not json", restored, other]);

        Assert.DoesNotContain("raid:42", wrongIds);
        Assert.Contains("raid:43", wrongIds);
    }

    [Fact]
    public void A_payload_that_is_not_a_scan_costs_one_row_and_nothing_else()
    {
        Assert.Null(RaidScanFact.TryParse("not json"));
        Assert.Null(RaidScanFact.TryParse("[]"));
        Assert.Null(RaidScanFact.TryParse("{}"));
    }

    private static async Task<string> Csv(IReadOnlyList<RaidExportRecord> records)
    {
        await using var stream = new MemoryStream();
        await RaidHistoryExport.WriteCsvAsync(stream, records, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string[] Cells(string line) => line.Split(',');

    private static RaidExportRecord Record(
        string? outcome = null,
        string? notes = null,
        IReadOnlyList<RaidScanFact>? scans = null,
        RaidManualMetadata? manual = null)
    {
        var raid = new RaidHistoryEntry(RaidId, ProfileId, "customs", "Regular", Start, Start.AddMinutes(24), outcome, notes);
        return new(raid, RaidFactRules.Classify(raid, []), scans ?? [], manual);
    }

    private static RaidScanFact Scan(bool recognised) => new(
        Start.AddMinutes(3),
        true,
        recognised,
        recognised ? "item-gpu" : null,
        recognised ? "Graphics card" : null,
        recognised ? 0.9 : null,
        recognised ? 12_000 : null,
        recognised ? 12_000 : null,
        recognised ? "Take" : null);
}
