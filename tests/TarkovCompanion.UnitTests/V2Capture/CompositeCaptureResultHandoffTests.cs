using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Profiles;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// The composite is the one <see cref="ICaptureResultHandoff"/> #271's coordinator resolves;
/// these tests exercise its routing directly, without a full capture session.
/// </summary>
public sealed class CompositeCaptureResultHandoffTests
{
    private static readonly CaptureContextMetadata CaptureContext = new("raid", null, "map-a", null, null, null, "desktop");

    [Theory]
    [InlineData(ScanIntent.ExtractsAndMap)]
    [InlineData(ScanIntent.HealthAndCharacter)]
    public async Task IntentsWithNoOwningHandoffAreAcknowledgedWithoutError(ScanIntent intent)
    {
        var result = await (await CompositeAsync()).AcceptAsync(Request(intent), CancellationToken.None);

        Assert.Equal(CaptureHandoffDisposition.DurablyAccepted, result.Disposition);
    }

    /// <summary>
    /// [V2 rough package 60 — Intel scan] #287. These four used to fall into the default arm and
    /// produce nothing at all, so "Understand this screen" understood nothing. They now reach the
    /// Intel handoff, which publishes whatever item the frame was read as.
    /// </summary>
    [Theory]
    [InlineData(ScanIntent.Auto)]
    [InlineData(ScanIntent.Ammo)]
    [InlineData(ScanIntent.Keys)]
    [InlineData(ScanIntent.QuestItems)]
    public async Task AnIdentifiedItemReachesIntel(ScanIntent intent)
    {
        var intel = new IntelCaptureHandoff();
        CaptureItemIdentification? published = null;
        intel.ItemIdentified += (_, identification) => published = identification;

        var result = await (await CompositeAsync(intel)).AcceptAsync(
            Request(intent, Identified("5447a9cd4bdc2dbd208b4567", "M4A1", 0.86)),
            CancellationToken.None);

        Assert.Equal(CaptureHandoffDisposition.DurablyAccepted, result.Disposition);
        Assert.NotNull(published);
        Assert.Equal("5447a9cd4bdc2dbd208b4567", published.Best.CanonicalId);
        Assert.Equal(intent, published.EffectiveIntent);
    }

    /// <summary>
    /// Intake turns a detected item screen into the Loot intent. One named item and no lattice
    /// is not a container, and used to reach the Loot Scan as an empty grid.
    /// </summary>
    [Fact]
    public async Task AnUnarmedItemScreenHandedOffAsLootStillOpensIntel()
    {
        var intel = new IntelCaptureHandoff();
        CaptureItemIdentification? published = null;
        intel.ItemIdentified += (_, identification) => published = identification;

        await (await CompositeAsync(intel)).AcceptAsync(
            Request(ScanIntent.Loot, Identified("5447a9cd4bdc2dbd208b4567", "M4A1", 0.86)),
            CancellationToken.None);

        Assert.Equal("5447a9cd4bdc2dbd208b4567", published?.Best.CanonicalId);
    }

    [Fact]
    public async Task AnIntelCaptureThatIdentifiedNothingOpensNothing()
    {
        var intel = new IntelCaptureHandoff();
        var published = 0;
        intel.ItemIdentified += (_, _) => published++;

        await (await CompositeAsync(intel)).AcceptAsync(Request(ScanIntent.Auto), CancellationToken.None);

        Assert.Equal(0, published);
    }

    [Fact]
    public async Task TheAlternatesTravelWithTheAnswer()
    {
        var intel = new IntelCaptureHandoff();
        CaptureItemIdentification? published = null;
        intel.ItemIdentified += (_, identification) => published = identification;

        await (await CompositeAsync(intel)).AcceptAsync(
            Request(
                ScanIntent.Auto,
                Identified("item-a", "Graphics card", 0.71),
                Identified("item-b", "Graphics tablet", 0.64)),
            CancellationToken.None);

        Assert.NotNull(published);
        Assert.Equal("item-a", published.Best.CanonicalId);
        Assert.Equal(["item-b"], published.Alternates.Select(alternate => alternate.CanonicalId));
    }

    private static CaptureIdentifiedItem Identified(string id, string name, double confidence) =>
        new(id, name, new Confidence(confidence), $"line={name}");

    private static async Task<CompositeCaptureResultHandoff> CompositeAsync(IntelCaptureHandoff? intel = null)
    {
        var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        return new CompositeCaptureResultHandoff(
            new LootScanCaptureHandoff(runtime, new InventoryGridReconstructor(), new LootScanDecisionService()),
            new StashScanCaptureHandoff(
                runtime,
                new InventoryGridReconstructor(),
                new StashScanWorkflow(new StashScanAssembler(), new UnusedSnapshotStore(), new StashSnapshotComparer())),
            intel ?? new IntelCaptureHandoff());
    }

    private static CaptureHandoffRequest Request(ScanIntent intent, params CaptureIdentifiedItem[] identified)
    {
        var now = DateTimeOffset.Parse("2026-09-17T00:00:00Z");
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "fixture://capture",
            now,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("fixture", "1"));
        var analysis = new CaptureAnalysis(
            "fixture-result",
            null,
            false,
            true,
            null,
            new Confidence(0.9),
            Identified: identified);
        return new CaptureHandoffRequest(
            new CaptureSessionId(Guid.NewGuid()),
            "capture-fixture",
            analysis,
            CaptureContext,
            CaptureCorrelationId.New(),
            CaptureSourceKind.GameWrittenScreenshot,
            now,
            now,
            null,
            CaptureDeliveryKind.WatchedFile,
            provenance,
            0,
            CaptureReviewAction.UseArmedIntent,
            intent,
            new CaptureCorrection(CaptureReviewAction.UseArmedIntent, intent, null, 0, now, "fixture"));
    }

    private sealed class UnusedSnapshotStore : IStashSnapshotStore
    {
        public Task SaveAsync(StashSnapshotRecord snapshot, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StashSnapshotRecord?> ReadCurrentAsync(InventoryProfileScope scope, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StashSnapshotRecord?> ReadAsync(InventoryProfileScope scope, Guid snapshotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StashSnapshotSummary>> ListAsync(InventoryProfileScope scope, int maximumCount, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StashSnapshotDeleteResult> DeleteAsync(InventoryProfileScope scope, Guid snapshotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StashSnapshotRetentionResult> ApplyRetentionAsync(InventoryProfileScope scope, DateTimeOffset retainFromUtc, bool dryRun, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
