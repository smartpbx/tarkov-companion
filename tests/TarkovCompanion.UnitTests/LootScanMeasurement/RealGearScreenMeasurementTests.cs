using System.Globalization;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>
/// The in-raid Gear screen, read from real screenshots: which grids the layout reader finds and
/// calls what, and what the recognizer names in the loot and the backpack, scored cell by cell
/// against hand labels.
/// </summary>
/// <remarks>
/// The screenshots are a player's own and carry a raid id, so they live outside every checkout
/// (<c>TARKOV_GEAR_SCREENSHOTS</c>, by default <c>/root/orca/incoming/loot-2026-09-20</c>), and
/// so do their labels (<c>&lt;screenshot&gt;.gear-labels.json</c> beside each). Reports only,
/// never asserts, and skips without the screenshots or the icon corpus.
/// </remarks>
public sealed class RealGearScreenMeasurementTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReportsWhatTheReaderMakesOfRealGearScreens()
    {
        var directory = Environment.GetEnvironmentVariable("TARKOV_GEAR_SCREENSHOTS") ?? "/root/orca/incoming/loot-2026-09-20";
        if (!Directory.Exists(directory) || IconCorpus.TryLoad() is not { } corpus)
        {
            output.WriteLine("[gear-screens] skipped: no screenshots or no icon corpus.");
            return;
        }

        var cache = await LootScanMeasurementIndex.OpenAsync(corpus, Now);
        var items = corpus.CreateRepository(Now);
        var index = await new IconReferenceIndex(cache, items).GetAsync(CancellationToken.None);
        var builder = new GridPixelReconstructionBuilder(cache, items, new NoOcr());
        var reader = new GearScreenLayoutReader();
        var loader = new SkiaScreenshotImageLoader();
        var report = new StringBuilder();
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.png").Order(StringComparer.Ordinal))
        {
            var image = await loader.LoadAsync(path, CancellationToken.None);
            if (image is null)
            {
                continue;
            }

            report.AppendLine(CultureInfo.InvariantCulture, $"=== {Path.GetFileName(path)[..16]} {image.Width}x{image.Height}");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var layout = reader.Read(image);
            report.AppendLine(CultureInfo.InvariantCulture, $"layout read in {watch.ElapsedMilliseconds} ms");
            foreach (var grid in layout?.Grids ?? [])
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  {grid.Section,-13} {grid.Columns}x{grid.Rows} frame at {grid.Frame.X},{grid.Frame.Y} size {grid.Frame.Width}x{grid.Frame.Height}");
            }

            var labels = await ReadLabelsAsync(path + ".gear-labels.json");
            var loot = await builder.BuildAsync(image, InventoryGridSurface.VisibleLoot, Now);
            await ReportGridAsync("loot", loot, labels, image, index, report, totals);
            var carried = await builder.BuildCarriedAsync(image, Now);
            await ReportGridAsync("backpack", carried, labels, image, index, report, totals);
        }

        report.AppendLine("=== totals over labelled cells");
        foreach (var (key, value) in totals.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {key}: {value}");
        }

        var reports = Environment.GetEnvironmentVariable("TARKOV_GEAR_REPORTS");
        if (!string.IsNullOrWhiteSpace(reports))
        {
            Directory.CreateDirectory(reports);
            await File.WriteAllTextAsync(Path.Combine(reports, "gear-report.txt"), report.ToString());
        }

        output.WriteLine(report.ToString());
    }

    private static async Task ReportGridAsync(
        string section,
        GridReconstructionRequest? request,
        IReadOnlyList<Label> labels,
        CapturedImage image,
        IconReferenceIndex.Snapshot index,
        StringBuilder report,
        Dictionary<string, int> totals)
    {
        if (request?.Lattice is not { } lattice)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"[{section}] no lattice");
            foreach (var label in labels.Where(label => label.Section == section))
            {
                Count(totals, $"{section}.missed-no-lattice");
            }

            return;
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"[{section}] lattice {lattice.Columns}x{lattice.Rows} at {lattice.Bounds.X},{lattice.Bounds.Y} cell {lattice.CellWidthPixels}; footprints {request.OccupiedCells.Count}");
        foreach (var cell in request.OccupiedCells)
        {
            var bounds = cell.Item.Bounds!;
            var width = (int)(bounds.Width / lattice.CellWidthPixels);
            var height = (int)(bounds.Height / lattice.CellHeightPixels);
            var verdict = cell.Item.Value is { } item
                ? $"NAMED {item.DisplayName.Value} ({item.CanonicalId.Value}) rotated={item.Rotated.Value?.ToString() ?? "?"}"
                : cell.Item.Candidates.Count > 0
                    ? "refused (" + string.Join(", ", cell.Item.Candidates.Take(3).Select(candidate => candidate.Value.DisplayName.Value + (candidate.Value.Rotated.Value == true ? " turned" : string.Empty))) + ")"
                    : "none";
            var label = labels.FirstOrDefault(label => label.Section == section && label.Row == cell.Anchor.Row && label.Column == cell.Anchor.Column);
            var score = label is null ? "unlabelled" : Score(label, width, height, cell.Item.Value?.CanonicalId.Value);
            Count(totals, $"{section}.{score}");
            var top = await TopAsync(image, bounds.X, bounds.Y, bounds.Width + 1, bounds.Height + 1, width, height, index);
            report.AppendLine(CultureInfo.InvariantCulture, $"  r{cell.Anchor.Row} c{cell.Anchor.Column} {width}x{height} {verdict} [{score}] | {top}");
        }

        foreach (var label in labels.Where(label => label.Section == section &&
                     !request.OccupiedCells.Any(cell => cell.Anchor.Row == label.Row && cell.Anchor.Column == label.Column)))
        {
            Count(totals, $"{section}.missed");
            var top = await TopAsync(
                image,
                lattice.Bounds.X + (label.Column * lattice.CellWidthPixels),
                lattice.Bounds.Y + (label.Row * lattice.CellHeightPixels),
                (label.Width * lattice.CellWidthPixels) + 1,
                (label.Height * lattice.CellHeightPixels) + 1,
                label.Width,
                label.Height,
                index);
            report.AppendLine(CultureInfo.InvariantCulture, $"  MISSED r{label.Row} c{label.Column} {label.Width}x{label.Height} {label.Note} | at the label: {top}");
        }
    }

    private static string Score(Label label, int width, int height, string? named)
    {
        if (width != label.Width || height != label.Height)
        {
            return "footprint-wrong";
        }

        return named is null ? "refused" : label.Ids.Contains(named, StringComparer.Ordinal) ? "named-right" : "named-WRONG";
    }

    private static void Count(Dictionary<string, int> totals, string key) =>
        totals[key] = totals.GetValueOrDefault(key) + 1;

    /// <summary>The three best correlations over every reference of the shape, either way round.</summary>
    private static async Task<string> TopAsync(
        CapturedImage image,
        int x,
        int y,
        int width,
        int height,
        int widthCells,
        int heightCells,
        IconReferenceIndex.Snapshot index)
    {
        if (x < 0 || y < 0 || x + width > image.Width || y + height > image.Height)
        {
            return "off frame";
        }

        var buffer = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            image.Pixels.Span.Slice(((y + row) * image.Stride) + (x * 4), width * 4).CopyTo(buffer.AsSpan(row * width * 4, width * 4));
        }

        var crop = new CapturedImage(buffer, width, height, width * 4, image.Format, Now, "gear-crop");
        if (IconPixelDescriptor.Create(crop, widthCells, heightCells) is not { } described)
        {
            return "no descriptor";
        }

        var scored = new List<(string Name, double Score)>();
        foreach (var reference in index.OfShape(widthCells, heightCells))
        {
            if (await index.DescribeAsync(reference, CancellationToken.None) is { } descriptor)
            {
                scored.Add((reference.Definition.ShortName, described.Correlate(descriptor)));
            }
        }

        return string.Join(", ", scored.OrderByDescending(entry => entry.Score).Take(3)
            .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Name} {entry.Score:0.000}")));
    }

    private static async Task<IReadOnlyList<Label>> ReadLabelsAsync(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        return
        [
            .. document.RootElement.GetProperty("cells").EnumerateArray().Select(cell => new Label(
                cell.GetProperty("section").GetString()!,
                cell.GetProperty("row").GetInt32(),
                cell.GetProperty("column").GetInt32(),
                cell.GetProperty("width").GetInt32(),
                cell.GetProperty("height").GetInt32(),
                [.. cell.GetProperty("ids").EnumerateArray().Select(id => id.GetString()!)],
                cell.TryGetProperty("note", out var note) ? note.GetString() ?? string.Empty : string.Empty)),
        ];
    }

    private sealed record Label(string Section, int Row, int Column, int Width, int Height, string[] Ids, string Note);

    private sealed class NoOcr : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "real-gear-screens", IsAvailable: false, DiagnosticCode: "ocr_not_installed"));
    }
}
