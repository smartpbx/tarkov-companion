using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// #887: a manual batch larger than the decoded-pixel budget completes instead of being cancelled.
/// </summary>
/// <remarks>
/// Run against the real coordinator with the frames and the budget scaled down together: a
/// 16x16 frame is 1 KiB, and the budget keeps the real ratio of 96 MiB to one decoded frame
/// (6.8 frames at 1440p, 3.03 at 4K). The old intake admitted every picture in one burst, the
/// seventh 1440p picture (the fourth at 4K) was refused, and the refusal cancelled the session
/// with the accepted pictures in it.
/// </remarks>
public sealed class ManualBatchPixelBudgetTests
{
    private const int FrameBytes = 16 * 16 * 4;

    private static readonly WorkspaceOrigin Origin = new(
        new(Guid.Parse("10000000-0000-0000-0000-000000000887")),
        new(Guid.Parse("10000000-0000-0000-0000-000000000888")),
        WorkspaceOriginKind.DesktopApplication,
        "desktop");

    private static readonly CaptureContextMetadata Context = new(
        "intel",
        "profile-a",
        "customs",
        "plan-a",
        "item-a",
        "scan-a",
        "this-desktop");

    [Theory]
    // 1440p: 100,663,296 / 14,745,600 = 6.83 frames.
    [InlineData(7, 6_990)]
    // 4K: 100,663,296 / 33,177,600 = 3.03 frames, and the batch limit of 32.
    [InlineData(32, 3_107)]
    public async Task EveryPictureOfABatchOverTheBudgetIsRead(int pictures, long budget)
    {
        await using var coordinator = new CaptureSessionCoordinator(
            new InlineCaptureWorkScheduler(),
            new ReadsAsStash(),
            new AcceptingHandoff(),
            Origin,
            TimeProvider.System,
            new CaptureSessionOptions(maximumRetainedPixelBytes: budget));
        coordinator.ReviewRequested += (_, request) => coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-user");
        var sessionId = new CaptureSessionId(Guid.NewGuid());
        var now = TimeProvider.System.GetUtcNow();
        Assert.True(coordinator.Arm(new(
            new(sessionId, ScanIntent.Stash, Origin, now, Context.ActiveProfile, Context.ActiveMap, now.AddMinutes(1)),
            Context,
            new("hold_screen", "Hold the requested screen steady."))).Accepted);
        var intake = new ManualImageIntake(coordinator, new NoFiles(), new FixedContext());
        var updates = new List<ManualImageBatchUpdate>();

        var outcome = await intake.SubmitBatchAsync(
            Frames(pictures),
            ManualImageOrigin.Drop,
            "batch-887",
            sessionId,
            update =>
            {
                lock (updates)
                {
                    updates.Add(update);
                }
            },
            new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        Assert.Equal(new ManualImageBatchOutcome(pictures, 0, 0), outcome);
        var session = await TerminalAsync(coordinator, sessionId);
        Assert.False(session.CancellationRequested);
        Assert.Equal(CaptureSessionStage.Complete, session.Snapshot.Progress[^1].Stage);
        Assert.Equal(pictures, session.Artifacts.Length);
        Assert.All(session.Artifacts, artifact =>
        {
            Assert.Equal(CaptureArtifactDisposition.Accepted, artifact.Disposition);
            Assert.False(artifact.PixelsRetained);
        });
        Assert.Equal(0, coordinator.Snapshot.PixelsInUse);
        Assert.DoesNotContain(coordinator.Snapshot.Notices, notice => notice.Code == "decoded_pixel_budget_exceeded");
    }

    [Fact]
    public async Task APictureLargerThanTheWholeBudgetFailsAloneAndTheRestAreRead()
    {
        await using var coordinator = new CaptureSessionCoordinator(
            new InlineCaptureWorkScheduler(),
            new ReadsAsStash(),
            new AcceptingHandoff(),
            Origin,
            TimeProvider.System,
            new CaptureSessionOptions(maximumRetainedPixelBytes: 3 * FrameBytes));
        coordinator.ReviewRequested += (_, request) => coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-user");
        var sessionId = new CaptureSessionId(Guid.NewGuid());
        var now = TimeProvider.System.GetUtcNow();
        Assert.True(coordinator.Arm(new(
            new(sessionId, ScanIntent.Stash, Origin, now, Context.ActiveProfile, Context.ActiveMap, now.AddMinutes(1)),
            Context,
            new("hold_screen", "Hold the requested screen steady."))).Accepted);
        var huge = new byte[64 * 64 * 4];
        Array.Fill(huge, (byte)200);
        var inputs = Frames(3).ToList();
        inputs.Insert(1, new("item-huge", "huge.png", null, new CapturedImage(huge, 64, 64, 256, PixelFormat.Bgra8888, now, "fixture")));
        var intake = new ManualImageIntake(coordinator, new NoFiles(), new FixedContext());

        var outcome = await intake.SubmitBatchAsync(
            inputs,
            ManualImageOrigin.Drop,
            "batch-887-huge",
            sessionId,
            _ => { },
            new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        Assert.Equal(new ManualImageBatchOutcome(3, 1, 0), outcome);
        Assert.All(huge, value => Assert.Equal(0, value));
        var session = await TerminalAsync(coordinator, sessionId);
        Assert.Equal(CaptureSessionStage.Complete, session.Snapshot.Progress[^1].Stage);
        Assert.Equal(3, session.Artifacts.Length);
    }

    private static async Task<CaptureSessionState> TerminalAsync(CaptureSessionCoordinator coordinator, CaptureSessionId sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var session = coordinator.Snapshot.Sessions.SingleOrDefault(item => item.Request.SessionId == sessionId);
            if (session is { IsTerminal: true } && coordinator.Snapshot.PixelsInUse == 0)
            {
                return session;
            }

            await Task.Delay(5);
        }

        Assert.Fail("The batch session did not finish.");
        return null!;
    }

    private static List<ManualImageInput> Frames(int count) =>
        [.. Enumerable.Range(1, count).Select(index =>
        {
            var pixels = new byte[FrameBytes];
            Array.Fill(pixels, checked((byte)index));
            return new ManualImageInput(
                $"item-{index}",
                $"stash-{index}.png",
                null,
                new CapturedImage(
                    pixels,
                    16,
                    16,
                    64,
                    PixelFormat.Bgra8888,
                    DateTimeOffset.Parse("2026-09-25T00:00:00Z").AddSeconds(index),
                    "batch-fixture"));
        })];

    private sealed class ReadsAsStash : ICaptureSessionPipeline
    {
        public Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CaptureAnalysis(
                $"result-{request.CorrelationId}",
                RecognizedContext.Stash,
                false,
                true,
                null,
                new Confidence(0.9)));
    }

    private sealed class AcceptingHandoff : ICaptureResultHandoff
    {
        public ValueTask<CaptureHandoffResult> AcceptAsync(CaptureHandoffRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CaptureHandoffResult.Accepted);
    }

    private sealed class FixedContext : ICaptureContextSource
    {
        public CaptureContextMetadata Describe(string? initiatingDevice = null) => Context;
    }

    private sealed class NoFiles : TarkovCompanion.Core.Abstractions.IScreenshotImageLoader
    {
        public Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fixture supplies pixels directly.");
    }
}
