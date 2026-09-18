using System.Security.Cryptography;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Stash;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Profiles;
using TarkovCompanion.UnitTests.Runtime;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// Exercises #273's Stash Scan wiring: a reviewed Stash-intent capture becomes one real,
/// pixel-derived <see cref="StashScanCaptureFrame"/> and a saved snapshot, instead of #382's
/// placeholder empty request.
/// </summary>
public sealed class StashScanCaptureHandoffTests
{
    private static readonly WorkspaceOrigin Origin = new(
        new(Guid.Parse("33333333-3333-3333-3333-333333333333")),
        new(Guid.Parse("44444444-4444-4444-4444-444444444444")),
        WorkspaceOriginKind.DesktopApplication,
        "stash-scan-handoff-tests");

    private static readonly CaptureContextMetadata CaptureContext = new(
        "raid",
        null,
        "map-a",
        null,
        null,
        null,
        "desktop");

    [Fact]
    public async Task StashIntentWithARealGridSavesASnapshotInsteadOfAnEmptyOne()
    {
        var store = new MemorySnapshotStore();
        using var runtime = await ReadyProfileContextAsync();
        var handoff = new StashScanCaptureHandoff(
            runtime,
            new InventoryGridReconstructor(),
            new StashScanWorkflow(new StashScanAssembler(), store, new StashSnapshotComparer()));

        await using var harness = new Harness(handoff, RecognizedContext.Stash, ScanIntent.Stash, RealStashGrid());
        await harness.CaptureAsync();

        Assert.NotNull(store.Saved);
        var stash = store.Saved!.Recognition.Result.Value!;
        Assert.Single(stash.CapturedRegions);
        Assert.Equal("stash", stash.CapturedRegions[0].ContainerPath);
    }

