using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Catalogs;

namespace TarkovCompanion.Infrastructure.Persistence.Repositories;

/// <summary>
/// Reads synced craft definitions and their measured economics as one consistent planning view.
/// </summary>
/// <remarks>
/// The catalog and its history are read inside one SQLite snapshot. Without that snapshot, a
/// refresh between the header and component queries can join an old craft to new requirements,
/// which is a plausible-looking answer assembled from two different publications. History is
/// is read through bounded per-craft index probes under one station-wide cap, so an oversized
/// history tail cannot be ranked or materialized before the limit. Cost and yield remain
/// independent nullable facts.
/// </remarks>
public sealed class SqliteCraftPlanningCatalog(SqliteConnectionFactory connectionFactory) : ICraftPlanningCatalog
{
    public const int MaximumStationCraftHeaders = 1024;
    public const int MaximumStationCraftComponents = 8192;
    public const int MaximumStationCraftHistory = 4096;
    public const int MaximumAggregateSourceJsonBytes = SqliteV2DataStore.MaximumContractJsonBytes;

    internal const string StationCraftHeadersSql = """
        SELECT id, station_id, level,
               CASE WHEN typeof(id) = 'text' THEN length(CAST(id AS BLOB)) ELSE -1 END,
               CASE WHEN station_id IS NULL THEN NULL
                    WHEN typeof(station_id) = 'text' THEN length(CAST(station_id AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(source_json) = 'text' THEN length(CAST(source_json AS BLOB)) ELSE -1 END, source_json
        FROM crafts
        WHERE station_id = $scope
        ORDER BY level, id COLLATE BINARY
        LIMIT 1025;
        """;
    internal const string StationCraftRequirementsSql = """
        SELECT craft_id, item_id, count,
               CASE WHEN typeof(craft_id) = 'text' THEN length(CAST(craft_id AS BLOB)) ELSE -1 END,
               CASE WHEN item_id IS NULL THEN NULL
                    WHEN typeof(item_id) = 'text' THEN length(CAST(item_id AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(source_json) = 'text' THEN length(CAST(source_json AS BLOB)) ELSE -1 END, source_json
        FROM craft_requirements
        WHERE craft_id = $scope
        LIMIT 8193;
        """;
    internal const string StationCraftOutputsSql = """
        SELECT craft_id, item_id, count,
               CASE WHEN typeof(craft_id) = 'text' THEN length(CAST(craft_id AS BLOB)) ELSE -1 END,
               CASE WHEN item_id IS NULL THEN NULL
                    WHEN typeof(item_id) = 'text' THEN length(CAST(item_id AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(source_json) = 'text' THEN length(CAST(source_json AS BLOB)) ELSE -1 END, source_json
        FROM craft_outputs
        WHERE craft_id = $scope
        LIMIT 8193;
        """;
    internal const string StationCraftHistorySql = """
        SELECT craft_id, history_id, station_id, station_level, observed_utc,
               recorded_utc, output_item_id, output_count, estimated_cost_roubles,
               estimated_yield_roubles, source,
               CASE WHEN typeof(craft_id) = 'text' THEN length(CAST(craft_id AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(history_id) = 'text' THEN length(CAST(history_id AS BLOB)) ELSE -1 END,
               CASE WHEN station_id IS NULL THEN NULL
                    WHEN typeof(station_id) = 'text' THEN length(CAST(station_id AS BLOB)) ELSE -1 END,
               CASE WHEN observed_utc IS NULL THEN NULL
                    WHEN typeof(observed_utc) = 'text' THEN length(CAST(observed_utc AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(recorded_utc) = 'text' THEN length(CAST(recorded_utc AS BLOB)) ELSE -1 END,
               CASE WHEN output_item_id IS NULL THEN NULL
                    WHEN typeof(output_item_id) = 'text' THEN length(CAST(output_item_id AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(source) = 'text' THEN length(CAST(source AS BLOB)) ELSE -1 END,
               CASE WHEN typeof(payload_json) = 'text' THEN length(CAST(payload_json AS BLOB)) ELSE -1 END, payload_json
        FROM craft_history
        WHERE craft_id = $scope
        ORDER BY observed_utc DESC, recorded_utc DESC
        LIMIT $limit;
        """;

