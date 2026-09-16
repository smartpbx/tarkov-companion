using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>Reads a bounded, source-honest view of one item's persisted price history.</summary>
/// <remarks>
/// SQLite's dynamic types mean the declared schema is not a read-time guarantee. Each query asks
/// SQLite for the actual storage class or byte length before this reader touches the value. The
/// extra sentinel row makes an oversized history fail as a whole instead of returning a plausible
/// partial chart, and it is rejected before any value in that row is materialized.
/// </remarks>
public sealed class SqlitePriceHistoryRepository(SqliteConnectionFactory connectionFactory) : IPriceHistoryStore
{
    public const int MaximumResolvedPoints = 4096;
    public const int MaximumUnresolvedPoints = 4096;
    public const int MaximumItemIdUtf8Bytes = 512;
    public const int MaximumSourceUtf8Bytes = 1024;
    public const int MaximumRawJsonUtf8Bytes = 64 * 1024;
    public const int MaximumAggregateRawJsonUtf8Bytes = 8 * 1024 * 1024;
    public const int MaximumJsonStringUtf8Bytes = 4096;

    private const int MaximumTimestampUtf8Bytes = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal const string PriceHistorySql = """
        SELECT CASE WHEN typeof(timestamp_utc) = 'text'
                    THEN length(CAST(timestamp_utc AS BLOB)) ELSE -1 END,
               typeof(flea_price), typeof(trader_value),
               CASE WHEN typeof(source) = 'text'
                    THEN length(CAST(source AS BLOB)) ELSE -1 END,
               timestamp_utc, flea_price, trader_value, source
        FROM price_history
        WHERE item_id = $itemId AND timestamp_utc >= $sinceUtc
        ORDER BY timestamp_utc COLLATE BINARY, source COLLATE BINARY
        LIMIT 4097;
        """;

    internal const string UnresolvedPriceHistorySql = """
        SELECT typeof(source_ordinal), typeof(flea_price), typeof(trader_value),
               CASE WHEN typeof(source) = 'text'
                    THEN length(CAST(source AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(raw_json) = 'text'
                    THEN length(CAST(raw_json AS BLOB)) ELSE -1 END,
               source_ordinal, flea_price, trader_value, source, raw_json
        FROM price_history_unresolved_time
        WHERE item_id = $itemId
        ORDER BY source COLLATE BINARY, source_ordinal
        LIMIT 4097;
        """;

    public async Task<IReadOnlyList<PriceHistoryPoint>> GetAsync(
        string itemId,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken)
    {
        ValidateItemId(itemId);
        if (sinceUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The history lower bound must be expressed in UTC.", nameof(sinceUtc));
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = PriceHistorySql;
        command.Parameters.AddWithValue("$itemId", itemId);
        command.Parameters.AddWithValue("$sinceUtc", FormatUtc(sinceUtc));

        var points = new List<PriceHistoryPoint>();
        DateTimeOffset? previousTimestamp = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (points.Count == MaximumResolvedPoints)
            {
                throw new InvalidDataException(
                    $"Persisted price history exceeds the {MaximumResolvedPoints}-point read boundary.");
            }

            var timestampText = ReadRequiredText(
                reader, 4, 0, MaximumTimestampUtf8Bytes, "price timestamp");
            var timestamp = ReadCanonicalUtc(timestampText, "price timestamp");
            if (timestamp < sinceUtc || previousTimestamp is { } previous && timestamp < previous)
            {
                throw new InvalidDataException("Persisted price history is outside its requested chronological scope.");
            }

            var fleaPrice = ReadOptionalPrice(reader, 5, 1, "flea price");
            var traderValue = ReadOptionalPrice(reader, 6, 2, "trader value");
            var source = ReadRequiredText(reader, 7, 3, MaximumSourceUtf8Bytes, "price source");
            points.Add(new(timestamp, fleaPrice, traderValue, source));
            previousTimestamp = timestamp;
        }

        return points;
    }

    public async Task<IReadOnlyList<UnresolvedPriceHistoryPoint>> GetUnresolvedAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        ValidateItemId(itemId);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = UnresolvedPriceHistorySql;
        command.Parameters.AddWithValue("$itemId", itemId);

        var points = new List<UnresolvedPriceHistoryPoint>();
        long aggregateJsonBytes = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (points.Count == MaximumUnresolvedPoints)
            {
                throw new InvalidDataException(
                    $"Persisted unresolved price history exceeds the {MaximumUnresolvedPoints}-point read boundary.");
            }

