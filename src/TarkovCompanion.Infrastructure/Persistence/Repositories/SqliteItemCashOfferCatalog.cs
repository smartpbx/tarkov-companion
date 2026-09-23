using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Intel;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>Reads item purchase offers from the retained json.tarkov.dev item payload.</summary>
/// <remarks>
/// These fields are not columns: <c>buyFromTrader</c>, <c>minTraderLevel</c> and
/// <c>taskUnlock</c> exist only in <c>items.raw_json</c>. A malformed offer is skipped rather
/// than turned into an apparently available purchase.
/// </remarks>
public sealed class SqliteItemCashOfferCatalog(SqliteConnectionFactory connectionFactory) : IItemCashOfferCatalog
{
    public async Task<IReadOnlyList<ItemCashOffer>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, raw_json FROM items WHERE raw_json IS NOT NULL;";

        var offers = new List<ItemCashOffer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ReadOffers(reader.GetString(0), reader.GetString(1), offers);
        }

        return offers;
    }

    internal static IReadOnlyList<ItemCashOffer> ReadOffers(string itemId, string payload)
    {
        var offers = new List<ItemCashOffer>();
        ReadOffers(itemId, payload, offers);
        return offers;
    }

    private static void ReadOffers(string itemId, string payload, List<ItemCashOffer> offers)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("buyFromTrader", out var rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("trader", out var trader) || trader.ValueKind != JsonValueKind.String ||
                    ReadRoublePrice(row) is not { } roubles || roubles <= 0 ||
                    trader.GetString() is not { Length: > 0 } traderId)
                {
                    continue;
                }

                int? level = row.TryGetProperty("minTraderLevel", out var statedLevel) &&
                    statedLevel.ValueKind == JsonValueKind.Number && statedLevel.TryGetInt32(out var value) && value > 0
                    ? value
                    : null;
                offers.Add(new(itemId, traderId, roubles, level, ReadTaskId(row)));
            }
        }
        catch (JsonException)
        {
        }
    }

    private static long? ReadRoublePrice(JsonElement row)
    {
        if (row.TryGetProperty("priceRUB", out var converted) && converted.TryGetInt64(out var roubles))
        {
            return roubles;
        }

        return row.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String &&
               string.Equals(currency.GetString(), "RUB", StringComparison.OrdinalIgnoreCase) &&
               row.TryGetProperty("price", out var price) && price.TryGetInt64(out roubles)
            ? roubles
            : null;
    }

    internal static string? ReadTaskId(JsonElement row)
    {
        if (!row.TryGetProperty("taskUnlock", out var task))
        {
            return null;
        }

        return task.ValueKind switch
        {
            JsonValueKind.String => task.GetString(),
            JsonValueKind.Object when task.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String => id.GetString(),
            _ => null,
        };
    }
}
