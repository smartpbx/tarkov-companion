using System.Globalization;
using System.Text;
using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.StashScan;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// #712 1-1: what the screen detectors decide on a folder of real screenshots, with nothing armed.
/// </summary>
/// <remarks>
/// Skips, passing, unless <c>TARKOV_ROUTING_CORPUS</c> names a folder. The owner's screenshots
/// carry his identity and are never committed; this reads them where they are. Off Windows there
/// is no OCR, so every anchor score is zero here and only the evidence read from pixels and names
/// (the loot and stash lattices, the position in the name) is real: the counts are what the
/// detectors decide without text, not what they decide on Windows.
/// </remarks>
public sealed class RealCorpusRoutingMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReportsRoutingCountsOnARealFolder()
    {
        if (Environment.GetEnvironmentVariable("TARKOV_ROUTING_CORPUS") is not { Length: > 0 } folder || !Directory.Exists(folder))
        {
            output.WriteLine("[routing-real] skipped: TARKOV_ROUTING_CORPUS is not set.");
            return;
        }

        var paths = Directory.EnumerateFiles(folder, "*.png").OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var loader = new SkiaScreenshotImageLoader();
        var builder = new GridPixelReconstructionBuilder(
            new StashScanMeasurement.FixedIconEvidenceCache([]),
            new StashScanMeasurement.FixedItemRepository(new Dictionary<string, TarkovCompanion.Core.Domain.Items.ItemDefinition>()),
            new StashScanMeasurement.UnavailableOcrEngine());
        var runner = new ScreenDetectorRunner();
        var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
        int positionNames = 0, lootLattices = 0, stashLattices = 0, undecoded = 0;
        foreach (var path in paths)
        {
            var image = await loader.LoadAsync(path, CancellationToken.None);
            if (image is null)
            {
                undecoded++;
                continue;
            }

            var named = ScreenshotFilenameParser.Classify(path) == ScreenshotNameKind.InRaid;
            var loot = await builder.BuildAsync(image, InventoryGridSurface.VisibleLoot, DateTimeOffset.UtcNow, cancellationToken: CancellationToken.None);
            var stash = await builder.BuildAsync(image, InventoryGridSurface.Stash, DateTimeOffset.UtcNow, cancellationToken: CancellationToken.None);
            positionNames += named ? 1 : 0;
            lootLattices += loot.Lattice is null ? 0 : 1;
            stashLattices += stash.Lattice is null ? 0 : 1;

            // The pipeline measures the loot lattice in raid and the stash lattice out of one.
            var routing = runner.Decide(new(
                new Dictionary<ScanContext, double>(),
                InRaid: named,
                LootLatticeMeasured: named && loot.Lattice is not null,
                StashLatticeMeasured: !named && stash.Lattice is not null,
                NameCarriesPosition: named));
            var key = routing.Kind is { } kind ? $"{routing.Outcome}:{kind}" : routing.Outcome.ToString();
            outcomes[key] = outcomes.GetValueOrDefault(key) + 1;
        }

        var report = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"[routing-real] frames={paths.Length} undecoded={undecoded} positionNames={positionNames} lootLattice={lootLattices} stashLattice={stashLattices}");
        foreach (var (key, count) in outcomes.OrderByDescending(pair => pair.Value))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"[routing-real] {key}={count}");
        }

        output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("TARKOV_ROUTING_REPORT") is { Length: > 0 } reportPath)
        {
            await File.WriteAllTextAsync(reportPath, report.ToString());
        }
    }
}
