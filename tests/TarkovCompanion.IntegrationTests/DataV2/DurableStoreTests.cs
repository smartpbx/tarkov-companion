using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Infrastructure.Persistence;
using TarkovCompanion.Infrastructure.Persistence.Repositories;

namespace TarkovCompanion.IntegrationTests.DataV2;

[Collection(SqliteCollection.Name)]
public sealed class DurableStoreTests
{
    [Fact]
    public async Task ProfileWorkspaceCompareAndSwapRoundTripsNormalizedProgress()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteProfileWorkspaceStore(database.Factory);
        var profileId = Guid.NewGuid();
        var snapshot = new ProfileWorkspaceSnapshot(1, profileId,
        [
            new ProfileRecord(
                new(new(profileId, "wipe-2026"), ProfileGameMode.Pvp, new("2026.2"),
                    new("en-US", "US", "America/New_York"), new("catalog:abc", new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero))),
                "Primary",
                new(42, new Dictionary<string, int> { ["prapor"] = 3 }, ["quest-a"],
                    new Dictionary<string, int> { ["objective-a"] = 4 },
                    new Dictionary<string, int> { ["workbench"] = 2 }, ["item-a"],
                    new Dictionary<string, int> { ["item-a"] = 7 },
                    new Dictionary<string, string> { ["item-a"] = "event-ready" },
                    new Dictionary<string, string> { ["item-a"] = "keep" },
                    [new("quest", "quest-a", 0, "next")]),
                ProfileLifecycle.Active,
                new(2026, 9, 15, 1, 0, 0, TimeSpan.Zero)),
        ]);

        Assert.True(await store.TryReplaceAsync(0, snapshot, TestContext.Current.CancellationToken));
        Assert.False(await store.TryReplaceAsync(0, snapshot, TestContext.Current.CancellationToken));
        var restored = await new SqliteProfileWorkspaceStore(database.Factory).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, restored.Revision);
        Assert.Equal(profileId, restored.ActiveProfileId);
        Assert.Equal(7, restored.ActiveProfile.Progress.OwnedItemCounts["item-a"]);
        Assert.Equal("next", restored.ActiveProfile.Progress.Pins.Single().Note);
    }

    [Fact]
    public async Task DurableOutboxSurvivesStoreRestartDeduplicatesAndLeasesAggregateHead()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 2, 0, 0, TimeSpan.Zero);
        var first = Item("one", "aggregate", 1, now);
        var second = Item("two", "aggregate", 2, now);
        var store = new SqliteOutboxStore(database.Factory, capacity: 10, completedRetention: 2);
        var receipts = await store.EnqueueBatchAsync([first, second], TestContext.Current.CancellationToken);
        Assert.All(receipts, receipt => Assert.True(receipt.Added));
        var duplicate = await store.EnqueueAsync(Item("one", "different", 1, now), TestContext.Current.CancellationToken);
        Assert.False(duplicate.Added);
        Assert.Equal(first.OperationId, duplicate.OperationId);

        var restarted = new SqliteOutboxStore(database.Factory, capacity: 10, completedRetention: 2);
        var leased = await restarted.LeaseNextAsync(now, TimeSpan.FromSeconds(30), 10, TestContext.Current.CancellationToken);
        var head = Assert.Single(leased);
        Assert.Equal(first.OperationId, head.Item.OperationId);
        Assert.True(await restarted.CompleteAsync(head.Item.OperationId, head.LeaseToken!.Value, now.AddSeconds(1), TestContext.Current.CancellationToken));
        var next = Assert.Single(await restarted.LeaseNextAsync(now.AddSeconds(1), TimeSpan.FromSeconds(30), 10, TestContext.Current.CancellationToken));
        Assert.Equal(second.OperationId, next.Item.OperationId);
    }

    [Fact]
    public async Task DurableAggregateSequencesSurviveCompletedRowPruning()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 2, 0, 0, TimeSpan.Zero);
        var store = new SqliteOutboxStore(database.Factory, completedRetention: 0);
        var first = Item("first-sequence", "durable-sequence", 1, now);
        await store.EnqueueAsync(first, TestContext.Current.CancellationToken);
        var leased = Assert.Single(await store.LeaseNextAsync(
            now,
            TimeSpan.FromSeconds(10),
            1,
            TestContext.Current.CancellationToken));
        Assert.True(await store.CompleteAsync(
            first.OperationId,
            leased.LeaseToken!.Value,
            now.AddSeconds(1),
            TestContext.Current.CancellationToken));
        Assert.Empty(await store.ListAsync(TestContext.Current.CancellationToken));

        var restarted = new SqliteOutboxStore(database.Factory, completedRetention: 0);
        var heads = await restarted.ReadAggregateSequenceHeadsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, heads[new("durable-sequence")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.EnqueueAsync(
            Item("reused-sequence", "durable-sequence", 1, now.AddSeconds(2)),
            TestContext.Current.CancellationToken));
        Assert.True((await restarted.EnqueueAsync(
            Item("next-sequence", "durable-sequence", 2, now.AddSeconds(2)),
            TestContext.Current.CancellationToken)).Added);
    }

    [Fact]
    public async Task DurableRetryFencesAtTransitionTimeAndKeepsLaterEligibility()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 2, 0, 0, TimeSpan.Zero);
        var item = Item("retry", "retry-aggregate", 1, now);
        var store = new SqliteOutboxStore(database.Factory);
        await store.EnqueueAsync(item, TestContext.Current.CancellationToken);
        var leased = Assert.Single(await store.LeaseNextAsync(
            now,
            TimeSpan.FromSeconds(10),
            1,
            TestContext.Current.CancellationToken));
        var fault = new RuntimeFault(
            RuntimeFailureKind.Transient,
            new("test-retry"),
            RuntimeRecoveryAction.RetryAutomatically,
            new("test:retry"),
            now.AddSeconds(1));

        Assert.True(await store.RetryAsync(
            item.OperationId,
            leased.LeaseToken!.Value,
            now.AddSeconds(1),
            now.AddSeconds(30),
            fault,
            TestContext.Current.CancellationToken));
        var retrying = Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(OutboxDeliveryState.Retrying, retrying.State);
        Assert.Equal(now.AddSeconds(30), retrying.NextAttemptUtc);
        Assert.Null(retrying.LeaseToken);
        Assert.Empty(await store.LeaseNextAsync(now.AddSeconds(29), TimeSpan.FromSeconds(10), 1, TestContext.Current.CancellationToken));
        Assert.Single(await store.LeaseNextAsync(now.AddSeconds(30), TimeSpan.FromSeconds(10), 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredDurableLeaseCannotBeRenewedOrSettled()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 2, 0, 0, TimeSpan.Zero);
        var item = Item("expired", "expired-aggregate", 1, now);
        var store = new SqliteOutboxStore(database.Factory);
        await store.EnqueueAsync(item, TestContext.Current.CancellationToken);
        var leased = Assert.Single(await store.LeaseNextAsync(
            now,
            TimeSpan.FromSeconds(5),
            1,
            TestContext.Current.CancellationToken));
        var token = leased.LeaseToken!.Value;
        var expiredAt = now.AddSeconds(5);
        var fault = new RuntimeFault(
            RuntimeFailureKind.Transient,
            new("expired-lease"),
            RuntimeRecoveryAction.RetryAutomatically,
            new("test:expired-lease"),
            expiredAt);

        Assert.False(await store.RenewLeaseAsync(item.OperationId, token, expiredAt, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(await store.CompleteAsync(item.OperationId, token, expiredAt, TestContext.Current.CancellationToken));
        Assert.False(await store.RetryAsync(item.OperationId, token, expiredAt, expiredAt.AddSeconds(1), fault, TestContext.Current.CancellationToken));
        Assert.False(await store.DeadLetterAsync(item.OperationId, token, fault, expiredAt, TestContext.Current.CancellationToken));
        Assert.Equal(OutboxDeliveryState.Processing, Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task LeaseRenewalIsFencedAndExplicitDeadLetterResolutionReleasesAggregate()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 2, 0, 0, TimeSpan.Zero);
        var first = Item("renew-one", "renew-aggregate", 1, now);
        var second = Item("renew-two", "renew-aggregate", 2, now);
        var store = new SqliteOutboxStore(database.Factory);
        await store.EnqueueBatchAsync([first, second], TestContext.Current.CancellationToken);
        var leased = Assert.Single(await store.LeaseNextAsync(now, TimeSpan.FromSeconds(10), 10, TestContext.Current.CancellationToken));
        Assert.False(await store.RenewLeaseAsync(first.OperationId, OutboxLeaseToken.New(), now, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.True(await store.RenewLeaseAsync(first.OperationId, leased.LeaseToken!.Value, now, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var afterRenewal = (await store.ListAsync(TestContext.Current.CancellationToken)).Single(item => item.Item.OperationId == first.OperationId);
        Assert.Equal(now.AddSeconds(30), afterRenewal.LeaseExpiresUtc);

        var fault = new RuntimeFault(RuntimeFailureKind.Validation, new("test-dead-letter"), RuntimeRecoveryAction.None,
            new("test:dead-letter"), now.AddSeconds(1));
        Assert.True(await store.DeadLetterAsync(first.OperationId, leased.LeaseToken.Value, fault, now.AddSeconds(1), TestContext.Current.CancellationToken));
        Assert.Empty(await store.LeaseNextAsync(now.AddSeconds(2), TimeSpan.FromSeconds(10), 10, TestContext.Current.CancellationToken));
        Assert.True(await store.ResolveDeadLetterAsync(first.OperationId, now.AddSeconds(3), TestContext.Current.CancellationToken));
        var released = Assert.Single(await store.LeaseNextAsync(now.AddSeconds(3), TimeSpan.FromSeconds(10), 10, TestContext.Current.CancellationToken));
        Assert.Equal(second.OperationId, released.Item.OperationId);
    }

    [Fact]
    public async Task NestedInventoryKeepsNullableUnknownsAndUnknownJsonFieldsExactly()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var profile = Guid.NewGuid();
        const string payload = "{\"known\":1,\"futureField\":{\"shape\":\"v3\"}}";
        var snapshot = new ObservedInventorySnapshot(
            Guid.NewGuid(), profile, "wipe-a", "Pvp", null, null,
            new(2026, 9, 15, 3, 0, 0, TimeSpan.Zero), "manual-import", "2.0.0",
            null, null, true, payload, "{\"futureTop\":true}",
            [
                new("stash", null, "stash", null, null, null, null, null, null, null, null, "{\"layout\":\"unknown\"}"),
                new("bag", "stash", "container", "bag-item", 1, 2, 4, 5, null, null, .8, "{\"futureNode\":17}"),
                new("item", "bag", "item", "item-a", 0, 0, 1, 1, 2, null, null, "{\"unknownWeightReason\":\"not-published\"}"),
            ]);
        await store.SaveInventorySnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        var restored = await store.ReadCurrentInventoryAsync(profile, "wipe-a", "Pvp", TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.Equal(payload, restored.PayloadJson);
        Assert.Null(restored.Coverage);
        Assert.Null(restored.Nodes.Single(node => node.NodeId == "item").WeightKg);
        Assert.Equal("{\"futureNode\":17}", restored.Nodes.Single(node => node.NodeId == "bag").RawJson);
    }

    [Fact]
    public async Task NonFiniteEvidenceAndLivePredictionClaimsAreRefused()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var inventory = new ObservedInventorySnapshot(
            Guid.NewGuid(), Guid.NewGuid(), "wipe", "Pvp", null, null, DateTimeOffset.UtcNow,
            "manual", "2", null, double.NaN, true, "{}", "{}", []);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveInventorySnapshotAsync(inventory, TestContext.Current.CancellationToken));

        var model = new ModelSnapshotRecord(
            Guid.NewGuid(), null, null, null, "raid-risk", "predicted", "live-detector", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, .5, .7, .6, "model-1", "{}", "{}");
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveModelSnapshotAsync(model, TestContext.Current.CancellationToken));
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM model_snapshots;"));
    }

    [Fact]
    public async Task CraftPlanningModelAndRetentionStoresRoundTripNullableProvenance()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var now = new DateTimeOffset(2026, 9, 15, 3, 30, 0, TimeSpan.Zero);
        var craft = new CraftHistoryRecord(Guid.NewGuid(), "craft-a", "workbench", 2, null, now,
            "item-a", null, null, 45000, "json.tarkov.dev/crafts", "{\"futureCostModel\":\"unknown\"}");
        await store.AppendCraftHistoryAsync(craft, TestContext.Current.CancellationToken);
        var restoredCraft = Assert.Single(await store.ListCraftHistoryAsync("craft-a", TestContext.Current.CancellationToken));
        Assert.Null(restoredCraft.ObservedUtc);
        Assert.Null(restoredCraft.EstimatedCostRoubles);
        Assert.Equal(craft.PayloadJson, restoredCraft.PayloadJson);

        var planId = Guid.NewGuid();
        var plan = new LoadoutPlanRecord(planId, Guid.NewGuid(), "wipe", "Pvp", "Factory", 1, now,
            "{\"items\":[]}", "{\"futurePlanner\":true}");
        Assert.True(await store.TrySaveLoadoutPlanAsync(0, plan, TestContext.Current.CancellationToken));
        Assert.False(await store.TrySaveLoadoutPlanAsync(0, plan, TestContext.Current.CancellationToken));
        Assert.Equal(plan.ExtensionJson, (await store.ReadLoadoutPlanAsync(planId, TestContext.Current.CancellationToken))!.ExtensionJson);

        var model = new ModelSnapshotRecord(Guid.NewGuid(), plan.ProfileId, "wipe", "Pvp", "raid-risk", "predicted",
            "historical-raids", null, now.AddDays(-1), now, .5, null, .7, "risk-1", "{\"risk\":null}", "{\"future\":1}");
        await store.SaveModelSnapshotAsync(model, TestContext.Current.CancellationToken);
        var restoredModel = Assert.Single(await store.ListModelSnapshotsAsync(plan.ProfileId, "raid-risk", 10, TestContext.Current.CancellationToken));
        Assert.Null(restoredModel.Confidence);
        Assert.Equal(model.DataThroughUtc, restoredModel.DataThroughUtc);

        var policy = new DataRetentionPolicy("local", true, 24, false, 90, now, "{\"futureRetention\":true}");
        await store.SaveRetentionPolicyAsync(policy, TestContext.Current.CancellationToken);
        Assert.Equal(policy.ExtensionJson, (await store.ReadRetentionPolicyAsync("local", TestContext.Current.CancellationToken))!.ExtensionJson);
        var screenshotStore = new SqliteScreenshotRetentionStore(database.Factory);
        await screenshotStore.SaveAsync(new ScreenshotRetentionSettings(false, 48), TestContext.Current.CancellationToken);
        Assert.Equal(new ScreenshotRetentionSettings(false, 48), await screenshotStore.GetAsync(TestContext.Current.CancellationToken));
        Assert.False((await store.ReadRetentionPolicyAsync("local", TestContext.Current.CancellationToken))!.DebugCaptureEnabled);
    }

    [Fact]
    public async Task ManualAndObservedRaidFieldsRemainDistinctWithNullableObservationMetadata()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var raidId = await SeedRaidAsync(database.Factory);
        var store = new SqliteV2DataStore(database.Factory);
        var recorded = new DateTimeOffset(2026, 9, 15, 4, 0, 0, TimeSpan.Zero);
        await store.AppendRaidFieldAsync(new(0, raidId, "outcome", "{\"value\":\"survived\"}", "manual", "player", null, recorded, null), TestContext.Current.CancellationToken);
        await store.AppendRaidFieldAsync(new(0, raidId, "outcome", "{\"value\":\"unknown\"}", "observed", "eft-log", recorded.AddMinutes(-1), recorded, .6), TestContext.Current.CancellationToken);
        var fields = await store.ListRaidFieldsAsync(raidId, TestContext.Current.CancellationToken);
        Assert.Equal(["manual", "observed"], fields.Select(field => field.ProvenanceKind));
        Assert.Null(fields[0].ObservedUtc);
        Assert.Null(fields[0].Confidence);
    }

    [Fact]
    public async Task RaidTargetOperationLedgerMakesSideEffectAndReplayMarkerAtomic()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var service = new SqliteRaidHistoryService(database.Factory);
        var raidId = Guid.NewGuid();
        var operation = OperationId.New();
        var entry = new TarkovCompanion.Core.Domain.Raids.RaidHistoryEntry(
            raidId, Guid.NewGuid(), null, "Regular", DateTimeOffset.UtcNow, null, null, null);
        await service.ApplyOnceAsync(operation, OutboxCommandKind.RaidStarted, raidId,
            token => service.StartAsync(entry, token), TestContext.Current.CancellationToken);
        var replayCalled = false;
        await service.ApplyOnceAsync(operation, OutboxCommandKind.RaidStarted, raidId,
            _ => { replayCalled = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.False(replayCalled);
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, $"SELECT COUNT(*) FROM raids WHERE id = '{raidId:D}';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, $"SELECT COUNT(*) FROM outbox_target_operations WHERE operation_id = '{operation}';"));
    }

    private static OutboxItem Item(string key, string aggregate, long sequence, DateTimeOffset now)
    {
        var operation = OperationId.New();
        return new(operation, new(key), CorrelationId.New(), new("test"), OutboxCommandKind.RaidEnded,
            OutboxContractVersion.Current, new(aggregate), sequence, now, now, now.AddHours(1),
            OutboxPayload.CreateGenericJson("{\"state\":\"queued\"}"), OutboxAttemptPolicy.Default);
    }

    private static async Task<Guid> SeedRaidAsync(SqliteConnectionFactory factory)
    {
        var service = new SqliteRaidHistoryService(factory);
        var raidId = Guid.NewGuid();
        await service.StartAsync(new(raidId, Guid.NewGuid(), null, "Regular", DateTimeOffset.UtcNow, null, null, null), TestContext.Current.CancellationToken);
        return raidId;
    }
}
