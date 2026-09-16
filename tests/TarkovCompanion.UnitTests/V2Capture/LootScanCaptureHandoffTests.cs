using System.Security.Cryptography;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Profiles;
using TarkovCompanion.UnitTests.Runtime;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// Exercises the composition-owned #271/#274/#282 seam through a real
/// <see cref="CaptureSessionCoordinator"/>, since <see cref="CaptureHandoffRequest"/> is only ever
/// constructed by the coordinator itself.
/// </summary>
public sealed class LootScanCaptureHandoffTests
{
    private static readonly WorkspaceOrigin Origin = new(
        new(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        new(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        WorkspaceOriginKind.DesktopApplication,
        "loot-scan-handoff-tests");

    private static readonly CaptureContextMetadata CaptureContext = new(
        "raid",
        null,
        "map-a",
        null,
        null,
        null,
        "desktop");

    [Fact]
    public async Task LootIntentWithAnActiveProfileProducesAnHonestUnavailableResultUntilGridRecognitionIsWired()
    {
        using var runtime = await ReadyProfileContextAsync();
        var handoff = new LootScanCaptureHandoff(runtime, new InventoryGridReconstructor(), new LootScanDecisionService());
        LootScanResult? evaluated = null;
        handoff.LootScanEvaluated += (_, result) => evaluated = result;

        await using var harness = new Harness(handoff, RecognizedContext.Loot, ScanIntent.Loot);
        await harness.CaptureAsync();

        Assert.NotNull(evaluated);
        Assert.Equal(ResultCompleteness.Unavailable, evaluated!.Status.Completeness);
        Assert.Empty(evaluated.Decisions);
    }

    [Fact]
    public async Task NonLootIntentIsAcknowledgedWithoutProducingAScan()
    {
        using var runtime = await ReadyProfileContextAsync();
        var handoff = new LootScanCaptureHandoff(runtime, new InventoryGridReconstructor(), new LootScanDecisionService());
        var evaluated = false;
        handoff.LootScanEvaluated += (_, _) => evaluated = true;

        await using var harness = new Harness(handoff, RecognizedContext.Stash, ScanIntent.Stash);
        var receipt = await harness.CaptureAsync();

        Assert.Equal(CaptureQueueDisposition.Accepted, receipt.Disposition);
        Assert.False(evaluated);
    }

    [Fact]
    public async Task LootIntentWithoutAnActiveProfileIsAcknowledgedWithoutProducingAScan()
    {
        using var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        using var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        var handoff = new LootScanCaptureHandoff(runtime, new InventoryGridReconstructor(), new LootScanDecisionService());
        var evaluated = false;
        handoff.LootScanEvaluated += (_, _) => evaluated = true;

        await using var harness = new Harness(handoff, RecognizedContext.Loot, ScanIntent.Loot);
        var receipt = await harness.CaptureAsync();

        Assert.Equal(CaptureQueueDisposition.Accepted, receipt.Disposition);
        Assert.False(evaluated);
    }

    private static async Task<ProfileRuntimeContextService> ReadyProfileContextAsync()
    {
        var store = new MemoryProfileStore();
        var profiles = new ProfileContextService(store, new ProfileClock(Now));
        var profile = Profile(Context(Id(401), "generation-a", ProfileGameMode.Pvp), "item-a");
        await profiles.CreateAsync(Request(profile), CancellationToken.None);
        var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        return runtime;
    }

    private static byte[] Pixels() => [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];

    /// <summary>A minimal coordinator around a fixed detected context, enough to reach handoff once.</summary>
    private sealed class Harness(ICaptureResultHandoff handoff, RecognizedContext detected, ScanIntent intent) : IAsyncDisposable
    {
        private readonly ManualTimeProvider _clock = new(DateTimeOffset.Parse("2026-09-16T00:00:00Z"));
        private CaptureSessionCoordinator? _coordinator;

        public async Task<CaptureQueueReceipt> CaptureAsync()
        {
            _coordinator = new(
                new InlineCaptureWorkScheduler(),
                new StubPipeline(detected),
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

    private sealed class StubPipeline(RecognizedContext detected) : ICaptureSessionPipeline
    {
        public Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CaptureAnalysis(
                Convert.ToHexStringLower(SHA256.HashData(request.Image.Pixels.Span)),
                detected,
                false,
                true,
                null,
                new Confidence(0.9)));
    }
}
