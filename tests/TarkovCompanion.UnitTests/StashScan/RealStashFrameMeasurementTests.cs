using System.Globalization;
using System.Text;
using SkiaSharp;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.StashScan;

/// <summary>
/// The stash panel finder and footprint reader run over real screenshots of a real stash, which
/// live outside every checkout and are never committed.
/// </summary>
/// <remarks>
/// <para>
/// Everything package 40 measured was painted. Painted frames have one grid on screen, lines of a
/// chosen contrast and nothing behind the panel; the real inventory screen has a rig, pockets,
/// special slots, a backpack and sometimes an open case, all at the stash's own cell pitch, over a
/// lit scene. This reports what the same code does there.
/// </para>
/// <para>
/// Skips, passing, when the folder is absent. Set <c>TARKOV_STASH_REAL_FRAMES</c> to the folder
/// and <c>TARKOV_STASH_REAL_REPORT</c> to a directory outside the repository to get the report and
/// one annotated crop per frame: the lattice in green, each footprint in yellow.
/// </para>
/// </remarks>
public sealed class RealStashFrameMeasurementTests(ITestOutputHelper output)
{
    private const string DefaultFrames = "/root/orca/recognition-corpus/real-2026-09-18";

    [Fact]
    public async Task ReportsThePanelFinderAndFootprintReaderOnRealStashScreenshots()
    {
        var folder = Environment.GetEnvironmentVariable("TARKOV_STASH_REAL_FRAMES") ?? DefaultFrames;
        if (!Directory.Exists(folder))
        {
            output.WriteLine($"[stash-real] skipped: no folder at {folder}.");
            return;
        }

        var reportDirectory = Environment.GetEnvironmentVariable("TARKOV_STASH_REAL_REPORT");
        var paths = Directory.EnumerateFiles(folder, "*.png").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var loader = new SkiaScreenshotImageLoader();
        var detector = new StashPanelLatticeDetector();
        var reader = new StashFootprintReader();
        var report = new StringBuilder();
        for (var index = 0; index < paths.Length; index++)
        {
            var image = await loader.LoadAsync(paths[index], CancellationToken.None);
            if (image is null)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"[stash-real] frame {index}: could not be decoded");
                continue;
            }

