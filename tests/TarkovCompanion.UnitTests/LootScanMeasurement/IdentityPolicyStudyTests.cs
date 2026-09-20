using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>
/// What naming policy would do on real labelled cells and on composed frames, side by side:
/// for every floor and margin, how many items it names rightly and how many wrongly.
/// </summary>
/// <remarks>
/// Every true item is cropped at its labelled (or composed) place, so a footprint fault cannot be
/// mistaken for a matching fault. Two things are varied besides the policy: whether the bands
/// the game writes over an icon are masked out of the comparison, and whether the difference
/// hash shortlists the references first or every reference of the item's shape is compared.
/// Reports only; needs the local icon corpus, and the real half needs labelled screenshots.
/// </remarks>
public sealed class IdentityPolicyStudyTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly double[] Floors = [0.90, 0.85, 0.80, 0.75, 0.70, 0.65, 0.60, 0.55, 0.50];
    private static readonly double[] Margins = [0.04, 0.08, 0.12, 0.16, 0.20];

    [Fact]
    public async Task ReportsNamingPoliciesOnLabelledRealScreenshots()
    {
        var directory = Environment.GetEnvironmentVariable("TARKOV_REAL_SCREENSHOTS") ?? "/root/orca/recognition-corpus/real-2026-09-18";
        if (!Directory.Exists(directory) || IconCorpus.TryLoad() is not { } corpus)
        {
            output.WriteLine("[identity-study] skipped: no screenshots or no icon corpus.");
            return;
        }

        var references = new ReferenceSet(corpus);
        var loader = new SkiaScreenshotImageLoader();
        var samples = new List<Sample>();
        foreach (var sidecar in Directory.EnumerateFiles(directory, "*.expected.json").Order(StringComparer.Ordinal))
        {
            var image = await loader.LoadAsync(sidecar[..^".expected.json".Length], CancellationToken.None);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(sidecar));
            var panel = document.RootElement.GetProperty("panel");
            var (originX, originY, pitch) = (panel.GetProperty("originX").GetInt32(), panel.GetProperty("originY").GetInt32(), panel.GetProperty("pitch").GetInt32());
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                if (image is null || item.GetProperty("state").GetString() != "ok")
                {
                    continue;
                }

                var (row, column) = (item.GetProperty("row").GetInt32(), item.GetProperty("column").GetInt32());
                var (width, height) = (item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32());
                var truth = item.GetProperty("canonicalIds").EnumerateArray().Select(id => id.GetString()!).ToHashSet(StringComparer.Ordinal);
                if (Crop(image, originX + (column * pitch), originY + (row * pitch), (width * pitch) + 1, (height * pitch) + 1) is { } crop)
                {
                    samples.Add(new(crop, width, height, truth, corpus.ById[truth.First()].Category, $"{item.GetProperty("caption").GetString()} (frame file {Path.GetFileName(sidecar)[24..27]} r{row} c{column})"));
                }
            }
        }

        var report = Describe("REAL labelled cells", samples, references);
        output.WriteLine(report);
        await WriteAsync("identity-real.txt", report);
    }

    [Fact]
    public async Task ReportsTheSameNamingPoliciesOnComposedFrames()
    {
        if (IconCorpus.TryLoad() is not { } corpus)
        {
            output.WriteLine("[identity-study] skipped: no icon corpus.");
            return;
        }

        var references = new ReferenceSet(corpus);
        var anything = corpus.Items.Where(item => item.Width <= 4 && item.Height <= 3).ToArray();
        var samples = new List<Sample>();
        foreach (var variant in new[] { FrameVariant.Pristine1080, FrameVariant.Dimmed1080, FrameVariant.Scaled1440 })
        {
            for (var seed = 1; seed <= 4; seed++)
            {
                var frame = LootFrameComposer.Compose(corpus, anything, 8, 10, 0.9, 5000 + seed, variant);
                foreach (var placed in frame.Items)
                {
                    var x = (int)Math.Round(frame.OriginX + (placed.Column * frame.PitchPixels));
                    var y = (int)Math.Round(frame.OriginY + (placed.Row * frame.PitchPixels));
                    var width = (int)Math.Round(placed.Width * frame.PitchPixels) + 1;
                    var height = (int)Math.Round(placed.Height * frame.PitchPixels) + 1;
                    if (Crop(frame.Image, x, y, width, height) is { } crop)
                    {
                        samples.Add(new(crop, placed.Width, placed.Height, new HashSet<string>(StringComparer.Ordinal) { placed.Item.Id }, placed.Item.Category, placed.Item.ShortName));
                    }
                }
            }
        }

        var report = Describe("COMPOSED cells (pristine, dimmed, 1440p)", samples, references);
        output.WriteLine(report);
        await WriteAsync("identity-composed.txt", report);
    }

    private static string Describe(string title, IReadOnlyList<Sample> samples, ReferenceSet references)
    {
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"##### {title}: {samples.Count} true items");
        foreach (var masked in new[] { false, true })
        {
            foreach (var shortlist in new[] { true, false })
            {
                var scored = samples.Select(sample => references.Score(sample, masked, shortlist)).ToArray();
                report.AppendLine(CultureInfo.InvariantCulture, $"== {(masked ? "game writing masked" : "whole icon")}, {(shortlist ? "32 nearest hashes" : "every reference of the shape")}");
                var truths = scored.Select(entry => entry.Truth).Order().ToArray();
                report.AppendLine(CultureInfo.InvariantCulture, $"true item compared at all {scored.Count(entry => entry.Truth > -1)}/{scored.Length}; true item scores highest {scored.Count(entry => entry.Truth > entry.BestOther)}; true-item score min {truths[0]:0.00} p10 {truths[truths.Length / 10]:0.00} p25 {truths[truths.Length / 4]:0.00} median {truths[truths.Length / 2]:0.00} p75 {truths[truths.Length * 3 / 4]:0.00}; at or over 0.90: {truths.Count(value => value >= 0.90)}");
                foreach (var group in scored.Zip(samples).GroupBy(pair => pair.Second.Category).OrderByDescending(group => group.Count()).Take(6))
                {
                    var values = group.Select(pair => pair.First.Truth).Order().ToArray();
                    report.AppendLine(CultureInfo.InvariantCulture, $"   {group.Key,-14} n={values.Length,3} median true score {values[values.Length / 2]:0.00}, scores highest {group.Count(pair => pair.First.Truth > pair.First.BestOther)}");
                }

                foreach (var (entry, sample) in scored.Zip(samples).Where(pair => pair.First.BestOther > pair.First.Truth && pair.First.BestOther >= 0.70 && pair.First.BestOther - pair.First.Truth >= 0.04))
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"   WOULD BE WRONG at 0.70/0.04: truth {sample.Label} {sample.Width}x{sample.Height} scored {entry.Truth:0.000}, but {entry.BestOtherName} scored {entry.BestOther:0.000}");
                }

                report.AppendLine("   floor \\ margin: " + string.Join(" | ", Margins.Select(margin => margin.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(9))));
                foreach (var floor in Floors)
                {
                    var cells = Margins.Select(margin =>
                    {
                        var accepted = scored.Where(entry => Math.Max(entry.Truth, entry.BestOther) >= floor && Math.Abs(entry.Truth - entry.BestOther) >= margin).ToArray();
                        var right = accepted.Count(entry => entry.Truth > entry.BestOther);
                        return string.Create(CultureInfo.InvariantCulture, $"{right,4}/{accepted.Length - right,-4}");
                    });
                    report.AppendLine(CultureInfo.InvariantCulture, $"   {floor:0.00} right/WRONG: {string.Join(" | ", cells)}");
                }
            }
        }

        return report.ToString();
    }

    private static async Task WriteAsync(string name, string report)
    {
        var directory = Environment.GetEnvironmentVariable("TARKOV_REAL_SCREENSHOT_REPORTS") ?? "/root/orca/recognition-corpus/real-reports";
        if (Directory.Exists(directory))
        {
            await File.WriteAllTextAsync(Path.Combine(directory, name), report);
        }
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

    private sealed record Sample(CapturedImage Crop, int Width, int Height, IReadOnlySet<string> Truth, ItemCategory Category, string Label = "");

    private readonly record struct Scored(double Truth, double BestOther, string BestOtherName = "");

    /// <summary>Every corpus icon, hashed once and described on first use in either variant.</summary>
    private sealed class ReferenceSet
    {
        private readonly IconCorpus _corpus;
        private readonly Dictionary<(int, int), (CorpusItem Item, ulong Hash)[]> _byShape;
        private readonly ConcurrentDictionary<(string, bool), IconPixelDescriptor?> _descriptors = new();
        private readonly ConcurrentDictionary<string, CapturedImage?> _decoded = new(StringComparer.Ordinal);

        public ReferenceSet(IconCorpus corpus)
        {
            _corpus = corpus;
            _byShape = corpus.Items
                .Select(item => (Item: item, Image: Decode(item.Id)))
                .Where(entry => entry.Image is not null)
                .Select(entry => (entry.Item, Hash: SkiaPerceptualIconMatcher.ComputeDifferenceHash(entry.Image!)))
                .GroupBy(entry => (entry.Item.Width, entry.Item.Height))
                .ToDictionary(group => group.Key, group => group.ToArray());
        }

        public Scored Score(Sample sample, bool masked, bool shortlist)
        {
            if (!_byShape.TryGetValue((sample.Width, sample.Height), out var shaped) ||
                IconPixelDescriptor.Create(sample.Crop, sample.Width, sample.Height, masked) is not { } described)
            {
                return new(-1, -1);
            }

            IEnumerable<(CorpusItem Item, ulong Hash)> candidates = shaped;
            if (shortlist)
            {
                var hash = SkiaPerceptualIconMatcher.ComputeDifferenceHash(sample.Crop);
                candidates = shaped.OrderBy(entry => BitOperations.PopCount(hash ^ entry.Hash)).Take(32);
            }

            var truth = -1d;
            var other = -1d;
            var otherName = string.Empty;
            foreach (var (item, _) in candidates)
            {
                var descriptor = _descriptors.GetOrAdd((item.Id, masked), key =>
                    Decode(key.Item1) is { } image ? IconPixelDescriptor.Create(image, item.Width, item.Height, key.Item2) : null);
                if (descriptor is null)
                {
                    continue;
                }

                var score = described.Correlate(descriptor);
                if (sample.Truth.Contains(item.Id))
                {
                    truth = Math.Max(truth, score);
                }
                else if (score > other)
                {
                    other = score;
                    otherName = item.ShortName;
                }
            }

            return new(truth, other, otherName);
        }

        private CapturedImage? Decode(string itemId) => _decoded.GetOrAdd(itemId, id =>
        {
            using var decoded = SKBitmap.Decode(_corpus.ReadIcon(id));
            if (decoded is null)
            {
                return null;
            }

            using var bitmap = decoded.ColorType == SKColorType.Bgra8888 ? decoded.Copy() : decoded.Copy(SKColorType.Bgra8888);
            var pixels = new byte[bitmap.RowBytes * bitmap.Height];
            Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
            return new CapturedImage(pixels, bitmap.Width, bitmap.Height, bitmap.RowBytes, PixelFormat.Bgra8888, Now, "reference");
        });
    }
}
