using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// V1's OCR-anchor detector distinguishes four screen shapes; V2 intents distinguish nine. This
/// tests the honest, documented simplification that stands in until #273 tells container screens
/// apart from pixels: trust the armed intent for a generic container, otherwise map 1:1.
/// </summary>
public sealed class CaptureRecognitionPipelineTests
{
    [Theory]
    [InlineData(ScanContext.SingleItem, RecognizedContext.Item)]
    [InlineData(ScanContext.ExtractList, RecognizedContext.ExtractsAndMap)]
    [InlineData(ScanContext.FleaListings, RecognizedContext.Flea)]
    public void UnambiguousScreensMapOneToOneRegardlessOfArmedIntent(ScanContext detected, RecognizedContext expected)
    {
        foreach (var intent in Enum.GetValues<ScanIntent>())
        {
            Assert.Equal(expected, CaptureRecognitionPipeline.Map(detected, intent));
        }
    }

    [Theory]
    [InlineData(ScanIntent.Loot, RecognizedContext.Loot)]
    [InlineData(ScanIntent.Stash, RecognizedContext.Stash)]
    [InlineData(ScanIntent.Ammo, RecognizedContext.Ammo)]
    [InlineData(ScanIntent.Keys, RecognizedContext.Keys)]
    [InlineData(ScanIntent.QuestItems, RecognizedContext.QuestItems)]
    public void AGenericContainerTrustsTheArmedIntentForItsGridSubtype(ScanIntent intent, RecognizedContext expected)
    {
        Assert.Equal(expected, CaptureRecognitionPipeline.Map(ScanContext.Container, intent));
    }

    [Theory]
    [InlineData(ScanIntent.Auto)]
    [InlineData(ScanIntent.ExtractsAndMap)]
    [InlineData(ScanIntent.HealthAndCharacter)]
    [InlineData(ScanIntent.Flea)]
    public void AGenericContainerWithNoContainerShapedIntentReportsAnUnspecifiedGrid(ScanIntent intent)
    {
        Assert.Equal(RecognizedContext.Grid, CaptureRecognitionPipeline.Map(ScanContext.Container, intent));
    }

    /// <summary>
    /// Nobody looting a container arms an intent first, so an unarmed container screen during a
    /// raid is measured as loot. Outside a raid it may be the stash, and only an armed intent says.
    /// </summary>
    [Theory]
    [InlineData(ScanIntent.Auto, ScanContext.Container, true, InventoryGridSurface.VisibleLoot)]
    [InlineData(ScanIntent.Auto, ScanContext.Container, false, null)]
    [InlineData(ScanIntent.Auto, ScanContext.SingleItem, true, null)]
    [InlineData(ScanIntent.Loot, ScanContext.Unknown, false, InventoryGridSurface.VisibleLoot)]
    [InlineData(ScanIntent.Stash, ScanContext.Container, true, InventoryGridSurface.Stash)]
    [InlineData(ScanIntent.Flea, ScanContext.Container, true, null)]
    public void AGridIsMeasuredForAnArmedGridIntentOrAnUnarmedContainerInRaid(
        ScanIntent intent,
        ScanContext detected,
        bool inRaid,
        InventoryGridSurface? expected)
    {
        Assert.Equal(expected, CaptureRecognitionPipeline.GridSurfaceFor(intent, detected, inRaid));
    }

    /// <summary>
    /// "Analyse as armed" on a screen nobody could place was refused by intake as "context
    /// unknown, no change". A measured lattice under an armed Loot or Stash places the screen.
    /// </summary>
    [Theory]
    [InlineData(ScanIntent.Loot, true, RecognizedContext.Loot)]
    [InlineData(ScanIntent.Stash, true, RecognizedContext.Stash)]
    [InlineData(ScanIntent.Loot, false, null)]
    [InlineData(ScanIntent.Auto, true, null)]
    [InlineData(ScanIntent.Flea, true, null)]
    public void AnUnplacedScreenWithAMeasuredLatticeIsPlacedByTheArmedGridIntent(
        ScanIntent intent,
        bool latticeMeasured,
        RecognizedContext? expected)
    {
        var placed = CaptureRecognitionPipeline.PlaceFromLattice(null, true, new(0), latticeMeasured, intent);

        Assert.Equal(expected, placed.Context);
        Assert.Equal(expected is null, placed.IsAmbiguous);
        Assert.Equal(expected is null ? 0 : RecognitionThresholds.Ambiguous, placed.Confidence.Value);
    }

    [Fact]
    public void AScreenTheTextDetectorPlacedIsLeftAsItWasRead()
    {
        var placed = CaptureRecognitionPipeline.PlaceFromLattice(RecognizedContext.Flea, false, new(0.93), true, ScanIntent.Loot);

        Assert.Equal(RecognizedContext.Flea, placed.Context);
        Assert.Equal(0.93, placed.Confidence.Value);
    }
}
