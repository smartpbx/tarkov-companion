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
            VALUES
            (
                'demo-graphics-card', 'Graphics Card', 'GPU', 'graphics card', 'gpu',
                'Deterministic synthetic simulator item.', 'Barter', 2, 1, 2, 125000, 1180000,
                1100000, 1260000, 1200000, 1, $now)
            ,(
                'demo-ammo-pack', 'Pack of 5.45x39 BS gs ammo', 'BS pack',
                'pack of 5 45x39 bs gs ammo', 'bs pack', 'Deterministic synthetic simulator item.',
                'AmmunitionPack', 1, 1, 1, 45000, 78000, 72000, 82000, 76000, 1, $now)
            ,(
                'demo-dorm-key', 'Dorm room 206 key', '206 key', 'dorm room 206 key', '206 key',
                'Deterministic synthetic simulator item.', 'Key', 1, 1, 1, 12000, 42000,
                38000, 47000, 41000, 1, $now)
            ,(
                'demo-iskra', 'Iskra ration pack', 'Iskra', 'iskra ration pack', 'iskra',
                'Deterministic synthetic simulator item.', 'Provision', 1, 2, 2, 15000, 25000,
                22000, 29000, 24000, 1, $now)
            ,(
                'demo-wires', 'Wires', 'Wires', 'wires', 'wires',
                'Deterministic synthetic simulator item.', 'Barter', 1, 1, 1, 4000, 16000,
                14000, 18000, 15000, 1, $now)
            ,(
                'demo-bolts', 'Bolts', 'Bolts', 'bolts', 'bolts',
                'Deterministic synthetic simulator item.', 'Barter', 1, 1, 1, 3000, 22000,
                19000, 25000, 21000, 1, $now)
            ,(
                'demo-salewa', 'Salewa first aid kit', 'Salewa', 'salewa first aid kit', 'salewa',
                'Deterministic synthetic simulator item.', 'Medicine', 1, 2, 2, 18000, 36000,
                33000, 40000, 35000, 1, $now)
            ,(
                'demo-zb-1011', 'ZB-1011 key', 'ZB-1011', 'zb 1011 key', 'zb 1011',
                'Deterministic synthetic simulator item.', 'Key', 1, 1, 1, 10000, 30000,
                28000, 34000, 29000, 1, $now)
            ,(
                'demo-zb-1012', 'ZB-1012 key', 'ZB-1012', 'zb 1012 key', 'zb 1012',
                'Deterministic synthetic simulator item.', 'Key', 1, 1, 1, 10000, 31000,
                29000, 35000, 30000, 1, $now)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                short_name = excluded.short_name,
                normalized_name = excluded.normalized_name,
                normalized_short_name = excluded.normalized_short_name,
                description = excluded.description,
                category_type = excluded.category_type,
                width = excluded.width,
                height = excluded.height,
                slots = excluded.slots,
                base_price = excluded.base_price,
                avg_24h_price = excluded.avg_24h_price,
                low_24h_price = excluded.low_24h_price,
                high_24h_price = excluded.high_24h_price,
                last_low_price = excluded.last_low_price,
                flea_eligible = excluded.flea_eligible,
                source_updated_utc = excluded.source_updated_utc;

            DELETE FROM item_search WHERE item_id LIKE 'demo-%';
            INSERT INTO item_search(item_id, name, short_name, aliases, normalized_terms)
            VALUES
                ('demo-graphics-card', 'Graphics Card', 'GPU', 'graphics processor', 'graphics card gpu'),
                ('demo-ammo-pack', 'Pack of 5.45x39 BS gs ammo', 'BS pack', 'ammo carton', 'pack 5 45x39 bs gs ammo'),
                ('demo-dorm-key', 'Dorm room 206 key', '206 key', 'dormitory key', 'dorm room 206 key'),
                ('demo-iskra', 'Iskra ration pack', 'Iskra', 'ration', 'iskra ration pack'),
                ('demo-wires', 'Wires', 'Wires', 'wire', 'wires'),
                ('demo-bolts', 'Bolts', 'Bolts', 'bolt', 'bolts'),
                ('demo-salewa', 'Salewa first aid kit', 'Salewa', 'medkit', 'salewa first aid kit'),
                ('demo-zb-1011', 'ZB-1011 key', 'ZB-1011', '', 'zb 1011 key'),
                ('demo-zb-1012', 'ZB-1012 key', 'ZB-1012', '', 'zb 1012 key');

            INSERT INTO item_sell_offers(item_id, vendor_id, vendor_name, value, currency, updated_utc)
            VALUES
                ('demo-graphics-card', 'simulator-vendor', 'Synthetic Vendor', 118000, 'RUB', $now),
                ('demo-ammo-pack', 'simulator-vendor', 'Synthetic Vendor', 30000, 'RUB', $now),
                ('demo-dorm-key', 'simulator-vendor', 'Synthetic Vendor', 12000, 'RUB', $now),
                ('demo-iskra', 'simulator-vendor', 'Synthetic Vendor', 15000, 'RUB', $now),
                ('demo-wires', 'simulator-vendor', 'Synthetic Vendor', 9000, 'RUB', $now),
                ('demo-bolts', 'simulator-vendor', 'Synthetic Vendor', 11000, 'RUB', $now),
                ('demo-salewa', 'simulator-vendor', 'Synthetic Vendor', 18000, 'RUB', $now),
                ('demo-zb-1011', 'simulator-vendor', 'Synthetic Vendor', 10000, 'RUB', $now),
                ('demo-zb-1012', 'simulator-vendor', 'Synthetic Vendor', 10000, 'RUB', $now)
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
