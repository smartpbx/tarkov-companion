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
        var lowContrast = new SyntheticStashFrameOptions(CellLuminance: 38, LineLuminance: 50);
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

        // The floors these fixes reached. They ratchet: a change that loses one has to explain it.
        var identityOnly = results[0];
        var appToday = results[1];
        var catalogue = results[2];
        var inGame = results[3];
        var inGameLowContrast = results[4];
        Assert.All(results, result => Assert.Equal(3, result.LatticesExact));
        Assert.All(results, result => Assert.Equal(result.TruthFootprintsVisible, result.FootprintsFound));
        Assert.Equal(1, identityOnly.RegionsPlaced);
        Assert.Equal(3, appToday.RegionsPlacedCorrectly);
        Assert.Equal(0, appToday.ReconstructedOccurrences);
        Assert.True(appToday.UnknownTiles >= layout.Placements.Count, "every item the recognizer cannot name stays on the grid as unknown");

        // The shipped separator names a tile only on a bit-exact fingerprint, and a catalogue
        // icon is never bit-exact with the game's own drawing of it.
        Assert.Equal(0, catalogue.Identified);

        foreach (var named in new[] { inGame, inGameLowContrast })
        {
            Assert.Equal(3, named.RegionsPlacedCorrectly);
            Assert.Equal(0, named.ReconstructedWrong);
            Assert.True(named.Recall >= 0.95, $"recall fell to {named.Recall:P1}");
            Assert.True(named.DoubleCounted > 0, "the overlap rows are read twice, which is what the folded grid exists to absorb");
            var truthCounts = layout.Placements.GroupBy(placement => placement.Item.ItemId).ToDictionary(group => group.Key, group => group.Count());
            Assert.All(named.Reconstruction.OwnedCounts, pair => Assert.True(pair.Value <= truthCounts[pair.Key], $"{pair.Key} was counted {pair.Value} times against {truthCounts[pair.Key]} in the stash"));
        }
    }
}
