using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Views.V2.MapRenderer;

/// <summary>A packaged, deterministic host for renderer UIA, touch-target, and responsive evidence.</summary>
public sealed partial class MapSceneRendererGalleryWindow : Window
{
    // Avalonia's runtime XAML loader resolves the compiled resource through a public default
    // constructor. Keep the option-bearing overload for the packaged large-text scenario, but
    // do not make that verification-only option hide the window from the loader.
    public MapSceneRendererGalleryWindow()
        : this(largeText: false)
    {
    }

    public MapSceneRendererGalleryWindow(bool largeText)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = MapSceneRendererGalleryViewModel.Create(largeText);
    }
}

public sealed record MapSceneRendererGalleryViewModel(
    MapSceneRendererViewModel Renderer,
    double InterfaceScale)
{
    private static readonly DateTimeOffset ObservedUtc =
        new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);

    public static MapSceneRendererGalleryViewModel Create(bool largeText)
    {
        var presentation = MapSceneRendererPresentation.English(
            CultureInfo.GetCultureInfo("en-US"),
            TimeZoneInfo.Utc);
        var renderer = new MapSceneRendererViewModel(Scene(), presentation);
        renderer.ViewChangeRequested += change =>
        {
            var result = MapSceneViewReducer.Apply(renderer.Scene, change);
            if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
            {
                renderer.Present(result.Scene);
            }
        };
        return new(renderer, largeText ? 2 : 1);
    }

    private static MapSceneSnapshot Scene()
    {
        var reference = new MapSceneLayer(new("reference"), "Reference", 10, true);
        var observations = new MapSceneLayer(new("observations"), "Observations", 20, true);
        var estimates = new MapSceneLayer(new("estimates"), "Historical estimates", 30, true);
        var dense = Enumerable.Range(0, 305)
            .Select(index => Point(
                $"dense:{index:D3}",
                reference.Id,
                MapSceneObjectKind.LootSpawn,
                MapSceneTruthKind.PotentialSpawn,
                $"Potential loot {index + 1}",
                18,
                18))
            .ToArray();
        var objects = dense.Concat(
        [
            Point("extract:pmc", reference.Id, MapSceneObjectKind.Extract, MapSceneTruthKind.StaticReference,
                "PMC extract offered", 72, 22, MapFeatureFaction.Pmc, MapSceneOfferState.Offered),
            Point("extract:scav", reference.Id, MapSceneObjectKind.Extract, MapSceneTruthKind.StaticReference,
                "Scav extract not offered", 82, 34, MapFeatureFaction.Scav, MapSceneOfferState.NotOffered),
            Point("extract:shared", reference.Id, MapSceneObjectKind.Extract, MapSceneTruthKind.StaticReference,
                "Shared extract status unknown", 78, 48, MapFeatureFaction.Shared),
            Point("local:last", observations.Id, MapSceneObjectKind.LastKnownPosition, MapSceneTruthKind.LocalLastKnown,
                "My last recorded position", 48, 70),
            Point("team:last", observations.Id, MapSceneObjectKind.TeammateLastKnown, MapSceneTruthKind.TeamSharedLastKnown,
                "Team-shared last recorded position", 62, 76),
            HistoricalTraffic(estimates.Id),
            new(
                new("route:plan"),
                reference.Id,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.PersonalPlan,
                "Planned route",
                "User-authored route for the packaged renderer fixture.",
                new(MapSceneGeometryKind.Line, [new(12, 84), new(40, 58), new(88, 86)]),
                ["ground"],
                Provenance()),
        ]).ToArray();
        return new(
            1,
            "renderer-gallery",
            "reviewed-plan",
            "gallery-transform-1",
            new(0, 0, 100, 100),
            ["ground", "upper"],
            new(
                MapSceneCapability.Available,
                MapSceneCapability.Available,
                MapSceneCapability.Unavailable("The gallery has no reviewed interior model.")),
            new(
                MapSceneMode.Flat2D,
                "ground",
                new(50, 50, 1, 0, 0),
                [new(reference.Id, true), new(observations.Id, true), new(estimates.Id, true)]),
            [reference, observations, estimates],
            objects,
            [new(
                new("asset:gallery-plan"),
                MapSceneAssetKind.Background2D,
                new("https://example.test/maps/gallery.svg"),
                new("https://example.test/licence"),
                new string('a', 64),
                "Packaged renderer fixture",
                "map-1",
                "game-1",
                MapSceneAssetReviewStatus.Reviewed,
                ObservedUtc)]);
    }

    private static MapSceneObject HistoricalTraffic(MapSceneLayerId layerId) => new(
        new("traffic:historical"),
        layerId,
        MapSceneObjectKind.Traffic,
        MapSceneTruthKind.HistoricalEstimate,
        "Historical traffic estimate",
        "Historical route frequency; not a live detection.",
        MapSceneGeometry.At(new(34, 46)),
        ["ground"],
        Provenance(0.7),
        new(
            ObservedUtc.AddDays(-30),
            ObservedUtc.AddDays(-2),
            ObservedUtc.AddDays(-1),
            "fixture sample",
            "uncalibrated",
            "gallery-transform-1",
            "gallery-model-1"));

    private static MapSceneObject Point(
        string id,
        MapSceneLayerId layerId,
        MapSceneObjectKind kind,
        MapSceneTruthKind truth,
        string label,
        double x,
        double y,
        MapFeatureFaction faction = MapFeatureFaction.Unknown,
        MapSceneOfferState offer = MapSceneOfferState.Unknown) => new(
            new(id),
            layerId,
            kind,
            truth,
            label,
            "Packaged renderer fixture record.",
            MapSceneGeometry.At(new(x, y)),
            ["ground"],
            Provenance(),
            faction: faction,
            offerState: offer);

    private static DataProvenance Provenance(double confidence = 1) => new(
        "packaged-fixture",
        ObservedUtc,
        Confidence: new Confidence(confidence));
}
