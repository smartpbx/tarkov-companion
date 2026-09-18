using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.RecognitionTests.Grid;

/// <summary>Package 37: the feeder the icon cache never had.</summary>
public sealed class IconEvidenceIndexerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "icon-indexer-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task StoresWhatTheCatalogNamesOnceAndTheReferenceIndexSeesIt()
    {
        var cache = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(_directory));
        var index = new IconReferenceIndex(cache, new AnyItem());
        var fetcher = new CountingFetcher(_ => Png(200));
        await using var indexer = new IconEvidenceIndexer(cache, new FixedCatalog(3), fetcher, index);

        Assert.Empty((await index.GetAsync(CancellationToken.None)).References);
        var first = await indexer.RefreshAsync(CancellationToken.None);
        var second = await indexer.RefreshAsync(CancellationToken.None);

        Assert.Equal(new IconIndexReport(3, 0, 3, 0, false), first);
        Assert.Equal(new IconIndexReport(3, 3, 0, 0, false), second);
        Assert.Equal(3, fetcher.Calls);
        Assert.Equal(3, (await index.GetAsync(CancellationToken.None)).References.Count);
    }

    [Fact]
    public async Task StopsAskingWhenTheAssetHostKeepsFailing()
    {
        var cache = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(_directory));
        var fetcher = new CountingFetcher(_ => null);
        await using var indexer = new IconEvidenceIndexer(cache, new FixedCatalog(400), fetcher, new IconReferenceIndex(cache, new AnyItem()));

        var report = await indexer.RefreshAsync(CancellationToken.None);

        Assert.True(report.StoppedEarly);
        Assert.Equal(0, report.Stored);
        Assert.True(fetcher.Calls < 100, $"kept asking: {fetcher.Calls} requests");
    }

    [Fact]
    public async Task CanBeDisposedTwiceBecauseTheContainerHoldsItUnderTwoServiceTypes()
    {
        var cache = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(_directory));
        var indexer = new IconEvidenceIndexer(cache, new FixedCatalog(1), new CountingFetcher(_ => null), new IconReferenceIndex(cache, new AnyItem()));

        await indexer.DisposeAsync();
        await indexer.DisposeAsync();
        indexer.Invalidate();
    }

    [Theory]
    [InlineData("https://assets.tarkov.dev/a-grid-image.webp", true)]
    [InlineData("http://assets.tarkov.dev/a-grid-image.webp", false)]
    [InlineData("https://assets.tarkov.dev.example.org/a.webp", false)]
    [InlineData("https://example.org/a.webp", false)]
    public void FetchesOnlyFromTheCatalogsOwnAssetHost(string uri, bool allowed) =>
        Assert.Equal(allowed, HttpIconContentFetcher.IsAllowed(new Uri(uri)));

    private static byte[] Png(byte shade)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        var pixels = new byte[64 * 64 * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = (byte)(offset % 251 < 120 ? shade : 20);
            pixels[offset + 3] = 255;
        }

        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class FixedCatalog(int count) : IIconCatalog
    {
        public Task<IReadOnlyList<IconCatalogEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IconCatalogEntry>>(Enumerable.Range(0, count)
                .Select(index => new IconCatalogEntry($"item-{index}", new Uri($"https://assets.tarkov.dev/item-{index}-grid-image.webp")))
                .ToArray());
    }

    private sealed class CountingFetcher(Func<Uri, byte[]?> respond) : IIconContentFetcher
    {
        private int _calls;

        public int Calls => _calls;

        public Task<byte[]?> FetchAsync(Uri imageUri, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(respond(imageUri));
        }
    }

    private sealed class AnyItem : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(new(
                itemId,
                itemId,
                itemId,
                string.Empty,
                ItemCategory.Barter,
                new ItemDimensions(1, 1),
                true,
                null,
                null,
                null,
                null,
                null,
                new HashSet<string>(),
                new DataProvenance("fixture", DateTimeOffset.UnixEpoch)));

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }
}
