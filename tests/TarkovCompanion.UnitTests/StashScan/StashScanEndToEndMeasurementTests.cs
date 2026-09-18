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
        var results = new[]
        {
            await StashScanMeasurement.RunAsync("packed stash · icon cache fed", layout, ThreeScreens, new(), feedIconCache: true),
            await StashScanMeasurement.RunAsync("packed stash · icon cache empty (the app today)", layout, ThreeScreens, new(), feedIconCache: false),
            await StashScanMeasurement.RunAsync("packed stash · low line contrast · icon cache fed", layout, ThreeScreens, new(CellLuminance: 60, LineLuminance: 30), feedIconCache: true),
        };
        foreach (var result in results)
        {
            output.WriteLine(result.Describe());
            Console.WriteLine(result.Describe());
        }
    }
}
