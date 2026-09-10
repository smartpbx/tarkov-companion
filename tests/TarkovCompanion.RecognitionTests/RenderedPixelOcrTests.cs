using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

public sealed class RenderedPixelOcrTests
{
    [WindowsX64OcrFact]
    public async Task Rendered1080pItemPixelsReachCanonicalResult()
    {
        using var provider = RequireProvider();
        var image = Render(
            1920,
            1080,
            0.01,
            ("INSPECT", 520, 150, 46),
            ("Graphics Card", 520, 280, 58),
            ("WEIGHT", 520, 820, 40));
        await using var cache = ResolverCache(
        [
            new("graphics-card", "Graphics Card", ["GPU"]),
            new("power-cord", "Power Cord"),
        ]);
        var service = new RecognitionService(
            new OcrCoordinator(provider, new ScanContextDetector()),
            cache);

        var result = await service.RecognizeAsync(image, CancellationToken.None);

        Assert.Equal(ScanContext.SingleItem, result.Context);
        Assert.Equal("graphics-card", result.Selected?.CanonicalId);
    }

    [WindowsX64OcrFact]
    public async Task RenderedSimilarNamePixelsRemainAmbiguous()
    {
        using var provider = RequireProvider();
        var image = Render(
            1920,
            1080,
            0.02,
            ("INSPECT", 500, 140, 46),
            ("ZB-101", 500, 290, 64),
            ("WEIGHT", 500, 820, 40));
        await using var cache = ResolverCache(
        [
            new("zb-1011", "ZB-1011"),
            new("zb-1012", "ZB-1012"),
        ]);
        var service = new RecognitionService(
            new OcrCoordinator(provider, new ScanContextDetector()),
            cache);

        var result = await service.RecognizeAsync(image, CancellationToken.None);

        Assert.Equal(ScanContext.SingleItem, result.Context);
        Assert.Null(result.Selected);
        Assert.True(result.Candidates.Count >= 2);
    }

    [WindowsX64OcrFact]
    public async Task Rendered1440pExtractPixelsPreserveClosedStatus()
    {
        using var provider = RequireProvider();
        var image = Render(
            2560,
            1440,
            0.03,
            ("EXTRACTS", 1540, 150, 52),
            ("Road to Customs ACTIVE", 1540, 320, 46),
            ("Dorms V-EX CLOSED", 1540, 440, 46));
        var provenance = new DataProvenance("rendered-pixel-fixture", image.CapturedUtc);
        var map = new MapDefinition(
            "customs",
            "Customs",
            null,
            null,
            [],
            [
                new("road", "customs", "Road to Customs", null, null, provenance),
                new("dorms", "customs", "Dorms V-EX", null, null, provenance),
            ],
            null,
            provenance);

        var result = await new ExtractRecognitionService(provider)
            .RecognizeAsync(image, map, CancellationToken.None);

        Assert.Contains(result.Extracts, extract => extract.ExtractId == "road");
        Assert.DoesNotContain(result.Extracts, extract => extract.ExtractId == "dorms");
        Assert.Contains(result.Observations, extract =>
            extract.ExtractId == "dorms" && extract.Status == ExtractStatus.Closed);
    }

    [WindowsX64OcrFact]
    public async Task Rendered1440pMixedContainerPixelsFlagUnresolvedCellAndParseQuantities()
    {
        using var provider = RequireProvider();
        var grid = new ContainerGridSpec(new(500, 300, 1200, 600), 4, 2);
        var image = RenderContainer(
            2560,
            1440,
            grid,
            [
                new(0, 0, "Wires x2"),
                new(1, 2, "Graphics Card"),
                new(0, 3, null),
            ]);
        var items = new RenderedItemRepository(image.CapturedUtc);
        await using var cache = ResolverCache(
        [
            new("wires", "Wires"),
            new("graphics-card", "Graphics Card", ["GPU"]),
        ]);
        var service = new ContainerRecognitionService(provider, cache, items);

        var result = await service.RecognizeAsync(image, CancellationToken.None);

        Assert.Contains(result.Items, item => item.CanonicalId == "wires" && item.Quantity == 2);
        Assert.Contains(result.Items, item => item.CanonicalId == "graphics-card");
        Assert.NotEmpty(result.UnresolvedCells);
        Assert.True(result.IsPartial);
    }

    [WindowsX64OcrFact]
    public async Task Rendered4kFleaPixelsParseOnlyVisibleRows()
    {
        using var provider = RequireProvider();
        var image = Render(
            3840,
            2160,
            0.04,
            ("FLEA MARKET", 900, 260, 72),
            ("75500 RUB x2", 2100, 720, 64),
            ("189999 RUB", 2100, 900, 64));

        var result = await new FleaRecognitionService(provider)
            .RecognizeAsync(image, CancellationToken.None);

        Assert.True(result.ProviderAvailable);
        Assert.Contains(result.Listings, listing => listing.PriceRoubles == 75_500 && listing.Quantity == 2);
        Assert.Contains(result.Listings, listing => listing.PriceRoubles == 189_999);
    }

    private static TesseractOcrEngine RequireProvider()
    {
        var provider = new TesseractOcrEngine();
        if (!provider.Availability.IsAvailable)
        {
            var reason = provider.Availability.Reason ?? "no provider reason";
            provider.Dispose();
            throw new InvalidOperationException(
                "The packaged Windows x64 OCR provider failed its availability check: " + reason);
        }

        return provider;
    }

