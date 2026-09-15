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

public sealed record DatasetHeadEntry(
    string SourceKey,
    string GameMode,
    string Language,
    string State,
    string? VisiblePublicationId,
    string? LastKnownGoodPublicationId,
    DateTimeOffset UpdatedUtc);

public sealed record DatasetPublicationEntry(
    string PublicationId,
    string? RunId,
    string State,
    string? ContentSha256,
    int? RecordCount,
    DateTimeOffset AttemptedUtc,
    DateTimeOffset? PublishedUtc,
    string? Detail,
    string? PreviousLastKnownGoodPublicationId);

public sealed class SqliteSyncStateRepository(SqliteConnectionFactory connectionFactory)
{
    public async Task<DatasetHeadEntry?> GetDatasetHeadAsync(
        string sourceKey,
        string gameMode,
        string language,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT visible_publication_id, last_known_good_publication_id, state, updated_utc
            FROM dataset_heads
            WHERE source_key = $source AND game_mode = $mode AND language = $language;
            """;
        command.Parameters.AddWithValue("$source", sourceKey);
        command.Parameters.AddWithValue("$mode", gameMode);
        command.Parameters.AddWithValue("$language", language);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(sourceKey, gameMode, language, reader.GetString(2),
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime())
            : null;
    }

    public async Task<IReadOnlyList<DatasetPublicationEntry>> ListDatasetPublicationsAsync(
        string sourceKey,
        string gameMode,
        string language,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT publication_id, run_id, state, content_sha256, record_count, attempted_utc,
                   published_utc, detail, previous_lkg_publication_id
            FROM dataset_publications
            WHERE source_key = $source AND game_mode = $mode AND language = $language
            ORDER BY attempted_utc DESC, publication_id DESC LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$source", sourceKey);
        command.Parameters.AddWithValue("$mode", gameMode);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$maximum", maximumCount);
        var result = new List<DatasetPublicationEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt32(4),
                DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                ParseNullableTimestamp(reader, 6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        return result;
    }

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
        CancellationToken cancellationToken,
        string? runId = null,
        int? recordCount = null)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

        var publicationState = entry.Status switch
        {
            "current" => "current",
            "stale" => "stale",
            "refused" => "refused",
            _ => "partial",
        };
        var normalizedHash = contentHash?.ToLowerInvariant();
        if (normalizedHash is not null && !await RawBodyExistsAsync(connection, transaction, normalizedHash, cancellationToken).ConfigureAwait(false))
        {
            normalizedHash = null;
        }

        var publicationId = Guid.NewGuid().ToString("D");
        var previous = await ReadHeadAsync(connection, transaction, entry, cancellationToken).ConfigureAwait(false);
        await using (var publication = connection.CreateCommand())
        {
            publication.Transaction = transaction;
            publication.CommandText = """
                INSERT INTO dataset_publications(
                    publication_id, run_id, source_key, game_mode, language, state,
                    content_sha256, record_count, attempted_utc, published_utc, detail,
                    previous_lkg_publication_id)
                VALUES ($id, $runId, $source, $mode, $language, $state, $hash, $recordCount,
                        $attempted, $published, $detail, $previousLkg);
                """;
            publication.Parameters.AddWithValue("$id", publicationId);
            publication.Parameters.AddWithValue("$runId", (object?)runId ?? DBNull.Value);
            publication.Parameters.AddWithValue("$source", entry.SourceKey);
            publication.Parameters.AddWithValue("$mode", entry.GameMode);
            publication.Parameters.AddWithValue("$language", entry.Language);
            publication.Parameters.AddWithValue("$state", publicationState);
            publication.Parameters.AddWithValue("$hash", (object?)normalizedHash ?? DBNull.Value);
            publication.Parameters.AddWithValue("$recordCount", (object?)recordCount ?? DBNull.Value);
            publication.Parameters.AddWithValue("$attempted", entry.LastAttemptUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            publication.Parameters.AddWithValue("$published", publicationState is "current" or "stale" ? entry.LastAttemptUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            publication.Parameters.AddWithValue("$detail", (object?)entry.ErrorSummary ?? DBNull.Value);
            publication.Parameters.AddWithValue("$previousLkg", (object?)previous.LastKnownGood ?? DBNull.Value);
            await publication.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var visible = publicationState is "current" or "stale" ? publicationId : previous.Visible;
        var lastKnownGood = publicationState == "current" ? publicationId : previous.LastKnownGood;
        await using (var head = connection.CreateCommand())
        {
            head.Transaction = transaction;
            head.CommandText = """
                INSERT INTO dataset_heads(
                    source_key, game_mode, language, visible_publication_id,
                    last_known_good_publication_id, state, updated_utc)
                VALUES ($source, $mode, $language, $visible, $lkg, $state, $updated)
                ON CONFLICT(source_key, game_mode, language) DO UPDATE SET
                    visible_publication_id = excluded.visible_publication_id,
                    last_known_good_publication_id = excluded.last_known_good_publication_id,
                    state = excluded.state,
                    updated_utc = excluded.updated_utc;
                """;
            head.Parameters.AddWithValue("$source", entry.SourceKey);
            head.Parameters.AddWithValue("$mode", entry.GameMode);
            head.Parameters.AddWithValue("$language", entry.Language);
            head.Parameters.AddWithValue("$visible", (object?)visible ?? DBNull.Value);
            head.Parameters.AddWithValue("$lkg", (object?)lastKnownGood ?? DBNull.Value);
            head.Parameters.AddWithValue("$state", publicationState);
            head.Parameters.AddWithValue("$updated", entry.LastAttemptUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await head.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> BeginRunAsync(
        string gameMode,
        string language,
        int endpointCount,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("D");
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO dataset_sync_runs(
                run_id, game_mode, language, started_utc, completed_utc, state,
                endpoint_count, successful_count, stale_count, refused_count, failure_count, detail)
            VALUES ($id, $mode, $language, $started, NULL, 'partial', $count, 0, 0, 0, 0, NULL);
            """;
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$mode", gameMode);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$started", startedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$count", endpointCount);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return runId;
    }

