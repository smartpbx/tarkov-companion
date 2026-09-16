using System.Buffers;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests.Ocr;

/// <summary>
/// The whole-frame pixel walks refuse a frame over the pixel ceiling before reading a pixel of it,
/// as the providers already did.
/// </summary>
/// <remarks>
/// The frames here are one row of pixels over the ceiling and are never allocated: their buffer
/// reports its length without existing, and any read of it fails the test.
/// </remarks>
public sealed class PixelCeilingTests
{
    // 10,000 x 4,001 is 40,010,000 pixels, just over the 40,000,000 ceiling.
    private const int Width = 10_000;
    private const int OverHeight = 4_001;

    [Fact]
    public async Task AContainerScanOverThePixelCeilingIsRefusedBeforeGridDetectionReadsAPixel()
    {
        var pixels = new UnreadablePixels(Width * OverHeight);
        var engine = new CountingEngine();
        await using var cache = new CanonicalItemResolverCache(
            new InMemoryRecognitionCatalogRepository([new CanonicalItemReference("wires", "Wires")]));

        var result = await new ContainerRecognitionService(engine, cache, new NoPriceItemRepository())
            .RecognizeAsync(Image(pixels, OverHeight), CancellationToken.None);

        Assert.True(result.IsPartial);
        Assert.Equal("ocr_input_limit_exceeded", result.DiagnosticCode);
        Assert.Empty(result.Items);
        Assert.Equal(0, pixels.Reads);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public void GridDetectionSegmentationAndStashDiscoveryRefuseTheFrameThemselves()
    {
        var pixels = new UnreadablePixels(Width * OverHeight);
        var image = Image(pixels, OverHeight);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerGridDetector().Detect(image));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContainerGridSegmenter().Segment(
            image,
            new ContainerGridSpec(new PixelRect(200, 120, 400, 300), 4, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            StashGrid.Cells(image, new PixelRect(0, 0, Width, OverHeight)));

        Assert.Equal(0, pixels.Reads);
    }

    [Fact]
    public void AFrameExactlyAtTheCeilingIsNotOverIt()
    {
        var atCeiling = Image(new UnreadablePixels(Width * 4_000), 4_000);
        var over = Image(new UnreadablePixels(Width * OverHeight), OverHeight);

        Assert.False(CapturedImagePixels.ExceedsPixelCeiling(atCeiling));
        Assert.True(CapturedImagePixels.ExceedsPixelCeiling(over));
    }

    private static CapturedImage Image(UnreadablePixels pixels, int height) => new(
        pixels.Memory,
        Width,
        height,
        Width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        "fixture://pixel-ceiling");

    /// <summary>A buffer with a length and no pixels: taking its span counts as a read and fails.</summary>
    private sealed class UnreadablePixels(int length) : MemoryManager<byte>
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public override Memory<byte> Memory => CreateMemory(length);

        public override Span<byte> GetSpan()
        {
            Interlocked.Increment(ref _reads);
            throw new InvalidOperationException("A pixel of a frame over the ceiling was read.");
        }

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

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
