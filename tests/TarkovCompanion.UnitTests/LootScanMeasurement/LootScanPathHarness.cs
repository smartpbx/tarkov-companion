using System.Security.Cryptography;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>
/// Drives one frame down the path the running app takes: a real capture session, the real
/// pixel-to-grid builder, the real handoff and decision service.
/// </summary>
/// <remarks>
/// The one substitution is screen classification. The production pipeline asks Tesseract which
/// screen this is before it builds the grid, and Tesseract is not installed where this runs, so
/// the frame is declared a loot screen here the way a player confirming the review prompt would.
/// Everything after that is the shipped code.
/// </remarks>
internal sealed class LootScanPathHarness(GridPixelReconstructionBuilder builder, LootScanCaptureHandoff handoff)
{
    private static readonly WorkspaceOrigin Origin = new(
        new(Guid.Parse("37000000-0000-4000-8000-000000000001")),
        new(Guid.Parse("37000000-0000-4000-8000-000000000002")),
        WorkspaceOriginKind.DesktopApplication,
        "loot-scan-measurement");

    private static readonly CaptureContextMetadata Context = new("raid", null, "customs", null, null, null, "desktop");

    public async Task<(GridReconstructionRequest? Grid, LootScanResult? Result)> ScanAsync(CapturedImage image, DateTimeOffset nowUtc)
    {
        var clock = new ManualTimeProvider(nowUtc);
        // Observed at the instant the decision is evaluated. The session clock below is advanced
        // to make the coordinator progress, and evidence stamped from it would be in the
        // decision service's future, which it rightly refuses.
        var pipeline = new BuilderPipeline(builder, new ManualTimeProvider(nowUtc));
        LootScanResult? evaluated = null;
        void OnEvaluated(object? sender, LootScanResult result) => evaluated = result;
        handoff.LootScanEvaluated += OnEvaluated;
        try
        {
            await using var coordinator = new CaptureSessionCoordinator(
                new InlineCaptureWorkScheduler(),
                pipeline,
                handoff,
                Origin,
                clock);
            coordinator.ReviewRequested += (_, args) => coordinator.TryReview(
                args.Review.SessionId,
                args.Review.ArtifactId,
                args.Review.DecodeRevision,
                CaptureReviewAction.UseDetected,
                "measurement-auto-resolve");

            var sessionId = new CaptureSessionId(Guid.NewGuid());
            var armed = coordinator.Arm(new(
                new(sessionId, ScanIntent.Loot, Origin, nowUtc, null, null, nowUtc.AddSeconds(30)),
                Context,
                new("hold_screen", "Hold the screen steady.")));
            Assert.True(armed.Accepted);

            await coordinator.EnqueueAsync(
                new(
                    CaptureDeliveryKind.WatchedFile,
                    new MemoryCaptureSource(image, CaptureSourceKind.GameWrittenScreenshot),
                    Context,
                    nowUtc,
                    CaptureCorrelationId.New(),
                    sessionId),
                CancellationToken.None);

            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline && !coordinator.Snapshot.Sessions.Any(item => item.IsTerminal))
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
                await Task.Delay(1);
            }

            return (pipeline.LastGrid, evaluated);
        }
        finally
        {
            handoff.LootScanEvaluated -= OnEvaluated;
        }
    }

    private sealed class BuilderPipeline(GridPixelReconstructionBuilder builder, TimeProvider clock) : ICaptureSessionPipeline
    {
        public GridReconstructionRequest? LastGrid { get; private set; }

        public async Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken)
        {
            LastGrid = await builder.BuildAsync(
                request.Image,
                InventoryGridSurface.VisibleLoot,
                clock.GetUtcNow(),
                cancellationToken: cancellationToken);
            return new(
                Convert.ToHexStringLower(SHA256.HashData(request.Image.Pixels.Span)),
                RecognizedContext.Loot,
                false,
                true,
                null,
                new Confidence(0.9),
                LastGrid);
        }
    }
}
