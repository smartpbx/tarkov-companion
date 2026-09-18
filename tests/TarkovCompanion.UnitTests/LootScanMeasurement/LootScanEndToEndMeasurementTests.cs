using System.Diagnostics;
using System.Globalization;
using System.Text;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Profiles;
using TarkovCompanion.UnitTests.Runtime;
using Xunit.Abstractions;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>
/// Package 37's measurement: screenshot to grid to item to value to decision, counted.
/// </summary>
/// <remarks>
/// It reports and never asserts on the numbers, because the point is to know them. It needs the
/// local icon corpus (<see cref="IconCorpus"/>) and skips with a console note without it, so CI
/// is unaffected. Read <see cref="LootFrameComposer"/> before quoting a figure from it: the
/// frames are composed, not captured, and every figure is therefore a ceiling.
/// </remarks>
public sealed class LootScanEndToEndMeasurementTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly (int Rows, int Columns)[] Shapes = [(2, 2), (3, 4), (4, 4), (5, 5), (6, 6), (8, 10)];
    private static readonly double[] Fills = [0.45, 0.9];
    private const int SeedsPerCase = 3;

    private static readonly ItemCategory[] LootCategories =
    [
        ItemCategory.Barter,
        ItemCategory.Medicine,
        ItemCategory.Provision,
        ItemCategory.Key,
        ItemCategory.AmmunitionPack,
        ItemCategory.Container,
        ItemCategory.Headset,
    ];

    [Fact]
    public async Task ReportsTheWholeLootScanPathOverComposedFrames()
    {
        if (IconCorpus.TryLoad() is not { } corpus)
        {
            output.WriteLine($"[loot-scan-measurement] skipped: no icon corpus at ${IconCorpus.EnvironmentVariable} or {IconCorpus.DefaultDirectory}.");
            return;
        }

        // Two pools. Container loot is what a player mostly scans. "Anything" adds attachments,
        // weapons and gear up to 4x3, whose long art and wide footprints hide grid lines and
        // draw ridges of their own; the first frame set left it out and flattered the detector.
        var pool = corpus.Items
            .Where(item => LootCategories.Contains(item.Category) && item.Width <= 3 && item.Height <= 3)
            .ToArray();
        var anything = corpus.Items.Where(item => item.Width <= 4 && item.Height <= 3).ToArray();
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"[loot-scan-measurement] corpus {corpus.Items.Count} icons, loot pool {pool.Length} items, anything pool {anything.Length} items");
        foreach (var sample in pool.Take(6))
        {
            using var icon = SkiaSharp.SKBitmap.Decode(corpus.ReadIcon(sample.Id));
            report.AppendLine(CultureInfo.InvariantCulture, $"[loot-scan-measurement] icon {sample.ShortName} {icon.Width}x{icon.Height}: border {icon.GetPixel(0, 0)} inner {icon.GetPixel(1, 1)} {icon.GetPixel(3, 3)} {icon.GetPixel(6, 40)}");
        }

        var indexStopwatch = Stopwatch.StartNew();
        var cache = await LootScanMeasurementIndex.OpenAsync(corpus, Now);
        var listed = await cache.ListEvidenceAsync(CancellationToken.None);
        report.AppendLine(CultureInfo.InvariantCulture, $"[loot-scan-measurement] icon index {listed.Count} entries, opened in {indexStopwatch.Elapsed.TotalSeconds:0.0}s");

        using var runtime = await ReadyProfileContextAsync();
        foreach (var stage in LootScanMeasurementStages.All(corpus, cache, runtime, Now))
        {
            foreach (var variant in Enum.GetValues<FrameVariant>())
            {
                var metrics = new PathMetrics();
                foreach (var (rows, columns) in Shapes)
                {
                    foreach (var fill in Fills)
                    {
                        for (var seed = 1; seed <= SeedsPerCase * 2; seed++)
                        {
                            var frame = LootFrameComposer.Compose(corpus, seed % 2 == 0 ? anything : pool, rows, columns, fill, (rows * 1000) + (columns * 100) + seed, variant);
                            var stopwatch = Stopwatch.StartNew();
                            var (grid, result) = await stage.Harness.ScanAsync(frame.Image, Now);
                            metrics.Record(frame, grid, result, stage.Oracle, stopwatch.Elapsed);
                        }
                    }
                }

                report.AppendLine(metrics.Describe(stage.Name, variant));
            }
        }

        output.WriteLine(report.ToString());
        var reportPath = Environment.GetEnvironmentVariable("TARKOV_LOOT_SCAN_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            await File.WriteAllTextAsync(reportPath, report.ToString());
        }
    }

    /// <summary>
    /// The matcher on its own: every true item cropped at its true place, so segmentation cannot
    /// take the blame, scored under a range of distance and runner-up-gap policies.
    /// </summary>
    [Fact]
    public async Task ReportsHowWellFingerprintsSeparateItemsOnComposedFrames()
    {
        if (IconCorpus.TryLoad() is not { } corpus)
        {
            output.WriteLine($"[loot-scan-measurement] skipped: no icon corpus at ${IconCorpus.EnvironmentVariable} or {IconCorpus.DefaultDirectory}.");
            return;
        }

        var pool = corpus.Items
            .Where(item => LootCategories.Contains(item.Category) && item.Width <= 3 && item.Height <= 3)
            .ToArray();
        var index = (await (await LootScanMeasurementIndex.OpenAsync(corpus, Now)).ListEvidenceAsync(CancellationToken.None))
            .Where(entry => corpus.ById.ContainsKey(entry.CanonicalItemId))
            .Select(entry => (Item: corpus.ById[entry.CanonicalItemId], Hash: entry.Fingerprint.Value))
            .ToArray();
        var report = new StringBuilder();
        int[] distances = [0, 2, 4, 6, 8, 10, 12];
        int[] gaps = [1, 2, 4, 6, 8];
        var descriptors = new System.Collections.Concurrent.ConcurrentDictionary<string, IconPixelDescriptor?>(StringComparer.Ordinal);
        foreach (var variant in Enum.GetValues<FrameVariant>())
        {
            var samples = new List<(int Truth, int BestOther, bool TruthIsUniqueBest)>();
            var verified = new List<(double Truth, double BestOther, bool TruthShortlisted)>();
            foreach (var (rows, columns) in Shapes)
            {
                for (var seed = 1; seed <= SeedsPerCase; seed++)
                {
                    var frame = LootFrameComposer.Compose(corpus, pool, rows, columns, 0.9, (rows * 1000) + (columns * 100) + seed, variant);
                    foreach (var placed in frame.Items)
                    {
                        var hash = TarkovCompanion.Infrastructure.Recognition.SkiaPerceptualIconMatcher.ComputeDifferenceHash(Crop(frame, placed));
                        var truth = int.MaxValue;
                        var bestOther = int.MaxValue;
                        foreach (var reference in index)
                        {
                            var sameShape = (reference.Item.Width == placed.Width && reference.Item.Height == placed.Height) ||
                                            (reference.Item.Width == placed.Height && reference.Item.Height == placed.Width);
                            if (!sameShape)
                            {
                                continue;
                            }

                            var distance = System.Numerics.BitOperations.PopCount(hash ^ reference.Hash);
                            if (reference.Item.Id == placed.Item.Id)
                            {
                                truth = Math.Min(truth, distance);
                            }
                            else
                            {
                                bestOther = Math.Min(bestOther, distance);
                            }
                        }

                        samples.Add((truth, bestOther, truth < bestOther));

                        var crop = IconPixelDescriptor.Create(Crop(frame, placed), placed.Width, placed.Height);
                        var shortlist = index
                            .Where(reference => reference.Item.Width == placed.Width && reference.Item.Height == placed.Height)
                            .OrderBy(reference => System.Numerics.BitOperations.PopCount(hash ^ reference.Hash))
                            .Take(ShortlistSize)
                            .ToArray();
                        var truthScore = -1d;
                        var otherScore = -1d;
                        foreach (var reference in shortlist)
                        {
                            var descriptor = descriptors.GetOrAdd(reference.Item.Id, id =>
                                IconPixelDescriptor.Create(Decode(corpus.ReadIcon(id)), reference.Item.Width, reference.Item.Height));
                            var score = crop is null || descriptor is null ? -1 : crop.Correlate(descriptor);
                            if (reference.Item.Id == placed.Item.Id)
                            {
                                truthScore = score;
                            }
                            else
                            {
                                otherScore = Math.Max(otherScore, score);
                            }
                        }

                        verified.Add((truthScore, otherScore, shortlist.Any(reference => reference.Item.Id == placed.Item.Id)));
                    }
                }
            }

            report.AppendLine(CultureInfo.InvariantCulture, $"== fingerprints / {variant}: {samples.Count} true items ==");
            report.AppendLine(CultureInfo.InvariantCulture, $"truth distance: 0 bits {samples.Count(s => s.Truth == 0)}, 1-4 {samples.Count(s => s.Truth is >= 1 and <= 4)}, 5-8 {samples.Count(s => s.Truth is >= 5 and <= 8)}, 9-12 {samples.Count(s => s.Truth is >= 9 and <= 12)}, more {samples.Count(s => s.Truth > 12)}; truth is the unique nearest for {samples.Count(s => s.TruthIsUniqueBest)}");
            foreach (var maximumDistance in distances)
            {
                var cells = gaps.Select(gap =>
                {
                    var accepted = samples.Where(s => Math.Min(s.Truth, s.BestOther) <= maximumDistance && Math.Abs(s.Truth - s.BestOther) >= gap).ToArray();
                    var correct = accepted.Count(s => s.Truth < s.BestOther);
                    return string.Create(CultureInfo.InvariantCulture, $"gap>={gap}: {correct} right/{accepted.Length - correct} wrong");
                });
                report.AppendLine(CultureInfo.InvariantCulture, $"  distance<={maximumDistance,2}: {string.Join(" | ", cells)}");
            }

            report.AppendLine(CultureInfo.InvariantCulture, $"pixel check over the {ShortlistSize} nearest hashes: truth shortlisted {verified.Count(v => v.TruthShortlisted)}/{verified.Count}; truth score min {verified.Where(v => v.TruthShortlisted).Min(v => v.Truth):0.000} median {verified.Where(v => v.TruthShortlisted).Select(v => v.Truth).Order().ElementAt(verified.Count(v => v.TruthShortlisted) / 2):0.000}");
            foreach (var minimumScore in new[] { 0.80, 0.85, 0.90, 0.95 })
            {
                var cells = new[] { 0.01, 0.02, 0.04, 0.08 }.Select(margin =>
                {
                    var accepted = verified.Where(v => Math.Max(v.Truth, v.BestOther) >= minimumScore && Math.Abs(v.Truth - v.BestOther) >= margin).ToArray();
                    var correct = accepted.Count(v => v.Truth > v.BestOther);
                    return string.Create(CultureInfo.InvariantCulture, $"margin>={margin:0.00}: {correct} right/{accepted.Length - correct} wrong");
                });
                report.AppendLine(CultureInfo.InvariantCulture, $"  score>={minimumScore:0.00}: {string.Join(" | ", cells)}");
            }
        }

        output.WriteLine(report.ToString());
        var reportPath = Environment.GetEnvironmentVariable("TARKOV_LOOT_SCAN_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            await File.WriteAllTextAsync(reportPath + ".fingerprints.txt", report.ToString());
        }
    }

    /// <summary>
    /// Writes the frames the package's renders are scanned from, when asked to. Composed frames
    /// are game art, so they go to a directory outside the checkout and are never committed.
    /// </summary>
    [Fact]
    public void WritesComposedFramesForTheRenderPreviewWhenAskedTo()
    {
        var directory = Environment.GetEnvironmentVariable("TARKOV_LOOT_SCAN_FRAMES");
        if (string.IsNullOrWhiteSpace(directory) || IconCorpus.TryLoad() is not { } corpus)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var loot = corpus.Items
            .Where(item => LootCategories.Contains(item.Category) && item.Category != ItemCategory.Key && item.Width <= 3 && item.Height <= 3)
            .ToArray();
        var keys = corpus.Items.Where(item => item.Category == ItemCategory.Key).ToArray();
        LootFrameComposer.SavePng(
            LootFrameComposer.Compose(corpus, loot, 4, 5, 0.9, 3701, FrameVariant.Pristine1080).Image,
            Path.Combine(directory, "container-4x5.png"));
        LootFrameComposer.SavePng(
            LootFrameComposer.Compose(corpus, keys, 3, 4, 0.9, 3702, FrameVariant.Pristine1080).Image,
            Path.Combine(directory, "keys-3x4.png"));
        LootFrameComposer.SavePng(
            LootFrameComposer.Compose(corpus, loot, 1, 1, 0, 3703, FrameVariant.Pristine1080).Image,
            Path.Combine(directory, "no-container.png"));
    }

    private const int ShortlistSize = 32;

    private static CapturedImage Decode(byte[] content)
    {
        using var decoded = SkiaSharp.SKBitmap.Decode(content);
        using var bitmap = decoded.ColorType == SkiaSharp.SKColorType.Bgra8888 ? decoded.Copy() : decoded.Copy(SkiaSharp.SKColorType.Bgra8888);
        var pixels = new byte[bitmap.RowBytes * bitmap.Height];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return new(pixels, bitmap.Width, bitmap.Height, bitmap.RowBytes, PixelFormat.Bgra8888, Now, "reference-icon");
    }

    private static CapturedImage Crop(ComposedFrame frame, PlacedItem placed)
    {
        var x = (int)Math.Round(frame.OriginX + (placed.Column * frame.PitchPixels));
        var y = (int)Math.Round(frame.OriginY + (placed.Row * frame.PitchPixels));
        var width = (int)Math.Round(placed.Width * frame.PitchPixels) + 1;
        var height = (int)Math.Round(placed.Height * frame.PitchPixels) + 1;
        var buffer = new byte[width * height * 4];
        var source = frame.Image.Pixels.Span;
        for (var row = 0; row < height; row++)
        {
            source.Slice(((y + row) * frame.Image.Stride) + (x * 4), width * 4).CopyTo(buffer.AsSpan(row * width * 4, width * 4));
        }

        return new(buffer, width, height, width * 4, PixelFormat.Bgra8888, Now, "truth-crop");
    }

    private static async Task<ProfileRuntimeContextService> ReadyProfileContextAsync()
    {
        var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        var profile = Profile(Context(Id(437), "generation-a", ProfileGameMode.Pvp), "item-a");
        await profiles.CreateAsync(Request(profile), CancellationToken.None);
        var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        return runtime;
    }
}

