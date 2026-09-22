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

    /// <summary>
    /// [Issue 594] "Clicking an extract or transit doesn't tell me what one it is." Pressing a row
    /// is what selects and centres the extract's own marker on the map; this is the row's half of
    /// that wiring, without a live map or a click.
    /// </summary>
    [Fact]
    public void A_rows_SelectCommand_reports_its_own_scene_id_position_and_name()
    {
        var objects = new[]
        {
            Object("ruaf", MapSceneObjectKind.Extract, "RUAF Roadblock", MapFeatureFaction.Shared, MapSceneOfferState.Offered),
        };
        (MapSceneObjectId Id, MapScenePoint Point, string Name)? selected = null;

        var row = Assert.Single(RaidCockpitViewModel.BuildExtractRows(objects, (id, point, name) => selected = (id, point, name)));
        row.SelectCommand.Execute(null);

        Assert.NotNull(selected);
        Assert.Equal(new MapSceneObjectId("ruaf"), selected!.Value.Id);
        Assert.Equal(new MapScenePoint(1, 1), selected.Value.Point);
        Assert.Equal("RUAF Roadblock", selected.Value.Name);
    }

    /// <summary>A row with no select callback (the direct-coverage tests above) still has a
    /// harmless command rather than none, so nothing bound to it throws.</summary>
    [Fact]
    public void A_rows_SelectCommand_is_never_null_even_without_a_callback()
    {
        var objects = new[] { Object("a", MapSceneObjectKind.Transit, "Transit to Factory", MapFeatureFaction.Unknown, MapSceneOfferState.Unknown) };

        var row = Assert.Single(RaidCockpitViewModel.BuildExtractRows(objects));

        Assert.NotNull(row.SelectCommand);
        row.SelectCommand.Execute(null);
    }

    [Fact]
    public void Rows_built_again_from_the_same_extracts_read_the_same_and_a_changed_offer_does_not()
    {
        // [#453] The cockpit keeps the list it shows when a rebuild's rows read the same, so the
        // panel does not rebuild every row's controls three times a second.
        MapSceneObject[] Objects(MapSceneOfferState ruaf) =>
        [
            Object("a", MapSceneObjectKind.Extract, "ZB-1011", MapFeatureFaction.Pmc, MapSceneOfferState.NotOffered),
            Object("c", MapSceneObjectKind.Extract, "RUAF Roadblock", MapFeatureFaction.Shared, ruaf),
        ];

        var shown = RaidCockpitViewModel.BuildExtractRows(Objects(MapSceneOfferState.Unknown));

        Assert.True(RaidExtractRowViewModel.ReadSame(shown, RaidCockpitViewModel.BuildExtractRows(Objects(MapSceneOfferState.Unknown))));
        Assert.False(RaidExtractRowViewModel.ReadSame(shown, RaidCockpitViewModel.BuildExtractRows(Objects(MapSceneOfferState.Offered))));
        Assert.False(RaidExtractRowViewModel.ReadSame(shown, [shown[0]]));
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
