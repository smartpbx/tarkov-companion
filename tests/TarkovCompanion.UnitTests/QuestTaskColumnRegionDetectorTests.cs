using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.UnitTests;

public sealed class QuestTaskColumnRegionDetectorTests
{
    [Fact]
    public void FindsCentredPanelAndWidestTaskSpanInsteadOfRightItemGrid()
    {
        const int width = 384;
        const int height = 108;
        var pixels = new byte[width * height * 4];
        Fill(pixels, width, 0, 0, width, height, 6, 9, 12);
        Fill(pixels, width, 96, 0, 192, 11, 40, 86, 70);
        Fill(pixels, width, 96, 12, 192, 92, 18, 24, 21);
        foreach (var separator in new[] { 104, 110, 122, 174, 200, 220, 238 })
        {
            Fill(pixels, width, separator, 15, 2, 88, 82, 92, 86);
        }

        var image = new CapturedImage(
            pixels,
            width,
            height,
            width * 4,
            PixelFormat.Bgra8888,
            DateTimeOffset.UnixEpoch,
            "synthetic-tasks");

        var detected = new QuestTaskColumnRegionDetector().Detect(image);
        var region = detected.Region;

        Assert.Equal(QuestScreenshotLayout.SideTaskList, detected.Layout);
        Assert.InRange(region.X, 119, 128);
        Assert.InRange(region.X + region.Width, 168, 178);
        Assert.InRange(region.Y, 14, 16);
        Assert.True(region.X + region.Width < 220, "The right-side item grid must be outside OCR.");
    }

    [Fact]
    public void SelectedOperationalTabMarksTheTableAsNonCatalogTasks()
    {
        const int width = 384;
        const int height = 108;
        var pixels = new byte[width * height * 4];
        Fill(pixels, width, 0, 0, width, height, 6, 9, 12);
        Fill(pixels, width, 96, 0, 192, 11, 40, 86, 70);
        Fill(pixels, width, 126, 5, 44, 5, 210, 225, 216);
        Fill(pixels, width, 96, 12, 192, 92, 18, 24, 21);
        foreach (var separator in new[] { 104, 110, 122, 174, 200, 220, 238 })
        {
            Fill(pixels, width, separator, 15, 2, 88, 82, 92, 86);
        }

        var image = new CapturedImage(
            pixels,
            width,
            height,
            width * 4,
            PixelFormat.Bgra8888,
            DateTimeOffset.UnixEpoch,
            "synthetic-operational");

        var detected = new QuestTaskColumnRegionDetector().Detect(image);

        Assert.Equal(QuestScreenshotLayout.OperationalTaskList, detected.Layout);
    }

    [Fact]
    public void StoryScreenUsesOnlyTheChapterTitleBand()
    {
        const int width = 384;
        const int height = 108;
        var pixels = new byte[width * height * 4];
        Fill(pixels, width, 0, 0, width, height, 6, 9, 12);
        Fill(pixels, width, 96, 0, 192, 11, 40, 86, 70);
        Fill(pixels, width, 96, 12, 192, 92, 18, 24, 21);
        Fill(pixels, width, 112, 16, 75, 4, 188, 202, 192);
        Fill(pixels, width, 112, 40, 120, 2, 160, 170, 165);

        var image = new CapturedImage(
            pixels,
            width,
            height,
            width * 4,
            PixelFormat.Bgra8888,
            DateTimeOffset.UnixEpoch,
            "synthetic-story");

        var detected = new QuestTaskColumnRegionDetector().Detect(image);

        Assert.Equal(QuestScreenshotLayout.StoryChapter, detected.Layout);
        Assert.InRange(detected.Region.X, 109, 114);
        Assert.InRange(detected.Region.Y, 16, 17);
        Assert.InRange(detected.Region.Y + detected.Region.Height, 21, 23);
        Assert.True(detected.Region.X + detected.Region.Width < 240, "The item grid must be outside OCR.");
    }

    private static void Fill(
        byte[] pixels,
        int stridePixels,
        int x,
        int y,
        int width,
        int height,
        byte red,
        byte green,
        byte blue)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                var offset = ((row * stridePixels) + column) * 4;
                pixels[offset] = blue;
                pixels[offset + 1] = green;
                pixels[offset + 2] = red;
                pixels[offset + 3] = 255;
            }
        }
    }
}
