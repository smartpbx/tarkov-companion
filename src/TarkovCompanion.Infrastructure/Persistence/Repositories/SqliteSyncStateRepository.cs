using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed record SyncStateEntry(
    string SourceKey,
    string GameMode,
    string Language,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset LastAttemptUtc,
    string? ETag,
    DateTimeOffset? LastModified,
    string Status,
    string? ErrorSummary);

public sealed class SqliteSyncStateRepository(SqliteConnectionFactory connectionFactory)
{
    public async Task<SyncStateEntry?> GetAsync(
        string sourceKey,
        string gameMode,
        string language,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_success_utc, last_attempt_utc, etag, last_modified, status, error_summary
            FROM sync_state
            WHERE source_key = $sourceKey AND game_mode = $gameMode AND language = $language;
            """;
        command.Parameters.AddWithValue("$sourceKey", sourceKey);
        command.Parameters.AddWithValue("$gameMode", gameMode);
        command.Parameters.AddWithValue("$language", language);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new(
            sourceKey,
            gameMode,
            language,
            ParseNullableTimestamp(reader, 0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            ParseNullableTimestamp(reader, 3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public async Task RecordAsync(
        SyncStateEntry entry,
        string? contentHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_state(
                source_key, game_mode, language, last_success_utc, last_attempt_utc,
                etag, last_modified, content_hash, status, error_summary)
            VALUES (
                $sourceKey, $gameMode, $language, $lastSuccessUtc, $lastAttemptUtc,
                $etag, $lastModified, $contentHash, $status, $errorSummary)
            ON CONFLICT(source_key, game_mode, language) DO UPDATE SET
                last_success_utc = COALESCE(excluded.last_success_utc, sync_state.last_success_utc),
                last_attempt_utc = excluded.last_attempt_utc,
                etag = COALESCE(excluded.etag, sync_state.etag),
                last_modified = COALESCE(excluded.last_modified, sync_state.last_modified),
                content_hash = COALESCE(excluded.content_hash, sync_state.content_hash),
                status = excluded.status,
                error_summary = excluded.error_summary;
            """;
        command.Parameters.AddWithValue("$sourceKey", entry.SourceKey);
        command.Parameters.AddWithValue("$gameMode", entry.GameMode);
        command.Parameters.AddWithValue("$language", entry.Language);
        command.Parameters.AddWithValue(
            "$lastSuccessUtc",
            entry.LastSuccessUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$lastAttemptUtc", entry.LastAttemptUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$etag", (object?)entry.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lastModified",
            entry.LastModified?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$contentHash", (object?)contentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", entry.Status);
        command.Parameters.AddWithValue("$errorSummary", (object?)entry.ErrorSummary ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset? ParseNullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
