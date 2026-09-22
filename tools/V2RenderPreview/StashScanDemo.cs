using System.Security.Cryptography;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.StashScan;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.StashScanFixtures;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// Package 40's render fixture: painted stash screenshots put through the real pixel reader and
/// the composed guided scan, so what the render shows is what the workspace does - the headline,
/// the next step, the folded grid, the unknown tiles - and not a hand-built view state.
/// </summary>
/// <remarks>
/// The frames are the same painted stash the measurement tests score (linked from the test
/// project, so there is one painter). Item names are the painter's own; the fingerprints are the
/// tiles as painted, because the shipped separator names a tile only on a bit-exact match and
/// nothing else would show a named tile at all.
/// </remarks>
internal static class StashScanDemo
{
    private static readonly DateTimeOffset ObservedUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    public static async Task AddScreensAsync(GuidedStashScanService scan, IReadOnlyList<int> firstRows, bool nameItems)
    {
        var layout = SyntheticStashLayout.Build(rows: 34);
        var options = new SyntheticStashFrameOptions();
        var catalog = SyntheticStashLayout.Catalog;
        var builder = new GridPixelReconstructionBuilder(
            new FixedIconCache(nameItems
                ? catalog.Select(item => Evidence(item.ItemId, InGameFingerprint(item, options))).ToArray()
                : []),
            new FixedItems(catalog.ToDictionary(item => item.ItemId, Definition, StringComparer.Ordinal)),
            new NoOcr());
        var reconstructor = new InventoryGridReconstructor();
        foreach (var firstRow in firstRows)
        {
            var image = SyntheticStashPainter.RenderFrame(layout, firstRow, options);
            var request = await builder.BuildAsync(image, InventoryGridSurface.Stash, ObservedUtc, cancellationToken: CancellationToken.None);
            await scan.AddScreenshotAsync(
                $"render-row-{firstRow:D2}",
                CaptureCorrelationId.New(),
                new CaptureContextMetadata(null, null, null, null, null, null, "desktop"),
                Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span)),
                ObservedUtc,
                0,
                reconstructor.Reconstruct(request, CancellationToken.None),
                CancellationToken.None);
        }
    }

    public static async Task AddRealBurstAsync(
        GuidedStashScanService scan,
        IScreenshotImageLoader loader,
        GridPixelReconstructionBuilder builder,
        InventoryGridReconstructor reconstructor,
        string folder)
    {
        var paths = Directory.EnumerateFiles(folder, "*.png")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(7)
            .ToArray();
        if (paths.Length != 7)
        {
            throw new InvalidOperationException("The real stash burst requires seven PNG frames.");
        }

        for (var index = 0; index < paths.Length; index++)
        {
            var image = await loader.LoadAsync(paths[index], CancellationToken.None)
                ?? throw new InvalidOperationException($"The real stash frame {index} could not be decoded.");
            var request = await builder.BuildAsync(
                image,
                InventoryGridSurface.Stash,
                image.CapturedUtc,
                cancellationToken: CancellationToken.None);
            await scan.AddScreenshotAsync(
                $"real-stash-{index:D2}",
                CaptureCorrelationId.New(),
                new CaptureContextMetadata(null, null, null, null, null, null, "desktop"),
                Convert.ToHexStringLower(SHA256.HashData(image.Pixels.Span)),
                image.CapturedUtc,
                0,
                reconstructor.Reconstruct(request, CancellationToken.None),
                CancellationToken.None);
        }
    }

    private static ulong InGameFingerprint(SyntheticStashItem item, SyntheticStashFrameOptions options)
    {
        var image = SyntheticStashPainter.RenderFrame(SyntheticStashLayout.Of(8, new SyntheticStashPlacement(item, 1, 1)), 0, options);
        var x = options.PanelX + options.Pitch;
        var y = options.PanelY + options.Pitch;
        var width = item.Width * options.Pitch;
        var height = item.Height * options.Pitch;
        var buffer = new byte[width * 4 * height];
        for (var row = 0; row < height; row++)
        {
            image.Pixels.Span.Slice(((y + row) * image.Stride) + (x * 4), width * 4).CopyTo(buffer.AsSpan(row * width * 4));
        }

        return SkiaPerceptualIconMatcher.ComputeDifferenceHash(
            new CapturedImage(buffer, width, height, width * 4, image.Format, image.CapturedUtc, "render-crop"));
    }

    private static IconContentEvidence Evidence(string canonicalId, ulong fingerprint) => new(
        new IconEvidenceKey(canonicalId, new Uri($"https://example.invalid/icons/{canonicalId}.png")),
        ObservedUtc,
        new string('a', 64),
        new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            "render-icon-source",
            ObservedUtc,
            EvidenceConfidence.Certain,
            new ProducerIdentity("render", "1")),
        new IconPixelDimensions(64, 64),
        new IconFingerprintEvidence(
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8,
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8Version,
            IconFingerprintAlgorithms.DifferenceHashLuminance9X8Bits,
            fingerprint));

    private static ItemDefinition Definition(SyntheticStashItem item) => new(
        item.ItemId,
        item.Name,
        item.Name,
        string.Empty,
        ItemCategory.Barter,
        new ItemDimensions(item.Width, item.Height),
        true,
        null,
        null,
        null,
        null,
        null,
        new HashSet<string>(),
        new DataProvenance("render", ObservedUtc));

    private sealed class FixedIconCache(IReadOnlyList<IconContentEvidence> evidence) : IIconEvidenceCache
    {
        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken) =>
            Task.FromResult<IconContentEvidenceAsset?>(null);

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult(evidence);
    }

    private sealed class FixedItems(IReadOnlyDictionary<string, ItemDefinition> byId) : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(byId.TryGetValue(itemId, out var item) ? item : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    private sealed class NoOcr : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "render-ocr", IsAvailable: false, DiagnosticCode: "render_ocr_unavailable"));
    }
}
