using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.RecognitionTests.Grid;

/// <summary>
/// Package 37: a 64-bit hash shortlists and the pixels decide, through the real on-disk cache.
/// </summary>
public sealed class IconPixelMatchingTests : IDisposable
{
    private const int Pitch = 63;
    private const int OriginX = 1260;
    private const int OriginY = 180;
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "icon-pixel-matching-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void TheSamePictureCorrelatesFullyAndADarkerCopyStillDoes()
    {
        var icon = Icon(seed: 1);
        var same = IconPixelDescriptor.Create(icon, 1, 1)!;
        var darker = IconPixelDescriptor.Create(Scale(icon, 0.8), 1, 1)!;
        var other = IconPixelDescriptor.Create(Icon(seed: 2), 1, 1)!;

        Assert.Equal(1, same.Correlate(same), 3);
        Assert.True(same.Correlate(darker) > 0.99);
        Assert.True(same.Correlate(other) < 0.5);
    }

    [Fact]
    public void MaskingTheGamesWritingIgnoresWhatIsDrawnInTheCaptionAndBadgeBands()
    {
        // The variant the real-screenshot study measures. It is not what the builder uses: on
        // real pixels it lifts a dark item's true score from 0.4 to 0.9 and starts naming wrong
        // items, because a caption is sometimes all that tells two icons apart.
        var icon = Icon(seed: 1);
        var written = icon.Pixels.ToArray();
        for (var y = 2; y < 12; y++)
        {
            for (var x = 20; x < 62; x++)
            {
                written[(((y * icon.Width) + x) * 4) + 1] = 255;
                written[((((icon.Height - 1 - y) * icon.Width) + x) * 4) + 1] = 255;
            }
        }

        var overwritten = new CapturedImage(written, icon.Width, icon.Height, icon.Stride, icon.Format, Now, "written");

        var whole = IconPixelDescriptor.Create(icon, 1, 1)!.Correlate(IconPixelDescriptor.Create(overwritten, 1, 1)!);
        var masked = IconPixelDescriptor.Create(icon, 1, 1, maskGameWriting: true)!
            .Correlate(IconPixelDescriptor.Create(overwritten, 1, 1, maskGameWriting: true)!);

        Assert.True(whole < 0.9, $"whole-icon correlation {whole:0.000}");
        Assert.Equal(1, masked, 3);
    }

    [Fact]
    public void AFlatPictureHasNoDescriptorRatherThanMatchingEverything()
    {
        var flat = new CapturedImage(Enumerable.Repeat((byte)40, 64 * 64 * 4).ToArray(), 64, 64, 256, PixelFormat.Bgra8888, Now, "flat");

        Assert.Null(IconPixelDescriptor.Create(flat, 1, 1));
    }

    [Fact]
    public async Task NamesTheItemWhosePixelsMatchAndScoresTheEvidence()
    {
        var frame = Frame(Icon(seed: 1));
        var cache = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(_directory));
        await StoreAsync(cache, "item-true", Icon(seed: 1));
        await StoreAsync(cache, "item-other", Icon(seed: 2));
        var builder = new GridPixelReconstructionBuilder(cache, Items("item-true", "item-other"), new NoOcr());

        var request = await builder.BuildAsync(frame, InventoryGridSurface.VisibleLoot, Now);

