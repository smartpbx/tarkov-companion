using System.Collections.Concurrent;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.CaptureSessions;

public sealed class CaptureSessionCoordinatorTests
{
    private static readonly WorkspaceOrigin Origin = new(
        new(Guid.Parse("d6fa57c2-17ea-481f-8e5d-8fb483764f2d")),
        new(Guid.Parse("7775221e-f17a-4e99-ab43-86c9535de26b")),
        WorkspaceOriginKind.DesktopApplication,
        "capture-tests");

    [Theory]
    [InlineData(CaptureDeliveryKind.Desktop)]
    [InlineData(CaptureDeliveryKind.PairedDevice)]
    [InlineData(CaptureDeliveryKind.Paste)]
    [InlineData(CaptureDeliveryKind.Drop)]
    [InlineData(CaptureDeliveryKind.Picker)]
    [InlineData(CaptureDeliveryKind.WatchedFile)]
    [InlineData(CaptureDeliveryKind.ExternalCapture)]
    [InlineData(CaptureDeliveryKind.Batch)]
    public async Task EveryDeliveryPathUsesTheSameReviewedLifecycle(CaptureDeliveryKind delivery)
    {
        await using var harness = new Harness();
        harness.Coordinator.ReviewRequested += (_, request) =>
            harness.Coordinator.TryReview(
                request.Review.SessionId,
                request.Review.ArtifactId,
                CaptureReviewAction.UseDetected,
                "test-user");

        var receipt = await harness.EnqueueAsync(
            delivery,
            Pixels((byte)delivery),
            batchId: delivery == CaptureDeliveryKind.Batch ? "batch-1" : null);

        Assert.Equal(CaptureQueueDisposition.Accepted, receipt.Disposition);
        var session = await harness.WaitForTerminalAsync();
        var artifact = Assert.Single(session.Artifacts);
        Assert.Equal(delivery, artifact.DeliveryKind);
        Assert.Equal(CaptureSessionStage.Complete, session.Snapshot.Progress[^1].Stage);
        Assert.False(artifact.PixelsRetained);
    }

    [Fact]
    public async Task DuplicateBytesDoNotConsumeTheNextArmedIntent()
    {
        await using var harness = new Harness();
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            CaptureReviewAction.UseDetected,
            "test-user");

        await harness.EnqueueAsync(CaptureDeliveryKind.Paste, Pixels(7));
        await harness.WaitForTerminalAsync();

        var armed = harness.Arm(ScanIntent.Stash);
        await harness.EnqueueAsync(CaptureDeliveryKind.WatchedFile, Pixels(7));
        await harness.WaitForAsync(snapshot => snapshot.Duplicate == 1);

