using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [Issue 594] "Clicking an extract or transit doesn't tell me what one it is, either in the
/// sidebar or on the map, so there is no way to know what is what." Selecting a marker is what
/// the Raid page's "Selected" card and the map's own name label read from
/// (<see cref="MapSceneRendererViewModel.SelectedObject"/>); this proves that mapping for the
/// three shapes an extract comes in — a plain PMC one with a condition, a shared/co-op one, and a
/// transit — without needing the real app or a screenshot.
/// </summary>
public sealed class MapExtractSelectionDetailTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Selecting_a_pmc_extract_with_a_requirement_exposes_its_name_and_condition()
    {
        var renderer = Renderer(
            Extract("zb-1011", "ZB-1011", MapFeatureFaction.Pmc, "Requires a paracord and a screwdriver"));

        renderer.SelectObject(new("extract:zb-1011"));

        var selected = renderer.SelectedObject;
        Assert.NotNull(selected);
        Assert.Equal("ZB-1011", selected!.Label);
        Assert.Equal("Requires a paracord and a screwdriver", selected.Detail);
        Assert.True(selected.HasDetail);
        Assert.Equal("PMC", selected.FactionLabel);
        Assert.True(selected.IsExtractIcon);
        Assert.True(selected.IsExtractOrTransit);
        Assert.True(selected.ShowsSelectedName);
    }

    [Fact]
    public void Selecting_a_co_op_extract_exposes_who_shares_it()
    {
        var renderer = Renderer(
            Extract("cliff", "Cliff Descent", MapFeatureFaction.Shared, "Co-op · needs a friendly Scav to open the gate"));

        renderer.SelectObject(new("extract:cliff"));

        var selected = renderer.SelectedObject;
        Assert.NotNull(selected);
        Assert.Equal("Cliff Descent", selected!.Label);
        Assert.Contains("PMC", selected.FactionLabel);
        Assert.Contains("Scav", selected.FactionLabel);
        Assert.Contains("friendly Scav", selected.Detail);
        Assert.True(selected.IsExtractOrTransit);
        Assert.True(selected.ShowsSelectedName);
    }

    [Fact]
    public void Selecting_a_transit_exposes_where_it_leads_and_not_an_extract_faction()
    {
        var renderer = Renderer(
            new MapSceneObject(
                new("transit:factory"),
                new("extracts"),
                MapSceneObjectKind.Transit,
                MapSceneTruthKind.StaticReference,
                "Transit to Factory",
                "Leads to Factory",
                MapSceneGeometry.At(new(10, 10)),
                [],
                Provenance()));

        renderer.SelectObject(new("transit:factory"));

        var selected = renderer.SelectedObject;
        Assert.NotNull(selected);
        Assert.Equal("Transit to Factory", selected!.Label);
        Assert.Equal("Leads to Factory", selected.Detail);
        Assert.True(selected.IsTransitIcon);
        Assert.True(selected.IsExtractOrTransit);
        Assert.True(selected.ShowsSelectedName);
    }

    [Fact]
    public void An_unselected_extract_does_not_show_its_name_over_the_map()
    {
        var renderer = Renderer(Extract("zb-1011", "ZB-1011", MapFeatureFaction.Pmc, null));

        var marker = renderer.SpatialObjects.Single();

        Assert.False(marker.ShowsSelectedName);

        renderer.SelectObject(new("extract:zb-1011"));

        Assert.True(renderer.SpatialObjects.Single().ShowsSelectedName);
    }

    private static MapSceneObject Extract(string id, string label, MapFeatureFaction faction, string? detail) => new(
        new($"extract:{id}"),
        new("extracts"),
        MapSceneObjectKind.Extract,
        MapSceneTruthKind.StaticReference,
        label,
        detail,
        MapSceneGeometry.At(new(10, 10)),
        [],
        Provenance(),
        faction: faction,
        offerState: MapSceneOfferState.Offered);

    private static DataProvenance Provenance() => new("fixture", NowUtc, Confidence: new Confidence(1));

    private static MapSceneRendererViewModel Renderer(params MapSceneObject[] objects)
    {
        var layer = new MapSceneLayer(new("extracts"), "Extracts", 5, true);
        var scene = new MapSceneSnapshot(
            1,
            "customs",
            "customs",
            "customs",
            new MapSceneBounds(0, 0, 400, 300),
            [],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
            new(MapSceneMode.Flat2D, null, new(200, 150, 1, 0, 0), [new(layer.Id, true)]),
            [layer],
            objects,
            []);
        return new MapSceneRendererViewModel(
            scene,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            showsDetailsPanel: false);
    }
}
