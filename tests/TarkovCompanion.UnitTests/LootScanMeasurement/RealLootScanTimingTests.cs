using System.Diagnostics;
using System.Globalization;
using System.Text;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.LootScanMeasurement;

/// <summary>
/// Where the time goes when one real frame is read against the whole icon corpus: decode, the
/// reference index, the grid readers, and the icon matching, first scan of a session and again.
/// </summary>
/// <remarks>
/// #572: one real 3840x1080 loot frame took 57 s on dev, and nothing said which stage. This
/// reports each stage separately so a change to one is measured against that stage, not the sum.
/// Reports only; skips without the frames or the icon corpus (neither may be committed).
/// "Cold" is a fresh reference index, which is what the first scan after launch pays; "warm" is
/// the same index again, which is every later scan.
/// </remarks>
public sealed class RealLootScanTimingTests(ITestOutputHelper output)
{
    private const string LootFrame =
        "/root/orca/incoming/loot-2026-09-20/2026-09-20[21-12]_-86.18, 19.72, -91.78_0.25662, -0.42024, 0.12520, 0.86132_11.45 (0).png";

    private const string StashFrames = "/root/orca/recognition-corpus/real-2026-09-18";

    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReportsPerStageTimingsOnRealFrames()
    {
        if (IconCorpus.TryLoad() is not { } corpus)
        {
            output.WriteLine("[loot-timing] skipped: no icon corpus.");
            return;
        }

        var frames = new List<(string Path, InventoryGridSurface Surface)>();
        var loot = Environment.GetEnvironmentVariable("TARKOV_LOOT_FRAME") ?? LootFrame;
        if (File.Exists(loot))
        {
            frames.Add((loot, InventoryGridSurface.VisibleLoot));
        }

        var stash = Environment.GetEnvironmentVariable("TARKOV_STASH_REAL_FRAMES") ?? StashFrames;
        if (Directory.Exists(stash))
        {
            frames.AddRange(Directory.EnumerateFiles(stash, "*.expected.json")
                .Order(StringComparer.Ordinal)
                .Select(label => (label[..^".expected.json".Length], InventoryGridSurface.Stash)));
        }

        if (frames.Count == 0)
        {
            output.WriteLine("[loot-timing] skipped: no real frames.");
            return;
        }

        var cache = await LootScanMeasurementIndex.OpenAsync(corpus, Now);
        var items = corpus.CreateRepository(Now);
        var report = new StringBuilder();
        var watch = Stopwatch.StartNew();

        // What the app's first GetAsync pays: listing (and validating) every stored icon.
        var onDisk = new FileIconEvidenceCache(new FileIconEvidenceCacheOptions(LootScanMeasurementIndex.DirectoryFor(corpus)) { MaximumEntries = 8192 });
        var listed = await onDisk.ListEvidenceAsync(CancellationToken.None);
        report.AppendLine(CultureInfo.InvariantCulture, $"[loot-timing] cache listing: {listed.Count} icons validated in {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        var index = new IconReferenceIndex(cache, items);
        var snapshot = await index.GetAsync(CancellationToken.None);
        report.AppendLine(CultureInfo.InvariantCulture, $"[loot-timing] reference index: {snapshot.References.Count} references in {watch.ElapsedMilliseconds} ms (listing already held by the measurement cache)");

        // Describing every reference up front, the way the app now does after the icon index
        // refreshes, and a check that the batch read gives the numbers the single read gave.
        var warmIndex = new IconReferenceIndex(cache, items);
        watch.Restart();
        await warmIndex.WarmAsync(CancellationToken.None);
        var warmAll = watch.ElapsedMilliseconds;
        var warmed = await warmIndex.GetAsync(CancellationToken.None);
        var single = await new IconReferenceIndex(new OneAtATime(cache), items).GetAsync(CancellationToken.None);
        var compared = 0;
        var differing = 0;
        foreach (var (width, height) in warmed.Shapes)
        {
            var shaped = warmed.OfShape(width, height);
            var batch = await warmed.DescribeShapeAsync(width, height, CancellationToken.None);
            for (var i = 0; i < shaped.Count; i++)
            {
                var one = await single.DescribeAsync(shaped[i], CancellationToken.None);
                compared++;
                if ((one is null) != (batch[i] is null) || (one is not null && !one.Values.SequenceEqual(batch[i]!.Values)))
                {
                    differing++;
                }
            }
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"[loot-timing] describe every reference: {warmAll} ms; batch vs single read: {compared} compared, {differing} not bit-identical");

        var loader = new SkiaScreenshotImageLoader();
        foreach (var (path, surface) in frames)
        {
            watch.Restart();
            var image = await loader.LoadAsync(path, CancellationToken.None);
            var decode = watch.ElapsedMilliseconds;
            if (image is null)
            {
                continue;
            }

            watch.Restart();
            var gear = new GearScreenLayoutReader().Read(image);
            var gearMs = watch.ElapsedMilliseconds;
            watch.Restart();
            var lattice = new StashPanelLatticeDetector().Detect(image);
            var latticeMs = watch.ElapsedMilliseconds;

            // A fresh index per frame, so each frame's cold figure is a first scan of a session.
            var builder = new GridPixelReconstructionBuilder(cache, items, new NoOcr(), referenceIndex: new IconReferenceIndex(cache, items));
            watch.Restart();
            var cold = await builder.BuildAsync(image, surface, Now);
            var coldMs = watch.ElapsedMilliseconds;
            watch.Restart();
            var warm = await builder.BuildAsync(image, surface, Now);
            var warmMs = watch.ElapsedMilliseconds;
            var named = warm.OccupiedCells.Count(cell => cell.Item.Value is not null);
            report.AppendLine(CultureInfo.InvariantCulture, $"[loot-timing] {Path.GetFileName(path)[^21..]} {surface}: decode {decode} ms, gear reader {gearMs} ms ({(gear is null ? "none" : "found")}), stash lattice {latticeMs} ms, build cold {coldMs} ms, build warm {warmMs} ms, cells {warm.OccupiedCells.Count}, named {named} (cold named {cold.OccupiedCells.Count(cell => cell.Item.Value is not null)})");
        }

        output.WriteLine(report.ToString());
        Console.WriteLine(report.ToString());
    }

    /// <summary>The cache with its batch read hidden, which is the one-at-a-time path.</summary>
    private sealed class OneAtATime(IIconEvidenceCache inner) : IIconEvidenceCache
    {
        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            inner.StoreAsync(request, cancellationToken);

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken) =>
            inner.GetAsync(key, cancellationToken);

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            inner.ListEvidenceAsync(cancellationToken);
    }

    private sealed class NoOcr : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "loot-timing", IsAvailable: false, DiagnosticCode: "ocr_not_installed"));
    }
}