        var stillArmed = harness.Coordinator.Snapshot.Sessions.Single(item => item.Request.SessionId == armed);
        Assert.False(stillArmed.IntentClaimed);
        Assert.False(stillArmed.IsTerminal);

        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(8));
        var consumed = await harness.WaitForAsync(snapshot =>
            snapshot.Sessions.Any(item => item.Request.SessionId == armed && item.IsTerminal));
        Assert.True(consumed.Sessions.Single(item => item.Request.SessionId == armed).IntentClaimed);
    }

    [Fact]
    public async Task IntentDisagreementWaitsForReviewAndRecordsTheCorrection()
    {
        await using var harness = new Harness(new StubPipeline(RecognizedContext.Flea));
        var sessionId = harness.Arm(ScanIntent.Stash);
        CaptureReviewRequest? review = null;
        harness.Coordinator.ReviewRequested += (_, request) => review = request.Review;

        await harness.EnqueueAsync(CaptureDeliveryKind.PairedDevice, Pixels(12));
        await harness.WaitForAsync(_ => review is not null);

        Assert.True(review!.HasIntentDisagreement);
        var waiting = harness.Coordinator.Snapshot.Sessions.Single(item => item.Request.SessionId == sessionId);
        Assert.True(Assert.Single(waiting.Artifacts).PixelsRetained);
        Assert.True(harness.Coordinator.TryReview(
            sessionId,
            review.ArtifactId,
            CaptureReviewAction.UseArmedIntent,
            "paired-review"));

        var complete = await harness.WaitForAsync(snapshot =>
            snapshot.Sessions.Single(item => item.Request.SessionId == sessionId).IsTerminal);
        var correction = Assert.Single(Assert.Single(complete.Sessions.Single(item => item.Request.SessionId == sessionId).Artifacts).Corrections);
        Assert.Equal(CaptureReviewAction.UseArmedIntent, correction.Action);
    }

    [Fact]
    public async Task RedecodeUsesTheRetainedPixelsThenClearsThem()
    {
        var pipeline = new StubPipeline(RecognizedContext.Item);
        await using var harness = new Harness(pipeline);
        var sourcePixels = Pixels(23);
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision == 0 ? CaptureReviewAction.Redecode : CaptureReviewAction.UseDetected,
            "test-user");

        await harness.EnqueueAsync(CaptureDeliveryKind.Picker, sourcePixels);
        var session = await harness.WaitForTerminalAsync();

        Assert.Equal([0, 1], pipeline.DecodeRevisions);
        Assert.All(sourcePixels, value => Assert.Equal(0, value));
        Assert.Equal(1, Assert.Single(session.Artifacts).DecodeRevision);
    }

    [Fact]
    public async Task DecodeFailureRetriesWithoutInventingAResult()
    {
        await using var harness = new Harness();
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            CaptureReviewAction.UseDetected,
            "test-user");
        var source = new RetrySource(2, Pixels(31));

        await harness.Coordinator.EnqueueAsync(
            new(
                CaptureDeliveryKind.WatchedFile,
                source,
                Context,
                DateTimeOffset.UtcNow,
                CaptureCorrelationId.New()),
            CancellationToken.None);
        var session = await harness.WaitForTerminalAsync();

        var artifact = Assert.Single(session.Artifacts);
        Assert.Equal(3, artifact.DecodeAttempts);
        Assert.Equal(3, session.Snapshot.Progress.Count(item => item.Stage == CaptureSessionStage.Decoding));
    }

    [Fact]
    public async Task RapidCapturesRemainOrderedAndCarryOneCorrelationThroughout()
    {
        var pipeline = new StubPipeline(RecognizedContext.Item);
        await using var harness = new Harness(pipeline);
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            CaptureReviewAction.UseDetected,
            "test-user");
        var correlations = Enumerable.Range(0, 12).Select(_ => CaptureCorrelationId.New()).ToArray();

        var receipts = await Task.WhenAll(correlations.Select((correlation, index) =>
            harness.EnqueueAsync(CaptureDeliveryKind.WatchedFile, Pixels((byte)(index + 40)), correlation: correlation).AsTask()));
        await harness.WaitForAsync(snapshot => snapshot.Sessions.Count(item => item.IsTerminal) == correlations.Length);

        Assert.Equal(correlations, pipeline.Correlations);
        Assert.Equal(Enumerable.Range(0, correlations.Length), receipts.Select(item => checked((int)item.IntakeSequence)));
        Assert.All(receipts, item => Assert.Equal(CaptureQueueDisposition.Accepted, item.Disposition));
    }

    [Fact]
    public async Task ContextMetadataIsFrozenAtIntakeAndTimingContainsNoSourcePathOrDigest()
    {
        await using var harness = new Harness();
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            CaptureReviewAction.UseDetected,
            "test-user");

        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(88));
        var session = await harness.WaitForTerminalAsync();

        Assert.Equal("workspace-a", session.Context.ActiveWorkspace);
        Assert.Equal("profile-a", session.Context.ActiveProfile);
        Assert.Equal("map-a", session.Context.ActiveMap);
        Assert.Equal("plan-a", session.Context.ActivePlan);
        Assert.Equal("entity-a", session.Context.SelectedEntity);
        Assert.Equal("scan-a", session.Context.PriorScan);
        Assert.Equal("device-a", session.Context.InitiatingDevice);
        var timing = Assert.Single(harness.Coordinator.Snapshot.Timings);
        Assert.NotNull(timing.ArtifactId);
        Assert.DoesNotContain("/", timing.ArtifactId, StringComparison.Ordinal);
        Assert.All(harness.Coordinator.Snapshot.Notices, notice =>
            Assert.DoesNotContain("coordinate", notice.Code, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ArmedIntentExpiresVisiblyWithoutConsumingAnArtifact()
    {
        await using var harness = new Harness();
        var sessionId = harness.Arm(ScanIntent.Ammo, TimeSpan.FromMilliseconds(40));

        var snapshot = await harness.WaitForAsync(state => state.Notices.Any(notice =>
            notice.SessionId == sessionId && notice.Kind == CaptureSessionNoticeKind.IntentExpired));

        var session = snapshot.Sessions.Single(item => item.Request.SessionId == sessionId);
        Assert.True(session.IsTerminal);
        Assert.False(session.IntentClaimed);
        Assert.Empty(session.Artifacts);
        Assert.Equal(CaptureSessionStage.Cancelled, session.Snapshot.Progress[^1].Stage);
    }

    [Fact]
    public async Task ShutdownCancelsReviewAndClearsQueuedPixels()
    {
        var harness = new Harness();
        var first = Pixels(101);
        var second = Pixels(102);
        await harness.EnqueueAsync(CaptureDeliveryKind.Paste, first);
        await harness.WaitForAsync(state => state.PixelsInUse > 0);
        var queued = harness.EnqueueAsync(CaptureDeliveryKind.Drop, second).AsTask();
        await queued;

        await harness.DisposeAsync();

        Assert.All(first, value => Assert.Equal(0, value));
        Assert.All(second, value => Assert.Equal(0, value));
    }

    private static readonly CaptureContextMetadata Context = new(
        "workspace-a",
        "profile-a",
        "map-a",
        "plan-a",
        "entity-a",
        "scan-a",
        "device-a");

    private static byte[] Pixels(byte seed) =>
    [
        seed, 1, 2, 255,
        3, 4, 5, 255,
        6, 7, 8, 255,
        9, 10, 11, 255,
    ];

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(ICaptureSessionPipeline? pipeline = null)
        {
            Coordinator = new(
                new InlineCaptureWorkScheduler(),
                pipeline ?? new StubPipeline(RecognizedContext.Item),
                Origin,
                TimeProvider.System,
                new(
                    queueCapacity: 4,
                    maximumRetainedPixelBytes: 1024,
                    maximumDecodeAttempts: 4,
                    decodeRetryDelay: TimeSpan.FromMilliseconds(1),
                    reviewTimeout: TimeSpan.FromSeconds(2),
                    intentLifetime: TimeSpan.FromSeconds(2),
                    deduplicationLifetime: TimeSpan.FromMinutes(1),
                    historyLimit: 256));
        }

        public CaptureSessionCoordinator Coordinator { get; }

        public CaptureSessionId Arm(ScanIntent intent, TimeSpan? lifetime = null)
        {
            var id = new CaptureSessionId(Guid.NewGuid());
            var now = DateTimeOffset.UtcNow;
            var receipt = Coordinator.Arm(new(
                new(id, intent, Origin, now, Context.ActiveProfile, Context.ActiveMap, now.Add(lifetime ?? TimeSpan.FromSeconds(2))),
                Context,
                new("hold_screen", "Hold the requested screen steady.")));
            Assert.True(receipt.Accepted);
            return id;
        }

        public ValueTask<CaptureQueueReceipt> EnqueueAsync(
            CaptureDeliveryKind delivery,
            byte[] pixels,
            string? batchId = null,
            CaptureCorrelationId? correlation = null) =>
            Coordinator.EnqueueAsync(
                new(
                    delivery,
                    new MemoryCaptureSource(
                        new(pixels, 2, 2, 8, PixelFormat.Bgra8888, DateTimeOffset.UtcNow, "fixture"),
                        delivery == CaptureDeliveryKind.WatchedFile
                            ? CaptureSourceKind.GameWrittenScreenshot
                            : CaptureSourceKind.UserSelectedImage),
                    Context,
                    DateTimeOffset.UtcNow,
                    correlation ?? CaptureCorrelationId.New(),
                    batchId: batchId),
                CancellationToken.None);

        public async Task<CaptureSessionState> WaitForTerminalAsync()
        {
            var snapshot = await WaitForAsync(state => state.Sessions.Any(item => item.IsTerminal));
            return snapshot.Sessions.Last(item => item.IsTerminal);
        }

        public async Task<CaptureSessionServiceSnapshot> WaitForAsync(
            Func<CaptureSessionServiceSnapshot, bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                var snapshot = Coordinator.Snapshot;
                if (predicate(snapshot))
                {
                    return snapshot;
                }

                await Task.Delay(5);
            }

            Assert.Fail("Capture session did not reach the expected state.");
            return null!;
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
        }
    }

    private sealed class StubPipeline(RecognizedContext context) : ICaptureSessionPipeline
    {
        public ConcurrentQueue<CaptureCorrelationId> CorrelationQueue { get; } = new();

        public ConcurrentQueue<int> RevisionQueue { get; } = new();

        public CaptureCorrelationId[] Correlations => [.. CorrelationQueue];

        public int[] DecodeRevisions => [.. RevisionQueue];

        public Task<CaptureAnalysis> AnalyzeAsync(
            CaptureAnalysisRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CorrelationQueue.Enqueue(request.CorrelationId);
            RevisionQueue.Enqueue(request.DecodeRevision);
            return Task.FromResult(new CaptureAnalysis(
                $"result-{request.CorrelationId}",
                context,
                false,
                true,
                null));
        }
    }

    private sealed class RetrySource(int failures, byte[] pixels) : ICaptureContentSource
    {
        private int _remaining = failures;
        private CapturePixelLease? _pixels = new(
            new(pixels, 2, 2, 8, PixelFormat.Bgra8888, DateTimeOffset.UtcNow, "retry-fixture"));

        public CaptureSourceKind SourceKind => CaptureSourceKind.GameWrittenScreenshot;

        public ValueTask<CaptureSourceReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Decrement(ref _remaining) >= 0)
            {
                return ValueTask.FromResult(CaptureSourceReadResult.Failure("incomplete_write"));
            }

            return ValueTask.FromResult(CaptureSourceReadResult.Success(Interlocked.Exchange(ref _pixels, null)!));
        }

        public void Dispose() => Interlocked.Exchange(ref _pixels, null)?.Dispose();
    }
}