        var item = Assert.Single(request.OccupiedCells).Item;
        Assert.Equal("item-true", item.Value!.CanonicalId.Value);
        Assert.Equal(EvidenceConfidenceKind.ProviderScore, item.Provenance.Confidence.Kind);
        Assert.True(item.Provenance.Confidence.Score >= GridPixelReconstructionOptions.DefaultMinimumPixelCorrelation);
        // A barter item is one item with no condition, and the catalog is enough to say so.
        Assert.Equal(1, item.Value.Quantity.Value);
        Assert.Equal(ItemConditionReading.NotApplicable, item.Value.Condition.Value);
        Assert.Null(item.Value.FoundInRaid.Value);
    }

    [Fact]
    public async Task RefusesToChooseBetweenTwoItemsDrawnWithTheSameArt()
    {
        var frame = Frame(Icon(seed: 1));
        var cache = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(_directory));
        await StoreAsync(cache, "key-a", Icon(seed: 1));
        await StoreAsync(cache, "key-b", Icon(seed: 1));
        var builder = new GridPixelReconstructionBuilder(cache, Items("key-a", "key-b"), new NoOcr());

        var request = await builder.BuildAsync(frame, InventoryGridSurface.VisibleLoot, Now);

        var item = Assert.Single(request.OccupiedCells).Item;
        Assert.Null(item.Value);
        Assert.Equal(["key-a", "key-b"], item.Candidates.Select(candidate => candidate.CandidateId).Order().ToArray());
    }

    [Fact]
    public async Task RefusesAnItemNothingInTheIndexLooksLike()
    {
        var frame = Frame(Icon(seed: 9));
        var cache = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(_directory));
        await StoreAsync(cache, "item-a", Icon(seed: 1));
        await StoreAsync(cache, "item-b", Icon(seed: 2));
        var builder = new GridPixelReconstructionBuilder(cache, Items("item-a", "item-b"), new NoOcr());

        var request = await builder.BuildAsync(frame, InventoryGridSurface.VisibleLoot, Now);

        Assert.Null(Assert.Single(request.OccupiedCells).Item.Value);
    }

    /// <summary>A 64x64 cell picture: a bordered dark tile with a seeded blocky pattern on it.</summary>
    private static CapturedImage Icon(int seed)
    {
        const int size = Pitch + 1;
        var pixels = new byte[size * size * 4];
        var random = new Random(seed);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                Set(pixels, size, x, y, 30, 32, 34);
            }
        }

        for (var block = 0; block < 14; block++)
        {
            var left = random.Next(6, 46);
            var top = random.Next(6, 46);
            var (red, green, blue) = ((byte)random.Next(60, 255), (byte)random.Next(60, 255), (byte)random.Next(60, 255));
            for (var y = top; y < top + 12; y++)
            {
                for (var x = left; x < left + 12; x++)
                {
                    Set(pixels, size, x, y, red, green, blue);
                }
            }
        }

        for (var edge = 0; edge < size; edge++)
        {
            Set(pixels, size, edge, 0, 73, 81, 84);
            Set(pixels, size, edge, size - 1, 73, 81, 84);
            Set(pixels, size, 0, edge, 73, 81, 84);
            Set(pixels, size, size - 1, edge, 73, 81, 84);
        }

        return new(pixels, size, size, size * 4, PixelFormat.Bgra8888, Now, "test-icon");
    }

    /// <summary>A 1080p frame holding a 4x3 container with the icon in its second row.</summary>
    private static CapturedImage Frame(CapturedImage icon)
    {
        var frame = SyntheticDarkGrid(1920, 1080, columns: 4, rows: 3);
        var destination = frame.Pixels.ToArray();
        var source = icon.Pixels.Span;
        for (var y = 0; y < icon.Height; y++)
        {
            source.Slice(y * icon.Stride, icon.Width * 4)
                .CopyTo(destination.AsSpan(((OriginY + Pitch + y) * frame.Stride) + ((OriginX + Pitch) * 4), icon.Width * 4));
        }

        return new(destination, frame.Width, frame.Height, frame.Stride, frame.Format, Now, "test-frame");
    }

    private static CapturedImage SyntheticDarkGrid(int width, int height, int columns, int rows)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Set(pixels, width, x, y, 14, 14, 14);
            }
        }

        for (var y = OriginY; y <= OriginY + (rows * Pitch); y++)
        {
            for (var x = OriginX; x <= OriginX + (columns * Pitch); x++)
            {
                var onLine = (x - OriginX) % Pitch == 0 || (y - OriginY) % Pitch == 0;
                Set(pixels, width, x, y, onLine ? (byte)73 : (byte)24, onLine ? (byte)81 : (byte)25, onLine ? (byte)84 : (byte)25);
            }
        }

        return new(pixels, width, height, width * 4, PixelFormat.Bgra8888, Now, "dark-grid");
    }

    private static CapturedImage Scale(CapturedImage image, double factor)
    {
        var pixels = image.Pixels.ToArray();
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                pixels[offset + channel] = (byte)(pixels[offset + channel] * factor);
            }
        }

        return new(pixels, image.Width, image.Height, image.Stride, image.Format, Now, "scaled");
    }

    private static void Set(byte[] pixels, int width, int x, int y, byte red, byte green, byte blue)
    {
        var offset = ((y * width) + x) * 4;
        pixels[offset] = blue;
        pixels[offset + 1] = green;
        pixels[offset + 2] = red;
        pixels[offset + 3] = 255;
    }

    private static async Task StoreAsync(FileIconEvidenceCache cache, string itemId, CapturedImage icon)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(icon.Width, icon.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(icon.Pixels.ToArray(), 0, bitmap.GetPixels(), icon.Pixels.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        await cache.StoreAsync(
            new IconContentWriteRequest(
                new IconEvidenceKey(itemId, new Uri($"https://example.invalid/icons/{itemId}.png")),
                Now,
                new EvidenceProvenance(
                    EvidenceSourceClass.PublicStructuredData,
                    "fixture://icons",
                    Now,
                    EvidenceConfidence.Certain,
                    new ProducerIdentity("fixture", "1")),
                data.ToArray()),
            CancellationToken.None);
    }

    private static IItemRepository Items(params string[] ids) => new FixedItems(ids);

    private sealed class FixedItems(string[] ids) : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(ids.Contains(itemId)
                ? new ItemDefinition(
                    itemId,
                    "Name of " + itemId,
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
                    new DataProvenance("fixture", Now))
                : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    private sealed class NoOcr : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "none", IsAvailable: false, DiagnosticCode: "not_exercised"));
    }
}
