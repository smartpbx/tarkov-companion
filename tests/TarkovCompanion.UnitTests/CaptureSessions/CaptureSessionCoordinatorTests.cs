using System.Collections.Concurrent;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.UnitTests.Runtime;

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
                request.Review.DecodeRevision,
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
            request.Review.DecodeRevision,
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
    public async Task DuplicateBytesDoNotConsumeThePerSessionAdmissionLimit()
    {
        await using var harness = new Harness(options: new(maximumCapturesPerSession: 1));
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-user");

        await harness.EnqueueAsync(CaptureDeliveryKind.Paste, Pixels(9));
        await harness.WaitForTerminalAsync();

        var armed = harness.Arm(ScanIntent.Stash);
        var duplicate = await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(9));
        Assert.Equal(CaptureQueueDisposition.Accepted, duplicate.Disposition);
        await harness.WaitForAsync(snapshot => snapshot.Duplicate == 1
            && !snapshot.Sessions.Single(item => item.Request.SessionId == armed).IntentClaimed);

        var replacement = await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(10));
        Assert.Equal(CaptureQueueDisposition.Accepted, replacement.Disposition);
        await harness.WaitForAsync(snapshot =>
            snapshot.Sessions.Single(item => item.Request.SessionId == armed).IsTerminal);
    }

    [Fact]
    public async Task ClaimedArmedIntentCannotBeReplacedBeforeCaptureSettlement()
    {
        await using var harness = new Harness();
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-user");
        var firstSession = harness.Arm(ScanIntent.Stash);
        var source = new BlockingSource(Pixels(11), harness.Clock);
        var admission = await harness.Coordinator.EnqueueAsync(
            new(
                CaptureDeliveryKind.Drop,
                source,
                Context,
                harness.Clock.GetUtcNow(),
                CaptureCorrelationId.New(),
                firstSession),
            CancellationToken.None);
        Assert.Equal(CaptureQueueDisposition.Accepted, admission.Disposition);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var now = harness.Clock.GetUtcNow();
        var competingSession = new CaptureSessionId(Guid.NewGuid());
        var competing = harness.Coordinator.Arm(new(
            new(competingSession, ScanIntent.Ammo, Origin, now, ExpiresUtc: now.AddSeconds(1)),
            Context,
            new("hold_screen", "Hold the requested screen steady.")));

        Assert.False(competing.Accepted);
        Assert.Equal("intent_already_armed", competing.Code);
        source.Release();
        await harness.WaitForAsync(snapshot =>
            snapshot.Sessions.Single(item => item.Request.SessionId == firstSession).IsTerminal);
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
            review.DecodeRevision,
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
            request.Review.DecodeRevision,
            request.Review.DecodeRevision == 0 ? CaptureReviewAction.Redecode : CaptureReviewAction.UseDetected,
            "test-user");

        await harness.EnqueueAsync(CaptureDeliveryKind.Picker, sourcePixels);
        var session = await harness.WaitForTerminalAsync();

        Assert.Equal([0, 1], pipeline.DecodeRevisions);
        Assert.All(sourcePixels, value => Assert.Equal(0, value));
        Assert.Equal(1, Assert.Single(session.Artifacts).DecodeRevision);
    }

    [Fact]
    public async Task AReviewDecisionFromAnOlderDecodeRevisionCannotResolveTheNewReview()
    {
        await using var harness = new Harness();
        var reviews = new ConcurrentQueue<CaptureReviewRequest>();
        harness.Coordinator.ReviewRequested += (_, args) => reviews.Enqueue(args.Review);

        await harness.EnqueueAsync(CaptureDeliveryKind.Picker, Pixels(24));
        await harness.WaitForAsync(_ => reviews.Count == 1);
        var first = reviews.Single();
        Assert.True(harness.Coordinator.TryReview(
            first.SessionId,
            first.ArtifactId,
            first.DecodeRevision,
            CaptureReviewAction.Redecode,
            "test-redecode"));

        await harness.WaitForAsync(_ => reviews.Count == 2);
        var current = reviews.OrderBy(item => item.DecodeRevision).Last();
        Assert.Equal(1, current.DecodeRevision);
        Assert.False(harness.Coordinator.TryReview(
            first.SessionId,
            first.ArtifactId,
            first.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "stale-review"));
        Assert.True(harness.Coordinator.TryReview(
            current.SessionId,
            current.ArtifactId,
            current.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "current-review"));

        var terminal = await harness.WaitForTerminalAsync();
        Assert.Equal(
            new[] { CaptureReviewAction.Redecode, CaptureReviewAction.UseDetected },
            Assert.Single(terminal.Artifacts).Corrections.Select(item => item.Action).ToArray());
    }

    [Fact]
    public async Task DecodeFailureRetriesWithoutInventingAResult()
    {
        await using var harness = new Harness();
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-user");
        var source = new RetrySource(2, Pixels(31));

        await harness.Coordinator.EnqueueAsync(
            new(
                CaptureDeliveryKind.WatchedFile,
                source,
                Context,
                harness.Clock.GetUtcNow(),
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
        await using var harness = new Harness(pipeline, options: new(queueCapacity: 16));
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision,
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
            request.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-user");

        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(88));
        var session = await harness.WaitForTerminalAsync();

        Assert.Equal("workspace-a", session.Context.ActiveWorkspace);
        Assert.Equal(Profile.Identity.ProfileId.ToString("D"), session.Context.ActiveProfile);
        Assert.Equal("map-a", session.Context.ActiveMap);
        Assert.Equal("plan-a", session.Context.ActivePlan);
        Assert.Equal("entity-a", session.Context.SelectedEntity);
        Assert.Equal("scan-a", session.Context.PriorScan);
        Assert.Equal("device-a", session.Context.InitiatingDevice);
        Assert.Same(Profile, session.Context.ProfileContext);
        var timing = Assert.Single(harness.Coordinator.Snapshot.Timings);
        Assert.NotNull(timing.ArtifactId);
        Assert.DoesNotContain("/", timing.ArtifactId, StringComparison.Ordinal);
        Assert.All(harness.Coordinator.Snapshot.Notices, notice =>
            Assert.DoesNotContain("coordinate", notice.Code, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ArmedContextMismatchIsRejectedAndMatchingIntakeContextIsFrozen()
    {
        await using var harness = new Harness();
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-user");
        var sessionId = harness.Arm(ScanIntent.Stash);
        var mismatchedPixels = Pixels(89);
        var mismatch = new CaptureContextMetadata(
            "workspace-b",
            Context.ActiveProfile,
            Context.ActiveMap,
            Context.ActivePlan,
            Context.SelectedEntity,
            Context.PriorScan,
            Context.InitiatingDevice,
            Profile);

        var rejected = await harness.Coordinator.EnqueueAsync(
            new(
                CaptureDeliveryKind.Drop,
                new MemoryCaptureSource(
                    new(mismatchedPixels, 2, 2, 8, PixelFormat.Bgra8888, harness.Clock.GetUtcNow(), "fixture"),
                    CaptureSourceKind.UserSelectedImage),
                mismatch,
                harness.Clock.GetUtcNow(),
                CaptureCorrelationId.New(),
                sessionId),
            CancellationToken.None);

        Assert.Equal(CaptureQueueDisposition.Rejected, rejected.Disposition);
        Assert.Equal("capture_context_changed_since_arm", rejected.Code);
        Assert.All(mismatchedPixels, value => Assert.Equal(0, value));
        Assert.False(harness.Coordinator.Snapshot.Sessions.Single().IntentClaimed);

        var intakeContext = new CaptureContextMetadata(
            Context.ActiveWorkspace,
            Context.ActiveProfile,
            Context.ActiveMap,
            Context.ActivePlan,
            Context.SelectedEntity,
            Context.PriorScan,
            Context.InitiatingDevice,
            Profile);
        var acceptedPixels = Pixels(90);
        var accepted = await harness.Coordinator.EnqueueAsync(
            new(
                CaptureDeliveryKind.Drop,
                new MemoryCaptureSource(
                    new(acceptedPixels, 2, 2, 8, PixelFormat.Bgra8888, harness.Clock.GetUtcNow(), "fixture"),
                    CaptureSourceKind.UserSelectedImage),
                intakeContext,
                harness.Clock.GetUtcNow(),
                CaptureCorrelationId.New(),
                sessionId),
            CancellationToken.None);

        Assert.Equal(CaptureQueueDisposition.Accepted, accepted.Disposition);
        var terminal = await harness.WaitForAsync(snapshot => snapshot.Sessions.Single().IsTerminal);
        Assert.Same(intakeContext, terminal.Sessions.Single().Context);
        Assert.Same(intakeContext, Assert.Single(terminal.Sessions.Single().Artifacts).Context);
    }

    [Fact]
    public async Task ArmedIntentExpiresVisiblyWithoutConsumingAnArtifact()
    {
        var handoff = new RecordingHandoff();
        await using var harness = new Harness(handoff: handoff);
        var sessionId = harness.Arm(ScanIntent.Ammo, TimeSpan.FromMilliseconds(40));

        var snapshot = await harness.WaitForAsync(state => state.Notices.Any(notice =>
            notice.SessionId == sessionId && notice.Kind == CaptureSessionNoticeKind.IntentExpired));

        var session = snapshot.Sessions.Single(item => item.Request.SessionId == sessionId);
        Assert.True(session.IsTerminal);
        Assert.False(session.IntentClaimed);
        Assert.Empty(session.Artifacts);
        Assert.Equal(CaptureSessionStage.Cancelled, session.Snapshot.Progress[^1].Stage);
        Assert.Equal(0, handoff.Calls);
    }

    [Fact]
    public async Task CallerCannotArmPastTheConfiguredIntentLifetime()
    {
        await using var harness = new Harness();
        var now = harness.Clock.GetUtcNow();
        var sessionId = new CaptureSessionId(Guid.NewGuid());

        var receipt = harness.Coordinator.Arm(new(
            new(sessionId, ScanIntent.Ammo, Origin, now, ExpiresUtc: now.AddDays(30)),
            Context,
            new("hold_screen", "Hold the requested screen steady.")));

        Assert.True(receipt.Accepted);
        Assert.Equal(now.AddSeconds(2), harness.Coordinator.Snapshot.Sessions.Single().Request.ExpiresUtc);
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

    [Fact]
    public async Task IntentIsBoundAtAdmissionWhileAnEarlierCaptureIsStillAnalyzing()
    {
        var pipeline = new BlockingPipeline();
        await using var harness = new Harness(pipeline);
        await harness.EnqueueAsync(CaptureDeliveryKind.WatchedFile, Pixels(110));
        await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await harness.EnqueueAsync(CaptureDeliveryKind.WatchedFile, Pixels(111));
        var armed = harness.Arm(ScanIntent.Stash);
        await harness.EnqueueAsync(CaptureDeliveryKind.WatchedFile, Pixels(112));

        var snapshot = harness.Coordinator.Snapshot;
        var armedSession = snapshot.Sessions.Single(item => item.Request.SessionId == armed);
        Assert.True(armedSession.IntentClaimed);
        Assert.Empty(armedSession.Artifacts);
        pipeline.Release(RecognizedContext.Item);
    }

    [Fact]
    public async Task CancelDuringUninterruptibleAnalysisCannotPoisonSnapshotAndClearsPixels()
    {
        var pipeline = new BlockingPipeline();
        await using var harness = new Harness(pipeline);
        var pixels = Pixels(113);
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, pixels);
        var request = await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(harness.Coordinator.Cancel(request.SessionId, "test-cancel"));
        pipeline.Release(RecognizedContext.Item);

        var snapshot = await harness.WaitForAsync(state =>
            state.Sessions.Single(item => item.Request.SessionId == request.SessionId).IsTerminal);
        Assert.Equal(0, snapshot.PixelsInUse);
        Assert.All(pixels, value => Assert.Equal(0, value));
        Assert.Equal(
            CaptureSessionStage.Cancelled,
            snapshot.Sessions.Single(item => item.Request.SessionId == request.SessionId).Snapshot.Progress[^1].Stage);
        _ = harness.Coordinator.Snapshot;
    }

    [Fact]
    public async Task CancelReturnsBeforeHostileCancellationCallbackCompletes()
    {
        var pipeline = new HostileCancellationPipeline();
        await using var harness = new Harness(pipeline);
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(114));
        var request = await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancel = Task.Run(() => harness.Coordinator.Cancel(request.SessionId, "test-cancel"));

        try
        {
            Assert.True(await cancel.WaitAsync(TimeSpan.FromSeconds(2)));
            await pipeline.CallbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            pipeline.Release();
        }

        var terminal = await harness.WaitForAsync(snapshot =>
            snapshot.Sessions.Single(item => item.Request.SessionId == request.SessionId).IsTerminal);
        Assert.Equal(CaptureSessionStage.Cancelled, terminal.Sessions.Single().Snapshot.Progress[^1].Stage);
        Assert.Equal(0, terminal.PixelsInUse);
    }

    [Fact]
    public async Task CancelDuringUninterruptibleDecodeZerosPixelsReturnedLate()
    {
        await using var harness = new Harness();
        var pixels = Pixels(127);
        var source = new BlockingSource(pixels, harness.Clock);
        var receipt = await harness.Coordinator.EnqueueAsync(
            new(
                CaptureDeliveryKind.Drop,
                source,
                Context,
                harness.Clock.GetUtcNow(),
                CaptureCorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(CaptureQueueDisposition.Accepted, receipt.Disposition);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var sessionId = Assert.Single(harness.Coordinator.Snapshot.Sessions).Request.SessionId;

        Assert.True(harness.Coordinator.Cancel(sessionId, "test-cancel"));
        source.Release();

        var terminal = await harness.WaitForAsync(state => state.Sessions.Single().IsTerminal);
        Assert.Equal(0, terminal.PixelsInUse);
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task CancelDuringReviewEndsSessionEvenWhenEndAfterReviewIsFalse()
    {
        var handoff = new RecordingHandoff();
        await using var harness = new Harness(handoff: handoff);
        var pixels = Pixels(114);
        CaptureReviewRequest? review = null;
        harness.Coordinator.ReviewRequested += (_, args) => review = args.Review;
        await harness.EnqueueAsync(
            CaptureDeliveryKind.Paste,
            pixels,
            endSessionAfterReview: false);
        await harness.WaitForAsync(_ => review is not null);

        Assert.True(harness.Coordinator.Cancel(review!.SessionId, "test-cancel"));
        var terminal = await harness.WaitForAsync(state =>
            state.Sessions.Single(item => item.Request.SessionId == review.SessionId).IsTerminal);
        Assert.Equal(0, terminal.PixelsInUse);
        Assert.All(pixels, value => Assert.Equal(0, value));
        Assert.Equal(CaptureArtifactDisposition.NoChange, Assert.Single(
            terminal.Sessions.Single(item => item.Request.SessionId == review.SessionId).Artifacts).Disposition);
        Assert.Equal(0, handoff.Calls);
    }

    [Fact]
    public async Task CancelDuringRedecodeWinsEvenWhenAnalysisReturnsLate()
    {
        var pipeline = new BlockingRedecodePipeline();
        await using var harness = new Harness(pipeline);
        var pixels = Pixels(115);
        harness.Coordinator.ReviewRequested += (_, args) =>
        {
            if (args.Review.DecodeRevision == 0)
            {
                harness.Coordinator.TryReview(
                    args.Review.SessionId,
                    args.Review.ArtifactId,
                    args.Review.DecodeRevision,
                    CaptureReviewAction.Redecode,
                    "test-redecode");
            }
        };

        await harness.EnqueueAsync(CaptureDeliveryKind.Picker, pixels);
        var request = await pipeline.RedecodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(harness.Coordinator.Cancel(request.SessionId, "test-cancel"));
        pipeline.ReleaseRedecode();

        var terminal = await harness.WaitForAsync(state =>
            state.Sessions.Single(item => item.Request.SessionId == request.SessionId).IsTerminal);
        Assert.Equal(0, terminal.PixelsInUse);
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task RetryCaptureRearmsAndAllowsTheSameContent()
    {
        await using var harness = new Harness();
        var pixels = Pixels(116);
        var reviews = 0;
        harness.Coordinator.ReviewRequested += (_, args) => harness.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            Interlocked.Increment(ref reviews) == 1
                ? CaptureReviewAction.RetryCapture
                : CaptureReviewAction.UseDetected,
            "test-review");

        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, pixels);
        var retrying = await harness.WaitForAsync(state => state.Sessions.Any(item =>
            !item.IsTerminal
            && !item.IntentClaimed
            && item.Artifacts.Length == 1
            && item.Snapshot.Progress[^1].Stage == CaptureSessionStage.AwaitingCapture));
        var sessionId = retrying.Sessions.Single(item => !item.IsTerminal).Request.SessionId;

        var retryPixels = Pixels(116);
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, retryPixels);
        var terminal = await harness.WaitForAsync(state =>
            state.Sessions.Single(item => item.Request.SessionId == sessionId).IsTerminal);
        Assert.Equal(2, terminal.Sessions.Single(item => item.Request.SessionId == sessionId).Artifacts.Length);
        Assert.Equal(0, terminal.PixelsInUse);
        Assert.All(pixels, value => Assert.Equal(0, value));
        Assert.All(retryPixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task RetryConflictTerminatesTheOldSessionWithoutReplacingANewerIntent()
    {
        await using var harness = new Harness();
        CaptureReviewRequest? firstReview = null;
        harness.Coordinator.ReviewRequested += (_, args) => firstReview ??= args.Review;
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(117));
        await harness.WaitForAsync(_ => firstReview is not null);
        var newerSession = harness.Arm(ScanIntent.Stash);

        var reviewed = firstReview!;
        Assert.True(harness.Coordinator.TryReview(
            reviewed.SessionId,
            reviewed.ArtifactId,
            reviewed.DecodeRevision,
            CaptureReviewAction.RetryCapture,
            "test-retry"));

        var settled = await harness.WaitForAsync(snapshot => snapshot.Sessions.Any(session =>
            session.Request.SessionId == reviewed.SessionId && session.IsTerminal));
        var oldSession = settled.Sessions.Single(session => session.Request.SessionId == reviewed.SessionId);
        Assert.Equal(CaptureSessionStage.Failed, oldSession.Snapshot.Progress[^1].Stage);
        Assert.Equal("retry_rearm_conflict", Assert.Single(oldSession.Artifacts).DiagnosticCode);
        var stillArmed = settled.Sessions.Single(session => session.Request.SessionId == newerSession);
        Assert.False(stillArmed.IsTerminal);
        Assert.False(stillArmed.IntentClaimed);
    }

    [Fact]
    public async Task ReviewTimeoutUsesInjectedTimeAndClearsTheLease()
    {
        var handoff = new RecordingHandoff();
        await using var harness = new Harness(handoff: handoff);
        var pixels = Pixels(117);
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, pixels);
        await harness.WaitForAsync(state => state.PixelsInUse > 0);

        harness.Clock.Advance(TimeSpan.FromSeconds(3));
        var terminal = await harness.WaitForAsync(state => state.Sessions.Any(item => item.IsTerminal));
        Assert.Equal(0, terminal.PixelsInUse);
        Assert.All(pixels, value => Assert.Equal(0, value));
        var artifact = Assert.Single(Assert.Single(terminal.Sessions).Artifacts);
        Assert.Equal(CaptureArtifactDisposition.NoChange, artifact.Disposition);
        Assert.Equal("review_expired_no_change", artifact.DiagnosticCode);
        Assert.Equal(0, handoff.Calls);
    }

    [Theory]
    [InlineData(null, false, false, 0.0, "capture_result_unavailable_no_change")]
    [InlineData(null, false, true, 0.91, "capture_context_unknown_no_change")]
    [InlineData(RecognizedContext.Item, true, true, 0.91, "capture_result_ambiguous_no_change")]
    [InlineData(RecognizedContext.Item, false, true, 0.10, "capture_result_below_threshold_no_change")]
    public async Task NoResultAnalysisCannotBeForcedIntoDomainHandoff(
        RecognizedContext? context,
        bool ambiguous,
        bool available,
        double confidence,
        string expectedCode)
    {
        var handoff = new RecordingHandoff();
        await using var harness = new Harness(
            new FixedPipeline(context, ambiguous, available, new Confidence(confidence)),
            handoff: handoff);
        CaptureAcceptedEventArgs? accepted = null;
        harness.Coordinator.Accepted += (_, args) => accepted = args;
        harness.Coordinator.ReviewRequested += (_, args) => harness.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            CaptureReviewAction.UseArmedIntent,
            "force-intent");

        await harness.EnqueueAsync(CaptureDeliveryKind.Paste, Pixels(117));
        var terminal = await harness.WaitForTerminalAsync();

        var artifact = Assert.Single(terminal.Artifacts);
        Assert.Equal(CaptureArtifactDisposition.NoChange, artifact.Disposition);
        Assert.Equal(expectedCode, artifact.DiagnosticCode);
        Assert.Equal(0, handoff.Calls);
        Assert.Null(accepted);
        Assert.Contains(harness.Coordinator.Snapshot.Notices, notice =>
            notice.Kind == CaptureSessionNoticeKind.NoChange);
    }

    [Fact]
    public async Task FullQueueRejectsExplicitlyWithoutCreatingAnUnboundedWriterBacklog()
    {
        var pipeline = new BlockingPipeline();
        await using var harness = new Harness(
            pipeline,
            options: new(
                queueCapacity: 1,
                maximumRetainedPixelBytes: 1024,
                decodeRetryDelay: TimeSpan.FromMilliseconds(1),
                reviewTimeout: TimeSpan.FromSeconds(2),
                intentLifetime: TimeSpan.FromSeconds(2),
                deduplicationLifetime: TimeSpan.FromMinutes(1)));
        harness.Coordinator.ReviewRequested += (_, args) => harness.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-review");
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(118));
        await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(119));
        Assert.Equal(32, harness.Coordinator.Snapshot.PixelsInUse);

        var cancelledPixels = Pixels(120);
        var rejected = await harness.Coordinator.EnqueueAsync(
            new(
                CaptureDeliveryKind.Drop,
                new MemoryCaptureSource(new(
                    cancelledPixels,
                    2,
                    2,
                    8,
                    PixelFormat.Bgra8888,
                    harness.Clock.GetUtcNow(),
                    "fixture"), CaptureSourceKind.UserSelectedImage),
                Context,
                harness.Clock.GetUtcNow(),
                CaptureCorrelationId.New()),
            CancellationToken.None);
        Assert.Equal(CaptureQueueDisposition.Rejected, rejected.Disposition);
        Assert.Equal("capture_queue_full", rejected.Code);
        Assert.Equal(1, harness.Coordinator.Snapshot.QueueDepth);
        Assert.Equal(2, harness.Coordinator.Snapshot.Sessions.Length);
        Assert.All(cancelledPixels, value => Assert.Equal(0, value));
        pipeline.Release(RecognizedContext.Item);
    }

    [Fact]
    public async Task PixelBudgetFailureZerosTheRejectedFrameWithoutDisturbingReview()
    {
        await using var harness = new Harness(options: new(
            queueCapacity: 4,
            maximumRetainedPixelBytes: 16,
            decodeRetryDelay: TimeSpan.FromMilliseconds(1),
            reviewTimeout: TimeSpan.FromSeconds(2),
            intentLifetime: TimeSpan.FromSeconds(2),
            deduplicationLifetime: TimeSpan.FromMinutes(1)));
        var retained = Pixels(121);
        var rejected = Pixels(122);
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, retained);
        await harness.WaitForAsync(state => state.PixelsInUse == 16);
        var rejection = await harness.EnqueueAsync(CaptureDeliveryKind.Drop, rejected);

        Assert.Equal(CaptureQueueDisposition.Rejected, rejection.Disposition);
        Assert.Equal("decoded_pixel_budget_exceeded", rejection.Code);
        Assert.Single(harness.Coordinator.Snapshot.Sessions);
        Assert.All(rejected, value => Assert.Equal(0, value));
        var review = await harness.WaitForAsync(state => state.Sessions.Any(item =>
            item.Artifacts.Any(artifact => artifact.PixelsRetained)));
        var waiting = review.Sessions.Single(item => item.Artifacts.Any(a => a.PixelsRetained));
        Assert.True(harness.Coordinator.Cancel(waiting.Request.SessionId, "test-cleanup"));
    }

    [Fact]
    public async Task ThrowingSourceDisposeCannotStopTheCapturePump()
    {
        await using var harness = new Harness();
        harness.Coordinator.ReviewRequested += (_, request) => harness.Coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-user");
        var hostilePixels = Pixels(123);
        var healthyPixels = Pixels(124);

        var first = await harness.Coordinator.EnqueueAsync(
            new(
                CaptureDeliveryKind.Drop,
                new ThrowingDisposeSource(hostilePixels, harness.Clock),
                Context,
                harness.Clock.GetUtcNow(),
                CaptureCorrelationId.New()),
            CancellationToken.None);
        var second = await harness.EnqueueAsync(CaptureDeliveryKind.Drop, healthyPixels);

        Assert.Equal(CaptureQueueDisposition.Accepted, first.Disposition);
        Assert.Equal(CaptureQueueDisposition.Accepted, second.Disposition);
        var completed = await harness.WaitForAsync(snapshot =>
            snapshot.Sessions.Count(session => session.IsTerminal) == 2);
        Assert.Equal(0, completed.PixelsInUse);
        Assert.All(hostilePixels, value => Assert.Equal(0, value));
        Assert.All(healthyPixels, value => Assert.Equal(0, value));
        Assert.Contains(completed.Notices, notice => notice.Code == "capture_source_dispose_failed");
    }

    [Fact]
    public async Task AnalysisFailureEndsTheSessionAndZerosPixels()
    {
        var handoff = new RecordingHandoff();
        await using var harness = new Harness(new ThrowingPipeline(), handoff: handoff);
        var pixels = Pixels(123);
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, pixels);

        var terminal = await harness.WaitForAsync(state => state.Sessions.Any(item => item.IsTerminal));
        var artifact = Assert.Single(terminal.Sessions.Single().Artifacts);
        Assert.Equal("analysis_failed", artifact.DiagnosticCode);
        Assert.Equal(CaptureArtifactDisposition.NoChange, artifact.Disposition);
        Assert.Equal(0, handoff.Calls);
        Assert.Equal(0, terminal.PixelsInUse);
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task UseArmedIntentCarriesCorrectionAndProvenanceToConsumers()
    {
        await using var harness = new Harness(new StubPipeline(RecognizedContext.Flea));
        var sessionId = harness.Arm(ScanIntent.Stash);
        CaptureAcceptedEventArgs? accepted = null;
        harness.Coordinator.Accepted += (_, args) => accepted = args;
        harness.Coordinator.ReviewRequested += (_, args) => harness.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            CaptureReviewAction.UseArmedIntent,
            "test-correction");

        var capturedUtc = harness.Clock.GetUtcNow();
        await harness.EnqueueAsync(
            CaptureDeliveryKind.Batch,
            Pixels(124),
            batchId: "batch-provenance",
            sessionId: sessionId);
        await harness.WaitForAsync(_ => accepted is not null);

        Assert.Equal(CaptureReviewAction.UseArmedIntent, accepted!.Decision);
        Assert.Equal(ScanIntent.Stash, accepted.EffectiveIntent);
        Assert.Equal(CaptureSourceKind.UserSelectedImage, accepted.SourceKind);
        Assert.Equal(CaptureDeliveryKind.Batch, accepted.DeliveryKind);
        Assert.Equal("batch-provenance", accepted.BatchId);
        Assert.Equal(new Confidence(0.91), accepted.Analysis.Confidence);
        Assert.Equal(CaptureReviewAction.UseArmedIntent, accepted.Correction.Action);
        Assert.Equal(capturedUtc, accepted.CapturedUtc);
        Assert.Equal(CaptureHandoffDisposition.DurablyAccepted, accepted.HandoffDisposition);
        Assert.Equal(capturedUtc, accepted.Provenance.ObservedUtc);
        Assert.Equal(
            TarkovCompanion.Core.Domain.Evidence.EvidenceSourceClass.ExternalVisiblePixels,
            accepted.Provenance.SourceClass);
    }

    [Fact]
    public async Task RejectedDurableHandoffNeverPublishesAnAcceptedResult()
    {
        var handoff = new RecordingHandoff(new(
            CaptureHandoffDisposition.Rejected,
            "capture_handoff_refused"));
        await using var harness = new Harness(handoff: handoff);
        CaptureAcceptedEventArgs? accepted = null;
        harness.Coordinator.Accepted += (_, args) => accepted = args;
        harness.Coordinator.ReviewRequested += (_, args) => harness.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-review");

        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(125));
        var terminal = await harness.WaitForTerminalAsync();

        var artifact = Assert.Single(terminal.Artifacts);
        Assert.Equal(CaptureArtifactDisposition.NoChange, artifact.Disposition);
        Assert.Equal(CaptureHandoffDisposition.Rejected, artifact.HandoffDisposition);
        Assert.Equal("capture_handoff_refused", artifact.DiagnosticCode);
        Assert.Equal(1, handoff.Calls);
        Assert.Null(accepted);
    }

    [Fact]
    public async Task MissingDurableAcknowledgementIsBoundedAndNeverPublishesAccepted()
    {
        var handoff = new NeverAcknowledgingHandoff();
        await using var harness = new Harness(
            handoff: handoff,
            options: new(handoffTimeout: TimeSpan.FromMilliseconds(10)));
        CaptureAcceptedEventArgs? accepted = null;
        harness.Coordinator.Accepted += (_, args) => accepted = args;
        harness.Coordinator.ReviewRequested += (_, args) => harness.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-review");

        var pixels = Pixels(126);
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, pixels);
        var terminal = await harness.WaitForTerminalAsync();

        var artifact = Assert.Single(terminal.Artifacts);
        Assert.Equal(CaptureArtifactDisposition.NoChange, artifact.Disposition);
        Assert.Equal(CaptureHandoffDisposition.AcknowledgementUnknown, artifact.HandoffDisposition);
        Assert.Equal("capture_handoff_acknowledgement_unknown", artifact.DiagnosticCode);
        Assert.Null(accepted);
        Assert.Equal(0, harness.Coordinator.Snapshot.PixelsInUse);
        Assert.All(pixels, value => Assert.Equal(0, value));

        var duplicatePixels = Pixels(126);
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, duplicatePixels);
        await harness.WaitForAsync(snapshot => snapshot.Duplicate == 1);
        Assert.Equal(0, harness.Coordinator.Snapshot.PixelsInUse);
        Assert.All(duplicatePixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task HandoffTimeoutBoundsASynchronousDependencyPrefix()
    {
        var handoff = new BlockingPrefixHandoff();
        await using var harness = new Harness(
            handoff: handoff,
            options: new(handoffTimeout: TimeSpan.FromMilliseconds(10)));
        harness.Coordinator.ReviewRequested += (_, args) => harness.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-review");

        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(127));
        CaptureSessionState? terminal = null;
        try
        {
            await handoff.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            harness.Clock.Advance(TimeSpan.FromMilliseconds(20));
            terminal = await harness.WaitForTerminalAsync();
        }
        finally
        {
            handoff.Release();
        }

        await handoff.Finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var artifact = Assert.Single(terminal!.Artifacts);
        Assert.Equal(CaptureHandoffDisposition.AcknowledgementUnknown, artifact.HandoffDisposition);
        Assert.Equal("capture_handoff_acknowledgement_unknown", artifact.DiagnosticCode);
    }

    [Fact]
    public async Task NewCoordinatorDoesNotCarryProcessLocalDedupeAcrossRestart()
    {
        var firstPixels = Pixels(125);
        await using (var first = new Harness())
        {
            first.Coordinator.ReviewRequested += (_, args) => first.Coordinator.TryReview(
                args.Review.SessionId,
                args.Review.ArtifactId,
                args.Review.DecodeRevision,
                CaptureReviewAction.UseDetected,
                "test-review");
            await first.EnqueueAsync(CaptureDeliveryKind.Drop, firstPixels);
            await first.WaitForTerminalAsync();
        }

        var secondPixels = Pixels(125);
        await using var second = new Harness();
        second.Coordinator.ReviewRequested += (_, args) => second.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-review");
        await second.EnqueueAsync(CaptureDeliveryKind.Drop, secondPixels);
        var terminal = await second.WaitForTerminalAsync();
        Assert.Single(terminal.Artifacts);
        Assert.Equal(0, second.Coordinator.Snapshot.Duplicate);
    }

    [Fact]
    public void InvalidPixelLeaseClearsOwnedBytesBeforeRejectingDimensions()
    {
        var pixels = Pixels(126);
        Assert.Throws<ArgumentException>(() => new CapturePixelLease(new(
            pixels,
            2,
            2,
            32,
            PixelFormat.Bgra8888,
            DateTimeOffset.UnixEpoch,
            "invalid-fixture")));
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public void PixelLeaseRejectsAndClearsUnaccountedBufferTail()
    {
        var pixels = Enumerable.Repeat((byte)0xA5, 20).ToArray();
        Assert.Throws<ArgumentException>(() => new CapturePixelLease(new(
            pixels,
            2,
            2,
            8,
            PixelFormat.Bgra8888,
            DateTimeOffset.UnixEpoch,
            "tail-fixture")));
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task OneSessionCannotRetainAnUnboundedArtifactHistory()
    {
        await using var harness = new Harness(options: new(maximumCapturesPerSession: 2));
        harness.Coordinator.ReviewRequested += (_, args) => harness.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-review");
        var sessionId = harness.Arm(ScanIntent.Stash);

        await harness.EnqueueAsync(
            CaptureDeliveryKind.Drop,
            Pixels(130),
            sessionId: sessionId,
            endSessionAfterReview: false);
        await harness.WaitForAsync(state => state.Sessions.Single().Artifacts.Length == 1
            && state.Sessions.Single().Artifacts[0].Disposition == CaptureArtifactDisposition.Accepted);
        await harness.EnqueueAsync(
            CaptureDeliveryKind.Drop,
            Pixels(131),
            sessionId: sessionId,
            endSessionAfterReview: false);
        await harness.WaitForAsync(state => state.Sessions.Single().Artifacts.Length == 2
            && state.Sessions.Single().Artifacts.All(item => item.Disposition == CaptureArtifactDisposition.Accepted));

        var rejectedPixels = Pixels(132);
        var rejected = await harness.EnqueueAsync(
            CaptureDeliveryKind.Drop,
            rejectedPixels,
            sessionId: sessionId,
            endSessionAfterReview: false);
        Assert.Equal(CaptureQueueDisposition.Rejected, rejected.Disposition);
        Assert.Equal("capture_session_artifact_limit", rejected.Code);
        Assert.Equal(2, Assert.Single(harness.Coordinator.Snapshot.Sessions).Artifacts.Length);
        Assert.All(rejectedPixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task TerminalSessionCannotBeRearmedOrTargeted()
    {
        await using var harness = new Harness();
        harness.Coordinator.ReviewRequested += (_, args) => harness.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-review");
        var sessionId = harness.Arm(ScanIntent.Loot);
        await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(128), sessionId: sessionId);
        await harness.WaitForAsync(state => state.Sessions.Single().IsTerminal);

        var now = harness.Clock.GetUtcNow();
        var arm = harness.Coordinator.Arm(new(
            new(sessionId, ScanIntent.Loot, Origin, now, ExpiresUtc: now.AddSeconds(1)),
            Context,
            new("retry", "Retry.")));
        Assert.False(arm.Accepted);
        Assert.Equal("terminal_session_already_known", arm.Code);

        var pixels = Pixels(129);
        var receipt = await harness.EnqueueAsync(
            CaptureDeliveryKind.Drop,
            pixels,
            sessionId: sessionId);
        Assert.Equal(CaptureQueueDisposition.Rejected, receipt.Disposition);
        Assert.All(pixels, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task DisposeTerminalizesAnIdleArmedSession()
    {
        var harness = new Harness();
        var sessionId = harness.Arm(ScanIntent.Stash);

        await harness.DisposeAsync();

        var session = harness.Coordinator.Snapshot.Sessions.Single(item => item.Request.SessionId == sessionId);
        Assert.True(session.IsTerminal);
        Assert.True(session.CancellationRequested);
        Assert.Equal(CaptureSessionStage.Cancelled, session.Snapshot.Progress[^1].Stage);
        Assert.Equal("shutdown", session.Snapshot.Progress[^1].Detail);
    }

    [Fact]
    public async Task TerminalSessionHistoryIsPrunedToItsConfiguredBound()
    {
        await using var harness = new Harness(options: new(sessionLimit: 16));
        harness.Coordinator.ReviewRequested += (_, args) => harness.Coordinator.TryReview(
            args.Review.SessionId,
            args.Review.ArtifactId,
            args.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "test-review");

        for (var index = 0; index < 20; index++)
        {
            await harness.EnqueueAsync(CaptureDeliveryKind.Drop, Pixels(checked((byte)(140 + index))));
            var accepted = index + 1L;
            await harness.WaitForAsync(state => state.Accepted == accepted && state.Sessions.All(item => item.IsTerminal));
        }

        Assert.Equal(16, harness.Coordinator.Snapshot.Sessions.Length);
    }

    private static readonly ProfileContext Profile = new(
        new(Guid.Parse("5f5b026e-0801-4f29-aab1-d541e1a9bf59"), "2026-09"),
        ProfileGameMode.Pvp,
        new("2026-09"),
        new("en", "US", "Etc/UTC"),
        new("snapshot-a", DateTimeOffset.Parse("2026-09-15T00:00:00Z")));

    private static readonly CaptureContextMetadata Context = new(
        "workspace-a",
        Profile.Identity.ProfileId.ToString("D"),
        "map-a",
        "plan-a",
        "entity-a",
        "scan-a",
        "device-a",
        Profile);

    private static byte[] Pixels(byte seed) =>
    [
        seed, 1, 2, 255,
        3, 4, 5, 255,
        6, 7, 8, 255,
        9, 10, 11, 255,
    ];

    private sealed class Harness : IAsyncDisposable
    {
        public Harness(
            ICaptureSessionPipeline? pipeline = null,
            ICaptureWorkScheduler? scheduler = null,
            ICaptureResultHandoff? handoff = null,
            CaptureSessionOptions? options = null)
        {
            Clock = new(DateTimeOffset.Parse("2026-09-15T00:00:00Z"));
            Coordinator = new(
                scheduler ?? new InlineCaptureWorkScheduler(),
                pipeline ?? new StubPipeline(RecognizedContext.Item),
                handoff ?? new AcceptingHandoff(),
                Origin,
                Clock,
                options ?? new(
                    queueCapacity: 4,
                    maximumRetainedPixelBytes: 1024,
                    maximumDecodeAttempts: 4,
                    decodeRetryDelay: TimeSpan.FromMilliseconds(1),
                    reviewTimeout: TimeSpan.FromSeconds(2),
                    intentLifetime: TimeSpan.FromSeconds(2),
                    deduplicationLifetime: TimeSpan.FromMinutes(1),
                    historyLimit: 256));
        }

        public ManualTimeProvider Clock { get; }

        public CaptureSessionCoordinator Coordinator { get; }

        public CaptureSessionId Arm(ScanIntent intent, TimeSpan? lifetime = null)
        {
            var id = new CaptureSessionId(Guid.NewGuid());
            var now = Clock.GetUtcNow();
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
            CaptureCorrelationId? correlation = null,
            CaptureSessionId? sessionId = null,
            bool endSessionAfterReview = true) =>
            Coordinator.EnqueueAsync(
                new(
                    delivery,
                    new MemoryCaptureSource(
                        new(pixels, 2, 2, 8, PixelFormat.Bgra8888, Clock.GetUtcNow(), "fixture"),
                        delivery == CaptureDeliveryKind.WatchedFile
                            ? CaptureSourceKind.GameWrittenScreenshot
                            : CaptureSourceKind.UserSelectedImage),
                    Context,
                    Clock.GetUtcNow(),
                    correlation ?? CaptureCorrelationId.New(),
                    sessionId,
                    endSessionAfterReview,
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

                Clock.Advance(TimeSpan.FromMilliseconds(1));
                await Task.Delay(1);
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
                null,
                new Confidence(0.91)));
        }
    }

    private sealed class AcceptingHandoff : ICaptureResultHandoff
    {
        public ValueTask<CaptureHandoffResult> AcceptAsync(
            CaptureHandoffRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CaptureHandoffResult.Accepted);
        }
    }

    private sealed class RecordingHandoff(CaptureHandoffResult? result = null) : ICaptureResultHandoff
    {
        public int Calls { get; private set; }

        public ValueTask<CaptureHandoffResult> AcceptAsync(
            CaptureHandoffRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return ValueTask.FromResult(result ?? CaptureHandoffResult.Accepted);
        }
    }

    private sealed class NeverAcknowledgingHandoff : ICaptureResultHandoff
    {
        private readonly TaskCompletionSource<CaptureHandoffResult> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<CaptureHandoffResult> AcceptAsync(
            CaptureHandoffRequest request,
            CancellationToken cancellationToken) =>
            new(_never.Task);
    }

    private sealed class BlockingPrefixHandoff : ICaptureResultHandoff
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<CaptureHandoffResult> AcceptAsync(
            CaptureHandoffRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
            Finished.TrySetResult();
            return ValueTask.FromResult(CaptureHandoffResult.Accepted);
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class FixedPipeline(
        RecognizedContext? context,
        bool ambiguous,
        bool available,
        Confidence confidence) : ICaptureSessionPipeline
    {
        public Task<CaptureAnalysis> AnalyzeAsync(
            CaptureAnalysisRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CaptureAnalysis(
                $"fixed-{request.CorrelationId}",
                context,
                ambiguous,
                available,
                available ? null : "recognition_unavailable",
                confidence));
    }

    private sealed class BlockingPipeline : ICaptureSessionPipeline
    {
        private readonly TaskCompletionSource<RecognizedContext> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CaptureAnalysisRequest> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CaptureAnalysis> AnalyzeAsync(
            CaptureAnalysisRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(request);
            // Deliberately ignores cancellation. This fixture proves that a dependency returning
            // late cannot append after a terminal session or retain its caller-owned pixels.
            var context = await _release.Task.ConfigureAwait(false);
            return Analysis(context, request);
        }

        public void Release(RecognizedContext context) => _release.TrySetResult(context);
    }

    private sealed class HostileCancellationPipeline : ICaptureSessionPipeline
    {
        private readonly TaskCompletionSource _releaseCallback =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseOperation =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CaptureAnalysisRequest> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CallbackStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CaptureAnalysis> AnalyzeAsync(
            CaptureAnalysisRequest request,
            CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                CallbackStarted.TrySetResult();
                _releaseCallback.Task.GetAwaiter().GetResult();
                throw new InvalidOperationException("Hostile cancellation callback failure.");
            });
            Started.TrySetResult(request);
            await _releaseOperation.Task.ConfigureAwait(false);
            return Analysis(RecognizedContext.Item, request);
        }

        public void Release()
        {
            _releaseCallback.TrySetResult();
            _releaseOperation.TrySetResult();
        }
    }

    private sealed class BlockingRedecodePipeline : ICaptureSessionPipeline
    {
        private readonly TaskCompletionSource _releaseRedecode =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<CaptureAnalysisRequest> RedecodeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CaptureAnalysis> AnalyzeAsync(
            CaptureAnalysisRequest request,
            CancellationToken cancellationToken)
        {
            if (request.DecodeRevision == 0)
            {
                return Analysis(RecognizedContext.Item, request);
            }

            RedecodeStarted.TrySetResult(request);
            // As above, a hostile dependency is allowed to ignore the token and return late.
            await _releaseRedecode.Task.ConfigureAwait(false);
            return Analysis(RecognizedContext.Item, request);
        }

        public void ReleaseRedecode() => _releaseRedecode.TrySetResult();
    }

    private sealed class ThrowingPipeline : ICaptureSessionPipeline
    {
        public Task<CaptureAnalysis> AnalyzeAsync(
            CaptureAnalysisRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Hostile fixture analysis failure.");
    }

    private static CaptureAnalysis Analysis(
        RecognizedContext context,
        CaptureAnalysisRequest request) =>
        new(
            $"result-{request.CorrelationId}",
            context,
            false,
            true,
            null,
            new Confidence(0.91));

    private sealed class RetrySource(int failures, byte[] pixels) : ICaptureContentSource
    {
        private int _remaining = failures;
        private CapturePixelLease? _pixels = new(
            new(pixels, 2, 2, 8, PixelFormat.Bgra8888, DateTimeOffset.Parse("2026-09-15T00:00:00Z"), "retry-fixture"));

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

    private sealed class BlockingSource(byte[] pixels, TimeProvider timeProvider) : ICaptureContentSource
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CapturePixelLease? _pixels = new(new(
            pixels,
            2,
            2,
            8,
            PixelFormat.Bgra8888,
            timeProvider.GetUtcNow(),
            "blocking-fixture"));

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CaptureSourceKind SourceKind => CaptureSourceKind.UserSelectedImage;

        public async ValueTask<CaptureSourceReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            // Deliberately ignores cancellation and hands ownership over after cancellation.
            await _release.Task.ConfigureAwait(false);
            return CaptureSourceReadResult.Success(Interlocked.Exchange(ref _pixels, null)!);
        }

        public void Release() => _release.TrySetResult();

        public void Dispose() => Interlocked.Exchange(ref _pixels, null)?.Dispose();
    }

    private sealed class ThrowingDisposeSource(byte[] pixels, TimeProvider timeProvider) : ICaptureContentSource
    {
        private CapturePixelLease? _pixels = new(new(
            pixels,
            2,
            2,
            8,
            PixelFormat.Bgra8888,
            timeProvider.GetUtcNow(),
            "throwing-dispose-fixture"));

        public CaptureSourceKind SourceKind => CaptureSourceKind.UserSelectedImage;

        public ValueTask<CaptureSourceReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(CaptureSourceReadResult.Success(
                Interlocked.Exchange(ref _pixels, null)!));
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _pixels, null)?.Dispose();
            throw new InvalidOperationException("Hostile fixture disposal failure.");
        }
    }
}
