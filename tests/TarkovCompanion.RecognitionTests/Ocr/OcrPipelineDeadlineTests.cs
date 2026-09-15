using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests.Ocr;

/// <summary>
/// One deadline per frame across every OCR pass, and the empty outcome carried through
/// coordination rather than flattened into "complete" or "unavailable".
/// </summary>
public sealed class OcrPipelineDeadlineTests
{
    [Fact]
    public async Task SlowPassesThatEachFitTheirOwnTimeoutStillShareOneFrameDeadline()
    {
        // Each pass takes 500 ms. Given its own 800 ms budget each would finish; sharing one
        // 800 ms deadline, the contextual pass cannot.
        var engine = new SlowPassEngine(
            TimeSpan.FromMilliseconds(500),
            new OcrResult(
            [
                new OcrLine("INSPECT", new(400, 80, 150, 24), null),
                new OcrLine("WEIGHT", new(400, 700, 150, 24), null),
                new OcrLine("Graphics Card", new(600, 280, 250, 30), null),
            ], TimeSpan.FromMilliseconds(500), "fixture"),
            new OcrResult(
                [new OcrLine("Graphics Card", new(600, 280, 250, 30), null)],
                TimeSpan.FromMilliseconds(500),
                "fixture"));
        var coordinator = new OcrCoordinator(
            engine,
            new ScanContextDetector(),
            pipelineOptions: new OcrPipelineOptions { Timeout = TimeSpan.FromMilliseconds(800) });

        var result = await coordinator.RecognizeAsync(Frame(1920, 1080), CancellationToken.None);

        Assert.Equal(2, engine.Tokens.Count);
        Assert.Equal(engine.Tokens[0], engine.Tokens[1]);
        Assert.Equal(ScanContext.SingleItem, result.Detection.Context);
        Assert.True(result.FullFrame.IsAvailable);
        Assert.False(result.Contextual.IsAvailable);
        Assert.Equal("ocr_pipeline_timeout", result.Contextual.DiagnosticCode);
        Assert.Equal("ocr_pipeline_timeout", result.DiagnosticCode);
        Assert.True(result.IsPartial);
        Assert.False(result.IsEmpty);
        Assert.True(result.Candidates.IsAvailable);
        Assert.Contains(result.Candidates.Lines, line => line.Text == "Graphics Card");
    }

    [Fact]
    public async Task CallerCancellationDuringASlowPassStillThrows()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var engine = new SlowPassEngine(
            Timeout.InfiniteTimeSpan,
            new OcrResult([], TimeSpan.Zero, "fixture"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OcrCoordinator(engine, new ScanContextDetector())
                .RecognizeAsync(Frame(320, 180), cancellation.Token));
    }

