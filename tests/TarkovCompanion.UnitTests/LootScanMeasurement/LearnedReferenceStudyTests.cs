using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>
/// What the player's own corrections would add on the real labelled stash frames (epic #712,
/// 1-12): every cell the catalog refuses on one frame is "corrected" to its label and kept as a
/// learned crop, and every other frame is read again with those crops beside the catalog.
/// </summary>
/// <remarks>
/// Leave-one-frame-out, so a cell is never matched against its own crop. The frames overlap
/// (one stash, scrolled), so many learned crops are the same physical item seen again, which is
/// what a player re-photographing a stash does. The stress rows drop every learned crop of the
/// cell's own item, so anything named there is wrong by construction: that is the test of
/// "0 wrong stays 0 wrong". A named cell is never re-decided (see
/// <see cref="LearnedIconMatchPolicy"/>), so previously right names cannot change.
/// Reports only; needs the local icon corpus and the labelled screenshots, skips without them.
/// </remarks>
public sealed class LearnedReferenceStudyTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly double[] Floors = [0.97, 0.95, 0.93, 0.90, 0.88, 0.85, 0.80, 0.75];

    [Fact]
    public async Task ReportsWhatCorrectionsAddOnLabelledRealScreenshots()
    {
        var directory = Environment.GetEnvironmentVariable("TARKOV_REAL_SCREENSHOTS") ?? "/root/orca/recognition-corpus/real-2026-09-18";
        if (!Directory.Exists(directory) || IconCorpus.TryLoad() is not { } corpus)
        {
            output.WriteLine("[learned-study] skipped: no screenshots or no icon corpus.");
            return;
        }

        var catalog = new CatalogScores(corpus);
        var loader = new SkiaScreenshotImageLoader();
        var samples = new List<Sample>();
        foreach (var sidecar in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
        {
            var image = await loader.LoadAsync(sidecar[..^".expected.json".Length], CancellationToken.None);
            if (image is null)
            {
                continue;
            }

            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(sidecar));
            var panel = document.RootElement.GetProperty("panel");
            var (originX, originY, pitch) = (panel.GetProperty("originX").GetInt32(), panel.GetProperty("originY").GetInt32(), panel.GetProperty("pitch").GetInt32());
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                if (item.GetProperty("state").GetString() != "ok")
                {
                    continue;
                }

                var (row, column) = (item.GetProperty("row").GetInt32(), item.GetProperty("column").GetInt32());
                var (width, height) = (item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32());
                var truth = item.GetProperty("canonicalIds").EnumerateArray().Select(id => id.GetString()!).ToHashSet(StringComparer.Ordinal);
                if (Crop(image, originX + (column * pitch), originY + (row * pitch), (width * pitch) + 1, (height * pitch) + 1) is { } crop &&
                    IconPixelDescriptor.Create(crop, width, height) is { } described)
                {
                    var scores = catalog.Score(described, width, height);
                    samples.Add(new(Path.GetFileName(sidecar), described, width, height, truth, scores, CatalogNames(scores)));
                }
            }
        }

        var report = new StringBuilder();
        var namedRight = samples.Count(sample => sample.CatalogNamed is { } named && sample.Truth.Contains(named));
        var namedWrong = samples.Count(sample => sample.CatalogNamed is { } named && !sample.Truth.Contains(named));
        var refused = samples.Where(sample => sample.CatalogNamed is null).ToArray();
        report.AppendLine(CultureInfo.InvariantCulture, $"##### LEARNED crops on {samples.Count} labelled real cells, {samples.Select(sample => sample.Frame).Distinct().Count()} frames");
        report.AppendLine(CultureInfo.InvariantCulture, $"catalog alone (0.85/0.04): named {namedRight} right, {namedWrong} wrong, refused {refused.Length}");
        report.AppendLine("named cells are never re-decided, so their right/wrong counts are unchanged in every row below.");
        foreach (var (title, oncePerItem, dropOwnItem, teachNamedToo) in new[]
        {
            ("every refused cell on other frames corrected", false, false, false),
            ("only the first correction of each item kept", true, false, false),
            ("STRESS: refused-cell crops, own item's crops removed", false, true, false),
            ("STRESS: every cell's crop, own item's crops removed", false, true, true),
        })
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"== {title}");
            foreach (var floor in Floors)
            {
                var (right, wrong, overFloor, unguarded) = (0, 0, 0, 0);
                foreach (var sample in refused)
                {
                    var learned = new Dictionary<string, double>(StringComparer.Ordinal);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var teacher in samples)
                    {
                        if (teacher.Frame == sample.Frame ||
                            (!teachNamedToo && teacher.CatalogNamed is not null) ||
                            teacher.Width != sample.Width || teacher.Height != sample.Height)
                        {
                            continue;
                        }

                        var teacherItem = teacher.Truth.Order(StringComparer.Ordinal).First();
                        if ((dropOwnItem && sample.Truth.Contains(teacherItem)) || (oncePerItem && !seen.Add(teacherItem)))
                        {
                            continue;
                        }

                        var score = sample.Descriptor.Correlate(teacher.Descriptor);
                        learned[teacherItem] = learned.TryGetValue(teacherItem, out var kept) ? Math.Max(kept, score) : score;
                    }

                    if (learned.Count > 0 && learned.Values.Max() >= floor)
                    {
                        overFloor++;
                    }

                    // The same choice without the catalog-lookalike guard: what the guard is holding back.
                    if (LearnedIconMatchPolicy.Choose(learned.ToDictionary(pair => pair.Key, _ => 1d), learned, floor) is { } bare &&
                        !sample.Truth.Contains(bare))
                    {
                        unguarded++;
                    }

                    if (LearnedIconMatchPolicy.Choose(sample.CatalogScores, learned, floor) is { } named)
                    {
                        if (sample.Truth.Contains(named))
                        {
                            right++;
                        }
                        else
                        {
                            wrong++;
                        }
                    }
                }

                report.AppendLine(CultureInfo.InvariantCulture, $"   floor {floor:0.00}: newly named {right} right, {wrong} WRONG (best learned over floor {overFloor}, wrong without the catalog guard {unguarded}); coverage {namedRight}/{samples.Count} -> {namedRight + right}/{samples.Count}");
            }
        }

        output.WriteLine(report.ToString());
        var reports = Environment.GetEnvironmentVariable("TARKOV_REAL_SCREENSHOT_REPORTS") ?? "/root/orca/recognition-corpus/real-reports";
        if (Directory.Exists(reports))
        {
            await File.WriteAllTextAsync(Path.Combine(reports, "learned-references.txt"), report.ToString());
        }
    }

    /// <summary>The production catalog rule: 0.85 and 0.04 clear of the next item, or refused.</summary>
    private static string? CatalogNames(IReadOnlyDictionary<string, double> scores)
    {
        var ranked = scores.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(2).ToArray();
        return ranked.Length == 2 &&
               ranked[0].Value >= GridPixelReconstructionOptions.DefaultMinimumPixelCorrelation &&
               ranked[0].Value - ranked[1].Value >= GridPixelReconstructionOptions.DefaultMinimumPixelCorrelationMargin
            ? ranked[0].Key
            : null;
    }

    private static CapturedImage? Crop(CapturedImage image, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > image.Width || y + height > image.Height)
        {
            return null;
        }

        var buffer = new byte[width * height * 4];
        var source = image.Pixels.Span;
        for (var row = 0; row < height; row++)
        {
            source.Slice(((y + row) * image.Stride) + (x * 4), width * 4).CopyTo(buffer.AsSpan(row * width * 4, width * 4));
        }

        return new(buffer, width, height, width * 4, image.Format, Now, "study-crop");
    }

    private sealed record Sample(
        string Frame,
        IconPixelDescriptor Descriptor,
        int Width,
        int Height,
        IReadOnlySet<string> Truth,
        IReadOnlyDictionary<string, double> CatalogScores,
        string? CatalogNamed);

    /// <summary>Every corpus icon of a shape, described once; the best score per item for a cell.</summary>
    private sealed class CatalogScores(IconCorpus corpus)
    {
        private readonly ConcurrentDictionary<(int, int), (string Id, IconPixelDescriptor Descriptor)[]> _byShape = new();

        public IReadOnlyDictionary<string, double> Score(IconPixelDescriptor cell, int width, int height)
        {
            var shaped = _byShape.GetOrAdd((width, height), shape => corpus.Items
                .Where(item => item.Width == shape.Item1 && item.Height == shape.Item2)
                .Select(item => (item.Id, Descriptor: Describe(item.Id, item.Width, item.Height)))
                .Where(entry => entry.Descriptor is not null)
                .Select(entry => (entry.Id, entry.Descriptor!))
                .ToArray());
            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (id, descriptor) in shaped)
            {
                scores[id] = cell.Correlate(descriptor);
            }

            return scores;
        }

        private IconPixelDescriptor? Describe(string itemId, int width, int height)
        {
            using var decoded = SKBitmap.Decode(corpus.ReadIcon(itemId));
            if (decoded is null)
            {
                return null;
            }

            using var bitmap = decoded.Copy(SKColorType.Bgra8888);
            var pixels = new byte[bitmap.RowBytes * bitmap.Height];
            Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
            return IconPixelDescriptor.Create(
                new CapturedImage(pixels, bitmap.Width, bitmap.Height, bitmap.RowBytes, PixelFormat.Bgra8888, Now, "reference"),
                width,
                height);
        }
    }
}
