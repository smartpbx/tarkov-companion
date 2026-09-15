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

public sealed record DatasetSyncRunLease(string RunId, long PublicationOrder);

public sealed record DatasetSyncRunEntry(
    string RunId,
    long PublicationOrder,
    string GameMode,
    string Language,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    string State,
    int EndpointCount,
    int SuccessfulCount,
    int StaleCount,
    int RefusedCount,
    int FailureCount,
    string? Detail);

public sealed record DatasetEndpointMaterializationEntry(
    string SourceKey,
    long ClaimedPublicationOrder,
    string ClaimedRunId,
    long? MaterializedPublicationOrder,
    string? MaterializedRunId,
    string? MaterializedGameMode,
    string? MaterializedLanguage,
    string? MaterializedPublicationId);

public sealed class DatasetPublicationSupersededException : Exception
{
    internal DatasetPublicationSupersededException(
        string sourceKey,
        DatasetSyncRunLease attempted,
        long claimedPublicationOrder,
        string claimedRunId)
        : base(
            $"Publication order {attempted.PublicationOrder} for '{sourceKey}' was superseded " +
            $"by durable order {claimedPublicationOrder}.")
    {
        SourceKey = sourceKey;
        AttemptedPublicationOrder = attempted.PublicationOrder;
        AttemptedRunId = attempted.RunId;
        ClaimedPublicationOrder = claimedPublicationOrder;
        ClaimedRunId = claimedRunId;
    }

    public string SourceKey { get; }

    public long AttemptedPublicationOrder { get; }

    public string AttemptedRunId { get; }

    public long ClaimedPublicationOrder { get; }

    public string ClaimedRunId { get; }
}

