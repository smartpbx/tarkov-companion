using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests.Ocr;

/// <summary>
/// A provider's partial read keeps its diagnostic from the provider to the candidates, to the
/// recogniser's public result, and to the container cells it failed to read, instead of being
/// dropped wherever lines survived it.
/// </summary>
public sealed class PartialOcrDiagnosticsTests
{
    private static readonly OcrLine Inspect = new("INSPECT", new(100, 80, 150, 24), null);
    private static readonly OcrLine Weight = new("WEIGHT", new(100, 500, 150, 24), null);
    private static readonly OcrLine GraphicsCard = new("Graphics Card", new(300, 280, 250, 30), null);

    [Fact]
    public async Task APartialContextualPassKeepsItsCodeOnTheCandidatesItContributedTo()
    {
        var engine = new SequencedEngine(
            new OcrResult([Inspect, Weight, GraphicsCard], TimeSpan.FromMilliseconds(5), "fixture"),
            new OcrResult([GraphicsCard], TimeSpan.FromMilliseconds(5), "fixture", true, "ocr_line_limit_exceeded"));

        var result = await new OcrCoordinator(engine, new ScanContextDetector())
            .RecognizeAsync(Frame(800, 600), CancellationToken.None);

        Assert.Equal(ScanContext.SingleItem, result.Detection.Context);
        Assert.True(result.Candidates.IsAvailable);
        Assert.Contains(result.Candidates.Lines, line => line.Text == "Graphics Card");
        Assert.Equal("ocr_line_limit_exceeded", result.Candidates.DiagnosticCode);
        Assert.True(result.IsPartial);
        Assert.Equal("ocr_line_limit_exceeded", result.DiagnosticCode);
    }

    [Fact]
    public async Task APartialFullFramePassKeepsItsCodeWhenTheContextualPassIsComplete()
    {
        var engine = new SequencedEngine(
            new OcrResult([Inspect, Weight, GraphicsCard], TimeSpan.FromMilliseconds(5), "fixture", true, "ocr_frame_timeout_partial"),
            new OcrResult([GraphicsCard], TimeSpan.FromMilliseconds(5), "fixture"));

        var result = await new OcrCoordinator(engine, new ScanContextDetector())
            .RecognizeAsync(Frame(800, 600), CancellationToken.None);

        Assert.Equal("ocr_frame_timeout_partial", result.Candidates.DiagnosticCode);
        Assert.True(result.IsPartial);
        Assert.Equal("ocr_frame_timeout_partial", result.DiagnosticCode);
    }

