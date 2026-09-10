using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
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
