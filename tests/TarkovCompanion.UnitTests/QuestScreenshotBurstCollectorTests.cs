using TarkovCompanion.Application.Services.Quests;

namespace TarkovCompanion.UnitTests;

public sealed class QuestScreenshotBurstCollectorTests
{
    private static readonly DateTimeOffset Taken = new(2026, 9, 22, 22, 4, 0, TimeSpan.Zero);

    [Fact]
    public void FramesInsideTheWindowShareOneOfferAndDismissal()
    {
        var collector = new QuestScreenshotBurstCollector();

        var first = collector.Observe("first.png", Taken, raidActive: false);
        var second = collector.Observe("second.png", Taken.AddSeconds(50), raidActive: false);
        var review = collector.TakeForReview();
        var afterReview = collector.Observe("third.png", Taken.AddSeconds(90), raidActive: false);

        Assert.True(first.IsVisible);
        Assert.Equal(first.BurstId, second.BurstId);
        Assert.Equal(["first.png", "second.png"], review.Paths);
        Assert.False(afterReview.IsVisible);
        Assert.Equal(3, afterReview.ScreenshotCount);
    }

    [Fact]
    public void AFrameAfterTheWindowStartsOneNewOffer()
    {
        var collector = new QuestScreenshotBurstCollector();
        var first = collector.Observe("first.png", Taken, raidActive: false);
        collector.DismissCurrent();

        var next = collector.Observe("next.png", Taken.AddMinutes(2).AddSeconds(1), raidActive: false);

        Assert.True(next.IsVisible);
        Assert.NotEqual(first.BurstId, next.BurstId);
        Assert.Equal(["next.png"], next.Paths);
    }

    [Fact]
    public void FramesSeenDuringARaidNeverCreateAnOffer()
    {
        var collector = new QuestScreenshotBurstCollector();

        var result = collector.Observe("in-raid.png", Taken, raidActive: true);

        Assert.Equal(QuestScreenshotBurstOffer.Empty, result);
        Assert.False(collector.Current.IsVisible);
    }

    [Fact]
    public void ConcurrentCompletionOrderStillPresentsFilesByCaptureTime()
    {
        var collector = new QuestScreenshotBurstCollector();

        collector.Observe("later.png", Taken.AddSeconds(30), raidActive: false);
        var result = collector.Observe("earlier.png", Taken, raidActive: false);

        Assert.Equal(["earlier.png", "later.png"], result.Paths);
    }
}