    [Fact]
    public async Task TheRecognisersPublicResultKeepsThePartialCodeOnAnAutoSelectedItem()
    {
        var engine = new SequencedEngine(
            new OcrResult([Inspect, Weight, GraphicsCard], TimeSpan.FromMilliseconds(5), "fixture"),
            new OcrResult([GraphicsCard], TimeSpan.FromMilliseconds(5), "fixture", true, "ocr_partial_tiles"));
        await using var cache = GraphicsCardCatalog();

        var recognition = await new RecognitionService(new OcrCoordinator(engine, new ScanContextDetector()), cache)
            .RecognizeAsync(Frame(800, 600), CancellationToken.None);

        Assert.Equal(ScanContext.SingleItem, recognition.Context);
        Assert.Equal("item-1", recognition.Selected?.CanonicalId);
        Assert.Equal("ocr_partial_tiles", recognition.DiagnosticCode);
        Assert.Contains("partial=True", recognition.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownContextFromAPartialReadReportsThePartialReadRatherThanTheAnchors()
    {
        var engine = new SequencedEngine(
            new OcrResult([GraphicsCard], TimeSpan.FromMilliseconds(5), "fixture", true, "ocr_memory_exhausted"));
        await using var cache = GraphicsCardCatalog();

        var recognition = await new RecognitionService(new OcrCoordinator(engine, new ScanContextDetector()), cache)
            .RecognizeAsync(Frame(800, 600), CancellationToken.None);

        Assert.Equal(ScanContext.Unknown, recognition.Context);
        Assert.Equal("ocr_memory_exhausted", recognition.DiagnosticCode);
        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task APartialGridPassNamesTheContainerScanAndEachUnreadCellSaysWhy()
    {
        var image = ContainerFrame();
        var engine = new ContainerEngine(
            new OcrResult(
                [new OcrLine("Wires x2", new(215, 145, 70, 22), new Confidence(0.95))],
                TimeSpan.Zero,
                "container-fixture",
                true,
                "ocr_line_limit_exceeded"),
            _ => new OcrResult([], TimeSpan.Zero, "container-fixture", false, "ocr_frame_timeout"));
        await using var cache = ContainerCatalog();

        var result = await new ContainerRecognitionService(engine, cache, new NoPriceItemRepository())
            .RecognizeAsync(image, CancellationToken.None);

        Assert.True(result.IsPartial);
        Assert.Equal("ocr_line_limit_exceeded", result.DiagnosticCode);
        Assert.Contains(result.Items, item => item.CanonicalId == "wires");
        Assert.NotEmpty(result.UnresolvedCells);
        Assert.All(result.UnresolvedCells, cell =>
            Assert.Equal("occupied_without_candidate; ocr=ocr_frame_timeout", cell.Reason));
        Assert.Equal(result.UnresolvedCells.Count + result.AmbiguousCells.Count, engine.CellCalls);
    }

    [Fact]
    public async Task ADegradedCellReadNamesTheScanWhenTheGridPassWasComplete()
    {
        var image = ContainerFrame();
        var engine = new ContainerEngine(
            new OcrResult(
                [new OcrLine("Wires x2", new(215, 145, 70, 22), new Confidence(0.95))],
                TimeSpan.Zero,
                "container-fixture"),
            call => call == 0
                ? new OcrResult([], TimeSpan.Zero, "container-fixture", true, "ocr_partial_tiles")
                : new OcrResult([], TimeSpan.Zero, "container-fixture", true, "ocr_no_text"));
        await using var cache = ContainerCatalog();

        var result = await new ContainerRecognitionService(engine, cache, new NoPriceItemRepository())
            .RecognizeAsync(image, CancellationToken.None);

        Assert.True(result.IsPartial);
        Assert.Equal("ocr_partial_tiles", result.DiagnosticCode);
        var issues = result.UnresolvedCells.Concat(result.AmbiguousCells)
            .OrderBy(cell => cell.Row)
            .ThenBy(cell => cell.Column)
            .ToArray();
        Assert.True(issues.Length >= 2, $"expected at least two unread cells, found {issues.Length}");
        Assert.EndsWith("; ocr=ocr_partial_tiles", issues[0].Reason, StringComparison.Ordinal);
        Assert.All(issues.Skip(1), cell => Assert.DoesNotContain("ocr=", cell.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACellReadThatRanOutOfMemoryStopsTheRemainingCellReads()
    {
        var image = ContainerFrame();
        var engine = new ContainerEngine(
            new OcrResult([], TimeSpan.Zero, "container-fixture", true, "ocr_no_text"),
            _ => new OcrResult([], TimeSpan.Zero, "container-fixture", false, "ocr_memory_exhausted"));
        await using var cache = ContainerCatalog();

        var result = await new ContainerRecognitionService(engine, cache, new NoPriceItemRepository())
            .RecognizeAsync(image, CancellationToken.None);

        Assert.Equal(1, engine.CellCalls);
        Assert.True(result.IsPartial);
        Assert.Equal("ocr_memory_exhausted", result.DiagnosticCode);
        Assert.True(result.UnresolvedCells.Count >= 2);
        Assert.All(result.UnresolvedCells, cell => Assert.EndsWith("; ocr=ocr_memory_exhausted", cell.Reason, StringComparison.Ordinal));
    }

    private static CanonicalItemResolverCache GraphicsCardCatalog() => new(
        new InMemoryRecognitionCatalogRepository([new CanonicalItemReference("item-1", "Graphics Card")]));

    private static CanonicalItemResolverCache ContainerCatalog() => new(
        new InMemoryRecognitionCatalogRepository(
        [
            new CanonicalItemReference("wires", "Wires"),
            new CanonicalItemReference("graphics-card", "Graphics Card"),
        ]));

    private static CapturedImage Frame(int width, int height) => new(
        new byte[checked(width * height)],
        width,
        height,
        width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        "fixture://partial-ocr");

    /// <summary>A 4x3 grid with three occupied cells, (0,0), (1,2) and (2,3).</summary>
    private static CapturedImage ContainerFrame()
    {
        const int width = 800;
        const int height = 600;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)24);
        var grid = new ContainerGridSpec(new PixelRect(200, 120, 400, 300), 4, 3);
        PaintCell(pixels, width, grid, 0, 0, 100);
        PaintCell(pixels, width, grid, 1, 2, 120);
        PaintCell(pixels, width, grid, 2, 3, 130);
        for (var column = 0; column <= grid.Columns; column++)
        {
            var x = grid.Bounds.X + ((grid.Bounds.Width * column) / grid.Columns);
            for (var y = grid.Bounds.Y; y <= grid.Bounds.Y + grid.Bounds.Height; y++)
            {
                pixels[(y * width) + x] = 220;
            }
        }

        for (var row = 0; row <= grid.Rows; row++)
        {
            var y = grid.Bounds.Y + ((grid.Bounds.Height * row) / grid.Rows);
            for (var x = grid.Bounds.X; x <= grid.Bounds.X + grid.Bounds.Width; x++)
            {
                pixels[(y * width) + x] = 220;
            }
        }

        return new CapturedImage(
            pixels,
            width,
            height,
            width,
            PixelFormat.Gray8,
            new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
            "fixture://partial-container");
    }

    private static void PaintCell(byte[] pixels, int stride, ContainerGridSpec grid, int row, int column, byte value)
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

    private sealed class SequencedEngine(params OcrResult[] results) : IOcrEngine
    {
        public int Calls { get; private set; }

        public Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results[Math.Min(Calls++, results.Length - 1)]);
        }
    }

    /// <summary>Answers the whole-grid pass with one result and each cell pass by its call order.</summary>
    private sealed class ContainerEngine(OcrResult grid, Func<int, OcrResult> cell) : IOcrEngine
    {
        public int CellCalls { get; private set; }

        public Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var region = Assert.IsType<PixelRect>(request.Region);
            return Task.FromResult(region.Width > 150 ? grid : cell(CellCalls++));
        }
    }

    private sealed class NoPriceItemRepository : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }
}
