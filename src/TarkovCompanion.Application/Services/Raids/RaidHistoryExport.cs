using System.Globalization;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// One scan recorded during a raid, read back from its stored event.
/// </summary>
/// <remarks>
/// A recognised item is <see cref="RaidFactKind.Inferred"/>: the companion matched pixels to a
/// catalog entry and says how sure it is. Its value is <see cref="RaidFactKind.Estimated"/>: a
/// market price at scan time, never something extracted from the raid. A scan that found nothing
/// says so and claims neither.
///
/// Read by hand from the stored JSON rather than deserialized to the runtime record: that record
/// carries a <c>Confidence</c> struct whose value does not survive default deserialization, and a
/// stored payload is untrusted input that should cost one row, not the whole raid, when it is odd.
/// </remarks>
public sealed record RaidScanFact(
    DateTimeOffset ObservedUtc,
    bool IsAvailable,
    bool Recognised,
    string? ItemId,
    string? ItemName,
    double? Confidence,
    long? ValueRoubles,
    long? ValuePerSlotRoubles,
    string? Recommendation)
{
    public RaidFactKind IdentityKind => Recognised ? RaidFactKind.Inferred : RaidFactKind.Unknown;

    public RaidFactKind ValueKind => ValueRoubles is null ? RaidFactKind.Unknown : RaidFactKind.Estimated;

    public static RaidScanFact? TryParse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ObservedUtc", out var observed)
                || !observed.TryGetDateTimeOffset(out var observedUtc))
            {
                return null;
            }

            return new(
                observedUtc.ToUniversalTime(),
                Bool(root, "IsAvailable"),
                Bool(root, "Succeeded"),
                Text(root, "CanonicalItemId"),
                Text(root, "ItemName"),
                root.TryGetProperty("Confidence", out var confidence)
                    && confidence.ValueKind == JsonValueKind.Object
                    && confidence.TryGetProperty("Value", out var value)
                    && value.TryGetDouble(out var score)
                    ? score
                    : null,
                Long(root, "ValueRoubles"),
                Long(root, "ValuePerSlotRoubles"),
                Text(root, "Recommendation"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : null;
}

/// <summary>One raid as it is exported: the record, where each field came from, and what was scanned during it.</summary>
public sealed record RaidExportRecord(
    RaidHistoryEntry Raid,
    RaidFactSources Sources,
    IReadOnlyList<RaidScanFact> Scans);

/// <summary>
/// Writes raid history as CSV or JSON with the source of every fact beside it.
/// </summary>
/// <remarks>
/// <para>
/// The schema is versioned and named, not implied by column order. Version 2 keeps version 1's
/// eight CSV columns first and unchanged, so a reader that took them by position still works, and
/// adds <c>schema_version</c>, a <c>_source</c> column for each field that has one, and scan
/// counts. A source is <c>observed</c>, <c>inferred</c>, <c>estimated</c> or <c>manual</c>, and is
/// empty where the field is empty: an absent value has no source, and is not "observed nothing".
/// </para>
/// <para>
/// JSON version 2 is an envelope, <c>{ schemaVersion, exportedUtc, raids }</c>, where version 1 was
/// a bare array. That is the one break, and it is why the version is now written down. Per raid the
/// eight fields keep their v1 names, and <c>sources</c> and <c>scans</c> are added. A scan's item is
/// <c>inferred</c> and its value <c>estimated</c>; no scan carries a claim of extracted value. The
/// format is described in <c>docs/DEBRIEF_EXPORT.md</c>.
/// </para>
/// <para>
/// Counts are per raid. A per-map numerator and denominator, which the issue also asks for, needs
/// the offered-extract record this export does not have yet.
/// </para>
/// </remarks>
public static class RaidHistoryExport
{
    public const int SchemaVersion = 2;

    public static readonly IReadOnlyList<string> CsvColumns =
    [
        "id", "profile_id", "map_id", "mode", "start_utc", "end_utc", "outcome", "notes",
        "schema_version",
        "map_source", "mode_source", "start_source", "end_source", "outcome_source", "notes_source",
        "scans", "scans_recognised",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task WriteCsvAsync(
        Stream destination,
        IReadOnlyList<RaidExportRecord> records,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(records);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteLineAsync(string.Join(',', CsvColumns).AsMemory(), cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            var raid = record.Raid;
            var sources = record.Sources;
            var row = string.Join(',', new[]
            {
                Escape(raid.Id.ToString("D")),
                Escape(raid.ProfileId.ToString("D")),
                Escape(raid.MapId),
                Escape(raid.Mode),
                Escape(raid.StartedUtc is null ? null : Format(raid.StartedUtc.Value)),
                Escape(raid.EndedUtc is null ? null : Format(raid.EndedUtc.Value)),
                Escape(raid.Outcome),
                Escape(raid.Notes),
                SchemaVersion.ToString(CultureInfo.InvariantCulture),
                Escape(sources.Map.Slug()),
                Escape(sources.Mode.Slug()),
                Escape(sources.Started.Slug()),
                Escape(sources.Ended.Slug()),
                Escape(sources.Outcome.Slug()),
                Escape(sources.Notes.Slug()),
                record.Scans.Count.ToString(CultureInfo.InvariantCulture),
                record.Scans.Count(scan => scan.Recognised).ToString(CultureInfo.InvariantCulture),
            });
            await writer.WriteLineAsync(row.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteJsonAsync(
        Stream destination,
        IReadOnlyList<RaidExportRecord> records,
        DateTimeOffset exportedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(records);
        var document = new ExportDocument(
            SchemaVersion,
            exportedUtc.ToUniversalTime(),
            [.. records.Select(ToDocument)]);
        await JsonSerializer.SerializeAsync(destination, document, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static RaidDocument ToDocument(RaidExportRecord record) => new(
        record.Raid.Id,
        record.Raid.ProfileId,
        record.Raid.MapId,
        record.Raid.Mode,
        record.Raid.StartedUtc?.ToUniversalTime(),
        record.Raid.EndedUtc?.ToUniversalTime(),
        record.Raid.Outcome,
        record.Raid.Notes,
        new SourcesDocument(
            record.Sources.Map.Slug(),
            record.Sources.Mode.Slug(),
            record.Sources.Started.Slug(),
            record.Sources.Ended.Slug(),
            record.Sources.Outcome.Slug(),
            record.Sources.Notes.Slug()),
        [
            .. record.Scans.Select(scan => new ScanDocument(
                scan.ObservedUtc,
                scan.IsAvailable,
                scan.Recognised,
                scan.ItemId,
                scan.ItemName,
                scan.IdentityKind.Slug(),
                scan.Confidence,
                scan.ValueRoubles,
                scan.ValuePerSlotRoubles,
                scan.ValueKind.Slug(),
                scan.Recommendation)),
        ]);

    private sealed record ExportDocument(int SchemaVersion, DateTimeOffset ExportedUtc, IReadOnlyList<RaidDocument> Raids);

    private sealed record RaidDocument(
        Guid Id,
        Guid ProfileId,
        string? MapId,
        string Mode,
        DateTimeOffset? StartedUtc,
        DateTimeOffset? EndedUtc,
        string? Outcome,
        string? Notes,
        SourcesDocument Sources,
        IReadOnlyList<ScanDocument> Scans);

    private sealed record SourcesDocument(
        string? Map,
        string? Mode,
        string? Started,
        string? Ended,
        string? Outcome,
        string? Notes);

    private sealed record ScanDocument(
        DateTimeOffset ObservedUtc,
        bool Available,
        bool Recognised,
        string? ItemId,
        string? ItemName,
        string? ItemSource,
        double? Confidence,
        long? ValueRoubles,
        long? ValuePerSlotRoubles,
        string? ValueSource,
        string? Recommendation);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
