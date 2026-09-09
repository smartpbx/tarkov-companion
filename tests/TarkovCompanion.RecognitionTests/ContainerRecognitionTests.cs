using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

public sealed class ContainerRecognitionTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void GridSegmentationIsResolutionIndependent(int width, int height)
    {
        var pixels = new byte[checked(width * height)];
        Array.Fill(pixels, (byte)20);
        var image = CreateImage(pixels, width, height);
        var grid = ContainerGridSpec.FromNormalized(image, 0.10, 0.10, 0.40, 0.50, 4, 3);
        PaintCell(pixels, width, grid, row: 0, column: 1, value: 180);
        PaintCell(pixels, width, grid, row: 2, column: 3, value: 160);

        var segments = new ContainerGridSegmenter().Segment(image, grid);

        Assert.Equal(12, segments.Count);
        Assert.Equal(2, segments.Count(segment => segment.IsOccupied));
        Assert.Contains(segments, segment => segment is { Row: 0, Column: 1, IsOccupied: true });
        Assert.Contains(segments, segment => segment is { Row: 2, Column: 3, IsOccupied: true });
    }

    [Fact]
    public void AggregationExcludesLowConfidenceAndRanksDropFirstByValuePerSlot()
    {
        ContainerSegment[] segments =
        [
            OccupiedCell(0, 0, 0),
            OccupiedCell(0, 1, 100),
            OccupiedCell(0, 2, 200),
            OccupiedCell(0, 3, 300),
        ];
        RecognitionCandidate[] candidates =
        [
            Candidate("gpu", "Graphics Card", 0.96, 0),
            Candidate("gpu", "Graphics Card", 0.92, 100),
            Candidate("wires", "Wires", 0.75, 200),
            Candidate("junk", "Junk", 0.69, 300),
        ];
        ContainerItemValuation[] valuations =
        [
            new("gpu", 200),
            new("wires", 50),
            new("junk", 1),
        ];

        var result = new ContainerScanAnalyzer().Analyze(segments, candidates, valuations);

        Assert.Equal(450, result.ApproximateValue);
        Assert.Equal(3, result.IncludedOccupiedSlots);
        Assert.Equal(["wires", "gpu"], result.DropFirstItemIds);
        Assert.Collection(
            result.Items,
            item =>
            {
                Assert.Equal("gpu", item.CanonicalId);
                Assert.Equal(2, item.Quantity);
            },
            item => Assert.Equal("wires", item.CanonicalId));
        Assert.DoesNotContain(result.Items, item => item.CanonicalId == "junk");
    }

    private static ContainerSegment OccupiedCell(int row, int column, int x) => new(
        row,
        column,
        new PixelRect(x, 0, 100, 100),
        Confidence.Certain,
        true);

    private static RecognitionCandidate Candidate(string id, string name, double confidence, int x) => new(
        id,
        name,
        new Confidence(confidence),
        "fixture",
        new PixelRect(x + 10, 10, 80, 80));

    private static CapturedImage CreateImage(byte[] pixels, int width, int height) => new(
        pixels,
        width,
        height,
        width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
        "fixture://container-grid");

    private static void PaintCell(
        byte[] pixels,
        int stride,
        ContainerGridSpec grid,
        int row,
        int column,
        byte value)
    {
        var left = grid.Bounds.X + ((grid.Bounds.Width * column) / grid.Columns);
        var right = grid.Bounds.X + ((grid.Bounds.Width * (column + 1)) / grid.Columns);
        var top = grid.Bounds.Y + ((grid.Bounds.Height * row) / grid.Rows);
        var bottom = grid.Bounds.Y + ((grid.Bounds.Height * (row + 1)) / grid.Rows);
        var horizontalMargin = Math.Max(1, (right - left) / 8);
        var verticalMargin = Math.Max(1, (bottom - top) / 8);
        for (var y = top + verticalMargin; y < bottom - verticalMargin; y++)
        {
            for (var x = left + horizontalMargin; x < right - horizontalMargin; x++)
            {
                pixels[(y * stride) + x] = value;
            }
        }
    }
}
