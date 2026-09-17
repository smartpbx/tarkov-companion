using System.Diagnostics;
using System.Text.Json;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.RecognitionTests.Grid;

/// <summary>
/// Package 14's benchmark: runs the real pixel-to-grid pipeline over Clayton's local, never-
/// committed screenshot corpus and prints what it measured. Skips cleanly (a passing test with a
/// console note) when the corpus is absent or empty, since it may not exist on every machine.
/// </summary>
/// <remarks>
/// The corpus is raw screenshots with no ground truth by default, so most of what this can report
/// without labels is descriptive: whether a grid was found, how many cells were occupied, how
/// item identity resolved (separated / ambiguous / no local icon evidence), and timing. Dropping
/// an optional <c>&lt;screenshot&gt;.expected.json</c> file next to a screenshot (see
/// <see cref="ExpectedFixture"/>) additionally turns on real grid precision/recall and item
/// top-1/top-3 for that image; nothing here invents labels a screenshot did not carry.
/// </remarks>
public sealed class GridRecognitionCorpusBenchmarkTests
{
    private static readonly DateTimeOffset ObservedUtc = DateTimeOffset.UtcNow;
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    [Fact]
    public async Task ReportsGridRecognitionMetricsOverTheLocalScreenshotCorpus()
    {
        var corpusPath = Environment.GetEnvironmentVariable("TARKOV_RECOGNITION_CORPUS")
            ?? "/root/orca/recognition-corpus";
        if (!Directory.Exists(corpusPath))
        {
            Console.WriteLine($"[grid-benchmark] skipped: corpus directory not found at {corpusPath}.");
            return;
        }

        var images = Directory.EnumerateFiles(corpusPath, "*", SearchOption.AllDirectories)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (images.Length == 0)
        {
            Console.WriteLine($"[grid-benchmark] skipped: no screenshots found under {corpusPath}.");
            return;
        }

        var loader = new SkiaScreenshotImageLoader();
        var builder = new GridPixelReconstructionBuilder(
            new EmptyIconEvidenceCache(),
            new NullItemRepository(),
            new UnavailableOcrEngine());
        var metrics = new BenchmarkMetrics();

        foreach (var path in images)
        {
            var expected = TryLoadExpectedFixture(path);
            var image = await loader.LoadAsync(path, CancellationToken.None);
            if (image is null)
            {
                metrics.RecordUndecodable(path);
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            var request = await builder.BuildAsync(image, InventoryGridSurface.Stash, ObservedUtc, cancellationToken: CancellationToken.None);
            stopwatch.Stop();
            metrics.Record(path, request, expected, stopwatch.Elapsed);
        }

        Console.WriteLine(metrics.BuildReport(corpusPath, images.Length));
        Assert.True(true, "This test reports measurements; it never fails on the corpus's own content.");
    }

    private static ExpectedFixture? TryLoadExpectedFixture(string screenshotPath)
    {
        var sidecarPath = screenshotPath + ".expected.json";
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ExpectedFixture>(File.ReadAllText(sidecarPath));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Hand-authored ground truth a screenshot may optionally carry alongside it.</summary>
    private sealed record ExpectedFixture(
        ExpectedGrid? Grid,
        IReadOnlyList<ExpectedItem>? Items);

    private sealed record ExpectedGrid(int Rows, int Columns);

    private sealed record ExpectedItem(int Row, int Column, string CanonicalId);

    private sealed class BenchmarkMetrics
    {
        private readonly List<string> _lines = [];
        private int _decoded;
        private int _undecodable;
        private int _gridDetected;
        private int _occupiedCells;
        private int _separated;
        private int _ambiguous;
        private int _noLocalEvidence;
        private TimeSpan _totalElapsed;

        private int _gridLabeled;
        private int _gridCorrect;
        private int _itemsLabeled;
        private int _itemsTop1;
        private int _itemsTop3;

        public void RecordUndecodable(string path)
        {
            _undecodable++;
            _lines.Add($"{Path.GetFileName(path)}: could not be decoded");
        }

        public void Record(
            string path,
            GridReconstructionRequest request,
            ExpectedFixture? expected,
            TimeSpan elapsed)
        {
            _decoded++;
            _totalElapsed += elapsed;
            var name = Path.GetFileName(path);
            if (request.Lattice is not { } lattice)
            {
                if (expected?.Grid is not null)
                {
                    _gridLabeled++;
                }

                _lines.Add($"{name}: no grid detected ({elapsed.TotalMilliseconds:F0} ms)");
                return;
            }

            _gridDetected++;
            _occupiedCells += request.OccupiedCells.Count;
            if (expected?.Grid is { } expectedGrid)
            {
                _gridLabeled++;
                if (expectedGrid.Rows == lattice.Rows && expectedGrid.Columns == lattice.Columns)
                {
                    _gridCorrect++;
                }
            }

            var expectedByAnchor = expected?.Items?.ToDictionary(item => (item.Row, item.Column), item => item.CanonicalId);
            foreach (var cell in request.OccupiedCells)
            {
                if (cell.Item.Value is not null)
                {
                    _separated++;
                }
                else if (cell.Item.Candidates.Count > 0)
                {
                    _ambiguous++;
                }
                else
                {
                    _noLocalEvidence++;
                }

                if (expectedByAnchor is null ||
                    !expectedByAnchor.TryGetValue((cell.Anchor.Row, cell.Anchor.Column), out var expectedId))
                {
                    continue;
                }

                _itemsLabeled++;
                if (string.Equals(cell.Item.Value?.CanonicalId.Value, expectedId, StringComparison.Ordinal))
                {
                    _itemsTop1++;
                    _itemsTop3++;
                }
                else if (cell.Item.Candidates.Any(candidate =>
                    string.Equals(candidate.Value.CanonicalId.Value, expectedId, StringComparison.Ordinal)))
                {
                    _itemsTop3++;
                }
            }

            _lines.Add(
                $"{name}: {lattice.Rows}x{lattice.Columns} grid, {request.OccupiedCells.Count} occupied cell(s) " +
                $"({elapsed.TotalMilliseconds:F0} ms)");
        }

        public string BuildReport(string corpusPath, int totalImages)
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine($"[grid-benchmark] {totalImages} screenshot(s) under {corpusPath}:");
            foreach (var line in _lines)
            {
                report.AppendLine("  " + line);
            }

            report.AppendLine($"[grid-benchmark] decoded: {_decoded}/{totalImages}, undecodable: {_undecodable}");
            report.AppendLine($"[grid-benchmark] grid detected: {_gridDetected}/{_decoded}");
            report.AppendLine(_gridLabeled == 0
                ? "[grid-benchmark] grid precision/recall: no labeled screenshots (add <file>.expected.json to measure)"
                : $"[grid-benchmark] grid geometry correct: {_gridCorrect}/{_gridLabeled} labeled screenshot(s)");
            report.AppendLine($"[grid-benchmark] occupied cells: {_occupiedCells} " +
                $"(separated: {_separated}, ambiguous: {_ambiguous}, no local icon evidence: {_noLocalEvidence})");
            report.AppendLine(_itemsLabeled == 0
                ? "[grid-benchmark] item top-1/top-3: no labeled cells (add \"items\" to an .expected.json to measure)"
                : $"[grid-benchmark] item top-1: {_itemsTop1}/{_itemsLabeled}, top-3: {_itemsTop3}/{_itemsLabeled}");
            var ambiguousRateDenominator = _separated + _ambiguous;
            report.AppendLine(ambiguousRateDenominator == 0
                ? "[grid-benchmark] ambiguous rate: no cells with any local icon evidence to separate"
                : $"[grid-benchmark] ambiguous rate: {_ambiguous}/{ambiguousRateDenominator} " +
                  $"({(100.0 * _ambiguous / ambiguousRateDenominator):F1}%)");
            report.Append(_decoded == 0
                ? "[grid-benchmark] timing: no decoded screenshots"
                : $"[grid-benchmark] timing: {_totalElapsed.TotalMilliseconds / _decoded:F0} ms/image average, " +
                  $"{_totalElapsed.TotalMilliseconds:F0} ms total");
            return report.ToString();
        }
    }

    private sealed class EmptyIconEvidenceCache : IIconEvidenceCache
    {
        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The benchmark cache is read-only.");

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken) =>
            Task.FromResult<IconContentEvidenceAsset?>(null);

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IconContentEvidence>>([]);
    }

    private sealed class NullItemRepository : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    private sealed class UnavailableOcrEngine : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new OcrResult([], TimeSpan.Zero, "benchmark-ocr", IsAvailable: false, DiagnosticCode: "benchmark_ocr_not_exercised"));
    }
}