    [Fact]
    public async Task AvailableEmptyReadStaysDistinctThroughCoordinationAndRecognition()
    {
        var empty = new OcrResult([], TimeSpan.FromMilliseconds(2), "fixture", true, "ocr_no_text");
        var coordinated = await new OcrCoordinator(new SequencedEngine(empty), new ScanContextDetector())
            .RecognizeAsync(Frame(1920, 1080), CancellationToken.None);
        await using var cache = new CanonicalItemResolverCache(
            new InMemoryRecognitionCatalogRepository([new CanonicalItemReference("item", "Item")]));

        var recognition = await new RecognitionService(
                new OcrCoordinator(new SequencedEngine(empty), new ScanContextDetector()),
                cache)
            .RecognizeAsync(Frame(1920, 1080), CancellationToken.None);

        Assert.True(coordinated.IsEmpty);
        Assert.False(coordinated.IsPartial);
        Assert.True(coordinated.FullFrame.IsAvailable);
        Assert.Equal("ocr_no_text", coordinated.DiagnosticCode);
        Assert.Equal(ScanContext.Unknown, recognition.Context);
        Assert.Equal("ocr_no_text", recognition.DiagnosticCode);
        Assert.Contains("empty=True", recognition.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimedOutFrameIsReportedAsATimeoutNotAsAMissingProvider()
    {
        var timedOut = new OcrResult([], TimeSpan.FromSeconds(15), "fixture", false, "ocr_frame_timeout");
        await using var cache = new CanonicalItemResolverCache(
            new InMemoryRecognitionCatalogRepository([new CanonicalItemReference("item", "Item")]));

        var recognition = await new RecognitionService(
                new OcrCoordinator(new SequencedEngine(timedOut), new ScanContextDetector()),
                cache)
            .RecognizeAsync(Frame(1920, 1080), CancellationToken.None);

        Assert.Equal("ocr_frame_timeout", recognition.DiagnosticCode);
    }

    [Fact]
    public async Task MemoryExhaustedFramePassIsNotFollowedByAContextualPass()
    {
        var engine = new SequencedEngine(
            new OcrResult(
            [
                new OcrLine("INSPECT", new(400, 80, 150, 24), null),
                new OcrLine("WEIGHT", new(400, 700, 150, 24), null),
            ], TimeSpan.FromMilliseconds(5), "fixture", true, "ocr_memory_exhausted"));

        var result = await new OcrCoordinator(engine, new ScanContextDetector())
            .RecognizeAsync(Frame(1920, 1080), CancellationToken.None);

        Assert.Equal(1, engine.Calls);
        Assert.True(result.IsPartial);
        Assert.Equal("ocr_memory_exhausted", result.DiagnosticCode);
    }

    [Fact]
    public async Task ContainerGridPassAndCellFallbacksShareOneDeadlineAndKeepReadEvidence()
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
        var image = new CapturedImage(
            pixels,
            width,
            height,
            width,
            PixelFormat.Gray8,
            new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
            "fixture://container-deadline");
        var engine = new SlowCellEngine([new OcrLine("Wires x2", new(215, 145, 70, 22), new Confidence(0.95))]);
        await using var cache = new CanonicalItemResolverCache(
            new InMemoryRecognitionCatalogRepository(
            [
                new("wires", "Wires"),
                new("graphics-card", "Graphics Card"),
            ]));
        var service = new ContainerRecognitionService(
            engine,
            cache,
            new NoPriceItemRepository(),
            pipelineOptions: new OcrPipelineOptions { Timeout = TimeSpan.FromMilliseconds(300) });

        var result = await service.RecognizeAsync(image, CancellationToken.None);

        Assert.True(engine.WholeGridAttempted);
        Assert.Equal(1, engine.CellAttempts);
        Assert.True(result.IsPartial);
        Assert.Equal("ocr_pipeline_timeout", result.DiagnosticCode);
        Assert.NotEmpty(result.UnresolvedCells);
    }

    private static CapturedImage Frame(int width, int height) => new(
        new byte[checked(width * height)],
        width,
        height,
        width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        "fixture://pipeline-deadline");

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

    private sealed class SlowPassEngine(TimeSpan delay, params OcrResult[] results) : IOcrEngine
    {
        public List<CancellationToken> Tokens { get; } = [];

        public async Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            var call = Tokens.Count;
            Tokens.Add(cancellationToken);
            await Task.Delay(delay, cancellationToken);
            return results[Math.Min(call, results.Length - 1)];
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

    /// <summary>Answers the whole-grid pass at once and never finishes a single-cell pass.</summary>
    private sealed class SlowCellEngine(IReadOnlyList<OcrLine> gridLines) : IOcrEngine
    {
        public bool WholeGridAttempted { get; private set; }

        public int CellAttempts { get; private set; }

        public async Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            var region = Assert.IsType<PixelRect>(request.Region);
            if (region.Width > 150)
            {
                WholeGridAttempted = true;
                return new OcrResult(gridLines, TimeSpan.Zero, "container-deadline-fixture");
            }

            CellAttempts++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new OcrResult([], TimeSpan.Zero, "container-deadline-fixture");
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
