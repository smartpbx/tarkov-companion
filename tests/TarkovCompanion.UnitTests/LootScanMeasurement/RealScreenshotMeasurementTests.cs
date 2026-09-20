using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>
/// The recognizer over real screenshots: which grid it picks, what it thinks the footprints are,
/// and what it names, written out as pictures a person can check against the game's own captions.
/// </summary>
/// <remarks>
/// Real screenshots are a player's own and live outside every checkout
/// (<c>TARKOV_REAL_SCREENSHOTS</c>, by default <c>/root/orca/recognition-corpus/real-2026-09-18</c>).
/// The report and overlays go beside them, never into the repository. This reports and never
/// asserts, and skips without the screenshots or the icon corpus.
/// </remarks>
public sealed class RealScreenshotMeasurementTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private const int ShortlistSize = 32;

    [Fact]
    public async Task ReportsWhatTheRecognizerMakesOfRealScreenshots()
    {
        var directory = Environment.GetEnvironmentVariable("TARKOV_REAL_SCREENSHOTS") ?? "/root/orca/recognition-corpus/real-2026-09-18";
        if (!Directory.Exists(directory) || IconCorpus.TryLoad() is not { } corpus)
        {
            output.WriteLine("[real-screenshots] skipped: no screenshots or no icon corpus.");
            return;
        }

        var reports = Environment.GetEnvironmentVariable("TARKOV_REAL_SCREENSHOT_REPORTS") ?? Path.Combine(Path.GetDirectoryName(directory.TrimEnd('/'))!, "real-reports");
        Directory.CreateDirectory(reports);
        var cache = await LootScanMeasurementIndex.OpenAsync(corpus, Now);
        var items = corpus.CreateRepository(Now);
        var index = await new IconReferenceIndex(cache, items).GetAsync(CancellationToken.None);
        var builder = new GridPixelReconstructionBuilder(cache, items, new NoOcr());
        var loader = new SkiaScreenshotImageLoader();
        var report = new StringBuilder();

        var frame = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.png").Order(StringComparer.Ordinal))
        {
            var image = await loader.LoadAsync(path, CancellationToken.None);
            if (image is null)
            {
                continue;
            }

            report.AppendLine(CultureInfo.InvariantCulture, $"=== frame {frame}: {Path.GetFileName(path)} {image.Width}x{image.Height}");
            report.AppendLine(CultureInfo.InvariantCulture, $"generic detector: {Describe(new ContainerGridDetector().Detect(image))}");
            report.AppendLine(CultureInfo.InvariantCulture, $"stash detector:   {Describe(new StashPanelLatticeDetector().Detect(image))}");
            if (new ContainerGridDetector().Detect(image) is { } found)
            {
                var rowLines = ContainerGridDetector.DescribeLines(image, vertical: false, found.Bounds.X, found.Bounds.X + found.Bounds.Width + 1)
                    .Where(line => line.Strength >= 200)
                    .Select(line => $"{line.Position}({line.Position % 63}):{line.Strength}");
                report.AppendLine(CultureInfo.InvariantCulture, $"row lines between the chosen columns, position(phase):pixels seen, 200+: {string.Join(" ", rowLines)}");
            }
            foreach (var surface in new[] { InventoryGridSurface.VisibleLoot, InventoryGridSurface.Stash })
            {
                var request = await builder.BuildAsync(image, surface, Now);
                if (request.Lattice is not { } lattice)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"[{surface}] no lattice");
                    continue;
                }

                var named = request.OccupiedCells.Count(cell => cell.Item.Value is not null);
                var lookalikes = request.OccupiedCells.Count(cell => cell.Item.Value is null && cell.Item.Candidates.Count > 0);
                report.AppendLine(CultureInfo.InvariantCulture, $"[{surface}] lattice {lattice.Rows}x{lattice.Columns} at {lattice.Bounds.X},{lattice.Bounds.Y} cell {lattice.CellWidthPixels}x{lattice.CellHeightPixels}; footprints {request.OccupiedCells.Count}: named {named}, lookalikes {lookalikes}, nothing close {request.OccupiedCells.Count - named - lookalikes}");
                var number = 0;
                var rows = new List<(int Number, EvidenceBounds Bounds, string Label)>();
                foreach (var cell in request.OccupiedCells)
                {
                    var bounds = cell.Item.Bounds!;
                    var widthCells = Math.Max(1, (int)Math.Round(bounds.Width / (double)lattice.CellWidthPixels));
                    var heightCells = Math.Max(1, (int)Math.Round(bounds.Height / (double)lattice.CellHeightPixels));
                    var top = await TopMatchesAsync(image, bounds.X, bounds.Y, bounds.Width + 1, bounds.Height + 1, widthCells, heightCells, index);
                    var verdict = cell.Item.Value is { } item ? "NAMED " + item.DisplayName.Value : cell.Item.Candidates.Count > 0 ? "refused" : "none";
                    report.AppendLine(CultureInfo.InvariantCulture, $"  #{number,3} r{cell.Anchor.Row} c{cell.Anchor.Column} {widthCells}x{heightCells} {verdict} | {top}");
                    rows.Add((number, new(bounds.X, bounds.Y, bounds.Width, bounds.Height), verdict));
                    number++;
                }

                SaveOverlay(image, lattice, rows, Path.Combine(reports, $"frame{frame}-{surface}.png"));
            }

            frame++;
        }

        await File.WriteAllTextAsync(Path.Combine(reports, "report.txt"), report.ToString());
        output.WriteLine(report.ToString());
    }

    /// <summary>
    /// Writes chosen cells four times life size beside the reference art of named catalog items,
    /// for the cases a caption cannot settle (two armbands both captioned "Prayer").
    /// TARKOV_REAL_CROPS is "frame:x:y:widthPixels:heightPixels:itemId+itemId;..." in frame pixels.
    /// </summary>
    [Fact]
    public async Task WritesZoomedCropsBesideReferenceArtWhenAskedTo()
    {
        var wanted = Environment.GetEnvironmentVariable("TARKOV_REAL_CROPS");
        var directory = Environment.GetEnvironmentVariable("TARKOV_REAL_SCREENSHOTS") ?? "/root/orca/recognition-corpus/real-2026-09-18";
        if (string.IsNullOrWhiteSpace(wanted) || !Directory.Exists(directory) || IconCorpus.TryLoad() is not { } corpus)
        {
            return;
        }

        var files = Directory.EnumerateFiles(directory, "*.png").Order(StringComparer.Ordinal).ToArray();
        var loader = new SkiaScreenshotImageLoader();
        var number = 0;
        foreach (var spec in wanted.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = spec.Split(':');
            var image = await loader.LoadAsync(files[int.Parse(parts[0], CultureInfo.InvariantCulture)], CancellationToken.None);
            var (x, y, width, height) = (int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[3], CultureInfo.InvariantCulture), int.Parse(parts[4], CultureInfo.InvariantCulture));
            var ids = parts.Length > 5 ? parts[5].Split('+') : [];
            using var full = new SKBitmap(new SKImageInfo(image!.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(image.Pixels.ToArray(), 0, full.GetPixels(), image.Pixels.Length);
            const int zoom = 4;
            using var sheet = new SKBitmap(new SKImageInfo((width * zoom * (ids.Length + 1)) + (8 * ids.Length), height * zoom, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(sheet))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(full, new SKRect(x, y, x + width, y + height), new SKRect(0, 0, width * zoom, height * zoom));
                for (var index = 0; index < ids.Length; index++)
                {
                    using var reference = SKBitmap.Decode(corpus.ReadIcon(ids[index]));
                    var left = ((index + 1) * width * zoom) + (8 * (index + 1));
                    canvas.DrawBitmap(reference, new SKRect(0, 0, reference.Width, reference.Height), new SKRect(left, 0, left + (width * zoom), height * zoom));
                }
            }

            var reports = Environment.GetEnvironmentVariable("TARKOV_REAL_SCREENSHOT_REPORTS") ?? "/root/orca/recognition-corpus/real-reports";
            using var data = sheet.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(Path.Combine(reports, $"crop-{number++}.png"), data.ToArray());
        }
    }

    private static string Describe(ContainerGridSpec? spec) => spec is null
        ? "none"
        : string.Create(CultureInfo.InvariantCulture, $"{spec.Rows}x{spec.Columns} at {spec.Bounds.X},{spec.Bounds.Y} size {spec.Bounds.Width}x{spec.Bounds.Height} pitch {spec.Bounds.Width / (double)spec.Columns:0.0}x{spec.Bounds.Height / (double)spec.Rows:0.0}");

    /// <summary>The three best pixel correlations among the nearest hashes of the same shape.</summary>
    private static async Task<string> TopMatchesAsync(
        CapturedImage image,
        int x,
        int y,
        int width,
        int height,
        int widthCells,
        int heightCells,
        IconReferenceIndex.Snapshot index)
    {
        var crop = Crop(image, x, y, width, height);
        if (crop is null || IconPixelDescriptor.Create(crop, widthCells, heightCells) is not { } described)
        {
            return "no descriptor";
        }

        var hash = SkiaPerceptualIconMatcher.ComputeDifferenceHash(crop);
        var scored = new List<(string Name, double Score)>();
        foreach (var reference in index.OfShape(widthCells, heightCells)
                     .OrderBy(reference => BitOperations.PopCount(hash ^ reference.Evidence.Fingerprint.Value))
                     .Take(ShortlistSize))
        {
            if (await index.DescribeAsync(reference, CancellationToken.None) is { } descriptor)
            {
                scored.Add((reference.Definition.ShortName, described.Correlate(descriptor)));
            }
        }

        return scored.Count == 0
            ? $"no references of shape {widthCells}x{heightCells}"
            : string.Join(", ", scored.OrderByDescending(entry => entry.Score).Take(3).Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Name} {entry.Score:0.000}")));
    }

    private static CapturedImage? Crop(CapturedImage image, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x + width > image.Width || y + height > image.Height || width <= 0 || height <= 0)
        {
            return null;
        }

        var buffer = new byte[width * height * 4];
        var source = image.Pixels.Span;
        for (var row = 0; row < height; row++)
        {
            source.Slice(((y + row) * image.Stride) + (x * 4), width * 4).CopyTo(buffer.AsSpan(row * width * 4, width * 4));
        }

        return new(buffer, width, height, width * 4, image.Format, Now, "real-crop");
    }

    private sealed record EvidenceBounds(int X, int Y, int Width, int Height);

    /// <summary>The lattice region at native size with each footprint boxed and numbered.</summary>
    private static void SaveOverlay(
        CapturedImage image,
        DetectedGridLattice lattice,
        IReadOnlyList<(int Number, EvidenceBounds Bounds, string Label)> rows,
        string path)
    {
        const int margin = 12;
        var left = Math.Max(0, lattice.Bounds.X - margin);
        var top = Math.Max(0, lattice.Bounds.Y - margin);
        var width = Math.Min(image.Width - left, lattice.Bounds.Width + (2 * margin));
        var height = Math.Min(image.Height - top, lattice.Bounds.Height + (2 * margin));
        using var full = new SKBitmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Marshal.Copy(image.Pixels.ToArray(), 0, full.GetPixels(), image.Pixels.Length);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawBitmap(full, new SKRect(left, top, left + width, top + height), new SKRect(0, 0, width, height));
            using var named = new SKPaint { Color = new SKColor(0, 255, 120), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            using var refused = new SKPaint { Color = new SKColor(255, 200, 0), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            using var none = new SKPaint { Color = new SKColor(255, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 170) };
            using var font = new SKFont(SKTypeface.Default, 13);
            foreach (var row in rows)
            {
                var rect = new SKRect(row.Bounds.X - left + 2, row.Bounds.Y - top + 2, row.Bounds.X - left + row.Bounds.Width - 1, row.Bounds.Y - top + row.Bounds.Height - 1);
                canvas.DrawRect(rect, row.Label.StartsWith("NAMED", StringComparison.Ordinal) ? named : row.Label == "refused" ? refused : none);
                canvas.DrawRect(new SKRect(rect.Left, rect.Bottom - 15, rect.Left + 26, rect.Bottom), shade);
                canvas.DrawText(row.Number.ToString(CultureInfo.InvariantCulture), rect.Left + 2, rect.Bottom - 3, font, text);
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private sealed class NoOcr : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "real-screenshots", IsAvailable: false, DiagnosticCode: "ocr_not_installed"));
    }
}