    [Fact]
    public async Task NonStashIntentIsAcknowledgedWithoutSavingASnapshot()
    {
        var store = new MemorySnapshotStore();
        using var runtime = await ReadyProfileContextAsync();
        var handoff = new StashScanCaptureHandoff(
            runtime,
            new InventoryGridReconstructor(),
            new StashScanWorkflow(new StashScanAssembler(), store, new StashSnapshotComparer()));

        await using var harness = new Harness(handoff, RecognizedContext.Loot, ScanIntent.Loot);
        var receipt = await harness.CaptureAsync();

        Assert.Equal(CaptureQueueDisposition.Accepted, receipt.Disposition);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task StashIntentWithoutAnActiveProfileIsAcknowledgedWithoutSavingASnapshot()
    {
        var store = new MemorySnapshotStore();
        using var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        using var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        var handoff = new StashScanCaptureHandoff(
            runtime,
            new InventoryGridReconstructor(),
            new StashScanWorkflow(new StashScanAssembler(), store, new StashSnapshotComparer()));

        await using var harness = new Harness(handoff, RecognizedContext.Stash, ScanIntent.Stash, RealStashGrid());
        var receipt = await harness.CaptureAsync();

        Assert.Equal(CaptureQueueDisposition.Accepted, receipt.Disposition);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task WhileAGuidedScanCollectsAStashCaptureJoinsItAndSavesNothingOfItsOwn()
    {
        var store = new MemorySnapshotStore();
        using var runtime = await ReadyProfileContextAsync();
        var guided = GuidedScan(store);
        await guided.StartAsync(new(Id(402), "generation-a", "Pvp"), "data-1", CancellationToken.None);
        var handoff = new StashScanCaptureHandoff(
            runtime,
            new InventoryGridReconstructor(),
            new StashScanWorkflow(new StashScanAssembler(), store, new StashSnapshotComparer()),
            guidedScan: guided);

        // The anchor detector called the screen a flea listing; it was armed as Stash and carries
        // a Stash grid, and a scroll-through must not lose a screen to that.
        await using var harness = new Harness(handoff, RecognizedContext.Flea, ScanIntent.Stash, RealStashGrid());
        await harness.CaptureAsync();

        Assert.Null(store.Saved);
        Assert.Equal(1, guided.Current.Screenshots);

        await guided.FinishAsync(CancellationToken.None);
        Assert.NotNull(store.Saved);
        Assert.Single(store.Saved!.Recognition.Result.Value!.CapturedRegions);
    }

    [Fact]
    public async Task TheStashIntentIsHeldArmedOnlyWhileAScanIsActivelyBeingTaken()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-17T00:00:00Z"));
        var guided = GuidedScan(new MemorySnapshotStore());
        await using var coordinator = new CaptureSessionCoordinator(
            new InlineCaptureWorkScheduler(),
            new StubPipeline(RecognizedContext.Stash),
            new CompositeFreeHandoff(),
            Origin,
            clock);
        using var arming = new GuidedStashScanArming(coordinator, guided, Origin, clock);

        // No scan: asking to resume arms nothing.
        arming.Resume();
        Assert.DoesNotContain(coordinator.Snapshot.Sessions, session => !session.IsTerminal);

        // A scan read back from disk waits until the player says to keep going.
        await guided.StartAsync(new(Id(402), "generation-a", "Pvp"), "data-1", CancellationToken.None);
        arming.Pause();
        Assert.False(arming.IsActive);
        Assert.DoesNotContain(coordinator.Snapshot.Sessions, session => !session.IsTerminal);

        arming.Resume();
        var armed = Assert.Single(coordinator.Snapshot.Sessions, session => !session.IsTerminal);
        Assert.Equal(ScanIntent.Stash, armed.Request.Intent);

        // Left alone past the idle limit, it lets the one armed slot go.
        clock.Advance(GuidedStashScanArming.IdleAfter + TimeSpan.FromMinutes(1));
        coordinator.Cancel(armed.Request.SessionId, "test");
        Assert.False(arming.IsActive);
        Assert.DoesNotContain(coordinator.Snapshot.Sessions, session => !session.IsTerminal);
    }

    private static GuidedStashScanService GuidedScan(IStashSnapshotStore store)
    {
        var assembler = new StashScanAssembler();
        return new(
            assembler,
            new StashLayoutAligner(),
            new StashReconstructionProjector(),
            new StashScanWorkflow(assembler, store, new StashSnapshotComparer()),
            new StashOwnedCountsApplier(new StubProfileService(TarkovCompanion.UnitTests.V2Shell.V2ShellTestData.Snapshot().Profile!)),
            new MemoryPendingStore());
    }

    private sealed class CompositeFreeHandoff : ICaptureResultHandoff
    {
        public ValueTask<CaptureHandoffResult> AcceptAsync(CaptureHandoffRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CaptureHandoffResult.Accepted);
    }

    private sealed class MemoryPendingStore : IGuidedStashScanPendingStore
    {
        private GuidedStashScanPending? _pending;

        public Task<GuidedStashScanPending?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_pending);

        public Task SaveAsync(GuidedStashScanPending pending, CancellationToken cancellationToken)
        {
            _pending = pending;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            _pending = null;
            return Task.CompletedTask;
        }
    }

