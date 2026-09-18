using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

public sealed class SqliteItemRepository(SqliteConnectionFactory connectionFactory) : IItemRepository
{
    /// <summary>
    /// The least a candidate may score to be a hit.
    /// </summary>
    /// <remarks>
    /// Measured against the 5,321-item catalog: real matches (an exact name, a whole-word
    /// containment, a typo of a name) score 0.7 or more, while edit distance alone puts any name
    /// that shares a few letters at 0.45 to 0.5 — "salewa" matched "Weather station safe key" at
    /// 0.5, and a search for "graphics card" listed six crates and plates. 0.6 keeps a typo of a
    /// short name ("salwa", 0.83) and drops that noise.
    /// </remarks>
    private const double MinimumScore = 0.6;

    internal const string ExactItemSql = """
        SELECT id, name, short_name, description, category_type, width, height, flea_eligible,
               icon_url, image_url, wiki_url, properties_type, properties_json, source_updated_utc
        FROM items
        WHERE id = $itemId;
        """;

    public async Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetAsync(connection, itemId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ItemSearchHit>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var normalized = TextNormalizer.Normalize(query);
        if (normalized.Length == 0)
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await LoadCandidatesAsync(connection, normalized, cancellationToken).ConfigureAwait(false);
        var selected = candidates.Values
            .Where(candidate => candidate.Score >= MinimumScore)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();

        var results = new List<ItemSearchHit>(selected.Length);
        foreach (var candidate in selected)
        {
            var item = await GetAsync(connection, candidate.Id, cancellationToken).ConfigureAwait(false);
            if (item is not null)
            {
                results.Add(new(item, candidate.Score, candidate.MatchedText));
            }
        }

        return results;
    }