            var sourceOrdinal = ReadSourceOrdinal(reader, 5, 0);
            var fleaPrice = ReadOptionalPrice(reader, 6, 1, "unresolved flea price");
            var traderValue = ReadOptionalPrice(reader, 7, 2, "unresolved trader value");
            var source = ReadRequiredText(
                reader, 8, 3, MaximumSourceUtf8Bytes, "unresolved price source");
            var rawJson = ReadJsonObject(reader, 9, 4, ref aggregateJsonBytes);
            points.Add(new(sourceOrdinal, fleaPrice, traderValue, source, rawJson));
        }

        return points;
    }

    private static void ValidateItemId(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        int byteLength;
        try
        {
            byteLength = StrictUtf8.GetByteCount(itemId);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Item identifiers must contain valid Unicode.", nameof(itemId), exception);
        }

        if (byteLength > MaximumItemIdUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(itemId),
                $"Item identifiers may not exceed {MaximumItemIdUtf8Bytes} UTF-8 bytes.");
        }
    }

    private static int ReadSourceOrdinal(SqliteDataReader reader, int valueOrdinal, int typeOrdinal)
    {
        if (!string.Equals(reader.GetString(typeOrdinal), "integer", StringComparison.Ordinal) ||
            reader.IsDBNull(valueOrdinal))
        {
            throw new InvalidDataException("The persisted price source ordinal is not an integer.");
        }

        var value = reader.GetInt64(valueOrdinal);
        if (value is < 0 or > int.MaxValue)
        {
            throw new InvalidDataException("The persisted price source ordinal is outside its supported range.");
        }

        return (int)value;
    }

    private static long? ReadOptionalPrice(
        SqliteDataReader reader,
        int valueOrdinal,
        int typeOrdinal,
        string description)
    {
        var storageClass = reader.GetString(typeOrdinal);
        if (string.Equals(storageClass, "null", StringComparison.Ordinal))
        {
            if (!reader.IsDBNull(valueOrdinal))
            {
                throw new InvalidDataException($"The persisted {description} has inconsistent storage metadata.");
            }

            return null;
        }

        if (!string.Equals(storageClass, "integer", StringComparison.Ordinal) ||
            reader.IsDBNull(valueOrdinal))
        {
            throw new InvalidDataException($"The persisted {description} is not an integer.");
        }

        var value = reader.GetInt64(valueOrdinal);
        if (value < 0)
        {
            throw new InvalidDataException($"The persisted {description} is negative.");
        }

        return value;
    }

    private static string ReadRequiredText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        int maximumUtf8Bytes,
        string description)
    {
        var byteLength = reader.GetInt64(lengthOrdinal);
        if (byteLength is < 1 || byteLength > maximumUtf8Bytes || reader.IsDBNull(valueOrdinal))
        {
            throw new InvalidDataException($"The persisted {description} is missing or exceeds its UTF-8 byte budget.");
        }

        var value = reader.GetString(valueOrdinal);
        if (string.IsNullOrWhiteSpace(value) || GetUtf8ByteCount(value, description) != byteLength)
        {
            throw new InvalidDataException($"The persisted {description} is empty or has inconsistent UTF-8 length.");
        }

        return value;
    }

    private static string ReadJsonObject(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        ref long aggregateJsonBytes)
    {
        var byteLength = reader.GetInt64(lengthOrdinal);
        if (byteLength is < 2 or > MaximumRawJsonUtf8Bytes ||
            byteLength > MaximumAggregateRawJsonUtf8Bytes - aggregateJsonBytes ||
            reader.IsDBNull(valueOrdinal))
        {
            throw new InvalidDataException("Persisted unresolved price JSON exceeds its read boundary.");
        }

        aggregateJsonBytes += byteLength;
        var json = reader.GetString(valueOrdinal);
        if (GetUtf8ByteCount(json, "unresolved price JSON") != byteLength)
        {
            throw new InvalidDataException("Persisted unresolved price JSON has inconsistent UTF-8 length.");
        }

        ValidateJsonObject(json);
        return json;
    }

    private static void ValidateJsonObject(string json)
    {
        try
        {
            var utf8 = StrictUtf8.GetBytes(json);
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new InvalidDataException("Persisted unresolved price JSON is not an object.");
            }

            var completeRoot = false;
            while (reader.Read())
            {
                if ((reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String) &&
                    JsonStringUtf8Length(ref reader) > MaximumJsonStringUtf8Bytes)
                {
                    throw new InvalidDataException("Persisted unresolved price JSON contains an oversized string.");
                }

                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0)
                {
                    completeRoot = true;
                }
            }

            if (!completeRoot)
            {
                throw new InvalidDataException("Persisted unresolved price JSON is incomplete.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Persisted unresolved price JSON is invalid.", exception);
        }
    }

    private static int JsonStringUtf8Length(ref Utf8JsonReader reader) => reader.ValueIsEscaped
        ? GetUtf8ByteCount(reader.GetString() ?? string.Empty, "JSON string")
        : reader.ValueSpan.Length;

    private static DateTimeOffset ReadCanonicalUtc(string value, string description)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero ||
            !string.Equals(value, FormatUtc(parsed), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The persisted {description} is not a canonical UTC timestamp.");
        }

        return parsed;
    }

    private static int GetUtf8ByteCount(string value, string description)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException($"The persisted {description} contains invalid Unicode.", exception);
        }
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
