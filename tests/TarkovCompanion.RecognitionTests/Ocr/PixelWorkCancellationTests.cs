using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests.Ocr;

/// <summary>
/// The pixel work before and around OCR stops for a cancelled token or an expired deadline: at
/// once when the token is already cancelled, and within one bounded chunk of reads otherwise.
/// </summary>
public sealed class PixelWorkCancellationTests
{
    // Mirrors the production check interval, so a check that regressed to once per column or
    // once per call reads far past it and fails these bounds.
    private const int CheckInterval = 1 << 16;
    private const int Width = 800;
    private const int Height = 600;

    [Fact]
    public void StashGridDiscoveryHonoursAPreCancelledTokenBeforeReadingAPixel()
    {
        var pixels = new ObservedPixels(new byte[640 * 120]);
        var image = Image(pixels, 640, 120);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            StashGrid.Cells(image, new PixelRect(0, 0, 640, 120), cancellation.Token));

        Assert.Equal(0, pixels.Reads);
    }

    [Fact]
    public void StashGridDiscoveryStopsWithinOneCheckIntervalOfCancellation()
    {
        // 640 columns of 120 rows is 76,800 reads for the first axis alone.
        using var cancellation = new CancellationTokenSource();
        var pixels = new ObservedPixels(new byte[640 * 120], atRead: 10, cancellation.Cancel);
        var image = Image(pixels, 640, 120);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            StashGrid.Cells(image, new PixelRect(0, 0, 640, 120), cancellation.Token));

        Assert.InRange(pixels.Reads, 10, CheckInterval + 10);
    }

    [Fact]
    public async Task ContainerScanWithAPreCancelledTokenReadsNoPixelsAndCallsNoProvider()
    {
        var pixels = new ObservedPixels(GridPixels());
        var image = Image(pixels, Width, Height);
        var engine = new CountingEngine();
        await using var cache = Cache();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ContainerRecognitionService(engine, cache, new NoPriceItemRepository())
                .RecognizeAsync(image, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => new ContainerGridSegmenter().Segment(
            image,
            new ContainerGridSpec(new PixelRect(200, 120, 400, 300), 4, 3),
            cancellation.Token));

        Assert.Equal(0, pixels.Reads);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task CallerCancellationDuringGridDetectionThrowsWithinOneCheckInterval()
    {
        using var cancellation = new CancellationTokenSource();
        var pixels = new ObservedPixels(GridPixels(), atRead: 10, cancellation.Cancel);
        var image = Image(pixels, Width, Height);
        var engine = new CountingEngine();
        await using var cache = Cache();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ContainerRecognitionService(engine, cache, new NoPriceItemRepository())
                .RecognizeAsync(image, cancellation.Token));

        Assert.InRange(pixels.Reads, 10, CheckInterval + 10);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task TheContainerDeadlineCoversGridDetectionAndEndsItAsAPipelineTimeout()
    {
        // Grid detection over this frame is nearly a million reads. The deadline expires while
        // the tenth one is stalled, so only a deadline that had already started can stop it.
        var pixels = new ObservedPixels(GridPixels(), atRead: 10, () => Thread.Sleep(400));
        var image = Image(pixels, Width, Height);
        var engine = new CountingEngine();
        await using var cache = Cache();
        var service = new ContainerRecognitionService(
            engine,
            cache,
            new NoPriceItemRepository(),
            pipelineOptions: new OcrPipelineOptions { Timeout = TimeSpan.FromMilliseconds(100) });

        var result = await service.RecognizeAsync(image, CancellationToken.None);

        Assert.True(result.IsPartial);
        Assert.Equal("ocr_pipeline_timeout", result.DiagnosticCode);
        Assert.InRange(pixels.Reads, 10, CheckInterval + 10);
        Assert.Equal(0, engine.Calls);
    }

    private static byte[] GridPixels()
    {
        var pixels = new byte[Width * Height];
        Array.Fill(pixels, (byte)24);
        var grid = new ContainerGridSpec(new PixelRect(200, 120, 400, 300), 4, 3);
        for (var column = 0; column <= grid.Columns; column++)
        {
            var x = grid.Bounds.X + ((grid.Bounds.Width * column) / grid.Columns);
            for (var y = grid.Bounds.Y; y <= grid.Bounds.Y + grid.Bounds.Height; y++)
            {
                pixels[(y * Width) + x] = 220;
            }
        }

        for (var row = 0; row <= grid.Rows; row++)
        {
            var y = grid.Bounds.Y + ((grid.Bounds.Height * row) / grid.Rows);
            for (var x = grid.Bounds.X; x <= grid.Bounds.X + grid.Bounds.Width; x++)
            {
                pixels[(y * Width) + x] = 220;
            }
        }

        return pixels;
    }

    private static CapturedImage Image(ObservedPixels pixels, int width, int height) => new(
        pixels.Memory,
        width,
        height,
        width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        "fixture://pixel-work-cancellation");

    private static CanonicalItemResolverCache Cache() => new(
        new InMemoryRecognitionCatalogRepository([new CanonicalItemReference("wires", "Wires")]));

    private sealed class CountingEngine : IOcrEngine
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new OcrResult([], TimeSpan.Zero, "counting-fixture", true, "ocr_no_text"));
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
