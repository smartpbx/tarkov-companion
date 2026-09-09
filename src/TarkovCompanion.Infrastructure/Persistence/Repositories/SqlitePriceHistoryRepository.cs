using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed class SqlitePriceHistoryRepository(SqliteConnectionFactory connectionFactory) : IPriceHistoryStore
{
    public async Task<IReadOnlyList<PriceHistoryPoint>> GetAsync(
        string itemId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT timestamp_utc, flea_price, trader_value, source
            FROM price_history
            WHERE item_id = $itemId AND timestamp_utc >= $sinceUtc
            ORDER BY timestamp_utc;
            """;
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue("$sinceUtc", sinceUtc.ToString("O", CultureInfo.InvariantCulture));

        var points = new List<PriceHistoryPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            points.Add(new(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3)));
        }

        return points;
    }
}
