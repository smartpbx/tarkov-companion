using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2Raid;

public sealed class RaidCockpitExtractRowsTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExtractRowsListOfferedFirstThenUnknownThenNotOfferedAndSkipOtherObjects()
    {
        var objects = new[]
        {
            Object("a", MapSceneObjectKind.Extract, "ZB-1011", MapFeatureFaction.Pmc, MapSceneOfferState.NotOffered),
            Object("b", MapSceneObjectKind.Extract, "Old Gas Station", MapFeatureFaction.Scav, MapSceneOfferState.Unknown),
            Object("c", MapSceneObjectKind.Extract, "RUAF Roadblock", MapFeatureFaction.Shared, MapSceneOfferState.Offered),
            Object("d", MapSceneObjectKind.Transit, "Transit to Factory", MapFeatureFaction.Unknown, MapSceneOfferState.Unknown),
            Object("e", MapSceneObjectKind.SpawnArea, "Big Red", MapFeatureFaction.Pmc, MapSceneOfferState.Unknown),
        };

        var rows = RaidCockpitViewModel.BuildExtractRows(objects);

        Assert.Equal(["RUAF Roadblock", "Old Gas Station", "Transit to Factory", "ZB-1011"], rows.Select(row => row.Name));
        Assert.True(rows[0].IsOffered);
        Assert.Equal("Offered", rows[0].OfferLabel);
        Assert.Equal("PMC · Scav", rows[0].Detail);
        Assert.False(rows[1].HasOfferLabel);
        Assert.Equal("Transit", rows[2].Detail);
        Assert.True(rows[3].IsNotOffered);
        Assert.Equal("Not offered", rows[3].OfferLabel);
    }

    [Fact]
    public void ADuplicatedExtractLabelIsListedOnceKeepingItsOfferedCopy()
    {
        var objects = new[]
        {
            Object("a", MapSceneObjectKind.Extract, "Crossroads", MapFeatureFaction.Pmc, MapSceneOfferState.Unknown),
            Object("b", MapSceneObjectKind.Extract, "Crossroads", MapFeatureFaction.Pmc, MapSceneOfferState.Offered),
        };

        var row = Assert.Single(RaidCockpitViewModel.BuildExtractRows(objects));

        Assert.True(row.IsOffered);
    }

    private static MapSceneObject Object(
        string id,
        MapSceneObjectKind kind,
        string label,
        MapFeatureFaction faction,
        MapSceneOfferState offerState) => new(
        new(id),
        new("extracts"),
        kind,
        MapSceneTruthKind.StaticReference,
        label,
        null,
        MapSceneGeometry.At(new(1, 1)),
        [],
        new DataProvenance("test", NowUtc),
        faction: faction,
        offerState: offerState);
}
