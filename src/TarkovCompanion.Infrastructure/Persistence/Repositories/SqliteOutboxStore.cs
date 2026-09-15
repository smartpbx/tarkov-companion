using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Execution;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>Durable, bounded, aggregate-ordered implementation of the frozen runtime outbox.</summary>
public sealed class SqliteOutboxStore(
    SqliteConnectionFactory connectionFactory,
    int capacity = 10_000,
    int completedRetention = 10_000) : IOutboxStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly int _capacity = capacity is >= 1 and <= 100_000 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly int _completedRetention = completedRetention is >= 0 and <= 100_000 ? completedRetention : throw new ArgumentOutOfRangeException(nameof(completedRetention));

    public async Task<OutboxEnqueueReceipt> EnqueueAsync(OutboxItem item, CancellationToken cancellationToken) =>
        (await EnqueueBatchAsync([item], cancellationToken).ConfigureAwait(false))[0];

    public async Task<ImmutableArray<OutboxEnqueueReceipt>> EnqueueBatchAsync(
        ImmutableArray<OutboxItem> items,
        CancellationToken cancellationToken)
    {
        if (items.IsDefaultOrEmpty || items.Any(item => item is null)) throw new ArgumentException("At least one non-null item is required.", nameof(items));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var receipts = new OutboxEnqueueReceipt[items.Length];
        var additions = new List<OutboxItem>(items.Length);
        var batchKeys = new Dictionary<IdempotencyKey, OperationId>();
        var operations = new HashSet<OperationId>();
        var sequences = new HashSet<(OutboxAggregateId, long)>();
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var duplicate = await FindByIdempotencyAsync(connection, transaction, item.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (duplicate is { } existing || batchKeys.TryGetValue(item.IdempotencyKey, out existing))
            {
                receipts[index] = new(false, existing);
                continue;
            }

            if (!operations.Add(item.OperationId) || await OperationExistsAsync(connection, transaction, item.OperationId, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("An outbox operation id may be enqueued only once.");
            if (!sequences.Add((item.AggregateId, item.AggregateSequence)) || await SequenceExistsAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("An aggregate sequence may be enqueued only once.");
            batchKeys.Add(item.IdempotencyKey, item.OperationId);
            additions.Add(item);
            receipts[index] = new(true, item.OperationId);
        }

        var active = await ScalarIntAsync(connection, transaction,
            "SELECT COUNT(*) FROM durable_outbox WHERE delivery_state IN (1, 2, 3);", cancellationToken).ConfigureAwait(false);
        if (active + additions.Count > _capacity) throw new OutboxCapacityException();
        foreach (var item in additions)
        {
            await InsertAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
            await using var sequence = connection.CreateCommand();
            sequence.Transaction = transaction;
            sequence.CommandText = """
                INSERT INTO outbox_aggregate_sequences(aggregate_id, next_sequence) VALUES ($aggregate, $next)
                ON CONFLICT(aggregate_id) DO UPDATE SET next_sequence = MAX(next_sequence, excluded.next_sequence);
                """;
            sequence.Parameters.AddWithValue("$aggregate", item.AggregateId.Value);
            sequence.Parameters.AddWithValue("$next", checked(item.AggregateSequence + 1));
            await sequence.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return [.. receipts];
    }

    public async Task<ImmutableArray<OutboxStoredItem>> LeaseNextAsync(
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > OperationPolicy.MaximumDuration) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        if (maximumCount is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        nowUtc = nowUtc.ToUniversalTime();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await RecoverExpiredAsync(connection, transaction, nowUtc, cancellationToken).ConfigureAwait(false);
        await ExpireOutstandingAsync(connection, transaction, nowUtc, cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT candidate.operation_id
                FROM durable_outbox AS candidate
                WHERE candidate.delivery_state IN (1, 3)
                  AND candidate.next_attempt_utc <= $now AND candidate.expires_utc > $now
                  AND NOT EXISTS (
                      SELECT 1 FROM durable_outbox AS predecessor
                      WHERE predecessor.aggregate_id = candidate.aggregate_id
                        AND predecessor.aggregate_sequence < candidate.aggregate_sequence
                        AND predecessor.delivery_state <> 5)
                ORDER BY candidate.next_attempt_utc, candidate.created_utc, candidate.aggregate_id
                LIMIT $maximum;
                """;
            command.Parameters.AddWithValue("$now", Format(nowUtc));
            command.Parameters.AddWithValue("$maximum", maximumCount);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        }

        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE durable_outbox SET delivery_state = 2, attempt_count = attempt_count + 1,
                    lease_token = $lease, lease_expires_utc = $expires
                WHERE operation_id = $id AND delivery_state IN (1, 3);
                """;
            command.Parameters.AddWithValue("$lease", OutboxLeaseToken.New().Value.ToString("D"));
            command.Parameters.AddWithValue("$expires", Format(AddBounded(nowUtc, leaseDuration)));
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var leased = await ReadManyAsync(connection, transaction, ids, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return leased;
    }

    public Task<bool> CompleteAsync(OperationId operationId, OutboxLeaseToken leaseToken, DateTimeOffset completedUtc, CancellationToken cancellationToken)
    {
        if (!leaseToken.IsDefined) throw new ArgumentException("A lease token is required.", nameof(leaseToken));
        return MutateLeaseAsync(operationId, leaseToken, """
            delivery_state = 5, completed_utc = $time, dead_lettered_utc = NULL,
            lease_token = NULL, lease_expires_utc = NULL, last_fault_json = NULL
            """, completedUtc, null, cancellationToken, pruneCompleted: true, requireBeforeExpiry: false);
    }

    /// <summary>Extends only the lease held by the supplied fenced owner token.</summary>
    public async Task<bool> RenewLeaseAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        DateTimeOffset nowUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (!leaseToken.IsDefined) throw new ArgumentException("A lease token is required.", nameof(leaseToken));
        if (leaseDuration <= TimeSpan.Zero || leaseDuration > OperationPolicy.MaximumDuration) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE durable_outbox SET lease_expires_utc = $expires
            WHERE operation_id = $id AND delivery_state = 2 AND lease_token = $lease;
            """;
        command.Parameters.AddWithValue("$expires", Format(AddBounded(nowUtc.ToUniversalTime(), leaseDuration)));
        command.Parameters.AddWithValue("$id", operationId.ToString());
        command.Parameters.AddWithValue("$lease", leaseToken.Value.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public Task<bool> RetryAsync(OperationId operationId, OutboxLeaseToken leaseToken, DateTimeOffset notBeforeUtc, RuntimeFault fault, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (!leaseToken.IsDefined) throw new ArgumentException("A lease token is required.", nameof(leaseToken));
        return MutateLeaseAsync(operationId, leaseToken, """
            delivery_state = 3, next_attempt_utc = $time, lease_token = NULL,
            lease_expires_utc = NULL, last_fault_json = $fault
            """, notBeforeUtc, fault, cancellationToken, requireBeforeExpiry: true);
    }

    public Task<bool> DeadLetterAsync(OperationId operationId, OutboxLeaseToken leaseToken, RuntimeFault fault, DateTimeOffset deadLetteredUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fault);
        if (!leaseToken.IsDefined) throw new ArgumentException("A lease token is required.", nameof(leaseToken));
        return MutateLeaseAsync(operationId, leaseToken, """
            delivery_state = 4, dead_lettered_utc = $time, completed_utc = NULL,
            lease_token = NULL, lease_expires_utc = NULL, last_fault_json = $fault
            """, deadLetteredUtc, fault, cancellationToken, requireBeforeExpiry: false);
    }

    public async Task<int> RecoverExpiredLeasesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var count = await RecoverExpiredAsync(connection, transaction, nowUtc.ToUniversalTime(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public async Task<bool> ManualRetryAsync(OperationId operationId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        nowUtc = nowUtc.ToUniversalTime();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await ScalarIntAsync(connection, transaction, "SELECT COUNT(*) FROM durable_outbox WHERE delivery_state IN (1, 2, 3);", cancellationToken).ConfigureAwait(false) >= _capacity)
            return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE durable_outbox SET delivery_state = 3, attempt_count = 0, next_attempt_utc = $now,
                completed_utc = NULL, dead_lettered_utc = NULL, last_fault_json = NULL
            WHERE operation_id = $id AND delivery_state = 4 AND expires_utc > $now;
            """;
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        command.Parameters.AddWithValue("$id", operationId.ToString());
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <summary>Records an operator's terminal decision and releases the aggregate head.</summary>
    public async Task<bool> ResolveDeadLetterAsync(
        OperationId operationId,
        DateTimeOffset resolvedUtc,
        CancellationToken cancellationToken)
    {
        resolvedUtc = resolvedUtc.ToUniversalTime();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var bounds = connection.CreateCommand())
        {
            bounds.Transaction = transaction;
            bounds.CommandText = "SELECT created_utc FROM durable_outbox WHERE operation_id = $id AND delivery_state = 4;";
            bounds.Parameters.AddWithValue("$id", operationId.ToString());
            var created = await bounds.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (created is not string createdText) return false;
            if (resolvedUtc < Parse(createdText)) throw new ArgumentOutOfRangeException(nameof(resolvedUtc));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE durable_outbox SET delivery_state = 5, completed_utc = $resolved,
                dead_lettered_utc = NULL, lease_token = NULL, lease_expires_utc = NULL
            WHERE operation_id = $id AND delivery_state = 4 AND created_utc <= $resolved;
            """;
        command.Parameters.AddWithValue("$resolved", Format(resolvedUtc));
        command.Parameters.AddWithValue("$id", operationId.ToString());
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed) await PruneCompletedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<OutboxSnapshot> GetSnapshotAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        nowUtc = nowUtc.ToUniversalTime();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var counts = new int[5];
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT delivery_state, COUNT(*) FROM durable_outbox GROUP BY delivery_state;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) counts[reader.GetInt32(0) - 1] = reader.GetInt32(1);
        }

        DateTimeOffset? oldest = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT MIN(created_utc) FROM durable_outbox WHERE delivery_state <> 5;";
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is string text) oldest = Parse(text);
        }

        var dead = ImmutableArray.CreateBuilder<OutboxDeadLetterSnapshot>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT operation_id, aggregate_id, aggregate_sequence, command_kind, attempt_count,
                       last_fault_json, dead_lettered_utc
                FROM durable_outbox WHERE delivery_state = 4
                ORDER BY dead_lettered_utc, aggregate_id, aggregate_sequence LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", OutboxSnapshot.MaxListedDeadLetters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                dead.Add(new(new(Guid.Parse(reader.GetString(0))), new(reader.GetString(1)), reader.GetInt64(2),
                    (OutboxCommandKind)reader.GetInt32(3), reader.GetInt32(4), Fault(reader, 5), Time(reader, 6)));
        }

        var age = oldest is null ? null : oldest > nowUtc ? TimeSpan.Zero : nowUtc - oldest;
        return new(new(counts[0], counts[1], counts[2], counts[3], counts[4]), age) { DeadLetters = dead.ToImmutable() };
    }

    public async Task<ImmutableArray<OutboxStoredItem>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadManyAsync(connection, null, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> MutateLeaseAsync(
        OperationId operationId,
        OutboxLeaseToken leaseToken,
        string setSql,
        DateTimeOffset time,
        RuntimeFault? fault,
        CancellationToken cancellationToken,
        bool pruneCompleted = false,
        bool requireBeforeExpiry = false)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        time = time.ToUniversalTime();
        await using (var bounds = connection.CreateCommand())
        {
            bounds.Transaction = transaction;
            bounds.CommandText = """
                SELECT created_utc, expires_utc FROM durable_outbox
                WHERE operation_id = $id AND delivery_state = 2 AND lease_token = $lease;
                """;
            bounds.Parameters.AddWithValue("$id", operationId.ToString());
            bounds.Parameters.AddWithValue("$lease", leaseToken.Value.ToString("D"));
            await using var reader = await bounds.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return false;
            var created = Parse(reader.GetString(0));
            var expires = Parse(reader.GetString(1));
            if (time < created || requireBeforeExpiry && time >= expires) throw new ArgumentOutOfRangeException(nameof(time));
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE durable_outbox SET {setSql} WHERE operation_id = $id AND delivery_state = 2 AND lease_token = $lease;";
        command.Parameters.AddWithValue("$id", operationId.ToString());
        command.Parameters.AddWithValue("$lease", leaseToken.Value.ToString("D"));
        command.Parameters.AddWithValue("$time", Format(time));
        command.Parameters.AddWithValue("$fault", fault is null ? DBNull.Value : JsonSerializer.Serialize(fault, JsonOptions));
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed && pruneCompleted) await PruneCompletedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private static async Task<int> RecoverExpiredAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, int Attempts, int MaxAttempts, string Expires)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT operation_id, attempt_count, max_attempts, expires_utc FROM durable_outbox WHERE delivery_state = 2 AND lease_expires_utc <= $now;";
            select.Parameters.AddWithValue("$now", Format(nowUtc));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3)));
        }

        foreach (var row in rows)
        {
            var exhausted = row.Attempts >= row.MaxAttempts || Parse(row.Expires) <= nowUtc;
            var fault = LeaseFault(new(Guid.Parse(row.Id)), exhausted, nowUtc);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE durable_outbox SET delivery_state = $state, next_attempt_utc = $now,
                    lease_token = NULL, lease_expires_utc = NULL, last_fault_json = $fault,
                    dead_lettered_utc = $dead
                WHERE operation_id = $id AND delivery_state = 2;
                """;
            update.Parameters.AddWithValue("$state", exhausted ? 4 : 3);
            update.Parameters.AddWithValue("$now", Format(nowUtc));
            update.Parameters.AddWithValue("$fault", JsonSerializer.Serialize(fault, JsonOptions));
            update.Parameters.AddWithValue("$dead", exhausted ? Format(nowUtc) : DBNull.Value);
            update.Parameters.AddWithValue("$id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return rows.Count;
    }

    private static async Task ExpireOutstandingAsync(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT operation_id FROM durable_outbox WHERE delivery_state IN (1, 3) AND expires_utc <= $now;";
            select.Parameters.AddWithValue("$now", Format(nowUtc));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        }
        foreach (var id in ids)
        {
            var operation = new OperationId(Guid.Parse(id));
            var fault = new RuntimeFault(RuntimeFailureKind.Validation, new("outbox-item-expired"), RuntimeRecoveryAction.None, new($"operation:{operation}"), nowUtc);
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE durable_outbox SET delivery_state = 4, dead_lettered_utc = $now, last_fault_json = $fault WHERE operation_id = $id;";
            update.Parameters.AddWithValue("$now", Format(nowUtc));
            update.Parameters.AddWithValue("$fault", JsonSerializer.Serialize(fault, JsonOptions));
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PruneCompletedAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM durable_outbox WHERE operation_id IN (
                SELECT operation_id FROM durable_outbox WHERE delivery_state = 5
                ORDER BY completed_utc DESC, aggregate_id DESC, aggregate_sequence DESC
                LIMIT -1 OFFSET $retain);
            """;
        command.Parameters.AddWithValue("$retain", _completedRetention);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, OutboxItem item, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO durable_outbox(
                operation_id, idempotency_key, correlation_id, feature_id, command_kind,
                version_major, version_minor, aggregate_id, aggregate_sequence, created_utc,
                not_before_utc, expires_utc, payload, max_attempts, attempt_timeout_ticks,
                initial_retry_delay_ticks, max_retry_delay_ticks, backoff_factor, delivery_state,
                attempt_count, next_attempt_utc)
            VALUES ($operation, $idempotency, $correlation, $feature, $command, $major, $minor,
                $aggregate, $sequence, $created, $notBefore, $expires, $payload, $maxAttempts,
                $attemptTimeout, $initialDelay, $maxDelay, $backoff, 1, 0, $notBefore);
            """;
        command.Parameters.AddWithValue("$operation", item.OperationId.ToString());
        command.Parameters.AddWithValue("$idempotency", item.IdempotencyKey.Value);
        command.Parameters.AddWithValue("$correlation", item.CorrelationId.ToString());
        command.Parameters.AddWithValue("$feature", item.FeatureId.Value);
        command.Parameters.AddWithValue("$command", (int)item.Command);
        command.Parameters.AddWithValue("$major", item.Version.Major);
        command.Parameters.AddWithValue("$minor", item.Version.Minor);
        command.Parameters.AddWithValue("$aggregate", item.AggregateId.Value);
        command.Parameters.AddWithValue("$sequence", item.AggregateSequence);
        command.Parameters.AddWithValue("$created", Format(item.CreatedUtc));
        command.Parameters.AddWithValue("$notBefore", Format(item.NotBeforeUtc));
        command.Parameters.AddWithValue("$expires", Format(item.ExpiresUtc));
        command.Parameters.AddWithValue("$payload", item.Payload.Bytes.ToArray());
        command.Parameters.AddWithValue("$maxAttempts", item.AttemptPolicy.MaxAttempts);
        command.Parameters.AddWithValue("$attemptTimeout", item.AttemptPolicy.AttemptTimeout.Ticks);
        command.Parameters.AddWithValue("$initialDelay", item.AttemptPolicy.InitialRetryDelay.Ticks);
        command.Parameters.AddWithValue("$maxDelay", item.AttemptPolicy.MaxRetryDelay.Ticks);
        command.Parameters.AddWithValue("$backoff", item.AttemptPolicy.BackoffFactor);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ImmutableArray<OutboxStoredItem>> ReadManyAsync(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<string>? ids, CancellationToken cancellationToken)
    {
        if (ids is { Count: 0 }) return [];
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT operation_id, idempotency_key, correlation_id, feature_id, command_kind,
                version_major, version_minor, aggregate_id, aggregate_sequence, created_utc,
                not_before_utc, expires_utc, payload, max_attempts, attempt_timeout_ticks,
                initial_retry_delay_ticks, max_retry_delay_ticks, backoff_factor, delivery_state,
                attempt_count, next_attempt_utc, lease_token, lease_expires_utc, last_fault_json,
                COALESCE(completed_utc, dead_lettered_utc)
            FROM durable_outbox
            """ + (ids is null ? " ORDER BY aggregate_id, aggregate_sequence;" : $" WHERE operation_id IN ({string.Join(',', ids.Select((_, index) => $"$id{index}"))}) ORDER BY aggregate_id, aggregate_sequence;");
        if (ids is not null) for (var index = 0; index < ids.Count; index++) command.Parameters.AddWithValue($"$id{index}", ids[index]);
        var result = ImmutableArray.CreateBuilder<OutboxStoredItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = new OutboxItem(
                new(Guid.Parse(reader.GetString(0))), new(reader.GetString(1)), new(Guid.Parse(reader.GetString(2))),
                new(reader.GetString(3)), (OutboxCommandKind)reader.GetInt32(4), new(reader.GetInt32(5), reader.GetInt32(6)),
                new(reader.GetString(7)), reader.GetInt64(8), Parse(reader.GetString(9)), Parse(reader.GetString(10)),
                Parse(reader.GetString(11)), OutboxPayload.FromStoredBytes((byte[])reader[12]),
                new(reader.GetInt32(13), TimeSpan.FromTicks(reader.GetInt64(14)), TimeSpan.FromTicks(reader.GetInt64(15)),
                    TimeSpan.FromTicks(reader.GetInt64(16)), reader.GetDouble(17)));
            result.Add(new(item, (OutboxDeliveryState)reader.GetInt32(18), reader.GetInt32(19), Parse(reader.GetString(20)),
                reader.IsDBNull(21) ? null : new OutboxLeaseToken(Guid.Parse(reader.GetString(21))), Time(reader, 22), Fault(reader, 23), Time(reader, 24)));
        }
        return result.ToImmutable();
    }

    private static async Task<OperationId?> FindByIdempotencyAsync(SqliteConnection connection, SqliteTransaction transaction, IdempotencyKey key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT operation_id FROM durable_outbox WHERE idempotency_key = $key;"; command.Parameters.AddWithValue("$key", key.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string value ? new(Guid.Parse(value)) : null;
    }
    private static async Task<bool> OperationExistsAsync(SqliteConnection connection, SqliteTransaction transaction, OperationId id, CancellationToken cancellationToken) =>
        await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM durable_outbox WHERE operation_id = $one);", id.ToString(), null, cancellationToken).ConfigureAwait(false);
    private static async Task<bool> SequenceExistsAsync(SqliteConnection connection, SqliteTransaction transaction, OutboxItem item, CancellationToken cancellationToken) =>
        await ExistsAsync(connection, transaction, "SELECT EXISTS(SELECT 1 FROM durable_outbox WHERE aggregate_id = $one AND aggregate_sequence = $two);", item.AggregateId.Value, item.AggregateSequence, cancellationToken).ConfigureAwait(false);
    private static async Task<bool> ExistsAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, object one, object? two, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$one", one); if (two is not null) command.Parameters.AddWithValue("$two", two);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }
    private static async Task<int> ScalarIntAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
    private static RuntimeFault LeaseFault(OperationId id, bool exhausted, DateTimeOffset now) => new(
        exhausted ? RuntimeFailureKind.Timeout : RuntimeFailureKind.Transient,
        new(exhausted ? "outbox-lease-attempts-exhausted" : "outbox-lease-expired"),
        exhausted ? RuntimeRecoveryAction.RetryManually : RuntimeRecoveryAction.RetryAutomatically,
        new($"operation:{id}"), now);
    private static RuntimeFault? Fault(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : JsonSerializer.Deserialize<RuntimeFault>(reader.GetString(ordinal), JsonOptions);
    private static DateTimeOffset? Time(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Parse(reader.GetString(ordinal));
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset AddBounded(DateTimeOffset value, TimeSpan duration) { try { return value + duration; } catch (ArgumentOutOfRangeException) { return DateTimeOffset.MaxValue; } }
}