    public async Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_low_price, avg_24h_price, low_24h_price, high_24h_price, source_updated_utc
            FROM items
            WHERE id = $itemId;
            """;
        command.Parameters.AddWithValue("$itemId", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        long? fleaPrice = reader.IsDBNull(0) ? null : reader.GetInt64(0);
        long? averagePrice = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        long? lowPrice = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        long? highPrice = reader.IsDBNull(3) ? null : reader.GetInt64(3);
        var sourceUpdatedUtc = ParseTimestamp(reader.GetString(4));
        await reader.DisposeAsync().ConfigureAwait(false);

        var offers = await LoadOffersAsync(connection, itemId, sourceUpdatedUtc, cancellationToken).ConfigureAwait(false);
        return new(
            fleaPrice,
            offers,
            averagePrice,
            lowPrice,
            highPrice,
            Provenance(sourceUpdatedUtc));
    }

    private static async Task<ItemDefinition?> GetAsync(
        SqliteConnection connection,
        string itemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = ExactItemSql;
        command.Parameters.AddWithValue("$itemId", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var row = new ItemRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseCategory(reader.GetString(4)),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetBoolean(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            ParseTimestamp(reader.GetString(13)));
        await reader.DisposeAsync().ConfigureAwait(false);

        var categories = await LoadCategoriesAsync(connection, itemId, cancellationToken).ConfigureAwait(false);
        return new(
            row.Id,
            row.Name,
            row.ShortName,
            row.Description,
            row.Category,
            new(row.Width, row.Height),
            row.FleaEligible,
            row.IconUri,
            row.ImageUri,
            row.WikiUri,
            row.PropertiesType,
            row.PropertiesJson,
            categories,
            Provenance(row.SourceUpdatedUtc));
    }

    private static async Task<Dictionary<string, SearchCandidate>> LoadCandidatesAsync(
        SqliteConnection connection,
        string normalizedQuery,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, SearchCandidate>(StringComparer.Ordinal);
        await using (var exact = connection.CreateCommand())
        {
            exact.CommandText = """
                SELECT id, name, short_name, normalized_name, normalized_short_name
                FROM items
                WHERE normalized_name = $query OR normalized_short_name = $query;
                """;
            exact.Parameters.AddWithValue("$query", normalizedQuery);
            await using var reader = await exact.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var matchesName = reader.GetString(3) == normalizedQuery;
                var score = matchesName ? 1.0 : 0.99;
                candidates[reader.GetString(0)] = new(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    score,
                    matchesName ? reader.GetString(1) : reader.GetString(2));
            }
        }

        await using (var fts = connection.CreateCommand())
        {
            fts.CommandText = """
                SELECT item_id, name, short_name, bm25(item_search) AS rank
                FROM item_search
                WHERE item_search MATCH $query
                ORDER BY rank
                LIMIT 100;
                """;
            fts.Parameters.AddWithValue("$query", BuildFtsQuery(normalizedQuery));
            await using var reader = await fts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var shortName = reader.GetString(2);
                var matchedText = FuzzyMatcher.ShortNameSimilarity(normalizedQuery, shortName) >
                    FuzzyMatcher.Similarity(normalizedQuery, name) ? shortName : name;
                AddOrImprove(candidates, new(id, name, shortName, 0.9, matchedText));
            }
        }

        await using (var fuzzy = connection.CreateCommand())
        {
            fuzzy.CommandText = "SELECT id, name, short_name FROM items;";
            await using var reader = await fuzzy.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var shortName = reader.GetString(2);
                var nameScore = FuzzyMatcher.Similarity(normalizedQuery, name);
                var shortScore = FuzzyMatcher.ShortNameSimilarity(normalizedQuery, shortName);
                AddOrImprove(candidates, new(
                    id,
                    name,
                    shortName,
                    Math.Max(nameScore, shortScore),
                    shortScore > nameScore ? shortName : name));
            }
        }

        return candidates;
    }

    private static void AddOrImprove(Dictionary<string, SearchCandidate> candidates, SearchCandidate candidate)
    {
        if (!candidates.TryGetValue(candidate.Id, out var current) || candidate.Score > current.Score)
        {
            candidates[candidate.Id] = candidate;
        }
    }

    private static async Task<IReadOnlySet<string>> LoadCategoriesAsync(
        SqliteConnection connection,
        string itemId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT category_id
            FROM item_category_membership
            WHERE item_id = $itemId
            ORDER BY category_id;
            """;
        command.Parameters.AddWithValue("$itemId", itemId);
        var categories = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            categories.Add(reader.GetString(0));
        }

        return categories;
    }

    private static async Task<IReadOnlyList<TraderOffer>> LoadOffersAsync(
        SqliteConnection connection,
        string itemId,
        DateTimeOffset sourceUpdatedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT vendor_id, vendor_name, value
            FROM item_sell_offers
            WHERE item_id = $itemId
            ORDER BY value DESC;
            """;
        command.Parameters.AddWithValue("$itemId", itemId);
        var offers = new List<TraderOffer>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            offers.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), Provenance(sourceUpdatedUtc)));
        }

        return offers;
    }

    private static string BuildFtsQuery(string normalizedQuery) => string.Join(
        " AND ",
        normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => $"\"{token.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*"));

    private static ItemCategory ParseCategory(string value) =>
        Enum.TryParse<ItemCategory>(value, true, out var category) ? category : ItemCategory.Unknown;

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DataProvenance Provenance(DateTimeOffset sourceUpdatedUtc) =>
        new("json.tarkov.dev", sourceUpdatedUtc, sourceUpdatedUtc, Confidence: Confidence.Certain);

    private sealed record SearchCandidate(string Id, string Name, string ShortName, double Score, string MatchedText);

    private sealed record ItemRow(
        string Id,
        string Name,
        string ShortName,
        string Description,
        ItemCategory Category,
        int Width,
        int Height,
        bool FleaEligible,
        string? IconUri,
        string? ImageUri,
        string? WikiUri,
        string? PropertiesType,
        string? PropertiesJson,
        DateTimeOffset SourceUpdatedUtc);
}
