using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Platform.Windows.Watching;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests.CaptureSessions;

/// <summary>
/// #712 0-12: every kind of screenshot gets a timeline of milestones on one monotonic clock, kept
/// in a bounded ring and summarised as p50/p95 per kind; and a settling file is probed again a
/// tenth of a second later rather than a whole poll later, without ever reading a partial file.
/// </summary>
public sealed class ScreenshotStageTimelineTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-26T12:00:00Z");

    private static readonly WorkspaceOrigin Origin = new(
        new(Guid.Parse("2f0d1c8a-4c43-4b19-9d8e-3bd0e6d0a4a1")),
        new(Guid.Parse("a3a8f1d7-5d8e-44fb-a0e4-0b0b8a0e6c11")),
        WorkspaceOriginKind.DesktopApplication,
        "timeline-tests");

    /// <summary>A whole, tiny PNG: signature, IHDR, IDAT, IEND.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void MilestonesAreMeasuredFromTheMomentTheNameWasSeen()
    {
        var clock = new ManualTimeProvider(Start);
        var timeline = new CaptureStageTimeline(timeProvider: clock);
        var id = CaptureCorrelationId.New();
        var seen = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromMilliseconds(120));
        var settled = clock.GetTimestamp();

        timeline.Begin(id, clock.GetUtcNow(), seen, null);
        timeline.Reached(id, CaptureTimelineKinds.Settled, settled);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        timeline.Reached(id, CaptureTimelineKinds.Decoded);
        timeline.Reached(id, CaptureTimelineKinds.Decoded);
        clock.Advance(TimeSpan.FromMilliseconds(80));
        timeline.Classify(id, "Stash");
        timeline.Reached(id, CaptureTimelineKinds.Shown);
        var summary = timeline.Complete(id, clock.GetUtcNow());

        Assert.NotNull(summary);
        Assert.Equal("Stash", summary.Kind);
        Assert.Equal(
            [(CaptureTimelineKinds.Settled, 120d), (CaptureTimelineKinds.Decoded, 420d), (CaptureTimelineKinds.Shown, 500d)],
            summary.Milestones.Select(milestone => (milestone.Stage, milestone.ElapsedMilliseconds)));
    }

    [Fact]
    public void AnotherKindNeitherReplacesTheLastLootScanNorStartsTheLootProgressLine()
    {
        var clock = new ManualTimeProvider(Start);
        var timeline = new CaptureStageTimeline(timeProvider: clock);
        var steps = new List<CaptureStageStep>();
        timeline.Progressed += steps.Add;
        var loot = CaptureCorrelationId.New();
        timeline.Begin(loot, Start);
        timeline.Classify(loot, CaptureTimelineKinds.Loot);
        var lootSummary = timeline.Complete(loot, Start.AddMilliseconds(900));

        var position = CaptureCorrelationId.New();
        timeline.Begin(position, Start, clock.GetTimestamp(), CaptureTimelineKinds.Position);
        timeline.Reached(position, CaptureTimelineKinds.Applied);
        timeline.Complete(position, Start);
        var stash = CaptureCorrelationId.New();
        timeline.Begin(stash, Start);
        timeline.Classify(stash, "Stash");
        timeline.Complete(stash, Start.AddMilliseconds(700));

        Assert.Same(lootSummary, timeline.LastCompleted);
        Assert.DoesNotContain(steps, step => step.CorrelationId == position);
        Assert.Contains(steps, step => step.CorrelationId == stash && step.Summary is { IsLoot: false });
        Assert.Equal(3, timeline.Recent.Count);
    }

    [Fact]
    public void TheLootProgressLineLetsGoOfAScreenshotThatWasSomethingElse()
    {
        var timeline = new CaptureStageTimeline();
        var progress = new LootScanProgressViewModel(timeline, action => action());
        var id = CaptureCorrelationId.New();

        timeline.Begin(id, Start);
        Assert.True(progress.IsScanning);
        timeline.Classify(id, "Stash");
        timeline.Complete(id, Start.AddMilliseconds(400));

        Assert.False(progress.IsScanning);
        Assert.False(progress.IsVisible);
    }

    [Fact]
    public void StatisticsAreNearestRankP50AndP95PerKindAndMilestone()
    {
        var clock = new ManualTimeProvider(Start);
        var timeline = new CaptureStageTimeline(timeProvider: clock);
        for (var shown = 1; shown <= 20; shown++)
        {
            var id = CaptureCorrelationId.New();
            timeline.Begin(id, clock.GetUtcNow(), clock.GetTimestamp(), "Flea");
            clock.Advance(TimeSpan.FromMilliseconds(shown * 10));
            timeline.Reached(id, CaptureTimelineKinds.Shown);
            timeline.Complete(id, clock.GetUtcNow());
        }

        var statistic = Assert.Single(timeline.Statistics());

        Assert.Equal(("Flea", CaptureTimelineKinds.Shown, 20), (statistic.Kind, statistic.Milestone, statistic.Count));
        Assert.Equal(100, statistic.P50Milliseconds);
        Assert.Equal(190, statistic.P95Milliseconds);
        var row = Assert.Single(ScreenshotTimingViewModel.Describe(timeline.Statistics()));
        Assert.Equal("Flea (20): shown 100 ms / 190 ms", row);
    }

    [Fact]
    public void TheRingKeepsOnlyTheNewestTimelines()
    {
        var timeline = new CaptureStageTimeline();
        for (var index = 0; index < CaptureStageTimeline.RecentCapacity + 40; index++)
        {
            var id = CaptureCorrelationId.New();
            timeline.Begin(id, Start, null, "Stash");
            timeline.Complete(id, Start);
        }

        Assert.Equal(CaptureStageTimeline.RecentCapacity, timeline.Recent.Count);
    }

    [Fact]
    public void ARelaySendIsAddedOnceToTheNewestPosition()
    {
        var clock = new ManualTimeProvider(Start);
        var timeline = new CaptureStageTimeline(timeProvider: clock);
        var older = BeginPosition(timeline, clock);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        var newest = BeginPosition(timeline, clock);
        clock.Advance(TimeSpan.FromMilliseconds(40));

        timeline.PositionPublished(clock.GetTimestamp());
        clock.Advance(TimeSpan.FromSeconds(3));
        timeline.PositionPublished(clock.GetTimestamp());

        var recent = timeline.Recent;
        Assert.DoesNotContain(recent.Single(summary => summary.CorrelationId == older).Milestones, milestone => milestone.Stage == CaptureTimelineKinds.Published);
        var published = Assert.Single(recent.Single(summary => summary.CorrelationId == newest).Milestones, milestone => milestone.Stage == CaptureTimelineKinds.Published);
        Assert.Equal(40, published.ElapsedMilliseconds);
    }

    [Theory]
    [InlineData(RecognizedContext.Loot, "Loot")]
    [InlineData(RecognizedContext.Stash, "Stash")]
    [InlineData(RecognizedContext.Flea, "Flea")]
    [InlineData(RecognizedContext.Ammo, "Ammo")]
    [InlineData(RecognizedContext.Keys, "Keys")]
    [InlineData(RecognizedContext.QuestItems, "QuestItems")]
    [InlineData(RecognizedContext.ExtractsAndMap, "ExtractsAndMap")]
    [InlineData(RecognizedContext.HealthAndCharacter, "HealthAndCharacter")]
    public async Task EveryCaptureKindRecordsEveryMilestoneThroughTheCoordinator(RecognizedContext context, string kind)
    {
        var timeline = new CaptureStageTimeline();
        var id = CaptureCorrelationId.New();
        await using var coordinator = Coordinator(new FixedPipeline(timeline, context), timeline);
        coordinator.ReviewRequested += (_, request) => coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision,
            CaptureReviewAction.UseDetected,
            "system-watched-file");

        await coordinator.EnqueueAsync(Submission(id), CancellationToken.None);
        await UntilAsync(() => timeline.Recent.Any(summary => summary.CorrelationId == id));

        var summary = Assert.Single(timeline.Recent);
        Assert.Equal(kind, summary.Kind);
        Assert.Equal(
            [CaptureTimelineKinds.Dequeued, CaptureTimelineKinds.Decoded, CaptureTimelineKinds.Classified, CaptureTimelineKinds.Recognised, CaptureTimelineKinds.Shown],
            summary.Milestones.Select(milestone => milestone.Stage));
    }

    [Fact]
    public async Task AScreenNobodyCouldPlaceStillCompletesAsUnread()
    {
        var timeline = new CaptureStageTimeline();
        var id = CaptureCorrelationId.New();
        await using var coordinator = Coordinator(new FixedPipeline(timeline, null), timeline);
        coordinator.ReviewRequested += (_, request) => coordinator.TryReview(
            request.Review.SessionId,
            request.Review.ArtifactId,
            request.Review.DecodeRevision,
            CaptureReviewAction.Cancel,
            "system-watched-file");

        await coordinator.EnqueueAsync(Submission(id), CancellationToken.None);
        await UntilAsync(() => timeline.Recent.Any(summary => summary.CorrelationId == id));

        Assert.Equal(CaptureTimelineKinds.Unread, Assert.Single(timeline.Recent).Kind);
    }

    [Fact]
    public async Task ACompleteFileSettlesWithinTheShortProbeNotAWholePoll()
    {
        // The idle poll is a second. Before #712 0-12 the second probe waited for it, so a stash
        // screenshot outside a raid was read a second after it appeared, at the earliest.
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var watcher = new WindowsScreenshotWatcher(pollInterval: TimeSpan.FromSeconds(1));
            await using var sightings = watcher.WatchAsync(root, stopping.Token).GetAsyncEnumerator(stopping.Token);
            var first = sightings.MoveNextAsync().AsTask();
            // Let the first listing finish (empty) so this file is new rather than startup history.
            await Task.Delay(100, stopping.Token);
            var path = Path.Combine(root, "stash.png");
            await File.WriteAllBytesAsync(path, Png, stopping.Token);

            Assert.True(await first);
            Assert.Equal(ScreenshotSightingKind.NameSeen, sightings.Current.Kind);
            var named = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(await sightings.MoveNextAsync());
            Assert.Equal(ScreenshotSightingKind.Settled, sightings.Current.Kind);

            Assert.True(named.Elapsed < TimeSpan.FromMilliseconds(700), $"settled {named.Elapsed.TotalMilliseconds:0} ms after its name");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task APartialPngIsNeverSettledHoweverFastItIsProbed()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var path = Path.Combine(root, "partial.png");
            var watcher = new WindowsScreenshotWatcher(
                pollInterval: TimeSpan.FromSeconds(1),
                settleProbeInterval: TimeSpan.FromMilliseconds(10));
            await using var sightings = watcher.WatchAsync(root, stopping.Token).GetAsyncEnumerator(stopping.Token);
            var first = sightings.MoveNextAsync().AsTask();
            await Task.Delay(100, stopping.Token);
            // Everything but IEND: stable in size, a valid signature, and not a whole image.
            await File.WriteAllBytesAsync(path, Png[..^12], stopping.Token);

            Assert.True(await first);
            Assert.Equal(ScreenshotSightingKind.NameSeen, sightings.Current.Kind);
            var next = sightings.MoveNextAsync().AsTask();
            // Dozens of fast probes pass over the stable, incomplete file.
            await Task.Delay(400, stopping.Token);
            Assert.False(next.IsCompleted);

            await File.WriteAllBytesAsync(path, Png, stopping.Token);
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(3), stopping.Token));
            Assert.Equal(ScreenshotSightingKind.Settled, sightings.Current.Kind);
            Assert.Equal(Png, await File.ReadAllBytesAsync(path, stopping.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CaptureCorrelationId BeginPosition(CaptureStageTimeline timeline, ManualTimeProvider clock)
    {
        var id = CaptureCorrelationId.New();
        timeline.Begin(id, clock.GetUtcNow(), clock.GetTimestamp(), CaptureTimelineKinds.Position);
        timeline.Reached(id, CaptureTimelineKinds.Applied);
        timeline.Complete(id, clock.GetUtcNow());
        return id;
    }

    private static CaptureSessionCoordinator Coordinator(ICaptureSessionPipeline pipeline, ICaptureStageTimeline timeline) =>
        new(
            new InlineCaptureWorkScheduler(),
            pipeline,
            new AcceptingHandoff(),
            Origin,
            options: new(
                queueCapacity: 4,
                maximumRetainedPixelBytes: 1024,
                maximumDecodeAttempts: 2,
                decodeRetryDelay: TimeSpan.FromMilliseconds(1),
                reviewTimeout: TimeSpan.FromSeconds(5),
                intentLifetime: TimeSpan.FromSeconds(2),
                deduplicationLifetime: TimeSpan.FromMinutes(1),
                historyLimit: 64),
            stageTimeline: timeline);

    private static CaptureSubmission Submission(CaptureCorrelationId id) =>
        new(
            CaptureDeliveryKind.WatchedFile,
            new MemoryCaptureSource(
                new(Guid.NewGuid().ToByteArray(), 2, 2, 8, PixelFormat.Bgra8888, DateTimeOffset.UtcNow, "fixture"),
                CaptureSourceKind.GameWrittenScreenshot),
            CaptureContextMetadata.Empty,
            DateTimeOffset.UtcNow,
            id);

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "tc-timeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var poll = 0; poll < 3000 && !condition(); poll++)
        {
            await Task.Delay(1);
        }

        Assert.True(condition());
    }

    /// <summary>Reads the frame as <paramref name="context"/>, marking "classified" as the real pipeline does after OCR.</summary>
    private sealed class FixedPipeline(ICaptureStageTimeline timeline, RecognizedContext? context) : ICaptureSessionPipeline
    {
        public Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken)
        {
            timeline.Reached(request.CorrelationId, CaptureTimelineKinds.Classified);
            return Task.FromResult(new CaptureAnalysis(
                $"result-{request.CorrelationId}",
                context,
                context is null,
                true,
                null,
                new Confidence(0.91)));
        }
    }

    private sealed class AcceptingHandoff : ICaptureResultHandoff
    {
        public ValueTask<CaptureHandoffResult> AcceptAsync(CaptureHandoffRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CaptureHandoffResult.Accepted);
    }
}
