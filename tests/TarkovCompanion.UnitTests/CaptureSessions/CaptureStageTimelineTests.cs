using TarkovCompanion.Application.Services.CaptureSessions;

namespace TarkovCompanion.UnitTests.CaptureSessions;

/// <summary>
/// #572: the only instrumentation the log had before this was ScanUseCase's "recognised context
/// ..." line, from a different pipeline than the one that produces the V2 Loot page. These test
/// the timeline that gives the real pipeline stage timing, correlated by id rather than threaded
/// through every stage's own parameters.
/// </summary>
public sealed class CaptureStageTimelineTests
{
    private static readonly CaptureCorrelationId Id = CaptureCorrelationId.New();
    private static readonly DateTimeOffset FileSeen = new(2026, 9, 20, 21, 12, 0, TimeSpan.Zero);

    [Fact]
    public void CompleteReportsEveryMarkedStageAndTheTotal()
    {
        var timeline = new CaptureStageTimeline();
        timeline.Begin(Id, FileSeen);
        timeline.Mark(Id, "context_ocr", TimeSpan.FromMilliseconds(150));
        timeline.Mark(Id, "grid_and_icon_matching", TimeSpan.FromMilliseconds(80));

        var summary = timeline.Complete(Id, FileSeen + TimeSpan.FromMilliseconds(300));

        Assert.NotNull(summary);
        Assert.Equal(Id, summary.CorrelationId);
        Assert.Equal(300, summary.TotalMilliseconds);
        Assert.Equal(
            [("context_ocr", 150d), ("grid_and_icon_matching", 80d)],
            summary.Stages.Select(stage => (stage.Stage, stage.ElapsedMilliseconds)));
    }

    [Fact]
    public void CompleteIsTheLastCompletedSummaryAfterwards()
    {
        var timeline = new CaptureStageTimeline();
        timeline.Begin(Id, FileSeen);

        var summary = timeline.Complete(Id, FileSeen + TimeSpan.FromMilliseconds(50));

        Assert.Same(summary, timeline.LastCompleted);
    }

    [Fact]
    public void ANewerCompletionReplacesTheOlderOne()
    {
        var timeline = new CaptureStageTimeline();
        var first = CaptureCorrelationId.New();
        var second = CaptureCorrelationId.New();
        timeline.Begin(first, FileSeen);
        timeline.Begin(second, FileSeen);

        timeline.Complete(first, FileSeen + TimeSpan.FromMilliseconds(10));
        var latest = timeline.Complete(second, FileSeen + TimeSpan.FromMilliseconds(20));

        Assert.Same(latest, timeline.LastCompleted);
        Assert.Equal(second, timeline.LastCompleted!.CorrelationId);
    }

    [Fact]
    public void MarkingAnIdNothingBeganIsANoOp()
    {
        var timeline = new CaptureStageTimeline();

        timeline.Mark(Id, "context_ocr", TimeSpan.FromMilliseconds(10));

        Assert.Null(timeline.Complete(Id, FileSeen));
    }

    [Fact]
    public void CompletingTwiceReturnsNullTheSecondTime()
    {
        var timeline = new CaptureStageTimeline();
        timeline.Begin(Id, FileSeen);
        timeline.Complete(Id, FileSeen + TimeSpan.FromMilliseconds(10));

        var second = timeline.Complete(Id, FileSeen + TimeSpan.FromMilliseconds(20));

        Assert.Null(second);
    }

    [Fact]
    public void ASecondBeginForTheSameIdIsIgnored()
    {
        var timeline = new CaptureStageTimeline();
        timeline.Begin(Id, FileSeen);
        timeline.Begin(Id, FileSeen + TimeSpan.FromSeconds(5));

        var summary = timeline.Complete(Id, FileSeen + TimeSpan.FromMilliseconds(100));

        // The first Begin's file-seen time won, so the total is measured from it.
        Assert.Equal(100, summary!.TotalMilliseconds);
    }

    [Fact]
    public void AnAbandonedBeginIsSweptAfterTwoMinutes()
    {
        var timeline = new CaptureStageTimeline();
        var abandoned = CaptureCorrelationId.New();
        timeline.Begin(abandoned, FileSeen);

        // A later scan's Begin, arriving more than two minutes after the abandoned one, sweeps it.
        timeline.Begin(CaptureCorrelationId.New(), FileSeen + TimeSpan.FromMinutes(3));

        Assert.Null(timeline.Complete(abandoned, FileSeen + TimeSpan.FromMinutes(3)));
    }
}