/// <summary>What a person looking at the true items would do, used to score the decisions.</summary>
internal delegate LootScanVerdict? LootOracle(CorpusItem item);

internal sealed record MeasurementStage(string Name, LootScanPathHarness Harness, LootOracle Oracle);

/// <summary>Counts for one stage and one frame variant.</summary>
internal sealed class PathMetrics
{
    private readonly Dictionary<string, int> _reviewReasons = new(StringComparer.Ordinal);
    private int _frames;
    private int _gridFound;
    private int _latticeExact;
    private int _truthItems;
    private int _footprints;
    private int _footprintsExact;
    private int _identified;
    private int _identifiedCorrect;
    private int _identifiedWrong;
    private int _refusedWithTruthAmongCandidates;
    private int _refusedWithOtherCandidates;
    private int _refusedNoCandidate;
    private int _decisions;
    private int _decisive;
    private int _decisiveAgreed;
    private int _decisiveDisagreed;
    private double _seconds;
    private readonly Dictionary<LootScanVerdict, int> _verdicts = [];
    private readonly List<string> _latticeNotes = [];
    private readonly Dictionary<ItemCategory, (int Seen, int Named)> _byCategory = [];

    public void Record(ComposedFrame frame, GridReconstructionRequest? grid, LootScanResult? result, LootOracle oracle, TimeSpan elapsed)
    {
        _frames++;
        _seconds += elapsed.TotalSeconds;
        _truthItems += frame.Items.Count;
        if (grid?.Lattice is not { } lattice)
        {
            _latticeNotes.Add($"{frame.Rows}x{frame.Columns}:none");
            return;
        }


        _gridFound++;
        var truthByAnchor = frame.Items.ToDictionary(item => (item.Row, item.Column));
        if (lattice.Rows == frame.Rows && lattice.Columns == frame.Columns &&
            Math.Abs(lattice.Bounds.X - frame.OriginX) <= 2 && Math.Abs(lattice.Bounds.Y - frame.OriginY) <= 2 &&
            Math.Abs(lattice.CellWidthPixels - frame.PitchPixels) <= 1)
        {
            _latticeExact++;
        }
        else
        {
            _latticeNotes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{frame.Rows}x{frame.Columns}:{lattice.Rows}x{lattice.Columns}@{lattice.Bounds.X - frame.OriginX:0},{lattice.Bounds.Y - frame.OriginY:0}/{lattice.CellWidthPixels}"));
        }

        (int Row, int Column) TruthAnchor(GridCellAddress anchor) => (
            (int)Math.Round((lattice.Bounds.Y + (anchor.Row * lattice.CellHeightPixels) - frame.OriginY) / frame.PitchPixels),
            (int)Math.Round((lattice.Bounds.X + (anchor.Column * lattice.CellWidthPixels) - frame.OriginX) / frame.PitchPixels));

        var truthByObservedAnchor = new Dictionary<GridCellAddress, PlacedItem?>();
        foreach (var observation in grid.OccupiedCells)
        {
            _footprints++;
            truthByAnchor.TryGetValue(TruthAnchor(observation.Anchor), out var truth);
            truthByObservedAnchor[observation.Anchor] = truth;
            var item = observation.Item;
            var width = item.Value?.WidthCells.Value ?? item.Candidates.FirstOrDefault()?.Value.WidthCells.Value;
            var height = item.Value?.HeightCells.Value ?? item.Candidates.FirstOrDefault()?.Value.HeightCells.Value;
            var footprintExact = truth is not null &&
                ((width == truth.Width && height == truth.Height) || (width is null && truth is { Width: 1, Height: 1 }));
            if (footprintExact)
            {
                _footprintsExact++;
            }

            if (truth is not null)
            {
                var tally = _byCategory.GetValueOrDefault(truth.Item.Category);
                _byCategory[truth.Item.Category] = (tally.Seen + 1, tally.Named + (item.Value?.CanonicalId.Value == truth.Item.Id ? 1 : 0));
            }

            if (item.Value is { } recognized)
            {
                _identified++;
                if (truth is not null && recognized.CanonicalId.Value == truth.Item.Id)
                {
                    _identifiedCorrect++;
                }
                else
                {
                    _identifiedWrong++;
                }
            }
            else if (item.Candidates.Count == 0)
            {
                _refusedNoCandidate++;
            }
            else if (truth is not null && item.Candidates.Any(candidate => candidate.CandidateId == truth.Item.Id))
            {
                _refusedWithTruthAmongCandidates++;
            }
            else
            {
                _refusedWithOtherCandidates++;
            }
        }

        foreach (var decision in result?.Decisions ?? [])
        {
            _decisions++;
            _verdicts[decision.Verdict] = _verdicts.GetValueOrDefault(decision.Verdict) + 1;
            if (decision.Verdict == LootScanVerdict.Review)
            {
                var code = decision.Reasons.FirstOrDefault()?.Code ?? "none";
                _reviewReasons[code] = _reviewReasons.GetValueOrDefault(code) + 1;
                continue;
            }

            _decisive++;
            var expected = truthByObservedAnchor.GetValueOrDefault(decision.SourceAnchor) is { } truth ? oracle(truth.Item) : null;
            var agreed = expected is not null &&
                (expected == decision.Verdict ||
                 (expected == LootScanVerdict.Take && decision.Verdict == LootScanVerdict.Swap));
            if (agreed)
            {
                _decisiveAgreed++;
            }
            else
            {
                _decisiveDisagreed++;
            }
        }
    }