    public async Task<IReadOnlyList<CraftPlanningEntry>> GetByStationAsync(
        string stationId,
        int historyLimitPerCraft,
        CancellationToken cancellationToken)
    {
        ValidateScope(stationId, nameof(stationId));
        ValidateHistoryLimit(historyLimitPerCraft, nameof(historyLimitPerCraft));

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var scope = CraftScope.ForStation(stationId);
        IReadOnlyList<CraftPlanningEntry> result;
        try
        {
            result = await ReadAsync(
                connection,
                transaction,
                scope,
                historyLimitPerCraft,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistedValueFailure(exception))
        {
            throw new InvalidDataException("Persisted station craft planning data is invalid.", exception);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<CraftPlanningEntry?> GetByCraftAsync(
        string craftId,
        int historyLimit,
        CancellationToken cancellationToken)
    {
        ValidateScope(craftId, nameof(craftId));
        ValidateHistoryLimit(historyLimit, nameof(historyLimit));

        await using var connection = await connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<CraftPlanningEntry> result;
        try
        {
            result = await ReadAsync(
                connection,
                transaction,
                CraftScope.ForCraft(craftId),
                historyLimit,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsPersistedValueFailure(exception))
        {
            throw new InvalidDataException("Persisted craft planning data is invalid.", exception);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result.SingleOrDefault();
    }

    private static async Task<IReadOnlyList<CraftPlanningEntry>> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CraftScope scope,
        int historyLimit,
        CancellationToken cancellationToken)
    {
        var budget = new CraftReadBudget();
        var headers = await ReadHeadersAsync(connection, transaction, scope, budget, cancellationToken)
            .ConfigureAwait(false);
        if (headers.Count == 0)
        {
            return [];
        }

        var requirements = await ReadComponentsAsync(
            connection,
            transaction,
            CraftComponentKind.Requirement,
            scope,
            headers,
            budget,
            cancellationToken).ConfigureAwait(false);
        var outputs = await ReadComponentsAsync(
            connection,
            transaction,
            CraftComponentKind.Output,
            scope,
            headers,
            budget,
            cancellationToken).ConfigureAwait(false);
        var history = historyLimit == 0
            ? new Dictionary<string, List<CraftEconomicsObservation>>(StringComparer.Ordinal)
            : await ReadHistoryAsync(
                connection,
                transaction,
                scope,
                headers,
                historyLimit,
                budget,
                cancellationToken).ConfigureAwait(false);

        return headers.Select(header => new CraftPlanningEntry(
            header.CraftId,
            header.StationId,
            header.StationLevel,
            header.SourceJson,
            requirements.GetValueOrDefault(header.CraftId) ?? [],
            outputs.GetValueOrDefault(header.CraftId) ?? [],
            history.GetValueOrDefault(header.CraftId) ?? [])).ToArray();
    }

    private static async Task<List<CraftHeader>> ReadHeadersAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CraftScope scope,
        CraftReadBudget budget,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = scope.ByStation
            ? StationCraftHeadersSql
            : """
                SELECT id, station_id, level,
                       CASE WHEN typeof(id) = 'text' THEN length(CAST(id AS BLOB)) ELSE -1 END,
                       CASE WHEN station_id IS NULL THEN NULL
                            WHEN typeof(station_id) = 'text' THEN length(CAST(station_id AS BLOB)) ELSE -1 END,
                       CASE WHEN typeof(source_json) = 'text' THEN length(CAST(source_json AS BLOB)) ELSE -1 END, source_json
                FROM crafts
                WHERE id = $scope
                LIMIT 1;
                """;
        command.Parameters.AddWithValue("$scope", scope.Value);

        var headers = new List<CraftHeader>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            budget.AddHeader();
            var craftId = ReadRequiredText(reader, 0, 3, "craft id");
            var stationId = ReadOptionalText(reader, 1, 4, "craft station id");
            var stationLevel = ReadOptionalInt32(reader, 2, "craft station level");
            if (stationLevel is < 0 ||
                (scope.ByStation && !string.Equals(stationId, scope.Value, StringComparison.Ordinal)) ||
                (!scope.ByStation && !string.Equals(craftId, scope.Value, StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Persisted craft header does not match its requested scope.");
            }

            headers.Add(new(
                craftId,
                stationId,
                stationLevel,
                ReadJson(reader, 6, 5, budget, "craft source JSON")));
        }

        return headers;
    }

    private static async Task<Dictionary<string, List<CraftItemQuantity>>> ReadComponentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CraftComponentKind componentKind,
        CraftScope scope,
        IReadOnlyList<CraftHeader> headers,
        CraftReadBudget budget,
        CancellationToken cancellationToken)
    {
        var query = componentKind switch
        {
            CraftComponentKind.Requirement => StationCraftRequirementsSql,
            CraftComponentKind.Output => StationCraftOutputsSql,
            _ => throw new ArgumentOutOfRangeException(nameof(componentKind)),
        };
        var result = new Dictionary<string, List<CraftItemQuantity>>(StringComparer.Ordinal);
        IEnumerable<string> craftIds = scope.ByStation
            ? headers.Select(header => header.CraftId)
            : new[] { scope.Value };
        foreach (var requestedCraftId in craftIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = query;
            command.Parameters.AddWithValue("$scope", requestedCraftId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                budget.AddComponent();
                var craftId = ReadRequiredText(reader, 0, 3, "component craft id");
                if (!string.Equals(craftId, requestedCraftId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Persisted craft component does not match its requested scope.");
                }

                if (!result.TryGetValue(craftId, out var items))
                {
                    result[craftId] = items = [];
                }

                var itemId = ReadOptionalText(reader, 1, 4, "component item id");
                var count = ReadOptionalDecimal(reader, 2, "component count");
                if (count is < 0)
                {
                    throw new InvalidDataException("Persisted craft component count is negative.");
                }

                items.Add(new(itemId, count, ReadJson(reader, 6, 5, budget, "component source JSON")));
            }
        }

        foreach (var items in result.Values)
        {
            items.Sort(static (left, right) =>
            {
                var itemOrder = string.Compare(left.ItemId, right.ItemId, StringComparison.Ordinal);
                return itemOrder != 0 ? itemOrder : Nullable.Compare<decimal>(left.Count, right.Count);
            });
        }

        return result;
    }

    private static async Task<Dictionary<string, List<CraftEconomicsObservation>>> ReadHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CraftScope scope,
        IReadOnlyList<CraftHeader> headers,
        int historyLimit,
        CraftReadBudget budget,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<CraftEconomicsObservation>>(StringComparer.Ordinal);
        IEnumerable<string> craftIds = scope.ByStation
            ? headers.Select(header => header.CraftId)
            : new[] { scope.Value };
        foreach (var requestedCraftId in craftIds)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = StationCraftHistorySql;
            command.Parameters.AddWithValue("$scope", requestedCraftId);
            command.Parameters.AddWithValue(
                "$limit",
                Math.Min(historyLimit, budget.RemainingHistoryRows + 1));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                budget.AddHistory();
                var craftId = ReadRequiredText(reader, 0, 11, "history craft id");
                if (!string.Equals(craftId, requestedCraftId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Persisted craft history does not match its requested scope.");
                }

                if (!result.TryGetValue(craftId, out var observations))
                {
                    result[craftId] = observations = [];
                }

                var historyId = ReadRequiredGuid(reader, 1, 12, "craft history id");
                var stationId = ReadOptionalText(reader, 2, 13, "history station id");
                var stationLevel = ReadOptionalInt32(reader, 3, "history station level");
                var observedUtc = ReadOptionalTimestamp(reader, 4, 14, "craft observation time");
                var recordedUtc = ReadRequiredTimestamp(reader, 5, 15, "craft recording time");
                var outputItemId = ReadOptionalText(reader, 6, 16, "history output item id");
                var outputCount = ReadOptionalDouble(reader, 7, "history output count");
                var cost = ReadOptionalInt64(reader, 8, "history cost");
                var yield = ReadOptionalInt64(reader, 9, "history yield");
                var source = ReadRequiredText(reader, 10, 17, "history source");
                var payloadJson = ReadJson(reader, 19, 18, budget, "craft history payload");
                if (stationLevel is < 0 || outputCount is < 0 || cost is < 0 || yield is < 0)
                {
                    throw new InvalidDataException("Persisted craft history contains negative facts.");
                }

                observations.Add(new(historyId, stationId, stationLevel, observedUtc, recordedUtc,
                    outputItemId, outputCount, cost, yield, source, payloadJson));
            }
        }

        return result;
    }

    private static void ValidateHistoryLimit(int limit, string parameterName)
    {
        if (limit is < 0 or > ICraftPlanningCatalog.MaximumHistoryLimitPerCraft)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                limit,
                $"History limits must be between 0 and {ICraftPlanningCatalog.MaximumHistoryLimitPerCraft}.");
        }
    }

