using TarkovCompanion.StashScanFixtures;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.StashScan;

/// <summary>
/// Package 40's measurement: the guided stash path run end to end over a painted stash whose
/// truth is known, reported as numbers. See <see cref="StashScanMeasurement"/> for what these
/// frames can and cannot say about the real game.
/// </summary>
public sealed class StashScanEndToEndMeasurementTests(ITestOutputHelper output)
{
    /// <summary>A 34-row stash needs three 1080p screens: rows 0-13, 10-23 and 20-33.</summary>
    private static readonly int[] ThreeScreens = [0, 10, 20];

    [Fact]
    public async Task ReportsTheGuidedPathOverAPackedStash()
    {
        var layout = SyntheticStashLayout.Build(rows: 34);
        var lowContrast = new SyntheticStashFrameOptions(CellLuminance: 60, LineLuminance: 30);
        var results = new[]
        {
            await StashScanMeasurement.RunAsync("icon cache empty (the app today) · identity stitch only", layout, ThreeScreens, new(), StashIconReferences.None, layoutStitch: false),
            await StashScanMeasurement.RunAsync("icon cache empty (the app today) · layout stitch", layout, ThreeScreens, new(), StashIconReferences.None),
            await StashScanMeasurement.RunAsync("catalogue-icon references · layout stitch", layout, ThreeScreens, new(), StashIconReferences.CatalogueIcons),
            await StashScanMeasurement.RunAsync("in-game tile references · layout stitch", layout, ThreeScreens, new(), StashIconReferences.InGameTiles),
            await StashScanMeasurement.RunAsync("in-game tile references · low line contrast", layout, ThreeScreens, lowContrast, StashIconReferences.InGameTiles),
        };
        foreach (var result in results)
        {
            output.WriteLine(result.Describe());
            Console.WriteLine(result.Describe());
        }

        var nearMatch = StashScanMeasurement.DescribeNearMatch(layout, new(), maximumDistance: 12, minimumGap: 4);
        output.WriteLine(nearMatch);
        Console.WriteLine(nearMatch);
    }
}
