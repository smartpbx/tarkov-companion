using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.StashScanFixtures;

namespace TarkovCompanion.UnitTests.StashScan;

/// <summary>
/// The stash panel's lattice and footprints, read from painted frames at the sizes that matter:
/// 1920x1080 first, then the two that must merely not break.
/// </summary>
public sealed class StashPanelRecognitionTests
{
    [Theory]
    [InlineData(1920, 1080, 1270, 81)]
    [InlineData(2560, 1440, 1700, 108)]
    // The one real measurement was taken on this shape: a 630-pixel panel in a 3840-wide frame.
    [InlineData(3840, 1080, 2224, 81)]
    public void FindsTheTenColumnPanelWhereverItSitsAndLeavesOutTheCutRow(int width, int height, int panelX, int panelY)
    {
        var options = new SyntheticStashFrameOptions(FrameWidth: width, FrameHeight: height, PanelX: panelX, PanelY: panelY);
        var image = SyntheticStashPainter.RenderFrame(SyntheticStashLayout.Build(rows: 34), firstRow: 10, options);

        var spec = new StashPanelLatticeDetector().Detect(image);

        Assert.NotNull(spec);
        Assert.Equal(StashPanelLatticeDetector.Columns, spec!.Columns);
        Assert.Equal(options.VisibleRows, spec.Rows);
        Assert.InRange(spec.Bounds.X, panelX - 1, panelX + 1);
        Assert.InRange(spec.Bounds.Y, panelY - 1, panelY + 1);
        Assert.Equal(options.Pitch * StashPanelLatticeDetector.Columns, spec.Bounds.Width);
    }

    [Fact]
    public void SaysThereIsNoStashPanelRatherThanInventingOne()
    {
        // The inventory screen's gear slots alone: boxes and lines, and no ten-column panel.
        var image = SyntheticStashPainter.RenderFrame(SyntheticStashLayout.Of(0), firstRow: 0, new(VisibleRows: 0, PartialRowPixels: 0));

        Assert.Null(new StashPanelLatticeDetector().Detect(image));
    }

    /// <summary>
    /// On two real screenshots an opened Junk case - fourteen columns at the stash's own pitch,
    /// in front of everything - was read as the stash: a ten-column window of it scored higher
    /// than the panel behind it.
    /// </summary>
    [Fact]
    public void AnOpenedCaseAtTheSamePitchIsNotTheStash()
    {
        var caseAlone = SyntheticStashPainter.RenderFrame(SyntheticStashLayout.Of(0), firstRow: 0, new(VisibleRows: 0, PartialRowPixels: 0));
        DrawEmptyGrid(caseAlone, x: 300, y: 120, columns: 14, rows: 12, pitch: 63);

        Assert.Null(new StashPanelLatticeDetector().Detect(caseAlone));

        var options = new SyntheticStashFrameOptions();
        var caseBesideTheStash = SyntheticStashPainter.RenderFrame(SyntheticStashLayout.Build(rows: 34), firstRow: 10, options);
        DrawEmptyGrid(caseBesideTheStash, x: 300, y: 120, columns: 14, rows: 12, pitch: 63);

        var spec = new StashPanelLatticeDetector().Detect(caseBesideTheStash);

        Assert.NotNull(spec);
        Assert.InRange(spec!.Bounds.X, options.PanelX - 1, options.PanelX + 1);
        Assert.Equal(options.VisibleRows, spec.Rows);
    }

    /// <summary>A grid of empty cells as the game draws them: a one-pixel hatch, lines fifteen levels over it.</summary>
    private static void DrawEmptyGrid(TarkovCompanion.Core.Domain.Recognition.CapturedImage image, int x, int y, int columns, int rows, int pitch)
    {
        var pixels = System.Runtime.InteropServices.MemoryMarshal.AsMemory(image.Pixels).Span;
        for (var row = y; row <= y + (rows * pitch); row++)
        {
            for (var column = x; column <= x + (columns * pitch); column++)
            {
                var onLine = (row - y) % pitch == 0 || (column - x) % pitch == 0;
                var value = (byte)(onLine ? 53 : ((row + column) & 1) == 0 ? 35 : 41);
                var offset = (row * image.Stride) + (column * 4);
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }
    }

    [Fact]
    public void ReadsEveryWholeItemOfAPackedScreenAsItsOwnRectangle()
    {
        var layout = SyntheticStashLayout.Build(rows: 14);
        var options = new SyntheticStashFrameOptions();
        var image = SyntheticStashPainter.RenderFrame(layout, firstRow: 0, options);
        var spec = new StashPanelLatticeDetector().Detect(image);
        Assert.NotNull(spec);

        var footprints = new StashFootprintReader().Read(image, spec!);

        var truth = layout.Placements
            .Select(placement => new StashFootprint(placement.Row, placement.Column, placement.Item.Width, placement.Item.Height))
            .OrderBy(footprint => footprint.Row)
            .ThenBy(footprint => footprint.Column)
            .ToArray();
        Assert.Equal(truth, footprints);
    }

    [Fact]
    public void KeepsTwoOfTheSameItemSideBySideApart()
    {
        var bolts = SyntheticStashLayout.Catalog[0];
        var layout = SyntheticStashLayout.Of(
            14,
            new SyntheticStashPlacement(bolts, 2, 3),
            new SyntheticStashPlacement(bolts, 2, 4),
            new SyntheticStashPlacement(bolts, 3, 3));
        var image = SyntheticStashPainter.RenderFrame(layout, firstRow: 0);
        var spec = new StashPanelLatticeDetector().Detect(image);
        Assert.NotNull(spec);

        var footprints = new StashFootprintReader().Read(image, spec!);

        Assert.Equal(
            [new StashFootprint(2, 3, 1, 1), new StashFootprint(2, 4, 1, 1), new StashFootprint(3, 3, 1, 1)],
            footprints);
    }
}