    private static GridReconstructionRequest RealStashGrid()
    {
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "fixture://grid",
            Now,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("fixture", "1"));
        var bounds = new EvidenceRegion(0, 0, 64, 64, EvidenceCoordinateSpace.SourcePixels);
        var lattice = new DetectedGridLattice(
            rows: 1,
            columns: 1,
            cellWidthPixels: 64,
            cellHeightPixels: 64,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            provenance,
            bounds);
        var item = new EvidencedValue<RecognizedItem>(
            "grid.cell.item",
            null,
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current),
            provenance,
            bounds);
        var observation = new GridCellObservation("cell-000-000", new GridCellAddress(0, 0), item);
        return new GridReconstructionRequest(InventoryGridSurface.Stash, lattice, [observation]);
    }

    private static async Task<ProfileRuntimeContextService> ReadyProfileContextAsync()
    {
        var store = new MemoryProfileStore();
        var profiles = new ProfileContextService(store, new ProfileClock(Now));
        var profile = Profile(Context(Id(402), "generation-a", ProfileGameMode.Pvp), "item-a");
        await profiles.CreateAsync(Request(profile), CancellationToken.None);
        var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        return runtime;
    }

    private static byte[] Pixels() => [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];

    /// <summary>A minimal coordinator around a fixed detected context, enough to reach handoff once.</summary>
    private sealed class Harness(
        ICaptureResultHandoff handoff,
        RecognizedContext detected,
        ScanIntent intent,
        GridReconstructionRequest? grid = null) : IAsyncDisposable
    {
        private readonly ManualTimeProvider _clock = new(DateTimeOffset.Parse("2026-09-17T00:00:00Z"));
        private CaptureSessionCoordinator? _coordinator;

        public async Task<CaptureQueueReceipt> CaptureAsync()
        {
            _coordinator = new(
                new InlineCaptureWorkScheduler(),
                new StubPipeline(detected, grid),
                handoff,
                Origin,
                _clock);
            _coordinator.ReviewRequested += (_, args) => _coordinator.TryReview(
                args.Review.SessionId,
                args.Review.ArtifactId,
                args.Review.DecodeRevision,
                CaptureReviewAction.UseDetected,
                "test-auto-resolve");

            var sessionId = new CaptureSessionId(Guid.NewGuid());
            var now = _clock.GetUtcNow();
            var armed = _coordinator.Arm(new(
                new(sessionId, intent, Origin, now, null, null, now.AddSeconds(30)),
                CaptureContext,
                new("hold_screen", "Hold the screen steady.")));
            Assert.True(armed.Accepted);

            var receipt = await _coordinator.EnqueueAsync(
                new(
                    CaptureDeliveryKind.WatchedFile,
                    new MemoryCaptureSource(
                        new(Pixels(), 2, 2, 8, PixelFormat.Bgra8888, now, "fixture"),
                        CaptureSourceKind.GameWrittenScreenshot),
                    CaptureContext,
                    now,
                    CaptureCorrelationId.New(),
                    sessionId),
                CancellationToken.None);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline &&
                   !_coordinator.Snapshot.Sessions.Any(item => item.IsTerminal))
            {
                _clock.Advance(TimeSpan.FromMilliseconds(1));
                await Task.Delay(1);
            }

            Assert.Contains(_coordinator.Snapshot.Sessions, item => item.IsTerminal);
            return receipt;
        }

        public async ValueTask DisposeAsync()
        {
            if (_coordinator is not null)
            {
                await _coordinator.DisposeAsync();
            }
        }
    }

    private sealed class StubPipeline(RecognizedContext detected, GridReconstructionRequest? grid = null) : ICaptureSessionPipeline
    {
        public Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CaptureAnalysis(
                Convert.ToHexStringLower(SHA256.HashData(request.Image.Pixels.Span)),
                detected,
                false,
                true,
                null,
                new Confidence(0.9),
                grid));
    }

    private sealed class MemorySnapshotStore : IStashSnapshotStore
    {
        public StashSnapshotRecord? Saved { get; private set; }

        public Task SaveAsync(StashSnapshotRecord snapshot, CancellationToken cancellationToken)
        {
            Saved = snapshot;
            return Task.CompletedTask;
        }

        public Task<StashSnapshotRecord?> ReadCurrentAsync(InventoryProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(Saved);

        public Task<StashSnapshotRecord?> ReadAsync(InventoryProfileScope scope, Guid snapshotId, CancellationToken cancellationToken) =>
            Task.FromResult(Saved);

        public Task<IReadOnlyList<StashSnapshotSummary>> ListAsync(InventoryProfileScope scope, int maximumCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StashSnapshotSummary>>([]);

        public Task<StashSnapshotDeleteResult> DeleteAsync(InventoryProfileScope scope, Guid snapshotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StashSnapshotRetentionResult> ApplyRetentionAsync(InventoryProfileScope scope, DateTimeOffset retainFromUtc, bool dryRun, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