    public string Describe(string stage, FrameVariant variant)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"== {stage} / {variant} ==");
        builder.AppendLine(CultureInfo.InvariantCulture, $"frames {_frames}, grid found {_gridFound}, lattice exact {_latticeExact}, {_seconds / Math.Max(1, _frames):0.00}s a frame");
        builder.AppendLine(CultureInfo.InvariantCulture, $"true items {_truthItems}; footprints reported {_footprints}, exact {_footprintsExact}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"identified {_identified}: correct {_identifiedCorrect}, WRONG {_identifiedWrong}; refused {_refusedWithTruthAmongCandidates + _refusedWithOtherCandidates + _refusedNoCandidate} (truth among candidates {_refusedWithTruthAmongCandidates}, other candidates {_refusedWithOtherCandidates}, no candidate {_refusedNoCandidate})");
        builder.AppendLine(CultureInfo.InvariantCulture, $"decisions {_decisions}: {string.Join(", ", _verdicts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value}"))}; decisive {_decisive}, a person agrees {_decisiveAgreed}, disagrees {_decisiveDisagreed}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"named by category: {string.Join(", ", _byCategory.OrderByDescending(pair => pair.Value.Seen).Select(pair => $"{pair.Key} {pair.Value.Named}/{pair.Value.Seen}"))}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"lattices missed or inexact (truth rows x columns : found @ origin error / cell): {string.Join(" ", _latticeNotes)}");
        builder.Append(CultureInfo.InvariantCulture, $"review reasons: {string.Join(", ", _reviewReasons.OrderByDescending(pair => pair.Value).Take(5).Select(pair => $"{pair.Key} {pair.Value}"))}");
        return builder.ToString();
    }
}