    private static CanonicalItemResolverCache ResolverCache(IReadOnlyList<CanonicalItemReference> items) =>
        new(new InMemoryRecognitionCatalogRepository(items));

    private static CapturedImage Render(
        int width,
        int height,
        double noise,
        params (string Text, float X, float BaselineY, float Size)[] lines)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(18, 20, 22));
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        foreach (var line in lines)
        {
            using var font = new SKFont(SKTypeface.Default, line.Size);
            canvas.DrawText(line.Text, line.X, line.BaselineY, SKTextAlign.Left, font, paint);
        }

        return ToCapturedImage(bitmap, noise, "rendered://text");
    }

    private static CapturedImage RenderContainer(
        int width,
        int height,
        ContainerGridSpec grid,
        IReadOnlyList<RenderedCell> cells)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(18, 20, 22));
        using var fill = new SKPaint { Color = new SKColor(76, 82, 88), Style = SKPaintStyle.Fill };
        foreach (var cell in cells)
        {
            var bounds = CellBounds(grid, cell.Row, cell.Column);
            canvas.DrawRect(bounds.Left + 3, bounds.Top + 3, bounds.Width - 6, bounds.Height - 6, fill);
        }

        using var gridPaint = new SKPaint { Color = new SKColor(210, 214, 218), StrokeWidth = 2 };
        for (var column = 0; column <= grid.Columns; column++)
        {
            var x = grid.Bounds.X + ((grid.Bounds.Width * column) / grid.Columns);
            canvas.DrawLine(x, grid.Bounds.Y, x, grid.Bounds.Y + grid.Bounds.Height, gridPaint);
        }

        for (var row = 0; row <= grid.Rows; row++)
        {
            var y = grid.Bounds.Y + ((grid.Bounds.Height * row) / grid.Rows);
            canvas.DrawLine(grid.Bounds.X, y, grid.Bounds.X + grid.Bounds.Width, y, gridPaint);
        }

        using var textPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 38);
        foreach (var cell in cells.Where(cell => cell.Text is not null))
        {
            var bounds = CellBounds(grid, cell.Row, cell.Column);
            canvas.DrawText(
                cell.Text!,
                bounds.Left + 18,
                bounds.Top + (bounds.Height / 2f),
                SKTextAlign.Left,
                font,
                textPaint);
        }

        return ToCapturedImage(bitmap, 0.02, "rendered://mixed-container");
    }

    private static SKRectI CellBounds(ContainerGridSpec grid, int row, int column)
    {
        var left = grid.Bounds.X + ((grid.Bounds.Width * column) / grid.Columns);
        var right = grid.Bounds.X + ((grid.Bounds.Width * (column + 1)) / grid.Columns);
        var top = grid.Bounds.Y + ((grid.Bounds.Height * row) / grid.Rows);
        var bottom = grid.Bounds.Y + ((grid.Bounds.Height * (row + 1)) / grid.Rows);
        return new(left, top, right, bottom);
    }

    private static CapturedImage ToCapturedImage(SKBitmap bitmap, double noise, string source)
    {
        var pixels = new byte[checked(bitmap.RowBytes * bitmap.Height)];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        var interval = Math.Max(400, (int)Math.Round(1 / Math.Max(noise, 0.0001)) * 160);
        for (var offset = interval * 4; offset + 3 < pixels.Length; offset += interval * 4)
        {
            pixels[offset] = 30;
            pixels[offset + 1] = 28;
            pixels[offset + 2] = 26;
            pixels[offset + 3] = 255;
        }

        return new(
            pixels,
            bitmap.Width,
            bitmap.Height,
            bitmap.RowBytes,
            PixelFormat.Bgra8888,
            new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
            source);
    }

    private sealed record RenderedCell(int Row, int Column, string? Text);

    private sealed class RenderedItemRepository : IItemRepository
    {
        private readonly IReadOnlyDictionary<string, ItemDefinition> _items;
        private readonly IReadOnlyDictionary<string, ItemPriceSnapshot> _prices;

        public RenderedItemRepository(DateTimeOffset observedUtc)
        {
            var provenance = new DataProvenance("rendered-pixel-fixture", observedUtc);
            _items = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal)
            {
                ["wires"] = Item("wires", "Wires", provenance),
                ["graphics-card"] = Item("graphics-card", "Graphics Card", provenance),
            };
            _prices = new Dictionary<string, ItemPriceSnapshot>(StringComparer.Ordinal)
            {
                ["wires"] = Price(12_000, provenance),
                ["graphics-card"] = Price(200_000, provenance),
            };
        }

        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(_items.GetValueOrDefault(itemId));

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(
            string query,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(_prices.GetValueOrDefault(itemId));

        private static ItemDefinition Item(string id, string name, DataProvenance provenance) => new(
            id,
            name,
            name,
            string.Empty,
            ItemCategory.Barter,
            new(1, 1),
            true,
            null,
            null,
            null,
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            provenance);

        private static ItemPriceSnapshot Price(long value, DataProvenance provenance) => new(
            value,
            [],
            value,
            value,
            value,
            provenance);
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class WindowsX64OcrFactAttribute : FactAttribute
{
    public WindowsX64OcrFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Skip = "Rendered-pixel OCR requires the packaged Windows x64 provider; this host is not Windows x64.";
        }
    }
}
