using TarkovCompanion.Application.Services.Intel;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads the catalog's own top items by worth, for the Intel landing page.
/// </summary>
/// <remarks>
/// One indexed query over <c>items</c>, the same fallback chain (flea's last low price, then the
/// 24-hour average, then the base price) <c>SqliteItemFactCatalog</c> uses for a key's acquisition
/// cost. No trader-offer join: the items that dominate this list — Bitcoin, graphics cards, LEDX,
/// the high-end keycards — all carry a flea price, so ranking by it alone puts the same items at
/// the top a trader-aware ranking would, without a second join over every item in the catalog.
/// </remarks>
public sealed class SqliteHighValueItemCatalog(SqliteConnectionFactory connectionFactory) : IHighValueItemCatalog
{
    public async Task<IReadOnlyList<HighValueItemSummary>> GetTopAsync(int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,
                   MAX(COALESCE(last_low_price, 0), COALESCE(avg_24h_price, 0), COALESCE(base_price, 0)) AS value
            FROM items
            WHERE flea_eligible = 1
            ORDER BY value DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<HighValueItemSummary>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var value = reader.GetInt64(1);
            if (value <= 0)
            {
                continue;
            }

            rows.Add(new(reader.GetString(0), value));
        }

        return rows;
    }
}
