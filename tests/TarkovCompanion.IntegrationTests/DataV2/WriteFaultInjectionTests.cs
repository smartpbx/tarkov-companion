using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests.DataV2;

/// <summary>
/// #270: a write that fails half-way through leaves nothing of itself behind, and the same write
/// goes through once the fault is gone. The fault is SQLite's own: a trigger counts writes to one
/// table and aborts the Nth, so the production code runs unmodified up to a real failed statement
/// in the middle of its own transaction. The earlier coverage stopped at three representative
/// writes and a first-ever publication; these are the catalog replacing a published catalog, the
/// durable outbox admitting a batch, and raid history applying and purging.
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class WriteFaultInjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("item_category_membership", "INSERT", 2)]
    [InlineData("items", "INSERT", 2)]
    [InlineData("price_history", "INSERT", 1)]
    public async Task ACatalogRepublicationThatFailsMidwayKeepsThePublishedCatalogAndTheRetryLands(
        string table,
        string operation,
        int failingWrite)
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var first = await RefreshAsync(database.Factory, new FixtureApiHandler());
        Assert.All(first, endpoint => Assert.Null(endpoint.Error));
        var published = await CatalogStateAsync(database.Factory);
        var head = await ItemsHeadAsync(database.Factory);

        await using (await Fault.AtAsync(database.Factory, table, operation, failingWrite))
        {
            var failed = await RefreshAsync(database.Factory, new RenamedItemsHandler());
            var items = Assert.Single(failed, endpoint => endpoint.Endpoint == "items");
            Assert.False(items.Updated);
            Assert.Contains("injected-fault", items.Error, StringComparison.Ordinal);
        }

        // Every row of the old catalog, its search index and its head, exactly as they were.
        Assert.Equal(published, await CatalogStateAsync(database.Factory));
        Assert.Equal(head, await ItemsHeadAsync(database.Factory));
        Assert.Equal("ok", await IntegrityAsync(database.Factory));
        var search = await new SqliteItemRepository(database.Factory)
            .SearchAsync("salewa", 5, TestContext.Current.CancellationToken);
        Assert.Equal("Salewa first aid kit", Assert.Single(search, hit => hit.Item.Id == "item-001").Item.Name);

        var retried = await RefreshAsync(database.Factory, new RenamedItemsHandler());
        Assert.True(Assert.Single(retried, endpoint => endpoint.Endpoint == "items").Updated);
        Assert.Equal("Salewa field kit", await TextAsync(database.Factory, "SELECT name FROM items WHERE id = 'item-001';"));
        Assert.NotEqual(head.Visible, (await ItemsHeadAsync(database.Factory)).Visible);
        Assert.Equal(
            await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM items;"),
            await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM item_search;"));
    }

    [Theory]
    [InlineData("durable_outbox", 3)]
    [InlineData("outbox_aggregate_sequences", 2)]
    public async Task AnOutboxBatchThatFailsMidwayAdmitsNothingAndTheSameBatchIsAdmittedOnRetry(
        string table,
        int failingWrite)
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteOutboxStore(database.Factory);
        await store.EnqueueAsync(Item("earlier", "group-a", 1), TestContext.Current.CancellationToken);
        var before = await OutboxStateAsync(database.Factory);
        ImmutableArray<OutboxItem> batch =
            [Item("one", "group-a", 2), Item("two", "group-b", 1), Item("three", "group-b", 2)];

        await using (await Fault.AtAsync(database.Factory, table, "INSERT", failingWrite))
        {
            var failure = await Assert.ThrowsAsync<SqliteException>(() =>
                store.EnqueueBatchAsync(batch, TestContext.Current.CancellationToken));
            Assert.Contains("injected-fault", failure.Message, StringComparison.Ordinal);
        }

        // No item of the batch, and no sequence it claimed: a half-admitted batch would burn
        // group-b's sequence 1 and refuse the retry as a reused sequence.
        Assert.Equal(before, await OutboxStateAsync(database.Factory));
        Assert.Equal("ok", await IntegrityAsync(database.Factory));

        var receipts = await store.EnqueueBatchAsync(batch, TestContext.Current.CancellationToken);
        Assert.All(receipts, receipt => Assert.True(receipt.Added));
        Assert.Equal(4, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM durable_outbox;"));
        Assert.Equal(3, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT next_sequence FROM outbox_aggregate_sequences WHERE aggregate_id = 'group-a';"));
        Assert.Equal(3, await V2TestDatabase.ScalarAsync(database.Factory,
            "SELECT next_sequence FROM outbox_aggregate_sequences WHERE aggregate_id = 'group-b';"));
    }

    [Theory]
    [InlineData("raids", 1)]
    [InlineData("raid_events", 2)]
    [InlineData("outbox_target_operations", 1)]
    public async Task ARaidHistoryOperationThatFailsMidwayLeavesNoHalfRaidAndIsAppliedOnceOnRetry(
        string table,
        int failingWrite)
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var history = new SqliteRaidHistoryService(database.Factory);
        var operationId = OperationId.New();
        var raidId = Guid.NewGuid();
        var applied = 0;
        Task Apply(CancellationToken token) => ApplyRaidAsync(history, raidId, () => applied++, token);

        await using (await Fault.AtAsync(database.Factory, table, "INSERT", failingWrite))
        {
            var failure = await Assert.ThrowsAsync<SqliteException>(() => history.ApplyOnceAsync(
                operationId, OutboxCommandKind.RaidStarted, raidId, Apply, TestContext.Current.CancellationToken));
            Assert.Contains("injected-fault", failure.Message, StringComparison.Ordinal);
        }

        // Not the raid, not the events that were written before the fault, and not the ledger row
        // that would make the outbox's redelivery a silent no-op.
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM raids;"));
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM raid_events;"));
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM outbox_target_operations;"));
        Assert.Equal("ok", await IntegrityAsync(database.Factory));

        await history.ApplyOnceAsync(operationId, OutboxCommandKind.RaidStarted, raidId, Apply, TestContext.Current.CancellationToken);
        await history.ApplyOnceAsync(operationId, OutboxCommandKind.RaidStarted, raidId, Apply, TestContext.Current.CancellationToken);

        Assert.Equal(2, applied);
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM raids;"));
        Assert.Equal(3, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM raid_events;"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM outbox_target_operations;"));
    }

    [Fact]
    public async Task APurgeThatFailsOnItsSecondRaidKeepsEveryDeletedRaidRestorable()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var history = new SqliteRaidHistoryService(database.Factory);
        Guid[] raids = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        foreach (var raidId in raids)
        {
            await history.StartAsync(new(raidId, Guid.Parse("00000000-0000-0000-0000-0000000000aa"), "customs", "Regular", Now, null, null, null),
                TestContext.Current.CancellationToken);
            await history.RecordEventAsync(raidId, "position", Now, "{\"x\":1}", TestContext.Current.CancellationToken);
        }

        await history.SoftDeleteAsync(raids, Now, TestContext.Current.CancellationToken);

        await using (await Fault.AtAsync(database.Factory, "raids", "DELETE", 2))
        {
            var failure = await Assert.ThrowsAsync<SqliteException>(() =>
                history.PurgeDeletedAsync([], TestContext.Current.CancellationToken));
            Assert.Contains("injected-fault", failure.Message, StringComparison.Ordinal);
        }

        // The first raid's delete ran before the fault; it must have gone back with the rest, events included.
        Assert.Equal(3, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM raids;"));
        Assert.Equal(3, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM raid_events;"));
        Assert.Equal("ok", await IntegrityAsync(database.Factory));

        await history.PurgeDeletedAsync([], TestContext.Current.CancellationToken);
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM raids;"));
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM raid_events;"));
    }

    private static async Task ApplyRaidAsync(
        SqliteRaidHistoryService history,
        Guid raidId,
        Action counted,
        CancellationToken cancellationToken)
    {
        counted();
        await history.StartAsync(
            new(raidId, Guid.Parse("00000000-0000-0000-0000-0000000000aa"), "customs", "Regular", Now, null, null, null),
            cancellationToken);
        for (var index = 0; index < 3; index++)
        {
            await history.RecordEventAsync(
                raidId,
                "position",
                Now.AddSeconds(index),
                $"{{\"x\":{index.ToString(CultureInfo.InvariantCulture)}}}",
                cancellationToken);
        }
    }

    private static OutboxItem Item(string key, string aggregate, long sequence) =>
        new(OperationId.New(), new(key), CorrelationId.New(), new("test"), OutboxCommandKind.RaidEnded,
            OutboxContractVersion.Current, new(aggregate), sequence, Now, Now, Now.AddHours(1),
            OutboxPayload.CreateGenericJson("{\"state\":\"queued\"}"), OutboxAttemptPolicy.Default);

    private static async Task<IReadOnlyList<SyncEndpointResult>> RefreshAsync(
        SqliteConnectionFactory factory,
        HttpMessageHandler handler)
    {
        await using var client = new TarkovDevJsonClient(
            new HttpClient(handler),
            new InMemoryTarkovDevResponseCache(),
            new DataTranslationService(),
            new()
            {
                BaseAddress = new("https://fixture.invalid/"),
                InitialRetryDelay = TimeSpan.Zero,
                MaxAttempts = 1,
            });
        return await new TarkovDevDataRefreshOperation(
                client,
                new SqliteDataRefreshRepository(factory),
                new SqliteSyncStateRepository(factory))
            .RefreshAsync(new(GameMode.Regular, "en", true), TestContext.Current.CancellationToken);
    }

    private static Task<string> CatalogStateAsync(SqliteConnectionFactory factory) => DumpAsync(
        factory,
        "SELECT id, name, short_name, normalized_name, base_price, source_updated_utc FROM items",
        "SELECT item_id, name, short_name, normalized_terms FROM item_search",
        "SELECT * FROM item_categories",
        "SELECT * FROM item_category_membership",
        "SELECT * FROM item_sell_offers",
        "SELECT * FROM price_history",
        // The failed attempt is recorded as a publication row of its own, with no content; the
        // content-bearing publications are the ones a half-written catalog would add to.
        "SELECT * FROM dataset_publications WHERE source_key = 'items' AND content_sha256 IS NOT NULL");

    private static Task<string> OutboxStateAsync(SqliteConnectionFactory factory) => DumpAsync(
        factory,
        "SELECT operation_id, idempotency_key, aggregate_id, aggregate_sequence, delivery_state FROM durable_outbox",
        "SELECT * FROM outbox_aggregate_sequences");

    private static async Task<(string? Visible, string? LastKnownGood)> ItemsHeadAsync(SqliteConnectionFactory factory)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT visible_publication_id, last_known_good_publication_id FROM dataset_heads WHERE source_key = 'items';";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>Every row of every query, sorted, so two states compare as one string.</summary>
    private static async Task<string> DumpAsync(SqliteConnectionFactory factory, params string[] queries)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        var dump = new StringBuilder();
        foreach (var query in queries)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                var fields = new string[reader.FieldCount];
                for (var ordinal = 0; ordinal < fields.Length; ordinal++)
                {
                    fields[ordinal] = reader.IsDBNull(ordinal)
                        ? "<null>"
                        : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
                }

                rows.Add(string.Join('|', fields));
            }

            rows.Sort(StringComparer.Ordinal);
            dump.Append(query).Append(": ").Append(rows.Count).AppendLine();
            foreach (var row in rows)
            {
                dump.AppendLine(row);
            }
        }

        return dump.ToString();
    }

    private static async Task<string> TextAsync(SqliteConnectionFactory factory, string sql)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), CultureInfo.InvariantCulture)!;
    }

    private static Task<string> IntegrityAsync(SqliteConnectionFactory factory) =>
        TextAsync(factory, "PRAGMA integrity_check;");

    /// <summary>
    /// Aborts the Nth insert or delete on one table from inside SQLite, whichever connection makes it.
    /// </summary>
    /// <remarks>
    /// The counter row is updated by the trigger itself, so it counts only the writes that got
    /// through; the aborted statement's own increment goes back with it. Disposing drops both, which
    /// is what "the disk has room again" or "the lock was released" looks like to the retry.
    /// </remarks>
    private sealed class Fault(SqliteConnectionFactory factory) : IAsyncDisposable
    {
        public static async Task<Fault> AtAsync(SqliteConnectionFactory factory, string table, string operation, int failingWrite)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(failingWrite, 1);
            await ExecuteAsync(factory, $"""
                CREATE TABLE fault_injection_counter(writes INTEGER NOT NULL);
                INSERT INTO fault_injection_counter(writes) VALUES (0);
                CREATE TRIGGER fault_injection BEFORE {operation} ON {table}
                BEGIN
                    UPDATE fault_injection_counter SET writes = writes + 1;
                    SELECT RAISE(ABORT, 'injected-fault')
                    FROM fault_injection_counter
                    WHERE writes >= {failingWrite.ToString(CultureInfo.InvariantCulture)};
                END;
                """);
            return new(factory);
        }

        public async ValueTask DisposeAsync() => await ExecuteAsync(factory, """
            DROP TRIGGER fault_injection;
            DROP TABLE fault_injection_counter;
            """);

        private static async Task ExecuteAsync(SqliteConnectionFactory factory, string sql)
        {
            await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The fixture catalog with the two items renamed, so a republication has something to change.</summary>
    private sealed class RenamedItemsHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _fixtures = new(new FixtureApiHandler());

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await _fixtures.SendAsync(request, cancellationToken);
            if (request.RequestUri?.AbsolutePath.Trim('/') != "regular/items_en")
            {
                return response;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return FixtureApiHandler.Json(body
                .Replace("Salewa first aid kit", "Salewa field kit", StringComparison.Ordinal)
                .Replace("Corrugated hose", "Corrugated hose, long", StringComparison.Ordinal));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fixtures.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
