using System.Globalization;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests.DataV2;

[Collection(SqliteCollection.Name)]
public sealed class PriceHistoryReadBoundaryTests
{
    [Fact]
    public async Task ReaderReturnsCanonicalChronologyAndPreservesBoundedUnresolvedEvidence()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await SeedItemAsync(database.Factory);
        var first = Utc(2026, 9, 14, 10);
        var second = first.AddHours(1);
        await ExecuteAsync(
            database.Factory,
            """
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            VALUES ('item', $second, NULL, 200, 'fixture://second'),
                   ('item', $first, 100, NULL, 'fixture://first');
            INSERT INTO price_history_unresolved_time(
                item_id, source_ordinal, flea_price, trader_value, source, raw_json)
            VALUES ('item', 7, NULL, 300, 'fixture://unresolved', $json);
            """,
            ("$first", Format(first)),
            ("$second", Format(second)),
            ("$json", "{\"future\":{\"retained\":true}}"));

        var repository = new SqlitePriceHistoryRepository(database.Factory);
        var resolved = await repository.GetAsync(
            "item",
            first.AddMinutes(-1),
            TestContext.Current.CancellationToken);
        var unresolved = Assert.Single(await repository.GetUnresolvedAsync(
            "item",
            TestContext.Current.CancellationToken));

        Assert.Equal([first, second], resolved.Select(point => point.TimestampUtc));
        Assert.Equal<long?>(100, resolved[0].FleaPriceRoubles);
        Assert.Null(resolved[0].TraderValueRoubles);
        Assert.Null(resolved[1].FleaPriceRoubles);
        Assert.Equal<long?>(200, resolved[1].TraderValueRoubles);
        Assert.All(resolved, point => Assert.Equal(TimeSpan.Zero, point.TimestampUtc.Offset));
        Assert.Equal(7, unresolved.SourceOrdinal);
        Assert.Null(unresolved.FleaPriceRoubles);
        Assert.Equal<long?>(300, unresolved.TraderValueRoubles);
        Assert.Equal("{\"future\":{\"retained\":true}}", unresolved.RawJson);