    private static void ValidateScope(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Encoding.UTF8.GetByteCount(value) > SqliteV2DataStore.MaximumContractStringUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static string ReadRequiredText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        var value = ReadText(reader, valueOrdinal, lengthOrdinal, description, optional: false)!;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"The persisted {description} is empty.");
        }

        return value;
    }

    private static string? ReadOptionalText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description) =>
        ReadText(reader, valueOrdinal, lengthOrdinal, description, optional: true);

    private static string? ReadText(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description,
        bool optional)
    {
        if (reader.IsDBNull(valueOrdinal))
        {
            if (!optional || !reader.IsDBNull(lengthOrdinal))
            {
                throw new InvalidDataException($"The persisted {description} is missing or inconsistent.");
            }

            return null;
        }

        if (reader.IsDBNull(lengthOrdinal))
        {
            throw new InvalidDataException($"The persisted {description} has no byte length.");
        }

        var byteLength = reader.GetInt64(lengthOrdinal);
        if (byteLength is < 1 or > SqliteV2DataStore.MaximumContractStringUtf8Bytes)
        {
            throw new InvalidDataException($"The persisted {description} exceeds its string byte budget.");
        }

        var value = reader.GetString(valueOrdinal);
        if (Encoding.UTF8.GetByteCount(value) != byteLength)
        {
            throw new InvalidDataException($"The persisted {description} has inconsistent UTF-8 length.");
        }

        return value;
    }

    private static string ReadJson(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        CraftReadBudget budget,
        string description)
    {
        if (reader.IsDBNull(valueOrdinal) || reader.IsDBNull(lengthOrdinal))
        {
            throw new InvalidDataException($"The persisted {description} is missing.");
        }

        var byteLength = reader.GetInt64(lengthOrdinal);
        budget.AddJson(byteLength);
        var value = reader.GetString(valueOrdinal);
        if (Encoding.UTF8.GetByteCount(value) != byteLength)
        {
            throw new InvalidDataException($"The persisted {description} has inconsistent UTF-8 length.");
        }

        var utf8 = Encoding.UTF8.GetBytes(value);
        var jsonReader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        if (!jsonReader.Read() || jsonReader.TokenType != JsonTokenType.StartObject)
        {
            throw new InvalidDataException($"The persisted {description} is not a JSON object.");
        }

        do
        {
            if ((jsonReader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String) &&
                JsonStringUtf8Length(ref jsonReader) > SqliteV2DataStore.MaximumContractStringUtf8Bytes)
            {
                throw new InvalidDataException($"The persisted {description} contains an oversized string.");
            }
        }
        while (jsonReader.Read());

        return value;
    }

    private static int JsonStringUtf8Length(ref Utf8JsonReader reader) => reader.ValueIsEscaped
        ? Encoding.UTF8.GetByteCount(reader.GetString() ?? string.Empty)
        : reader.ValueSpan.Length;

    private static Guid ReadRequiredGuid(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        if (reader.IsDBNull(valueOrdinal) || reader.IsDBNull(lengthOrdinal) ||
            reader.GetInt64(lengthOrdinal) != 36)
        {
            throw new InvalidDataException($"The persisted {description} is not a canonical UUID.");
        }

        var value = reader.GetString(valueOrdinal);
        if (!Guid.TryParseExact(value, "D", out var result) || result == Guid.Empty)
        {
            throw new InvalidDataException($"The persisted {description} is not a non-empty canonical UUID.");
        }

        return result;
    }

    private static int? ReadOptionalInt32(SqliteDataReader reader, int ordinal, string description)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (reader.GetValue(ordinal) is not long value)
        {
            throw new InvalidDataException($"The persisted {description} is not an integer.");
        }
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw new InvalidDataException($"The persisted {description} is outside the Int32 range.");
        }

        return (int)value;
    }

    private static long? ReadOptionalInt64(SqliteDataReader reader, int ordinal, string description)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            if (reader.GetValue(ordinal) is not long value)
            {
                throw new InvalidDataException($"The persisted {description} is not an integer.");
            }

            return value;
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException or FormatException)
        {
            throw new InvalidDataException($"The persisted {description} is not an integer.", exception);
        }
    }

    private static double? ReadOptionalDouble(SqliteDataReader reader, int ordinal, string description)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal) switch
        {
            long integer => integer,
            double real => real,
            _ => throw new InvalidDataException($"The persisted {description} is not numeric."),
        };
        if (!double.IsFinite(value))
        {
            throw new InvalidDataException($"The persisted {description} is not finite.");
        }

        return value;
    }

    private static decimal? ReadOptionalDecimal(SqliteDataReader reader, int ordinal, string description)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        try
        {
            return reader.GetValue(ordinal) switch
            {
                long integer => integer,
                double real when double.IsFinite(real) => Convert.ToDecimal(real),
                _ => throw new InvalidDataException($"The persisted {description} is not numeric."),
            };
        }
        catch (Exception exception) when (exception is InvalidCastException or OverflowException or FormatException)
        {
            throw new InvalidDataException($"The persisted {description} is not a finite decimal.", exception);
        }
    }

    private static DateTimeOffset ReadRequiredTimestamp(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        var value = ReadRequiredText(reader, valueOrdinal, lengthOrdinal, description);
        if (!TryParseTimestamp(value, out var result))
        {
            throw new InvalidDataException($"The persisted {description} is not a canonical UTC timestamp.");
        }

        return result;
    }

    private static DateTimeOffset? ReadOptionalTimestamp(
        SqliteDataReader reader,
        int valueOrdinal,
        int lengthOrdinal,
        string description)
    {
        var value = ReadOptionalText(reader, valueOrdinal, lengthOrdinal, description);
        if (value is null)
        {
            return null;
        }

        if (!TryParseTimestamp(value, out var result))
        {
            throw new InvalidDataException($"The persisted {description} is not a canonical UTC timestamp.");
        }

        return result;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset result) =>
        DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out result) && result != default && result.Offset == TimeSpan.Zero;

    private static bool IsPersistedValueFailure(Exception exception) =>
        exception is not InvalidDataException and
        (ArgumentException or FormatException or OverflowException or InvalidCastException or JsonException);

    private sealed class CraftReadBudget
    {
        private int _headers;
        private int _components;
        private int _history;
        private long _jsonBytes;

        public void AddHeader()
        {
            if (++_headers > MaximumStationCraftHeaders)
            {
                throw new InvalidDataException("Persisted station craft headers exceed the bounded planning view.");
            }
        }

        public void AddComponent()
        {
            if (++_components > MaximumStationCraftComponents)
            {
                throw new InvalidDataException("Persisted station craft components exceed the bounded planning view.");
            }
        }

        public void AddHistory()
        {
            if (++_history > MaximumStationCraftHistory)
            {
                throw new InvalidDataException("Persisted station craft history exceeds the bounded planning view.");
            }
        }

        public int RemainingHistoryRows => Math.Max(0, MaximumStationCraftHistory - _history);

        public void AddJson(long byteLength)
        {
            if (byteLength is < 1 or > MaximumAggregateSourceJsonBytes ||
                byteLength > MaximumAggregateSourceJsonBytes - _jsonBytes)
            {
                throw new InvalidDataException("Persisted craft JSON exceeds the aggregate planning byte budget.");
            }

            _jsonBytes += byteLength;
        }
    }

    private sealed record CraftHeader(
        string CraftId,
        string? StationId,
        int? StationLevel,
        string SourceJson);

    private enum CraftComponentKind
    {
        Requirement,
        Output,
    }

    private readonly record struct CraftScope(bool ByStation, string Value)
    {
        public static CraftScope ForStation(string stationId) => new(true, stationId);

        public static CraftScope ForCraft(string craftId) => new(false, craftId);
    }
}