    public async Task CompleteRunAsync(
        string runId,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dataset_sync_runs
            SET completed_utc = $completed,
                successful_count = (SELECT COUNT(*) FROM dataset_publications WHERE run_id = $id AND state = 'current'),
                stale_count = (SELECT COUNT(*) FROM dataset_publications WHERE run_id = $id AND state = 'stale'),
                refused_count = (SELECT COUNT(*) FROM dataset_publications WHERE run_id = $id AND state = 'refused'),
                failure_count = (SELECT COUNT(*) FROM dataset_publications WHERE run_id = $id AND state = 'partial'),
                state = CASE
                    WHEN (SELECT COUNT(*) FROM dataset_publications WHERE run_id = $id) < endpoint_count THEN 'partial'
                    WHEN (SELECT COUNT(*) FROM dataset_publications WHERE run_id = $id AND state IN ('refused', 'partial')) > 0 THEN 'partial'
                    WHEN (SELECT COUNT(*) FROM dataset_publications WHERE run_id = $id AND state = 'stale') > 0 THEN 'stale'
                    ELSE 'current'
                END
            WHERE run_id = $id;
            """;
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$completed", completedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> RawBodyExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string hash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM raw_endpoint_bodies WHERE content_sha256 = $hash);";
        command.Parameters.AddWithValue("$hash", hash);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<(string? Visible, string? LastKnownGood)> ReadHeadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncStateEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT visible_publication_id, last_known_good_publication_id
            FROM dataset_heads
            WHERE source_key = $source AND game_mode = $mode AND language = $language;
            """;
        command.Parameters.AddWithValue("$source", entry.SourceKey);
        command.Parameters.AddWithValue("$mode", entry.GameMode);
        command.Parameters.AddWithValue("$language", entry.Language);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return (null, null);
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static DateTimeOffset? ParseNullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
