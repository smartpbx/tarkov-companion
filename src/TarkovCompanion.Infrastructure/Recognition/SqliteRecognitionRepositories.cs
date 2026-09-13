using System.Globalization;
using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class SqliteRecognitionCatalogRepository(SqliteConnectionFactory connectionFactory)
    : IRecognitionCatalogRepository
{
    public async Task<IReadOnlyList<CanonicalItemReference>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, short_name FROM items ORDER BY id;";
        var items = new List<CanonicalItemReference>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var displayName = reader.GetString(1);
            var shortName = reader.GetString(2);
            IReadOnlyList<string> aliases = string.Equals(displayName, shortName, StringComparison.OrdinalIgnoreCase)
                ? []
                : [shortName];
            items.Add(new(reader.GetString(0), displayName, aliases));
        }

        return items;
    }
}

public sealed class SqliteScanEventRepository(SqliteConnectionFactory connectionFactory) : IScanEventRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync(ScanEventMetadata scanEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanEvent);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_history(
                id, timestamp_utc, scan_context, resolved_item_id, confidence,
                candidate_json, recommendation, source_geometry_json, diagnostic_code)
            VALUES (
                $id, $timestampUtc, $context, $resolvedItemId, $confidence,
                $candidates, $recommendation, $sourceGeometry, $diagnosticCode);
            """;
        command.Parameters.AddWithValue("$id", scanEvent.ScanId.ToString("D"));
        command.Parameters.AddWithValue("$timestampUtc", scanEvent.TimestampUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$context", scanEvent.Context.ToString());
        command.Parameters.AddWithValue("$resolvedItemId", DbValue(scanEvent.ResolvedItemId));
        command.Parameters.AddWithValue("$confidence", scanEvent.Confidence.Value);
        command.Parameters.AddWithValue("$candidates", JsonSerializer.Serialize(scanEvent.Candidates, JsonOptions));
        command.Parameters.AddWithValue("$recommendation", DbValue(scanEvent.Recommendation));
        command.Parameters.AddWithValue(
            "$sourceGeometry",
            scanEvent.SourceGeometry is null
                ? DBNull.Value
                : JsonSerializer.Serialize(scanEvent.SourceGeometry, JsonOptions));
        command.Parameters.AddWithValue("$diagnosticCode", DbValue(scanEvent.DiagnosticCode));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;
}

/// <summary>
/// Reads back the scans already on disk.
/// </summary>
/// <remarks>
/// The write half has existed since the first migration and had no counterpart: every scan a
/// player made was recorded and none of them could ever be shown again. That is the shape of
/// bug this repository keeps producing, so the read is deliberately plain.
///
/// The item is named by joining the catalog, because <c>scan_history</c> stores the id it
/// resolved to and an id is not something a player recognises. A scan that resolved nothing
/// falls back to the best candidate the recogniser offered, which is the honest answer to
/// "what did it think it saw"; a scan that offered none says so.
/// </remarks>
public sealed class SqliteScanHistoryService(SqliteConnectionFactory connectionFactory) : IScanHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ScanHistoryEntry>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                history.id,
                history.timestamp_utc,
                history.scan_context,
                history.confidence,
                history.candidate_json,
                history.recommendation,
                history.diagnostic_code,
                item.name
            FROM scan_history AS history
            LEFT JOIN items AS item ON item.id = history.resolved_item_id
            ORDER BY history.timestamp_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<ScanHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new(
                Guid.TryParse(reader.GetString(0), out var scanId) ? scanId : Guid.Empty,
                DateTimeOffset.TryParse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var observed)
                    ? observed
                    : DateTimeOffset.UnixEpoch,
                Enum.TryParse<ScanContext>(reader.GetString(2), out var context) ? context : ScanContext.Unknown,
                reader.IsDBNull(7) ? BestCandidate(reader.GetString(4)) : reader.GetString(7),
                Read(reader.GetDouble(3)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return entries;
    }

    /// <summary>
    /// A stored confidence, kept inside the range the type will accept.
    /// </summary>
    /// <remarks>
    /// The column is a plain REAL and the type throws outside nought to one, so a row written
    /// by an older build, or by a future one, would take the whole page down rather than the
    /// one row. Clamped instead.
    /// </remarks>
    private static Confidence Read(double stored) =>
        new(double.IsNaN(stored) ? 0 : Math.Clamp(stored, 0, 1));

    /// <summary>
    /// What the recogniser thought it saw, when it would not commit to an item.
    /// </summary>
    /// <remarks>
    /// Stored as JSON rather than as rows, which was the right call for a write-only table and
    /// is why this has to be parsed rather than joined. A row whose JSON will not parse is an
    /// old or corrupt one and is described as unreadable rather than throwing: a history page
    /// that fails entirely because of one bad row from months ago is worse than one that says
    /// so on that row.
    /// </remarks>
    private static string BestCandidate(string candidateJson)
    {
        try
        {
            var candidates = JsonSerializer.Deserialize<IReadOnlyList<RecognitionCandidate>>(
                candidateJson,
                JsonOptions);
            var best = candidates?
                .OrderByDescending(candidate => candidate.Confidence.Value)
                .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.DisplayName));
            return best is null ? "Nothing recognised" : best.DisplayName + " (unresolved)";
        }
        catch (JsonException)
        {
            return "Unreadable candidates";
        }
    }
}