            var spec = detector.Detect(image);
            if (spec is null)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"[stash-real] frame {index}: {image.Width}x{image.Height} · no stash panel found");
                continue;
            }

            var footprints = reader.Read(image, spec);
            var bySize = footprints
                .GroupBy(footprint => $"{footprint.Width}x{footprint.Height}")
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Key}:{group.Count()}");
            var cellsCovered = footprints.Sum(footprint => footprint.Width * footprint.Height);
            report.AppendLine(CultureInfo.InvariantCulture, $"[stash-real] frame {index}: lattice {spec.Columns}x{spec.Rows} @{spec.Bounds.Width / spec.Columns}px from ({spec.Bounds.X},{spec.Bounds.Y}) · {footprints.Count} footprints over {cellsCovered}/{spec.Columns * spec.Rows} cells · {string.Join(' ', bySize)}");
            if (reportDirectory is not null)
            {
                Directory.CreateDirectory(reportDirectory);
                SaveAnnotatedCrop(image, spec, footprints, Path.Combine(reportDirectory, $"frame-{index}.png"));
            }
        }

        output.WriteLine(report.ToString());
        Console.WriteLine(report.ToString());
        if (reportDirectory is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "report.txt"), report.ToString());
        }
    }

    /// <summary>
    /// Footprints scored against the hand-read labels beside the screenshots, on the labelled
    /// panel itself so that a change to the panel finder cannot move this number.
    /// </summary>
    /// <remarks>
    /// The labels (<c>&lt;screenshot&gt;.expected.json</c>, schema <c>real-stash-labels-1</c>) are written
    /// by the Loot Scan measurement so the two packages share one truth. A label that is cut by
    /// the viewport or covered by a tooltip is neither a hit nor a miss, and a footprint touching
    /// one is not counted as spurious; a footprint over cells no label claims is.
    /// </remarks>
    [Fact]
    public async Task ReportsFootprintsAgainstTheHandReadLabels()
    {
        var folder = Environment.GetEnvironmentVariable("TARKOV_STASH_REAL_FRAMES") ?? DefaultFrames;
        if (!Directory.Exists(folder))
        {
            output.WriteLine($"[stash-real] skipped: no folder at {folder}.");
            return;
        }

        var loader = new SkiaScreenshotImageLoader();
        var reader = new StashFootprintReader();
        var report = new StringBuilder();
        var totalTruth = 0;
        var totalFound = 0;
        var totalSpurious = 0;
        foreach (var labelPath in Directory.EnumerateFiles(folder, "*.expected.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var imagePath = labelPath[..^".expected.json".Length];
            var image = await loader.LoadAsync(imagePath, CancellationToken.None);
            if (image is null)
            {
                continue;
            }

            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(labelPath));
            var panel = document.RootElement.GetProperty("panel");
            var pitch = panel.GetProperty("pitch").GetInt32();
            var rows = panel.GetProperty("rows").GetInt32();
            var columns = panel.GetProperty("columns").GetInt32();
            var spec = new ContainerGridSpec(
                new PixelRect(panel.GetProperty("originX").GetInt32(), panel.GetProperty("originY").GetInt32(), columns * pitch, rows * pitch),
                columns,
                rows);
            var truth = new Dictionary<StashFootprint, bool>();
            var ignoredCells = new HashSet<(int Row, int Column)>();
            var claimedCells = new HashSet<(int Row, int Column)>();
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                var footprint = new StashFootprint(
                    item.GetProperty("row").GetInt32(),
                    item.GetProperty("column").GetInt32(),
                    item.GetProperty("width").GetInt32(),
                    item.GetProperty("height").GetInt32());
                var whole = item.GetProperty("state").GetString() is "ok" or "unknown";
                for (var row = footprint.Row; row < footprint.Row + footprint.Height; row++)
                {
                    for (var column = footprint.Column; column < footprint.Column + footprint.Width; column++)
                    {
                        (whole ? claimedCells : ignoredCells).Add((row, column));
                    }
                }

                if (whole)
                {
                    truth[footprint] = false;
                }
            }

            var spurious = new List<StashFootprint>();
            foreach (var footprint in reader.Read(image, spec))
            {
                if (truth.ContainsKey(footprint))
                {
                    truth[footprint] = true;
                    continue;
                }

                var touchesIgnored = false;
                for (var row = footprint.Row; row < footprint.Row + footprint.Height && !touchesIgnored; row++)
                {
                    for (var column = footprint.Column; column < footprint.Column + footprint.Width; column++)
                    {
                        touchesIgnored |= ignoredCells.Contains((row, column));
                    }
                }

                if (!touchesIgnored)
                {
                    spurious.Add(footprint);
                }
            }

            var found = truth.Values.Count(value => value);
            var missed = truth.Where(pair => !pair.Value).Select(pair => pair.Key).ToArray();
            totalTruth += truth.Count;
            totalFound += found;
            totalSpurious += spurious.Count;
            var name = Path.GetFileName(imagePath);
            report.AppendLine(CultureInfo.InvariantCulture, $"[stash-real] labels {name[^7..^4]}: footprints found {found}/{truth.Count} · spurious {spurious.Count} · missed {string.Join(' ', missed.Take(8).Select(Describe))} · spurious {string.Join(' ', spurious.Take(8).Select(Describe))}");
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"[stash-real] labels total: footprints found {totalFound}/{totalTruth} ({(totalTruth == 0 ? 0 : 100.0 * totalFound / totalTruth):F1}%) · spurious {totalSpurious}");
        output.WriteLine(report.ToString());
        Console.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("TARKOV_STASH_REAL_REPORT") is { } reportDirectory)
        {
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "labels.txt"), report.ToString());
        }
    }

    /// <summary>
    /// What the border measure reads on every boundary the labels say is a border, and on every
    /// one they say is inside an item: the two distributions a threshold has to sit between.
    /// </summary>
    [Fact]
    public async Task ReportsTheBorderMeasureOnLabelledBordersAndInteriors()
    {
        var folder = Environment.GetEnvironmentVariable("TARKOV_STASH_REAL_FRAMES") ?? DefaultFrames;
        if (!Directory.Exists(folder))
        {
            return;
        }

        var loader = new SkiaScreenshotImageLoader();
        var borders = new List<int>();
        var interiors = new List<int>();
        var samples = new List<(StashBoundaryMeasure Measure, bool IsBorder, string Where)>();
        foreach (var labelPath in Directory.EnumerateFiles(folder, "*.expected.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var imagePath = labelPath[..^".expected.json".Length];
            var image = await loader.LoadAsync(imagePath, CancellationToken.None);
            if (image is null)
            {
                continue;
            }

            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(labelPath));
            var panel = document.RootElement.GetProperty("panel");
            var pitch = panel.GetProperty("pitch").GetInt32();
            var rows = panel.GetProperty("rows").GetInt32();
            var columns = panel.GetProperty("columns").GetInt32();
            var spec = new ContainerGridSpec(
                new PixelRect(panel.GetProperty("originX").GetInt32(), panel.GetProperty("originY").GetInt32(), columns * pitch, rows * pitch),
                columns,
                rows);
            var owner = new int[rows, columns];
            var itemIndex = 0;
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                itemIndex++;
                var whole = item.GetProperty("state").GetString() is "ok" or "unknown";
                for (var row = item.GetProperty("row").GetInt32(); row < item.GetProperty("row").GetInt32() + item.GetProperty("height").GetInt32(); row++)
                {
                    for (var column = item.GetProperty("column").GetInt32(); column < item.GetProperty("column").GetInt32() + item.GetProperty("width").GetInt32(); column++)
                    {
                        if (row < rows && column < columns)
                        {
                            owner[row, column] = whole ? itemIndex : -1;
                        }
                    }
                }
            }

            var plane = StashLuminancePlane.From(image, CancellationToken.None);
            var name = Path.GetFileName(imagePath)[^7..^4];
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    foreach (var vertical in new[] { true, false })
                    {
                        var otherRow = vertical ? row : row + 1;
                        var otherColumn = vertical ? column + 1 : column;
                        if (otherRow >= rows || otherColumn >= columns || owner[row, column] <= 0 || owner[otherRow, otherColumn] <= 0)
                        {
                            continue;
                        }

                        var measure = StashFootprintReader.MeasureBoundary(plane, spec, row, column, pitch, pitch, vertical);
                        var isBorder = owner[row, column] != owner[otherRow, otherColumn];
                        samples.Add((measure, isBorder, $"{name}:r{row}c{column}{(vertical ? "|" : "/")}"));
                        (isBorder ? borders : interiors).Add(measure.Ridge);
                    }
                }
            }
        }

        // Candidate rules, scored on every labelled boundary: a border judged absent merges two
        // items, an interior judged a border splits one.
        (string Name, Func<StashBoundaryMeasure, bool> Rule)[] rules =
        [
            ("ridge>=14 alone", m => m.Ridge >= 14),
            ("shipped", m => m.IsBorder),
        ];
        var lines = new StringBuilder();
        var search = new List<(int Errors, int Missed, int Split, string Name)>();
        foreach (var ridge in new[] { 8, 10, 12, 14, 16, 18 })
        {
            foreach (var step in new[] { 25, 30, 35, 40, 45, 50, 999 })
            {
                bool Rule(StashBoundaryMeasure m) => m.Ridge >= ridge || m.Step >= step;
                var missed = samples.Count(sample => sample.IsBorder && !Rule(sample.Measure));
                var split = samples.Count(sample => !sample.IsBorder && Rule(sample.Measure));
                search.Add((missed + split, missed, split, $"ridge>={ridge} or step>={step}"));
            }
        }

        foreach (var candidate in search.OrderBy(candidate => candidate.Errors).ThenBy(candidate => candidate.Name, StringComparer.Ordinal).Take(8))
        {
            lines.AppendLine(CultureInfo.InvariantCulture, $"[stash-real] search: {candidate.Errors} errors (missed {candidate.Missed}, split {candidate.Split}) · {candidate.Name}");
        }

        foreach (var (ruleName, rule) in rules)
        {
            var merged = samples.Where(sample => sample.IsBorder && !rule(sample.Measure)).ToArray();
            var split = samples.Where(sample => !sample.IsBorder && rule(sample.Measure)).ToArray();
            lines.AppendLine(CultureInfo.InvariantCulture, $"[stash-real] rule {ruleName}: borders missed {merged.Length}/{samples.Count(sample => sample.IsBorder)} · interiors split {split.Length}/{samples.Count(sample => !sample.IsBorder)}");
            if (ruleName == "shipped")
            {
                lines.AppendLine(CultureInfo.InvariantCulture, $"[stash-real]   missed: {string.Join(' ', merged.Take(24).Select(sample => $"{sample.Where}=r{sample.Measure.Ridge}s{sample.Measure.Step}d{sample.Measure.DarkerSide}"))}");
                lines.AppendLine(CultureInfo.InvariantCulture, $"[stash-real]   split: {string.Join(' ', split.Take(24).Select(sample => $"{sample.Where}=r{sample.Measure.Ridge}s{sample.Measure.Step}d{sample.Measure.DarkerSide}"))}");
            }
        }

        borders.Sort();
        interiors.Sort();
        static string Spread(List<int> values) => values.Count == 0
            ? "none"
            : $"n={values.Count} min {values[0]} p5 {values[values.Count / 20]} p25 {values[values.Count / 4]} median {values[values.Count / 2]} p95 {values[values.Count * 19 / 20]} max {values[^1]}";
        var text = $"[stash-real] border measure on true borders: {Spread(borders)}\n" +
                   $"[stash-real] border measure inside items:    {Spread(interiors)}\n" +
                   lines;
        output.WriteLine(text);
        Console.WriteLine(text);
    }

    private static string Describe(StashFootprint footprint) =>
        $"r{footprint.Row}c{footprint.Column}:{footprint.Width}x{footprint.Height}";

    /// <summary>
    /// Stitch placement on pairs of real screens, scored against where the pixels themselves say
    /// the second sits: the vertical shift that best lays one stash viewport over the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That shift is found without the lattice's rows, the footprints or any label, so it can
    /// judge them. It is believed only when it clearly beats every other shift; the viewport
    /// rectangle is read off these frames by eye (3840x1080, stash at x 2232-2850, y 84-940) and
    /// is used for this ground truth alone.
    /// </para>
    /// <para>
    /// The burst was taken without guidance and the player paged the stash: most neighbouring
    /// screens share only the row the viewport cuts, which is no whole row at all. For those
    /// pairs the right answer is to place nothing, and placing something is the error.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ReportsStitchPlacementOnRealPairs()
    {
        var folder = Environment.GetEnvironmentVariable("TARKOV_STASH_REAL_FRAMES") ?? DefaultFrames;
        if (!Directory.Exists(folder))
        {
            output.WriteLine($"[stash-real] skipped: no folder at {folder}.");
            return;
        }

        var paths = Directory.EnumerateFiles(folder, "*.png").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var loader = new SkiaScreenshotImageLoader();
        var detector = new StashPanelLatticeDetector();
        var builder = new GridPixelReconstructionBuilder(
            new StashScanMeasurement.FixedIconEvidenceCache([]),
            new StashScanMeasurement.FixedItemRepository(new Dictionary<string, TarkovCompanion.Core.Domain.Items.ItemDefinition>()),
            new StashScanMeasurement.UnavailableOcrEngine());
        var reconstructor = new InventoryGridReconstructor();
        var report = new StringBuilder();

        var images = new Dictionary<int, CapturedImage>();
        var viewports = new Dictionary<int, byte[,]>();
        var lattices = new Dictionary<int, ContainerGridSpec?>();
        var reconstructions = new Dictionary<int, TarkovCompanion.Core.Domain.Recognition.Grid.GridReconstructionResult>();
        foreach (var index in Enumerable.Range(0, Math.Min(7, paths.Length)))
        {
            var image = await loader.LoadAsync(paths[index], CancellationToken.None) ?? throw new InvalidOperationException($"frame {index} did not decode");
            images[index] = image;
            viewports[index] = Viewport(image);
            lattices[index] = detector.Detect(image);
            var request = await builder.BuildAsync(image, TarkovCompanion.Core.Domain.Recognition.Grid.InventoryGridSurface.Stash, StashScanMeasurement.ObservedUtc, cancellationToken: CancellationToken.None);
            reconstructions[index] = reconstructor.Reconstruct(request, CancellationToken.None);
        }

        // Capture order, the repeat, and the one stretch where he scrolled by less than a screen.
        (int First, int Second)[] pairs = [(0, 1), (1, 2), (2, 3), (3, 4), (3, 6), (6, 4), (4, 5), (6, 5), (5, 6)];
        var overlapping = 0;
        var overlappingPlacedRight = 0;
        var apart = 0;
        var apartLeftUnplaced = 0;
        var placedWrong = 0;
        var assembler = new TarkovCompanion.Application.Services.StashScan.StashScanAssembler();
        foreach (var (first, second) in pairs)
        {
            if (!images.ContainsKey(first) || !images.ContainsKey(second) ||
                lattices[first] is not { } firstLattice || lattices[second] is not { } secondLattice ||
                reconstructions[first].Recognition is null || reconstructions[second].Recognition is null)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"[stash-real] pair ({first})->({second}): a screen carried no stash grid");
                continue;
            }

            var (shift, error, runnerUp) = BestShift(viewports[first], viewports[second], secondLattice.Bounds.Y - firstLattice.Bounds.Y);
            var believed = error < runnerUp * 0.75;
            int? expected = believed ? (int)Math.Round((shift + secondLattice.Bounds.Y - firstLattice.Bounds.Y) / 63.0) : null;
            if (expected is { } rows && (rows < 0 || rows >= firstLattice.Rows))
            {
                expected = null;
            }

            var sessionId = new TarkovCompanion.Core.Abstractions.V2.CaptureSessionId(Guid.NewGuid());
            var frames = new[]
            {
                StashScanMeasurement.Frame(sessionId, 0, images[first], reconstructions[first], confirmsStart: true),
                StashScanMeasurement.Frame(sessionId, 1, images[second], reconstructions[second], confirmsStart: false),
            };
            TarkovCompanion.Application.Services.StashScan.StashScanAssemblyRequest Request(IReadOnlyList<TarkovCompanion.Application.Services.StashScan.StashScanCaptureFrame> captures) => new(
                "real-result",
                "real-snapshot",
                sessionId,
                new(Guid.Parse("77777777-7777-7777-7777-777777777777"), "generation-a", "Pvp"),
                "real-data",
                StashScanMeasurement.ObservedUtc,
                captures);
            var aligned = new TarkovCompanion.Application.Services.StashScan.StashLayoutAligner()
                .AddLayoutOrigins(frames, assembler.Assemble(Request(frames)), StashScanMeasurement.ObservedUtc);
            var placed = assembler.Assemble(Request(aligned)).Recognition.Result.Value!.CapturedRegions
                .Single(region => region.CaptureOrdinal == 1).OriginInContainer.Value?.Row;

            string verdict;
            if (expected is { } truth)
            {
                overlapping++;
                if (placed == truth)
                {
                    overlappingPlacedRight++;
                    verdict = "right";
                }
                else if (placed is null)
                {
                    verdict = "MISSED";
                }
                else
                {
                    placedWrong++;
                    verdict = "WRONG";
                }
            }
            else
            {
                apart++;
                if (placed is null)
                {
                    apartLeftUnplaced++;
                    verdict = "rightly left unplaced";
                }
                else
                {
                    placedWrong++;
                    verdict = "WRONG: placed with nothing to place it by";
                }
            }

            report.AppendLine(CultureInfo.InvariantCulture, $"[stash-real] pair ({first})->({second}): pixels say {(expected is null ? $"no believable overlap (best {error:F1} against {runnerUp:F1})" : $"{expected} rows down (error {error:F1} against {runnerUp:F1})")} · aligner says {(placed is null ? "unplaced" : $"{placed} rows down")} · {verdict}");
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"[stash-real] stitch: overlapping pairs placed at the true row {overlappingPlacedRight}/{overlapping} · pairs with no whole row in common rightly left unplaced {apartLeftUnplaced}/{apart} · placed wrongly {placedWrong}");
        output.WriteLine(report.ToString());
        Console.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("TARKOV_STASH_REAL_REPORT") is { } reportDirectory)
        {
            Directory.CreateDirectory(reportDirectory);
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, "stitch.txt"), report.ToString());
        }
    }

    private const int ViewportX = 2232;
    private const int ViewportY = 84;
    private const int ViewportWidth = 618;
    private const int ViewportHeight = 856;

    private static byte[,] Viewport(CapturedImage image)
    {
        var luminance = new byte[ViewportHeight, ViewportWidth / 3];
        for (var y = 0; y < ViewportHeight; y++)
        {
            for (var x = 0; x < ViewportWidth / 3; x++)
            {
                var offset = ((ViewportY + y) * image.Stride) + ((ViewportX + (x * 3)) * 4);
                var pixel = image.Pixels.Span.Slice(offset, 3);
                luminance[y, x] = (byte)((pixel[0] + pixel[1] + pixel[1] + pixel[2]) / 4);
            }
        }

        return luminance;
    }

    /// <summary>How far the second viewport is scrolled below the first, in pixels.</summary>
    private static (int Shift, double Error, double RunnerUp) BestShift(byte[,] first, byte[,] second, int? phaseDifference)
    {
        var columns = first.GetLength(1);
        var errors = new SortedDictionary<int, double>();
        for (var shift = -(ViewportHeight - 50); shift <= ViewportHeight - 50; shift++)
        {
            if (phaseDifference is { } difference)
            {
                var residue = (((shift + difference) % 63) + 63) % 63;
                if (Math.Min(residue, 63 - residue) > 2)
                {
                    continue;
                }
            }

            long total = 0;
            long count = 0;
            for (var y = Math.Max(0, -shift); y < ViewportHeight && y + shift < ViewportHeight; y += 2)
            {
                for (var x = 0; x < columns; x++)
                {
                    total += Math.Abs(first[y + shift, x] - second[y, x]);
                    count++;
                }
            }

            errors[shift] = count == 0 ? double.MaxValue : (double)total / count;
        }

        var best = errors.MinBy(pair => pair.Value);
        var runnerUp = errors.Where(pair => Math.Abs(pair.Key - best.Key) > 8).Select(pair => pair.Value).DefaultIfEmpty(double.NaN).Min();
        return (best.Key, best.Value, runnerUp);
    }

    private static void SaveAnnotatedCrop(CapturedImage image, ContainerGridSpec spec, IReadOnlyList<StashFootprint> footprints, string path)
    {
        const int margin = 80;
        var left = Math.Max(0, spec.Bounds.X - margin);
        var top = Math.Max(0, spec.Bounds.Y - margin);
        var right = Math.Min(image.Width, spec.Bounds.X + spec.Bounds.Width + margin);
        var bottom = Math.Min(image.Height, spec.Bounds.Y + spec.Bounds.Height + margin);
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var source = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels.ToArray(), 0, source.GetPixels(), image.Stride * image.Height);
        using var surface = SKSurface.Create(new SKImageInfo(right - left, bottom - top));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(source, new SKRect(left, top, right, bottom), new SKRect(0, 0, right - left, bottom - top));
        var cellWidth = spec.Bounds.Width / spec.Columns;
        var cellHeight = spec.Bounds.Height / spec.Rows;
        using var lattice = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        using var item = new SKPaint { Color = SKColors.Yellow, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        canvas.DrawRect(spec.Bounds.X - left, spec.Bounds.Y - top, spec.Bounds.Width, spec.Bounds.Height, lattice);
        foreach (var footprint in footprints)
        {
            canvas.DrawRect(
                spec.Bounds.X - left + (footprint.Column * cellWidth) + 3,
                spec.Bounds.Y - top + (footprint.Row * cellHeight) + 3,
                (footprint.Width * cellWidth) - 6,
                (footprint.Height * cellHeight) - 6,
                item);
        }

        using var snapshot = surface.Snapshot();
        using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 90);
        using var file = File.Create(path);
        encoded.SaveTo(file);
    }
}
