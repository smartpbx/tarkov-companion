using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
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
        Assert.True(result.IsPartial);
        Assert.Empty(result.UnresolvedCells);
        Assert.Contains(result.AmbiguousCells, issue => issue.Column == 3);
    }

    [Fact]
    public void GridDetectorFindsRenderedRegularGrid()
    {
        const int width = 800;
        const int height = 600;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)24);
        var image = CreateImage(pixels, width, height);
        var expected = new ContainerGridSpec(new PixelRect(200, 120, 400, 300), 4, 3);
        PaintGridLines(pixels, width, expected, 220);

        var detected = new ContainerGridDetector().Detect(image);

        Assert.NotNull(detected);
        Assert.Equal(expected.Columns, detected.Columns);
        Assert.Equal(expected.Rows, detected.Rows);
        Assert.InRange(Math.Abs(expected.Bounds.X - detected.Bounds.X), 0, 2);
        Assert.InRange(Math.Abs(expected.Bounds.Y - detected.Bounds.Y), 0, 2);
    }

    [Fact]
    public async Task ContainerServiceDetectsGridParsesQuantityAndFlagsUnresolvedOccupiedCell()
    {
        const int width = 800;
        const int height = 600;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)24);
        var grid = new ContainerGridSpec(new PixelRect(200, 120, 400, 300), 4, 3);
        PaintCell(pixels, width, grid, 0, 0, 100);
        PaintCell(pixels, width, grid, 1, 2, 120);
        PaintCell(pixels, width, grid, 2, 3, 130);
        PaintGridLines(pixels, width, grid, 220);
        var image = CreateImage(pixels, width, height) with { Source = "fixture://container-service" };
        var engine = new FixtureOcrEngine(
        [
            new(
                image.Source,
                [
                    new("Wires x2", new(215, 145, 70, 22), new Confidence(0.95)),
                    new("Graphics Card", new(415, 245, 85, 22), new Confidence(0.94)),
                ])
        ]);
        await using var cache = new CanonicalItemResolverCache(
            new InMemoryRecognitionCatalogRepository(
            [
                new("wires", "Wires"),
                new("graphics-card", "Graphics Card"),
            ]));
        var service = new ContainerRecognitionService(
            engine,
            cache,
            new ValuationItemRepository(image.CapturedUtc));

        var result = await service.RecognizeAsync(image, CancellationToken.None);

        Assert.Contains(result.Items, item => item.CanonicalId == "wires" && item.Quantity == 2);
        Assert.Contains(result.Items, item => item.CanonicalId == "graphics-card");
        Assert.Contains(result.UnresolvedCells, cell => cell is { Row: 2, Column: 3 });
        Assert.True(result.IsPartial);
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

    private static void PaintGridLines(byte[] pixels, int stride, ContainerGridSpec grid, byte value)
    {
        for (var column = 0; column <= grid.Columns; column++)
        {
            var x = grid.Bounds.X + ((grid.Bounds.Width * column) / grid.Columns);
            for (var y = grid.Bounds.Y; y <= grid.Bounds.Y + grid.Bounds.Height; y++)
            {
                pixels[(y * stride) + x] = value;
            }
        }

        for (var row = 0; row <= grid.Rows; row++)
        {
            var y = grid.Bounds.Y + ((grid.Bounds.Height * row) / grid.Rows);
            for (var x = grid.Bounds.X; x <= grid.Bounds.X + grid.Bounds.Width; x++)
            {
                pixels[(y * stride) + x] = value;
            }
        }
    }

    private sealed class ValuationItemRepository : IItemRepository
    {
        private readonly DataProvenance _provenance;

        public ValuationItemRepository(DateTimeOffset observedUtc)
        {
            _provenance = new("fixture", observedUtc);
        }

        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken)
        {
            var value = itemId == "graphics-card" ? 200_000L : 12_000L;
            return Task.FromResult<ItemPriceSnapshot?>(new(
                value,
                [],
                value,
                value,
                value,
                _provenance));
        }
    }
}
