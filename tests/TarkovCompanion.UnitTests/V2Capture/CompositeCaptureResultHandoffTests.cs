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
    [InlineData(ScanIntent.Ammo)]
    [InlineData(ScanIntent.Keys)]
    [InlineData(ScanIntent.QuestItems)]
    [InlineData(ScanIntent.Auto)]
    public async Task IntentsWithNoOwningHandoffAreAcknowledgedWithoutError(ScanIntent intent)
    {
        using var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        using var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        var composite = new CompositeCaptureResultHandoff(
            new LootScanCaptureHandoff(runtime, new InventoryGridReconstructor(), new LootScanDecisionService()),
            new StashScanCaptureHandoff(
                runtime,
                new InventoryGridReconstructor(),
                new StashScanWorkflow(new StashScanAssembler(), new UnusedSnapshotStore(), new StashSnapshotComparer())));

        var result = await composite.AcceptAsync(Request(intent), CancellationToken.None);

        Assert.Equal(CaptureHandoffDisposition.DurablyAccepted, result.Disposition);
    }

    private static CaptureHandoffRequest Request(ScanIntent intent)
    {
        var now = DateTimeOffset.Parse("2026-09-17T00:00:00Z");
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "fixture://capture",
            now,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("fixture", "1"));
        var analysis = new CaptureAnalysis("fixture-result", null, false, true, null, new Confidence(0.9));
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