        var pricePlan = Assert.Single(
            await new SqliteQueryPlanAuditor(database.Factory)
                .CaptureAsync(TestContext.Current.CancellationToken),
            plan => plan.Path == "price");
        Assert.All(pricePlan.Statements, statement => Assert.True(
            statement.MeetsExpectation,
            string.Join(" | ", statement.Steps)));
    }

    [Fact]
    public async Task ReaderRejectsInvalidCallerScopeAndHostileResolvedStorage()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await SeedItemAsync(database.Factory);
        var repository = new SqlitePriceHistoryRepository(database.Factory);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.GetAsync(
            new string('x', SqlitePriceHistoryRepository.MaximumItemIdUtf8Bytes + 1),
            Utc(2026, 9, 14, 10),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetUnresolvedAsync(
            "\ud800",
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.GetAsync(
            "item",
            new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.FromHours(1)),
            TestContext.Current.CancellationToken));

        await ExecuteAsync(
            database.Factory,
            """
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            VALUES ('item', '2026-09-14T10:00:00.0000000+00:00', 1.5, NULL, 'fixture');
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(
            "item", Utc(2026, 9, 14, 0), TestContext.Current.CancellationToken));

        await ExecuteAsync(database.Factory, "DELETE FROM price_history;");
        await ExecuteAsync(
            database.Factory,
            $"""
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            VALUES ('item', '2026-09-14T10:00:00.0000000+00:00', 1, NULL,
                    printf('%.*c', {SqlitePriceHistoryRepository.MaximumSourceUtf8Bytes + 1}, 'x'));
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(
            "item", Utc(2026, 9, 14, 0), TestContext.Current.CancellationToken));

        await ExecuteAsync(database.Factory, "DELETE FROM price_history;");
        await ExecuteAsync(
            database.Factory,
            """
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            VALUES ('item', '2026-09-14T10:00:00.0000000+00:00', -1, NULL, 'fixture');
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(
            "item", Utc(2026, 9, 14, 0), TestContext.Current.CancellationToken));

        await ExecuteAsync(database.Factory, "DELETE FROM price_history;");
        await ExecuteAsync(
            database.Factory,
            """
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            VALUES ('item', '2026-09-14T10:00:00.0000000Z', 1, NULL, 'fixture');
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(
            "item", Utc(2026, 9, 14, 0), TestContext.Current.CancellationToken));

        await ExecuteAsync(database.Factory, "DELETE FROM price_history;");
        await ExecuteAsync(
            database.Factory,
            """
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            VALUES ('item', '2026-09-14T10:00:00.0000000+00:00', 1, NULL, zeroblob(65536));
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(
            "item", Utc(2026, 9, 14, 0), TestContext.Current.CancellationToken));

        await ExecuteAsync(database.Factory, "DELETE FROM price_history;");
        await ExecuteAsync(
            database.Factory,
            """
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            VALUES ('item', x'323032362D30392D31345431303A30303A30302E303030303030302B30303A3030',
                    1, NULL, 'fixture');
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(
            "item", Utc(2026, 9, 14, 0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LifetimeHistoryOutsideRequestedWindowDoesNotConsumeTheReadBoundary()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await SeedItemAsync(database.Factory);
        await ExecuteAsync(
            database.Factory,
            $"""
            WITH RECURSIVE sequence(value) AS (
                SELECT 1 UNION ALL
                SELECT value + 1 FROM sequence
                WHERE value <= {SqlitePriceHistoryRepository.MaximumResolvedPoints}
            )
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            SELECT 'item', '2025-09-14T10:00:00.0000000+00:00', value, NULL,
                   printf('fixture-%05d', value)
            FROM sequence;
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            VALUES ('item', '2026-09-14T10:00:00.0000000+00:00', 7, NULL, 'fixture://visible');
            """);

        var repository = new SqlitePriceHistoryRepository(database.Factory);
        var point = Assert.Single(await repository.GetAsync(
            "item", Utc(2026, 9, 14, 0), TestContext.Current.CancellationToken));
        Assert.Equal<long?>(7, point.FleaPriceRoubles);
        Assert.Equal("fixture://visible", point.Source);
    }

    [Fact]
    public async Task UnresolvedReaderRejectsDynamicNumbersAndNonContractJson()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await SeedItemAsync(database.Factory);
        var repository = new SqlitePriceHistoryRepository(database.Factory);

        await InsertUnresolvedAsync(database.Factory, 1.5, 1, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetUnresolvedAsync(
            "item", TestContext.Current.CancellationToken));

        await ExecuteAsync(database.Factory, "DELETE FROM price_history_unresolved_time;");
        await InsertUnresolvedAsync(database.Factory, (long)int.MaxValue + 1, 1, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetUnresolvedAsync(
            "item", TestContext.Current.CancellationToken));

        await ExecuteAsync(database.Factory, "DELETE FROM price_history_unresolved_time;");
        await InsertUnresolvedAsync(database.Factory, 1, -1, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetUnresolvedAsync(
            "item", TestContext.Current.CancellationToken));

        await ExecuteAsync(database.Factory, "DELETE FROM price_history_unresolved_time;");
        await InsertUnresolvedAsync(database.Factory, 1, 1, "[]");
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetUnresolvedAsync(
            "item", TestContext.Current.CancellationToken));

        await ExecuteAsync(database.Factory, "DELETE FROM price_history_unresolved_time;");
        var deepJson = string.Concat(Enumerable.Repeat("{\"nested\":", 40)) +
                       "0" + new string('}', 40);
        await InsertUnresolvedAsync(database.Factory, 1, 1, deepJson);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetUnresolvedAsync(
            "item", TestContext.Current.CancellationToken));

        await ExecuteAsync(database.Factory, "DELETE FROM price_history_unresolved_time;");
        var oversizedString = "{\"value\":\"" +
                              new string('x', SqlitePriceHistoryRepository.MaximumJsonStringUtf8Bytes + 1) +
                              "\"}";
        await InsertUnresolvedAsync(database.Factory, 1, 1, oversizedString);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetUnresolvedAsync(
            "item", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SentinelRowsRejectOversizedHistoriesBeforeReadingTheirPayloads()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await SeedItemAsync(database.Factory);
        await ExecuteAsync(
            database.Factory,
            $"""
            WITH RECURSIVE sequence(value) AS (
                SELECT 1 UNION ALL
                SELECT value + 1 FROM sequence
                WHERE value < {SqlitePriceHistoryRepository.MaximumResolvedPoints}
            )
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            SELECT 'item', '2026-09-14T10:00:00.0000000+00:00', value, NULL,
                   printf('fixture-%04d', value)
            FROM sequence;
            INSERT INTO price_history(item_id, timestamp_utc, flea_price, trader_value, source)
            VALUES ('item', '2026-09-14T10:00:00.0000000+00:00', 1, NULL, zeroblob(65536));
            """);

        var repository = new SqlitePriceHistoryRepository(database.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetAsync(
            "item", Utc(2026, 9, 14, 0), TestContext.Current.CancellationToken));

        await ExecuteAsync(
            database.Factory,
            $"""
            PRAGMA ignore_check_constraints = ON;
            WITH RECURSIVE sequence(value) AS (
                SELECT 0 UNION ALL
                SELECT value + 1 FROM sequence
                WHERE value < {SqlitePriceHistoryRepository.MaximumUnresolvedPoints - 1}
            )
            INSERT INTO price_history_unresolved_time(
                item_id, source_ordinal, flea_price, trader_value, source, raw_json)
            SELECT 'item', value, value, NULL, 'fixture', json_object()
            FROM sequence;
            INSERT INTO price_history_unresolved_time(
                item_id, source_ordinal, flea_price, trader_value, source, raw_json)
            VALUES ('item', {SqlitePriceHistoryRepository.MaximumUnresolvedPoints}, 1, NULL,
                    'fixture', zeroblob(65536));
            PRAGMA ignore_check_constraints = OFF;
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.GetUnresolvedAsync(
            "item", TestContext.Current.CancellationToken));
    }

    private static async Task SeedItemAsync(SqliteConnectionFactory factory) =>
        await ExecuteAsync(
            factory,
            """
            INSERT INTO items(
                id, name, short_name, normalized_name, category_type,
                width, height, slots, source_updated_utc)
            VALUES ('item', 'Item', 'Item', 'item', 'other', 1, 1, 1,
                    '2026-09-14T00:00:00.0000000+00:00');
            """);

    private static async Task InsertUnresolvedAsync(
        SqliteConnectionFactory factory,
        object ordinal,
        object fleaPrice,
        string rawJson) =>
        await ExecuteAsync(
            factory,
            """
            INSERT INTO price_history_unresolved_time(
                item_id, source_ordinal, flea_price, trader_value, source, raw_json)
            VALUES ('item', $ordinal, $fleaPrice, NULL, 'fixture', $json);
            """,
            ("$ordinal", ordinal),
            ("$fleaPrice", fleaPrice),
            ("$json", rawJson));

    private static async Task ExecuteAsync(
        SqliteConnectionFactory factory,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
