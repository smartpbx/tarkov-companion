using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Catalogs;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads the barters every sync has stored and nothing has ever read.
/// </summary>
/// <remarks>
/// <para>
/// 789 rows rewritten on every sync, across three tables, with no reader anywhere. They were
/// hidden from <c>sweep-unread.sh</c> by its DELETE blind spot: the refresh clears each table
/// before repopulating it, and "DELETE FROM barters" contains "FROM barters", so the sweep
/// counted the clear as a read.
/// </para>
/// <para>
/// Three queries rather than one join. A barter has one output and several inputs, so a single
/// join would return the output once per input and every count would have to be de-duplicated
/// back out again — which is the shape of arithmetic that quietly double-counts.
/// </para>
/// <para>
/// <c>minTraderLevel</c> and <c>taskUnlock</c> come out of the stored payload, because the
/// normaliser does not lift them into columns. They land in <c>source_json</c> through
/// <c>JsonExtensionData</c>, which is the one place they exist.
/// </para>
/// </remarks>
public sealed class SqliteBarterCatalog(SqliteConnectionFactory connectionFactory) : IBarterCatalog
{
    public async Task<IReadOnlyList<BarterOffer>> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var offers = await ReadOffersAsync(connection, cancellationToken).ConfigureAwait(false);
        if (offers.Count == 0)
        {
            return [];
        }

        var gives = await ReadItemsAsync(
            connection,
            "SELECT barter_id, item_id, count FROM barter_outputs;",
            cancellationToken).ConfigureAwait(false);
        var wants = await ReadItemsAsync(
            connection,
            "SELECT barter_id, item_id, count FROM barter_requirements WHERE item_id IS NOT NULL;",
            cancellationToken).ConfigureAwait(false);

        var built = new List<BarterOffer>(offers.Count);
        foreach (var (barterId, traderId, minimumLevel, taskUnlock) in offers)
        {
            // A barter with no recorded output is not a barter anybody can be told about. The
            // feed always states one; a row without it is a row that failed to store.
            if (!gives.TryGetValue(barterId, out var output) || output.Count == 0)
            {
                continue;
            }

            built.Add(new(
                barterId,
                traderId,
                minimumLevel,
                taskUnlock,
                output[0],
                wants.TryGetValue(barterId, out var inputs) ? inputs : []));
        }

        return built;
    }

    private static async Task<List<(string Id, string? TraderId, int? MinimumLevel, string? TaskUnlock)>> ReadOffersAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, trader_id, source_json FROM barters;";

        var offers = new List<(string, string?, int?, string?)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var (minimumLevel, taskUnlock) = reader.IsDBNull(2)
                ? (null, null)
                : ReadUnlocks(reader.GetString(2));
            offers.Add((id, reader.IsDBNull(1) ? null : reader.GetString(1), minimumLevel, taskUnlock));
        }

        return offers;
    }

    /// <summary>
    /// Pulls the two fields the normaliser does not lift out of the stored payload.
    /// </summary>
    /// <remarks>
    /// A payload that will not parse costs this barter its loyalty requirement, not the whole
    /// barter. The failure direction matters: without a requirement the trade is treated as one
    /// anybody can take, which is what the feed means when it omits the field — so an
    /// unreadable payload degrades to the feed's own default rather than to a hidden trade.
    /// </remarks>
    private static (int? MinimumLevel, string? TaskUnlock) ReadUnlocks(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            int? level = root.TryGetProperty("minTraderLevel", out var stated) &&
                stated.ValueKind == JsonValueKind.Number &&
                stated.TryGetInt32(out var value)
                ? value
                : null;
            var unlock = root.TryGetProperty("taskUnlock", out var task)
                ? task.ValueKind switch
                {
                    JsonValueKind.String => task.GetString(),
                    JsonValueKind.Object when task.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String => id.GetString(),
                    _ => null,
                }
                : null;
            return (level, unlock);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static async Task<Dictionary<string, List<BarterItem>>> ReadItemsAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var items = new Dictionary<string, List<BarterItem>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(1))
            {
                continue;
            }

            var barterId = reader.GetString(0);
            var itemId = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(barterId) || string.IsNullOrWhiteSpace(itemId))
            {
                continue;
            }

            // A count the feed left out is one, not none. Zero would make the item free, and
            // free inputs are how a barter becomes the cheapest route to everything.
            var count = reader.IsDBNull(2) ? 1 : Math.Max(1, reader.GetInt32(2));
            if (!items.TryGetValue(barterId, out var list))
            {
                items[barterId] = list = [];
            }

            list.Add(new(itemId, count));
        }

        return items;
    }
}
