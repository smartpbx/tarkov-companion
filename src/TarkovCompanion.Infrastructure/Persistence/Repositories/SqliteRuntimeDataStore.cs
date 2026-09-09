using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed class SqliteRuntimeDataStore(
    SqliteDatabaseOptions options,
    SqliteConnectionFactory connectionFactory,
    SqliteMigrationRunner migrationRunner,
    TimeProvider? timeProvider = null) : IRuntimeDataStore
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public string DatabasePath => options.DatabasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken) =>
        _ = await migrationRunner.ApplyAsync(cancellationToken).ConfigureAwait(false);

    public async Task SeedDemoAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(connection, (SqliteTransaction)transaction, """
            INSERT INTO items(
                id, name, short_name, normalized_name, normalized_short_name, description,
                category_type, width, height, slots, base_price, avg_24h_price, low_24h_price,
                high_24h_price, last_low_price, flea_eligible, source_updated_utc)
            VALUES (
                'demo-graphics-card', 'Graphics Card', 'GPU', 'graphics card', 'gpu',
                'Deterministic demo fixture item.', 'Barter', 2, 1, 2, 125000, 1180000,
                1100000, 1260000, 1200000, 1, $now)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                short_name = excluded.short_name,
                normalized_name = excluded.normalized_name,
                normalized_short_name = excluded.normalized_short_name,
                last_low_price = excluded.last_low_price,
                source_updated_utc = excluded.source_updated_utc;

            DELETE FROM item_search WHERE item_id = 'demo-graphics-card';
            INSERT INTO item_search(item_id, name, short_name, aliases, normalized_terms)
            VALUES ('demo-graphics-card', 'Graphics Card', 'GPU', 'graphics processor', 'graphics card gpu');

            INSERT INTO item_sell_offers(item_id, vendor_id, vendor_name, value, currency, updated_utc)
            VALUES ('demo-graphics-card', 'mechanic', 'Mechanic', 118000, 'RUB', $now)
            ON CONFLICT(item_id, vendor_id, currency) DO UPDATE SET
                value = excluded.value,
                updated_utc = excluded.updated_utc;

            INSERT INTO sync_state(
                source_key, game_mode, language, last_success_utc, last_attempt_utc,
                status, error_summary)
            VALUES ('demo-fixture', 'regular', 'en', $now, $now, 'demo-fixture', NULL)
            ON CONFLICT(source_key, game_mode, language) DO UPDATE SET
                last_success_utc = excluded.last_success_utc,
                last_attempt_utc = excluded.last_attempt_utc,
                status = excluded.status,
                error_summary = NULL;
            """, now, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CachedDataSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM items),
                (SELECT COUNT(*) FROM sync_state WHERE last_success_utc IS NOT NULL),
                (SELECT MAX(last_success_utc) FROM sync_state),
                (SELECT error_summary FROM sync_state WHERE error_summary IS NOT NULL ORDER BY last_attempt_utc DESC LIMIT 1);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2)
                ? null
                : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