public sealed class SqliteSyncStateRepository(SqliteConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<DatasetSyncRunEntry>> ListDatasetSyncRunsAsync(
        string gameMode,
        string language,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, publication_order, game_mode, language, started_utc, completed_utc, state, endpoint_count,
                   successful_count, stale_count, refused_count, failure_count, detail
            FROM dataset_sync_runs
            WHERE game_mode = $mode AND language = $language
            ORDER BY publication_order DESC
            LIMIT $maximum;
            """;
        command.Parameters.AddWithValue("$mode", gameMode);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$maximum", maximumCount);
        var result = new List<DatasetSyncRunEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                ParseNullableTimestamp(reader, 5),
                reader.GetString(6),
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return result;
    }

    public async Task<DatasetEndpointMaterializationEntry?> GetEndpointMaterializationAsync(
        string sourceKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT claimed_publication_order, claimed_run_id, materialized_publication_order,
                   materialized_run_id, materialized_game_mode, materialized_language,
                   materialized_publication_id
            FROM dataset_endpoint_materializations
            WHERE source_key = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new(
            sourceKey,
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

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
        DatasetSyncRunLease run,
        int? recordCount = null)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RecordInTransactionAsync(
            entry,
            contentHash,
            run,
            recordCount,
            connection,
            transaction,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RecordInTransactionAsync(
        SyncStateEntry entry,
        string? contentHash,
        DatasetSyncRunLease run,
        int? recordCount,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await RequireCurrentClaimAsync(connection, transaction, entry.SourceKey, run, cancellationToken)
            .ConfigureAwait(false);

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
            publication.Parameters.AddWithValue("$runId", run.RunId);
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

        if (publicationState is "current" or "stale")
        {
            // The normalized endpoint tables have no mode/language discriminator. Once this
            // context materializes, a head for any other context would point at rows that are
            // no longer in the database. Keep its LKG as history, but remove the false visible
            // claim in the same transaction that replaces the rows.
            await InvalidateOtherContextsAsync(
                    connection,
                    transaction,
                    entry,
                    cancellationToken)
                .ConfigureAwait(false);
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

        if (publicationState is "current" or "stale")
        {
            await MarkMaterializedAsync(
                    connection,
                    transaction,
                    entry,
                    publicationId,
                    run,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<DatasetSyncRunLease> BeginRunAsync(
        string gameMode,
        string language,
        IReadOnlyCollection<string> sourceKeys,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceKeys);
        if (sourceKeys.Count is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKeys));
        }

        var distinctSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceKey in sourceKeys)
        {
            if (string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > 256)
            {
                throw new ArgumentException("Every endpoint source key must contain 1 to 256 characters.", nameof(sourceKeys));
            }

            if (!distinctSources.Add(sourceKey))
            {
                throw new ArgumentException($"Endpoint source key '{sourceKey}' is duplicated.", nameof(sourceKeys));
            }
        }

        var runId = Guid.NewGuid().ToString("D");
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        long publicationOrder;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO dataset_sync_runs(
                    run_id, game_mode, language, started_utc, completed_utc, state,
                    endpoint_count, successful_count, stale_count, refused_count, failure_count, detail)
                VALUES ($id, $mode, $language, $started, NULL, 'partial', $count, 0, 0, 0, 0, NULL)
                RETURNING publication_order;
                """;
            command.Parameters.AddWithValue("$id", runId);
            command.Parameters.AddWithValue("$mode", gameMode);
            command.Parameters.AddWithValue("$language", language);
            command.Parameters.AddWithValue("$started", startedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$count", sourceKeys.Count);
            publicationOrder = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        foreach (var sourceKey in distinctSources)
        {
            await using var claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = """
                INSERT INTO dataset_endpoint_materializations(
                    source_key, claimed_publication_order, claimed_run_id)
                VALUES ($source, $publicationOrder, $runId)
                ON CONFLICT(source_key) DO UPDATE SET
                    claimed_publication_order = excluded.claimed_publication_order,
                    claimed_run_id = excluded.claimed_run_id
                WHERE dataset_endpoint_materializations.claimed_publication_order < excluded.claimed_publication_order;
                """;
            claim.Parameters.AddWithValue("$source", sourceKey);
            claim.Parameters.AddWithValue("$publicationOrder", publicationOrder);
            claim.Parameters.AddWithValue("$runId", runId);
            if (await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The durable publication sequence did not advance monotonically.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(runId, publicationOrder);
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

    private static async Task RequireCurrentClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceKey,
        DatasetSyncRunLease run,
        CancellationToken cancellationToken)
    {
        // RecordAsync starts a deferred transaction and otherwise has no write before this
        // check. The no-op UPDATE deliberately acquires SQLite's writer slot: a newer BeginRun
        // either commits before it (and is reported as Superseded below) or waits until this
        // publication finishes. A SELECT-first fence can instead die later as
        // SQLITE_BUSY_SNAPSHOT while upgrading its stale read transaction.
        await using (var fence = connection.CreateCommand())
        {
            fence.Transaction = transaction;
            fence.CommandText = """
                UPDATE dataset_endpoint_materializations
                SET claimed_publication_order = claimed_publication_order
                WHERE source_key = $source;
                """;
            fence.Parameters.AddWithValue("$source", sourceKey);
            await fence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var claim = await ReadClaimAsync(connection, transaction, sourceKey, cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            throw new InvalidOperationException(
                $"Refresh run '{run.RunId}' did not claim endpoint '{sourceKey}' before fetching it.");
        }

        if (claim.Value.PublicationOrder == run.PublicationOrder &&
            string.Equals(claim.Value.RunId, run.RunId, StringComparison.Ordinal))
        {
            return;
        }

        if (claim.Value.PublicationOrder < run.PublicationOrder)
        {
            throw new InvalidOperationException(
                $"Endpoint '{sourceKey}' carries durable order {claim.Value.PublicationOrder}, " +
                $"which is older than attempted order {run.PublicationOrder}.");
        }

        throw new DatasetPublicationSupersededException(
            sourceKey,
            run,
            claim.Value.PublicationOrder,
            claim.Value.RunId);
    }

    private static async Task<(long PublicationOrder, string RunId)?> ReadClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT claimed_publication_order, claimed_run_id
            FROM dataset_endpoint_materializations
            WHERE source_key = $source;
            """;
        command.Parameters.AddWithValue("$source", sourceKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private static async Task InvalidateOtherContextsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncStateEntry entry,
        CancellationToken cancellationToken)
    {
        await using (var heads = connection.CreateCommand())
        {
            heads.Transaction = transaction;
            heads.CommandText = """
                UPDATE dataset_heads
                SET visible_publication_id = NULL,
                    state = CASE
                        WHEN last_known_good_publication_id IS NULL THEN 'partial'
                        ELSE 'last_known_good'
                    END,
                    updated_utc = $updated
                WHERE source_key = $source
                  AND (game_mode <> $mode OR language <> $language);
                """;
            heads.Parameters.AddWithValue("$source", entry.SourceKey);
            heads.Parameters.AddWithValue("$mode", entry.GameMode);
            heads.Parameters.AddWithValue("$language", entry.Language);
            heads.Parameters.AddWithValue(
                "$updated",
                entry.LastAttemptUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            await heads.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var states = connection.CreateCommand();
        states.Transaction = transaction;
        states.CommandText = """
            UPDATE sync_state
            SET last_success_utc = NULL,
                content_hash = NULL,
                status = 'superseded',
                error_summary = NULL
            WHERE source_key = $source
              AND (game_mode <> $mode OR language <> $language)
              AND status IN ('current', 'stale');
            """;
        states.Parameters.AddWithValue("$source", entry.SourceKey);
        states.Parameters.AddWithValue("$mode", entry.GameMode);
        states.Parameters.AddWithValue("$language", entry.Language);
        await states.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkMaterializedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SyncStateEntry entry,
        string publicationId,
        DatasetSyncRunLease run,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dataset_endpoint_materializations
            SET materialized_publication_order = $publicationOrder,
                materialized_run_id = $runId,
                materialized_game_mode = $mode,
                materialized_language = $language,
                materialized_publication_id = $publicationId
            WHERE source_key = $source
              AND claimed_publication_order = $publicationOrder
              AND claimed_run_id = $runId;
            """;
        command.Parameters.AddWithValue("$source", entry.SourceKey);
        command.Parameters.AddWithValue("$publicationOrder", run.PublicationOrder);
        command.Parameters.AddWithValue("$runId", run.RunId);
        command.Parameters.AddWithValue("$mode", entry.GameMode);
        command.Parameters.AddWithValue("$language", entry.Language);
        command.Parameters.AddWithValue("$publicationId", publicationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) return;

        // This is a second conditional fence at the publication boundary. The transaction's
        // write lock normally makes the opening check sufficient, but this keeps a future
        // transaction-mode change from silently turning the check and update into a TOCTOU gap.
        var claim = await ReadClaimAsync(connection, transaction, entry.SourceKey, cancellationToken).ConfigureAwait(false);
        if (claim is { } actual && actual.PublicationOrder > run.PublicationOrder)
        {
            throw new DatasetPublicationSupersededException(
                entry.SourceKey,
                run,
                actual.PublicationOrder,
                actual.RunId);
        }

        throw new InvalidOperationException(
            $"Endpoint '{entry.SourceKey}' lost its durable publication claim during commit.");
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
