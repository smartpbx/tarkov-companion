using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.Persistence.Stash;
using TarkovCompanion.IntegrationTests.DataV2;

namespace TarkovCompanion.IntegrationTests.Stash;

public sealed class StashSnapshotPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DirectAndDerivedVisibleCaptureRootsRoundTrip()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var direct = Visible(EvidenceSourceClass.GameWrittenScreenshot, "fixture://shot-1");
        var nested = Derived(
            "fixture://nested-stitch",
            [Visible(EvidenceSourceClass.ExternalVisiblePixels, "fixture://window-2")]);
        var derived = Derived("fixture://stash-stitch", [direct, nested]);
        var snapshot = Snapshot(derived, isCurrent: true);

        await store.SaveInventorySnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        var restored = await store.ReadCurrentInventoryAsync(
            snapshot.ProfileId,
            snapshot.Generation,
            snapshot.GameMode,
            TestContext.Current.CancellationToken);

        Assert.NotNull(restored);
        Assert.Equal("catalog-snapshot", restored.DataSnapshotId);
        Assert.NotEqual(restored.Recognition.Result.Value!.SnapshotId, restored.DataSnapshotId);
        Assert.Equal(EvidenceSourceClass.DerivedCalculation, restored.Recognition.Result.Provenance.SourceClass);
        Assert.Equal(2, restored.Recognition.Result.Provenance.Inputs.Count);

        await store.SaveInventorySnapshotAsync(
            Snapshot(direct, isCurrent: false),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExplicitBlankDataSnapshotIdIsRejectedInsteadOfUsingRecognitionIdentity()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var snapshot = Snapshot(
            Visible(EvidenceSourceClass.GameWrittenScreenshot, "fixture://shot"),
            isCurrent: true) with
        {
            DataSnapshotId = " ",
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveInventorySnapshotAsync(
            snapshot,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LegacyInventoryWriterWithoutExplicitDataSnapshotIdKeepsPriorFallback()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);
        var snapshot = Snapshot(
            Visible(EvidenceSourceClass.GameWrittenScreenshot, "fixture://legacy-shot"),
            isCurrent: true) with
        {
            DataSnapshotId = null,
        };

        await store.SaveInventorySnapshotAsync(snapshot, TestContext.Current.CancellationToken);
        var restored = await store.ReadCurrentInventoryAsync(
            snapshot.ProfileId,
            snapshot.Generation,
            snapshot.GameMode,
            TestContext.Current.CancellationToken);

        Assert.NotNull(restored);
        Assert.Equal(restored.Recognition.Result.Value!.SnapshotId, restored.DataSnapshotId);
    }

    private static IEnumerable<EvidenceProvenance> RejectedRoots()
    {
        var screenshot = Visible(EvidenceSourceClass.GameWrittenScreenshot, "fixture://shot");
        yield return Derived("fixture://mixed-user", [screenshot, Direct(EvidenceSourceClass.UserEntered)]);
        yield return Derived("fixture://mixed-log", [screenshot, Direct(EvidenceSourceClass.GameWrittenLog)]);
        yield return Derived("fixture://mixed-unknown", [screenshot, Direct(EvidenceSourceClass.Unknown)]);
        yield return Direct(EvidenceSourceClass.UserEntered);
        yield return Direct(EvidenceSourceClass.GameWrittenLog);
        yield return Direct(EvidenceSourceClass.Unknown);
        yield return new EvidenceProvenance(
            EvidenceSourceClass.ModelledEstimate,
            "fixture://model",
            Now,
            new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.6, "fixture-calibration"),
            new ProducerIdentity("fixture-model", "1", "model-1"),
            dataThroughUtc: Now.AddMinutes(-1),
            generatedUtc: Now,
            coverage: new EvidenceCoverage(10, 0.5, "fixture model coverage"));
    }

    [Fact]
    public async Task DerivedInventoryRootRejectsAnyNonVisibleLeaf()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteV2DataStore(database.Factory);

        foreach (var provenance in RejectedRoots())
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveInventorySnapshotAsync(
                Snapshot(provenance, isCurrent: true),
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task LifecycleListsDryRunsPrunesAndPromotesWithoutDeletingCurrentHistory()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new SqliteStashSnapshotStore(
            database.Factory,
            new SqliteV2DataStore(database.Factory));
        var scope = new InventoryProfileScope(
            Guid.Parse("84000000-0000-0000-0000-000000000001"),
            "wipe-2026-09",
            "Pvp");
        var oldest = Record(scope, Now.AddDays(-10), false, "oldest");
        var prior = Record(scope, Now.AddDays(-2), true, "prior");
        await store.SaveAsync(oldest, TestContext.Current.CancellationToken);
        await store.SaveAsync(prior, TestContext.Current.CancellationToken);

        var listed = await store.ListAsync(scope, 10, TestContext.Current.CancellationToken);
        Assert.Equal(2, listed.Count);
        Assert.All(listed, summary => Assert.Equal("catalog-snapshot", summary.DataSnapshotId));
        var preview = await store.ApplyRetentionAsync(
            scope,
            Now.AddDays(-5),
            true,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, preview.MatchedSnapshots);
        Assert.Equal(0, preview.DeletedSnapshots);
        Assert.NotNull(await store.ReadAsync(scope, oldest.SnapshotId, TestContext.Current.CancellationToken));

        var pruned = await store.ApplyRetentionAsync(
            scope,
            Now.AddDays(-5),
            false,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, pruned.DeletedSnapshots);
        Assert.Null(await store.ReadAsync(scope, oldest.SnapshotId, TestContext.Current.CancellationToken));
        Assert.Equal(prior.SnapshotId, (await store.ReadCurrentAsync(scope, TestContext.Current.CancellationToken))!.SnapshotId);

        var newest = Record(scope, Now, true, "newest");
        await store.SaveAsync(newest, TestContext.Current.CancellationToken);
        var deleted = await store.DeleteAsync(scope, newest.SnapshotId, TestContext.Current.CancellationToken);
        Assert.True(deleted.Deleted);
        Assert.Equal(prior.SnapshotId, deleted.PromotedSnapshotId);
        Assert.Equal(prior.SnapshotId, (await store.ReadCurrentAsync(scope, TestContext.Current.CancellationToken))!.SnapshotId);
    }

    private static StashSnapshotRecord Record(
        InventoryProfileScope scope,
        DateTimeOffset recordedUtc,
        bool isCurrent,
        string suffix)
    {
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            $"fixture://{suffix}",
            recordedUtc.AddMinutes(-1),
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
            new ProducerIdentity("fixture-ocr", "1"));
        var stored = Snapshot(provenance, isCurrent, scope, recordedUtc, suffix);
        return new StashSnapshotRecord(
            stored.SnapshotId,
            scope,
            stored.DataSnapshotId!,
            recordedUtc,
            isCurrent,
            stored.Recognition);
    }

    private static ObservedInventorySnapshot Snapshot(
        EvidenceProvenance provenance,
        bool isCurrent,
        InventoryProfileScope? scope = null,
        DateTimeOffset? recordedUtc = null,
        string suffix = "snapshot")
    {
        scope ??= new InventoryProfileScope(
            Guid.Parse("84000000-0000-0000-0000-000000000002"),
            "wipe-2026-09",
            "Pvp");
        var recognition = Envelope(provenance, suffix);
        return new ObservedInventorySnapshot(
            Guid.NewGuid(),
            scope.ProfileId,
            scope.Generation,
            scope.GameMode,
            recordedUtc ?? Now.AddMinutes(1),
            isCurrent,
            recognition,
            "catalog-snapshot");
    }

    private static RecognitionResultEnvelope<StashRecognition> Envelope(
        EvidenceProvenance rootProvenance,
        string suffix)
    {
        var fieldProvenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            $"fixture://field-{suffix}",
            rootProvenance.ObservedUtc,
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
            new ProducerIdentity("fixture-ocr", "1"));
        var stash = new StashRecognition(
            $"stash-{suffix}",
            [],
            [],
            Unknown<long?>("stash.value", fieldProvenance),
            Complete<int?>("stash.unresolved", 0, fieldProvenance));
        var header = new RecognitionResultHeader(
            $"result-{suffix}",
            V2ContractVersion.Current,
            new CaptureSessionId(Guid.NewGuid()),
            $"artifact-{suffix}",
            rootProvenance.ObservedUtc.AddMinutes(-1),
            ScanIntent.Stash,
            Complete<RecognizedContext?>("context", RecognizedContext.Stash, fieldProvenance));
        return new RecognitionResultEnvelope<StashRecognition>(
            header,
            Complete("stash", stash, rootProvenance));
    }

    private static EvidenceProvenance Visible(EvidenceSourceClass sourceClass, string source) => new(
        sourceClass,
        source,
        Now,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
        new ProducerIdentity("fixture-ocr", "1"));

    private static EvidenceProvenance Direct(EvidenceSourceClass sourceClass) => new(
        sourceClass,
        $"fixture://{sourceClass}",
        Now,
        EvidenceConfidence.Unscored,
        new ProducerIdentity("fixture-source", "1"));

    private static EvidenceProvenance Derived(
        string source,
        IReadOnlyList<EvidenceProvenance> inputs) => new(
        EvidenceSourceClass.DerivedCalculation,
        source,
        Now,
        EvidenceConfidence.Unscored,
        new ProducerIdentity("fixture-stitcher", "1"),
        generatedUtc: Now,
        coverage: new EvidenceCoverage(inputs.Count, description: "fixture inputs"),
        inputs: inputs);

    private static EvidencedValue<T> Complete<T>(
        string fieldId,
        T value,
        EvidenceProvenance provenance) => new(
        fieldId,
        value,
        new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
        provenance);

    private static EvidencedValue<T> Unknown<T>(string fieldId, EvidenceProvenance provenance) => new(
        fieldId,
        default,
        new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current),
        provenance);
}
