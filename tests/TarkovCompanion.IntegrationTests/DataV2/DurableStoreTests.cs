using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
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
                new(2026, 9, 15, 1, 0, 0, TimeSpan.Zero),
                "{\"futureProfileField\":true}"),
        ]);

        Assert.True(await store.TryReplaceAsync(0, snapshot, TestContext.Current.CancellationToken));
        Assert.False(await store.TryReplaceAsync(0, snapshot, TestContext.Current.CancellationToken));
        var restored = await new SqliteProfileWorkspaceStore(database.Factory).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, restored.Revision);
        Assert.Equal(profileId, restored.ActiveProfileId);
        Assert.Equal(7, restored.ActiveProfile.Progress.OwnedItemCounts["item-a"]);
        Assert.Equal("next", restored.ActiveProfile.Progress.Pins.Single().Note);
        Assert.Equal("{\"futureProfileField\":true}", restored.ActiveProfile.ExtensionJson);

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryReplaceAsync(
            1,
            RevisedWorkspace(snapshot, 1, "Same revision"),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryReplaceAsync(
            1,
            RevisedWorkspace(snapshot, 3, "Skipped revision"),
            TestContext.Current.CancellationToken));

        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> ReplaceConcurrentlyAsync(string name)
        {
            await release.Task;
            return await new SqliteProfileWorkspaceStore(database.Factory).TryReplaceAsync(
                1,
                RevisedWorkspace(snapshot, 2, name),
                TestContext.Current.CancellationToken);
        }

        var writers = new[]
        {
            ReplaceConcurrentlyAsync("Writer A"),
            ReplaceConcurrentlyAsync("Writer B"),
        };
        release.SetResult(true);
        var results = await Task.WhenAll(writers);
        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        var afterRace = await store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, afterRace.Revision);
        Assert.Contains(afterRace.ActiveProfile.Name, new[] { "Writer A", "Writer B" });
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
    public async Task DurableDeadLetterConsumesCapacityButCanRetryWithoutNewAdmission()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 2, 0, 0, TimeSpan.Zero);
        var item = Item("capacity-dead-letter", "capacity-aggregate", 1, now);
        var store = new SqliteOutboxStore(database.Factory, capacity: 1);
        await store.EnqueueAsync(item, TestContext.Current.CancellationToken);
        var leased = Assert.Single(await store.LeaseNextAsync(
            now,
            TimeSpan.FromSeconds(10),
            1,
            TestContext.Current.CancellationToken));
        var fault = new RuntimeFault(
            RuntimeFailureKind.Validation,
            new("test-capacity-dead-letter"),
            RuntimeRecoveryAction.RetryManually,
            new("test:capacity-dead-letter"),
            now.AddSeconds(1));
        Assert.True(await store.DeadLetterAsync(
            item.OperationId,
            leased.LeaseToken!.Value,
            fault,
            now.AddSeconds(1),
            TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<OutboxCapacityException>(() => store.EnqueueAsync(
            Item("capacity-refused", "other-aggregate", 1, now.AddSeconds(2)),
            TestContext.Current.CancellationToken));
        Assert.True(await store.ManualRetryAsync(
            item.OperationId,
            now.AddSeconds(2),
            TestContext.Current.CancellationToken));
        Assert.Equal(
            OutboxDeliveryState.Retrying,
            Assert.Single(await store.ListAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task DurableSnapshotPrioritizesRetryableDeadLettersAndMarksExpiredRows()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 2, 0, 0, TimeSpan.Zero);
        var expired = Enumerable.Range(0, 17)
            .Select(index => Item($"expired-{index}", $"expired-{index}", 1, now))
            .ToArray();
        var retryable = Item(
            "retryable",
            "retryable",
            1,
            now,
            now.AddHours(3));
        var store = new SqliteOutboxStore(database.Factory, capacity: 18);
        await store.EnqueueBatchAsync([.. expired, retryable], TestContext.Current.CancellationToken);
        var leased = await store.LeaseNextAsync(
            now,
            TimeSpan.FromMinutes(1),
            18,
            TestContext.Current.CancellationToken);
        Assert.Equal(18, leased.Length);
        foreach (var entry in leased)
        {
            Assert.True(await store.DeadLetterAsync(
                entry.Item.OperationId,
                entry.LeaseToken!.Value,
                new(
                    RuntimeFailureKind.Validation,
                    new("test-dead-letter-order"),
                    RuntimeRecoveryAction.RetryManually,
                    new("test:dead-letter-order"),
                    now.AddSeconds(1)),
                now.AddSeconds(1),
                TestContext.Current.CancellationToken));
        }

        var snapshot = await store.GetSnapshotAsync(
            now.AddHours(2),
            TestContext.Current.CancellationToken);
        Assert.Equal(18, snapshot.Counts.DeadLetter);
        Assert.Equal(OutboxSnapshot.MaxListedDeadLetters, snapshot.DeadLetters.Length);
        Assert.Equal(retryable.OperationId, snapshot.DeadLetters[0].OperationId);
        Assert.True(snapshot.DeadLetters[0].CanRetry);
        Assert.All(snapshot.DeadLetters.Skip(1), deadLetter => Assert.False(deadLetter.CanRetry));
    }

    [Fact]
    public async Task DuplicateReplayRemainsIdempotentWhenAReopenedStoreHasALowerCapacity()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var now = new DateTimeOffset(2026, 9, 15, 2, 0, 0, TimeSpan.Zero);
        var first = Item("lower-capacity-first", "lower-capacity-first", 1, now);
        var second = Item("lower-capacity-second", "lower-capacity-second", 1, now);
        var original = new SqliteOutboxStore(database.Factory, capacity: 2);
        await original.EnqueueBatchAsync([first, second], TestContext.Current.CancellationToken);
        var leased = Assert.Single(await original.LeaseNextAsync(
            now,
            TimeSpan.FromMinutes(1),
            1,
            TestContext.Current.CancellationToken));
        Assert.Equal(first.OperationId, leased.Item.OperationId);
        Assert.True(await original.DeadLetterAsync(
            first.OperationId,
            leased.LeaseToken!.Value,
            new(
                RuntimeFailureKind.Validation,
                new("test-lower-capacity"),
                RuntimeRecoveryAction.RetryManually,
                new("test:lower-capacity"),
                now.AddSeconds(1)),
            now.AddSeconds(1),
            TestContext.Current.CancellationToken));

        var reopened = new SqliteOutboxStore(database.Factory, capacity: 1);
        var duplicate = await reopened.EnqueueAsync(
            Item("lower-capacity-first", "different-aggregate", 1, now.AddSeconds(1)),
            TestContext.Current.CancellationToken);
        Assert.False(duplicate.Added);
        Assert.Equal(first.OperationId, duplicate.OperationId);
        await Assert.ThrowsAsync<OutboxCapacityException>(() => reopened.EnqueueAsync(
            Item("lower-capacity-new", "lower-capacity-new", 1, now.AddSeconds(1)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TypedStashRecognitionRoundTripsWithEvidenceAndDerivedNodes()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var profile = Guid.NewGuid();
        var provenance = ScreenshotProvenance();
        var bag = RecognizedItem("bag-item", provenance);
        var rootGrid = UnreadGrid(
            new GridCellRecognition(
                new GridCellAddress(2, 1),
                Complete("stash.item", bag, provenance),
                "stash/bag-a"));
        var regions = new[]
        {
            Region("root-region", 0, "stash", rootGrid, provenance),
            Region("bag-region", 2, "stash/bag-a", UnreadGrid(), provenance),
        };
        var coverage = new[]
        {
            Coverage("stash", 70, 680, provenance),
            Coverage("stash/bag-a", 20, 20, provenance),
        };
        var recognition = StashEnvelope("stash-contract-1", regions, coverage, provenance);
        var snapshot = new ObservedInventorySnapshot(
            Guid.NewGuid(), profile, "wipe-a", "Pvp", RecordedUtc, true, recognition);

        await store.SaveInventorySnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        var restored = await store.ReadCurrentInventoryAsync(profile, "wipe-a", "Pvp", TestContext.Current.CancellationToken);
        Assert.NotNull(restored);
        Assert.Equal(
            JsonSerializer.Serialize(recognition, V2ContractJson.Options),
            JsonSerializer.Serialize(restored.Recognition, V2ContractJson.Options));
        Assert.Equal(EvidenceSourceClass.GameWrittenScreenshot, restored.Recognition.Result.Provenance.SourceClass);
        Assert.Null(restored.Recognition.Result.Value!.TotalKnownValueRoubles.Value);
        Assert.Equal("stash/bag-a", restored.Recognition.Result.Value.CapturedRegions[1].ContainerPath);
        Assert.Equal(3, await V2TestDatabase.ScalarAsync(
            database.Factory,
            $"SELECT COUNT(*) FROM observed_inventory_nodes WHERE snapshot_id = '{snapshot.SnapshotId:D}';"));
    }

    [Fact]
    public async Task InventoryRegionAndContainerBoundsAcceptTheLimitAndRejectLimitPlusOne()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var provenance = ScreenshotProvenance();
        var exactRegions = Enumerable.Range(0, SqliteV2DataStore.MaximumInventoryRegions)
            .Select(index => Region($"region-{index}", index, "stash", UnreadGrid(), provenance))
            .ToArray();
        var oneCoverage = new[] { Coverage("stash", 0, GridGeometry.MaxCells, provenance) };
        await store.SaveInventorySnapshotAsync(
            InventorySnapshot(StashEnvelope("region-limit", exactRegions, oneCoverage, provenance), isCurrent: false),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveInventorySnapshotAsync(
            InventorySnapshot(
                StashEnvelope(
                    "region-limit-plus-one",
                    exactRegions.Append(Region("region-over", exactRegions.Length, "stash", UnreadGrid(), provenance)).ToArray(),
                    oneCoverage,
                    provenance),
                isCurrent: false),
            TestContext.Current.CancellationToken));

        var exactCoverage = Enumerable.Range(0, SqliteV2DataStore.MaximumInventoryContainers)
            .Select(index => Coverage($"stash-{index}", 0, 1, provenance))
            .ToArray();
        await store.SaveInventorySnapshotAsync(
            InventorySnapshot(StashEnvelope("container-limit", [], exactCoverage, provenance), isCurrent: false),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveInventorySnapshotAsync(
            InventorySnapshot(
                StashEnvelope(
                    "container-limit-plus-one",
                    [],
                    exactCoverage.Append(Coverage("stash-over", 0, 1, provenance)).ToArray(),
                    provenance),
                isCurrent: false),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InventoryNodeBoundAcceptsTheLimitAndRejectsLimitPlusOne()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var provenance = ScreenshotProvenance();
        var unknownItem = Unknown<RecognizedItem>("stash.unknown-item", provenance);
        GridCellRecognition[] Cells(int count) => Enumerable.Range(0, count)
            .Select(index => new GridCellRecognition(
                new GridCellAddress(index / GridGeometry.MaxColumns, index % GridGeometry.MaxColumns),
                unknownItem))
            .ToArray();

        var exactCellCount = SqliteV2DataStore.MaximumInventoryNodes - 1;
        var exact = StashEnvelope(
            "node-limit",
            [Region("region", 0, "stash", UnreadGrid(Cells(exactCellCount)), provenance)],
            [Coverage("stash", exactCellCount, GridGeometry.MaxCells, provenance)],
            provenance);
        var exactSnapshot = InventorySnapshot(exact, isCurrent: false);
        await store.SaveInventorySnapshotAsync(exactSnapshot, TestContext.Current.CancellationToken);
        Assert.Equal(SqliteV2DataStore.MaximumInventoryNodes, await V2TestDatabase.ScalarAsync(
            database.Factory,
            $"SELECT COUNT(*) FROM observed_inventory_nodes WHERE snapshot_id = '{exactSnapshot.SnapshotId:D}';"));

        var over = StashEnvelope(
            "node-limit-plus-one",
            [Region("region", 0, "stash", UnreadGrid(Cells(exactCellCount + 1)), provenance)],
            [Coverage("stash", exactCellCount + 1, GridGeometry.MaxCells, provenance)],
            provenance);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveInventorySnapshotAsync(
            InventorySnapshot(over, isCurrent: false),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InventoryStringGridAndJsonBoundsAcceptTheLimitAndRejectLimitPlusOne()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var provenance = ScreenshotProvenance();
        var exactString = new string('s', SqliteV2DataStore.MaximumContractStringUtf8Bytes);
        var exactStringSnapshot = InventorySnapshot(StashEnvelope(exactString, [], [], provenance));
        await store.SaveInventorySnapshotAsync(exactStringSnapshot, TestContext.Current.CancellationToken);
        Assert.Equal(exactString, (await store.ReadCurrentInventoryAsync(
            exactStringSnapshot.ProfileId,
            exactStringSnapshot.Generation,
            exactStringSnapshot.GameMode,
            TestContext.Current.CancellationToken))!.Recognition.Result.Value!.SnapshotId);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveInventorySnapshotAsync(
            InventorySnapshot(StashEnvelope(exactString + "s", [], [], provenance), isCurrent: false),
            TestContext.Current.CancellationToken));

        var boundaryCell = new GridCellRecognition(
            new GridCellAddress(GridGeometry.MaxRows - 1, GridGeometry.MaxColumns - 1),
            Unknown<RecognizedItem>("stash.boundary-item", provenance));
        var gridRecognition = StashEnvelope(
            "grid-boundary",
            [Region("grid-region", 0, "stash", UnreadGrid(boundaryCell), provenance)],
            [Coverage("stash", 1, GridGeometry.MaxCells, provenance)],
            provenance);
        var gridSnapshot = InventorySnapshot(gridRecognition);
        await store.SaveInventorySnapshotAsync(gridSnapshot, TestContext.Current.CancellationToken);
        var hostileGrid = JsonSerializer.SerializeToNode(gridRecognition, V2ContractJson.Options)!;
        hostileGrid["result"]!["value"]!["capturedRegions"]![0]!["grid"]!["cells"]![0]!["anchor"]!["row"] =
            GridGeometry.MaxRows;
        await SetInventoryPayloadAsync(database.Factory, gridSnapshot.SnapshotId, hostileGrid.ToJsonString());
        await AssertContractReadRejectedAsync(() => store.ReadCurrentInventoryAsync(
            gridSnapshot.ProfileId,
            gridSnapshot.Generation,
            gridSnapshot.GameMode,
            TestContext.Current.CancellationToken));

        var jsonRecognition = StashEnvelope("json-boundary", [], [], provenance);
        var jsonSnapshot = InventorySnapshot(jsonRecognition);
        await store.SaveInventorySnapshotAsync(jsonSnapshot, TestContext.Current.CancellationToken);
        var canonical = JsonSerializer.Serialize(jsonRecognition, V2ContractJson.Options);
        var canonicalBytes = Encoding.UTF8.GetByteCount(canonical);
        Assert.True(canonicalBytes < SqliteV2DataStore.MaximumContractJsonBytes);
        var exactJson = canonical + new string(' ', SqliteV2DataStore.MaximumContractJsonBytes - canonicalBytes);
        Assert.Equal(SqliteV2DataStore.MaximumContractJsonBytes, Encoding.UTF8.GetByteCount(exactJson));
        await SetInventoryPayloadAsync(database.Factory, jsonSnapshot.SnapshotId, exactJson);
        Assert.NotNull(await store.ReadCurrentInventoryAsync(
            jsonSnapshot.ProfileId,
            jsonSnapshot.Generation,
            jsonSnapshot.GameMode,
            TestContext.Current.CancellationToken));

        await SetInventoryPayloadAsync(database.Factory, jsonSnapshot.SnapshotId, exactJson + " ");
        await AssertContractReadRejectedAsync(() => store.ReadCurrentInventoryAsync(
            jsonSnapshot.ProfileId,
            jsonSnapshot.Generation,
            jsonSnapshot.GameMode,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HistoricalAndModelledContractsRoundTripFullTypedLineage()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var profileId = Guid.NewGuid();
        var historical = HistoricalTraffic();
        var modelled = ModelledEncounter();
        var historicalSnapshot = new HistoricalIntelligenceSnapshot<ZoneTrafficIntensity>(
            Guid.NewGuid(), profileId, "wipe", "Pvp", historical);
        var modelledSnapshot = new ModelledIntelligenceSnapshot<EncounterLikelihood>(
            Guid.NewGuid(), profileId, "wipe", "Pvp", modelled);

        await store.SaveModelSnapshotAsync(historicalSnapshot, TestContext.Current.CancellationToken);
        await store.SaveModelSnapshotAsync(modelledSnapshot, TestContext.Current.CancellationToken);

        var restoredHistorical = Assert.Single(await store.ListHistoricalModelSnapshotsAsync<ZoneTrafficIntensity>(
            profileId, "wipe", "Pvp", 10, TestContext.Current.CancellationToken));
        var restoredModelled = Assert.Single(await store.ListModelledModelSnapshotsAsync<EncounterLikelihood>(
            profileId, "wipe", "Pvp", 10, TestContext.Current.CancellationToken));
        Assert.Equal(historical.Value.Provenance, restoredHistorical.Intelligence.Value.Provenance);
        Assert.Equal(historical.Inputs.Single().Provenance, restoredHistorical.Intelligence.Inputs.Single().Provenance);
        Assert.Equal(IntelligenceInputKind.PrivateLocalFeedback, restoredHistorical.Intelligence.Inputs.Single().Kind);
        Assert.Equal(modelled.Estimate.Provenance, restoredModelled.Intelligence.Estimate.Provenance);
        Assert.Equal(modelled.Inputs.Single().Provenance, restoredModelled.Intelligence.Estimate.Provenance.Inputs.Single());
        Assert.Equal(
            "fixture-calibration",
            restoredModelled.Intelligence.Estimate.Provenance.Confidence.CalibrationReference);
        Assert.Equal("Historical route estimate", restoredModelled.Intelligence.Explanation);
        Assert.Equal(2, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM model_snapshots;"));
        Assert.Equal(2, await V2TestDatabase.ScalarAsync(
            database.Factory,
            "SELECT COUNT(*) FROM model_snapshots WHERE calibration IS NULL;"));
    }

    [Fact]
    public async Task IntelligencePersistenceRejectsLiveClaimsAndFreeFormRows()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var live = ModelledEncounter("fixture://live-detector");
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveModelSnapshotAsync(
            new ModelledIntelligenceSnapshot<EncounterLikelihood>(Guid.NewGuid(), null, null, null, live),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, await V2TestDatabase.ScalarAsync(database.Factory, "SELECT COUNT(*) FROM model_snapshots;"));

        var valid = new ModelledIntelligenceSnapshot<EncounterLikelihood>(
            Guid.NewGuid(), null, null, null, ModelledEncounter());
        await store.SaveModelSnapshotAsync(valid, TestContext.Current.CancellationToken);
        await SetModelPayloadAsync(database.Factory, valid.ModelSnapshotId, "{}");
        await AssertContractReadRejectedAsync(() => store.ListModelledModelSnapshotsAsync<EncounterLikelihood>(
            null, null, null, 10, TestContext.Current.CancellationToken));

        var missingEvidence = JsonSerializer.SerializeToNode(valid.Intelligence, V2ContractJson.Options)!;
        missingEvidence["estimate"]!["provenance"] = null;
        await SetModelPayloadAsync(database.Factory, valid.ModelSnapshotId, missingEvidence.ToJsonString());
        await AssertContractReadRejectedAsync(() => store.ListModelledModelSnapshotsAsync<EncounterLikelihood>(
            null, null, null, 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CraftPlanningAndRetentionStoresRoundTripNullableProvenance()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var now = new DateTimeOffset(2026, 9, 15, 3, 30, 0, TimeSpan.Zero);
        var craft = new CraftHistoryRecord(Guid.NewGuid(), "craft-a", "workbench", 2, null, now,
            "item-a", null, null, 45000, "json.tarkov.dev/crafts", "{\"futureCostModel\":\"unknown\"}");
        await store.AppendCraftHistoryAsync(craft, TestContext.Current.CancellationToken);
        var restoredCraft = Assert.Single(await store.ListCraftHistoryAsync(
            "craft-a", 10, TestContext.Current.CancellationToken));
        Assert.Null(restoredCraft.ObservedUtc);
        Assert.Null(restoredCraft.EstimatedCostRoubles);
        Assert.Equal(craft.PayloadJson, restoredCraft.PayloadJson);

        var planId = Guid.NewGuid();
        var plan = new LoadoutPlanRecord(planId, Guid.NewGuid(), "wipe", "Pvp", "Factory", 1, now,
            "{\"items\":[]}", "{\"futurePlanner\":true}");
        Assert.True(await store.TrySaveLoadoutPlanAsync(0, plan, TestContext.Current.CancellationToken));
        Assert.False(await store.TrySaveLoadoutPlanAsync(0, plan, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => store.TrySaveLoadoutPlanAsync(
            1,
            plan,
            TestContext.Current.CancellationToken));
        var revisedPlan = plan with { Revision = 2, Name = "Factory revised" };
        Assert.False(await store.TrySaveLoadoutPlanAsync(
            1,
            revisedPlan with { ProfileId = Guid.NewGuid() },
            TestContext.Current.CancellationToken));
        Assert.False(await store.TrySaveLoadoutPlanAsync(
            1,
            revisedPlan with { Generation = "different-wipe" },
            TestContext.Current.CancellationToken));
        Assert.Null(await store.ReadLoadoutPlanAsync(
            planId, Guid.NewGuid(), plan.Generation, plan.GameMode,
            TestContext.Current.CancellationToken));
        Assert.Null(await store.ReadLoadoutPlanAsync(
            planId, plan.ProfileId, "different-wipe", plan.GameMode,
            TestContext.Current.CancellationToken));
        Assert.Null(await store.ReadLoadoutPlanAsync(
            planId, plan.ProfileId, plan.Generation, "Pve",
            TestContext.Current.CancellationToken));
        Assert.True(await store.TrySaveLoadoutPlanAsync(1, revisedPlan, TestContext.Current.CancellationToken));
        Assert.False(await store.TrySaveLoadoutPlanAsync(1, revisedPlan, TestContext.Current.CancellationToken));
        Assert.Equal(plan.ExtensionJson, (await store.ReadLoadoutPlanAsync(
            planId, plan.ProfileId, plan.Generation, plan.GameMode,
            TestContext.Current.CancellationToken))!.ExtensionJson);
        var readOverload = Assert.Single(typeof(SqliteV2DataStore).GetMethods()
            .Where(method => method.Name == nameof(SqliteV2DataStore.ReadLoadoutPlanAsync)));
        Assert.Equal(
            [typeof(Guid), typeof(Guid), typeof(string), typeof(string), typeof(CancellationToken)],
            readOverload.GetParameters().Select(parameter => parameter.ParameterType));

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
        var fields = await store.ListRaidFieldsAsync(raidId, 10, TestContext.Current.CancellationToken);
        Assert.Equal(["manual", "observed"], fields.Select(field => field.ProvenanceKind));
        Assert.Null(fields[0].ObservedUtc);
        Assert.Null(fields[0].Confidence);
    }

    [Fact]
    public async Task HistoryReadsReturnOnlyBoundedNewestWindows()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var raidId = await SeedRaidAsync(database.Factory);
        var store = new SqliteV2DataStore(database.Factory);
        var now = new DateTimeOffset(2026, 9, 15, 5, 0, 0, TimeSpan.Zero);
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            var recorded = now.AddMinutes(sequence);
            await store.AppendRaidFieldAsync(
                new(0, raidId, "sequence", $"{{\"value\":{sequence}}}", "manual", "fixture", null, recorded, null),
                TestContext.Current.CancellationToken);
            await store.AppendCraftHistoryAsync(
                new(Guid.NewGuid(), "bounded-craft", null, null, null, recorded, null, null, null, null,
                    "fixture", $"{{\"value\":{sequence}}}"),
                TestContext.Current.CancellationToken);
        }

        var raidWindow = await store.ListRaidFieldsAsync(raidId, 2, TestContext.Current.CancellationToken);
        Assert.Equal(["{\"value\":2}", "{\"value\":3}"], raidWindow.Select(row => row.ValueJson));
        var craftWindow = await store.ListCraftHistoryAsync(
            "bounded-craft", 2, TestContext.Current.CancellationToken);
        Assert.Equal(["{\"value\":3}", "{\"value\":2}"], craftWindow.Select(row => row.PayloadJson));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ListRaidFieldsAsync(raidId, 0, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ListCraftHistoryAsync(
                "bounded-craft",
                SqliteV2DataStore.MaximumCraftHistoryReadCount + 1,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CorruptDurableRowsFailAsInvalidDataInsteadOfBeingCoerced()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var now = new DateTimeOffset(2026, 9, 15, 6, 0, 0, TimeSpan.Zero);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.AppendCraftHistoryAsync(
                new(Guid.NewGuid(), "invalid-craft", null, null, null, default, null, null, null, null,
                    "fixture", "{}"),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.AppendCraftHistoryAsync(
                new(Guid.NewGuid(), "invalid-craft", null, null, null, now, null, null, -1, null,
                    "fixture", "{}"),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RecordLocalJsonRecoveryAsync(
                new("invalid", "current", now, new string('A', 64), "fixture"),
                TestContext.Current.CancellationToken));
        var raidId = await SeedRaidAsync(database.Factory);
        await store.AppendRaidFieldAsync(
            new(0, raidId, "state", "{}", "manual", "fixture", null, now, null),
            TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(
            database.Factory,
            "UPDATE raid_field_history SET recorded_utc = 'not-a-time' WHERE raid_id = $id;",
            ("$id", raidId.ToString("D")));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ListRaidFieldsAsync(raidId, 1, TestContext.Current.CancellationToken));

        await store.AppendCraftHistoryAsync(
            new(Guid.NewGuid(), "poisoned-craft", null, 1, null, now, null, 1, 1, 1, "fixture", "{}"),
            TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(
            database.Factory,
            "PRAGMA ignore_check_constraints = ON; UPDATE craft_history SET station_level = 1.5 WHERE craft_id = 'poisoned-craft'; PRAGMA ignore_check_constraints = OFF;");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ListCraftHistoryAsync("poisoned-craft", 1, TestContext.Current.CancellationToken));

        await store.SaveRetentionPolicyAsync(
            new("poisoned-policy", true, 24, false, 30, now, "{}"),
            TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(
            database.Factory,
            "PRAGMA ignore_check_constraints = ON; UPDATE retention_policies SET debug_capture_enabled = 2 WHERE policy_key = 'poisoned-policy'; PRAGMA ignore_check_constraints = OFF;");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ReadRetentionPolicyAsync("poisoned-policy", TestContext.Current.CancellationToken));

        await store.RecordLocalJsonRecoveryAsync(
            new("profile", "current", now, new string('a', 64), "fixture"),
            TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(
            database.Factory,
            "PRAGMA ignore_check_constraints = ON; UPDATE local_json_recovery SET content_sha256 = 'not-a-hash' WHERE document_key = 'profile'; PRAGMA ignore_check_constraints = OFF;");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ReadLocalJsonRecoveryAsync("profile", TestContext.Current.CancellationToken));

        var inventory = InventorySnapshot(StashEnvelope(
            "poisoned-inventory-metadata",
            [],
            [],
            ScreenshotProvenance()));
        await store.SaveInventorySnapshotAsync(inventory, TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(
            database.Factory,
            "UPDATE observed_inventory_snapshots SET source = x'37' WHERE snapshot_id = $id;",
            ("$id", inventory.SnapshotId.ToString("D")));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadCurrentInventoryAsync(
            inventory.ProfileId,
            inventory.Generation,
            inventory.GameMode,
            TestContext.Current.CancellationToken));

        var intelligence = new ModelledIntelligenceSnapshot<EncounterLikelihood>(
            Guid.NewGuid(),
            null,
            null,
            null,
            ModelledEncounter());
        await store.SaveModelSnapshotAsync(intelligence, TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(
            database.Factory,
            "UPDATE model_snapshots SET source = x'37' WHERE model_snapshot_id = $id;",
            ("$id", intelligence.ModelSnapshotId.ToString("D")));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ListModelledModelSnapshotsAsync<EncounterLikelihood>(
                null,
                null,
                null,
                1,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProfileWorkspaceRejectsSentinelOverflowAndNumericEnumText()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(
            database.Factory,
            """
            INSERT INTO profile_workspaces(workspace_key, revision, active_profile_id, updated_utc)
            VALUES (1, 0, NULL, '2026-09-15T00:00:00.0000000+00:00');
            WITH RECURSIVE sequence(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM sequence WHERE value < 65
            )
            INSERT INTO profile_contexts(
                profile_id, generation, name, game_mode, wipe_season, language, region, time_zone,
                data_snapshot_id, data_snapshot_published_utc, level, lifecycle, updated_utc, extension_json)
            SELECT printf('00000000-0000-0000-0000-%012x', value), 'wipe', 'Profile ' || value,
                   'Pvp', '2026.2', 'en-US', 'US', 'UTC', 'snapshot',
                   '2026-09-15T00:00:00.0000000+00:00', 1, 'Active',
                   '2026-09-15T00:00:00.0000000+00:00', '{}'
            FROM sequence;
            """);
        var store = new SqliteProfileWorkspaceStore(database.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ReadAsync(TestContext.Current.CancellationToken));

        await ExecuteSqlAsync(
            database.Factory,
            """
            DELETE FROM profile_contexts;
            PRAGMA ignore_check_constraints = ON;
            INSERT INTO profile_contexts(
                profile_id, generation, name, game_mode, wipe_season, language, region, time_zone,
                data_snapshot_id, data_snapshot_published_utc, level, lifecycle, updated_utc, extension_json)
            VALUES ('00000000-0000-0000-0000-000000000001', 'wipe', 'Poisoned', '1', '2026.2',
                    'en-US', 'US', 'UTC', 'snapshot', '2026-09-15T00:00:00.0000000+00:00',
                    1, 'Active', '2026-09-15T00:00:00.0000000+00:00', '{}');
            PRAGMA ignore_check_constraints = OFF;
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.ReadAsync(TestContext.Current.CancellationToken));
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
        var conflictCalled = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyOnceAsync(
            operation,
            OutboxCommandKind.RaidEnded,
            raidId,
            _ => { conflictCalled = true; return Task.CompletedTask; },
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyOnceAsync(
            operation,
            OutboxCommandKind.RaidStarted,
            Guid.NewGuid(),
            _ => { conflictCalled = true; return Task.CompletedTask; },
            TestContext.Current.CancellationToken));
        Assert.False(conflictCalled);
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, $"SELECT COUNT(*) FROM raids WHERE id = '{raidId:D}';"));
        Assert.Equal(1, await V2TestDatabase.ScalarAsync(database.Factory, $"SELECT COUNT(*) FROM outbox_target_operations WHERE operation_id = '{operation}';"));
    }

    private static readonly DateTimeOffset CapturedUtc =
        new(2026, 9, 15, 3, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ObservedUtc = CapturedUtc.AddMinutes(1);

    private static readonly DateTimeOffset RecordedUtc = ObservedUtc.AddMinutes(1);

    private static ProfileWorkspaceSnapshot RevisedWorkspace(
        ProfileWorkspaceSnapshot original,
        long revision,
        string profileName) => new(
        revision,
        original.ActiveProfileId,
        original.Profiles.Select(profile => new ProfileRecord(
            profile.Context,
            profileName,
            profile.Progress,
            profile.Lifecycle,
            profile.UpdatedUtc,
            profile.ExtensionJson)).ToArray());

    private static ObservedInventorySnapshot InventorySnapshot(
        RecognitionResultEnvelope<StashRecognition> recognition,
        bool isCurrent = true) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "wipe",
        "Pvp",
        RecordedUtc,
        isCurrent,
        recognition);

    private static RecognitionResultEnvelope<StashRecognition> StashEnvelope(
        string snapshotId,
        IReadOnlyList<StashCaptureRegion> regions,
        IReadOnlyList<StashContainerCoverage> coverage,
        EvidenceProvenance provenance)
    {
        var stash = new StashRecognition(
            snapshotId,
            regions,
            coverage,
            Unknown<long?>("stash.total", provenance),
            Complete<int?>("stash.unresolved", 0, provenance));
        var header = new RecognitionResultHeader(
            "stash-result",
            V2ContractVersion.Current,
            new CaptureSessionId(Guid.NewGuid()),
            "stash-artifact",
            CapturedUtc,
            ScanIntent.Stash,
            Complete<RecognizedContext?>("stash.context", RecognizedContext.Stash, provenance));
        return new(header, Complete("stash.result", stash, provenance));
    }

    private static StashCaptureRegion Region(
        string regionId,
        int ordinal,
        string containerPath,
        GridRecognition grid,
        EvidenceProvenance provenance) => new(
        regionId,
        $"artifact-{ordinal}",
        ordinal,
        containerPath,
        Complete<GridCellAddress?>("stash.origin", new GridCellAddress(0, 0), provenance),
        grid);

    private static StashContainerCoverage Coverage(
        string containerPath,
        int observedCells,
        int totalCells,
        EvidenceProvenance provenance) => new(
        containerPath,
        Complete<int?>("stash.coverage.observed", observedCells, provenance),
        Complete<int?>("stash.coverage.total", totalCells, provenance));

    private static GridRecognition UnreadGrid(params GridCellRecognition[] cells)
    {
        var provenance = ScreenshotProvenance();
        return new(
            new GridGeometry(
                Unknown<int?>("stash.grid.rows", provenance),
                Unknown<int?>("stash.grid.columns", provenance),
                Unknown<int?>("stash.grid.cellWidth", provenance),
                Unknown<int?>("stash.grid.cellHeight", provenance)),
            cells);
    }

    private static RecognizedItem RecognizedItem(string id, EvidenceProvenance provenance) => new(
        Complete("stash.item.id", id, provenance),
        Complete("stash.item.name", "Bag", provenance),
        Complete<int?>("stash.item.quantity", 1, provenance),
        Complete<int?>("stash.item.width", 1, provenance),
        Complete<int?>("stash.item.height", 1, provenance),
        Complete<bool?>("stash.item.rotated", false, provenance),
        Complete<bool?>("stash.item.foundInRaid", true, provenance),
        Complete("stash.item.condition", ItemConditionReading.NotApplicable, provenance));

    private static EvidencedValue<T> Complete<T>(string fieldId, T value, EvidenceProvenance provenance) => new(
        fieldId,
        value,
        new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
        provenance);

    private static EvidencedValue<T> Unknown<T>(string fieldId, EvidenceProvenance provenance) => new(
        fieldId,
        default,
        new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current),
        provenance);

    private static EvidenceProvenance ScreenshotProvenance() => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        "fixture://stash-screen",
        ObservedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.94),
        new ProducerIdentity("fixture-stash-recognizer", "2.0"),
        coverage: new EvidenceCoverage(fraction: 0.75, description: "Visible stash cells"));

    private static HistoricalIntelligence<ZoneTrafficIntensity> HistoricalTraffic()
    {
        var inputProvenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenLog,
            "fixture://own-raid-history",
            CapturedUtc.AddDays(-2),
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture-log-reader", "2.0"));
        var input = new IntelligenceInputReference(
            "own-raid-history",
            IntelligenceInputKind.PrivateLocalFeedback,
            inputProvenance);
        var provenance = IntelligenceProvenance(
            EvidenceSourceClass.HistoricalAggregate,
            "fixture://historical-traffic",
            inputProvenance);
        return new(
            "customs-dorms-history",
            Complete(
                "traffic.zone",
                new ZoneTrafficIntensity("customs", "dorms", RaidPhase.Mid, 0.45),
                provenance),
            [input]);
    }

    private static ModelledIntelligence<EncounterLikelihood> ModelledEncounter(
        string sourceIdentifier = "fixture://encounter-model")
    {
        var inputProvenance = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "fixture://map-topology",
            CapturedUtc.AddDays(-2),
            EvidenceConfidence.Certain,
            new ProducerIdentity("fixture-catalog", "2.0"));
        var input = new IntelligenceInputReference(
            "map-topology",
            IntelligenceInputKind.StaticMapData,
            inputProvenance);
        var provenance = IntelligenceProvenance(
            EvidenceSourceClass.ModelledEstimate,
            sourceIdentifier,
            inputProvenance);
        return new(
            "customs-dorms-estimate",
            Complete(
                "traffic.encounter",
                new EncounterLikelihood("customs", "dorms", RaidPhase.Mid, 0.62),
                provenance),
            [input],
            "Historical route estimate");
    }

    private static EvidenceProvenance IntelligenceProvenance(
        EvidenceSourceClass sourceClass,
        string sourceIdentifier,
        params EvidenceProvenance[] inputs) => new(
        sourceClass,
        sourceIdentifier,
        ObservedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.72, "fixture-calibration"),
        new ProducerIdentity("fixture-intelligence", "2.0", "traffic-model-4"),
        CapturedUtc.AddDays(-1),
        CapturedUtc,
        new EvidenceCoverage(240, 0.8, "Historical route samples"),
        inputs: inputs);

    private static async Task SetInventoryPayloadAsync(
        SqliteConnectionFactory factory,
        Guid snapshotId,
        string payloadJson)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE observed_inventory_snapshots SET payload_json = $payload WHERE snapshot_id = $id;";
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static async Task ExecuteSqlAsync(
        SqliteConnectionFactory factory,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SetModelPayloadAsync(
        SqliteConnectionFactory factory,
        Guid snapshotId,
        string payloadJson)
    {
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE model_snapshots SET payload_json = $payload WHERE model_snapshot_id = $id;";
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static async Task AssertContractReadRejectedAsync(Func<Task> read)
    {
        var failure = await Record.ExceptionAsync(read);
        Assert.NotNull(failure);
        Assert.True(
            failure is JsonException or ArgumentException or InvalidDataException ||
            failure.GetBaseException() is ArgumentException,
            failure.ToString());
    }

    private static OutboxItem Item(
        string key,
        string aggregate,
        long sequence,
        DateTimeOffset now,
        DateTimeOffset? expiresUtc = null)
    {
        var operation = OperationId.New();
        return new(operation, new(key), CorrelationId.New(), new("test"), OutboxCommandKind.RaidEnded,
            OutboxContractVersion.Current, new(aggregate), sequence, now, now, expiresUtc ?? now.AddHours(1),
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
