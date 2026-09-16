using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
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
        : this(largeText: false, lootOffline: false)
    {
    }

    public MapSceneRendererGalleryWindow(bool largeText)
        : this(largeText, lootOffline: false)
    {
    }

    public MapSceneRendererGalleryWindow(bool largeText, bool lootOffline)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = MapSceneRendererGalleryViewModel.Create(largeText, lootOffline);
    }
}

public sealed record MapSceneRendererGalleryViewModel(
    MapSceneRendererViewModel Renderer,
    double InterfaceScale)
{
    private static readonly DateTimeOffset ObservedUtc =
        new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);

    public static MapSceneRendererGalleryViewModel Create(bool largeText, bool lootOffline = false)
    {
        var presentation = MapSceneRendererPresentation.English(
            CultureInfo.GetCultureInfo("en-US"),
            TimeZoneInfo.Utc);
        var lootSnapshot = LootSnapshot();
        var lootService = new HighValueLootLayerService();
        var initialFilter = HighValueLootLayerFilterState.Default;
        var initialLoot = lootOffline
            ? OfflineLoot(initialFilter.Filter)
            : BuildLoot(lootService, lootSnapshot, initialFilter.Filter);
        var renderer = new MapSceneRendererViewModel(
            Scene(initialLoot),
            presentation,
            highValueLoot: initialLoot,
            highValueLootFilterState: initialFilter,
            highValueLootCategories: ["electronics", "medical"]);
        renderer.ViewChangeRequested += change =>
        {
            var result = MapSceneViewReducer.Apply(renderer.Scene, change);
            if (result.Status is MapSceneViewChangeStatus.Applied or MapSceneViewChangeStatus.Unchanged)
            {
                renderer.Present(result.Scene);
            }
        };
        renderer.HighValueLootFilterRequested += request =>
        {
            if (request.ExpectedRevision != renderer.Scene.Revision ||
                !string.Equals(request.LocationId, renderer.Scene.LocationId, StringComparison.Ordinal) ||
                !string.Equals(request.TransformVersion, renderer.Scene.TransformVersion, StringComparison.Ordinal))
            {
                return;
            }

            var filterState = request.State;
            if (lootOffline)
            {
                var unavailable = OfflineLoot(filterState.Filter);
                renderer.Present(
                    ReplaceLoot(renderer.Scene, unavailable),
                    unavailable,
                    filterState,
                    ["electronics", "medical"]);
                return;
            }

            var result = BuildLoot(lootService, lootSnapshot, filterState.Filter);
            renderer.Present(ReplaceLoot(renderer.Scene, result), result, filterState, ["electronics", "medical"]);
        };
        return new(renderer, largeText ? 2 : 1);
    }

    private static MapSceneSnapshot Scene(HighValueLootLayerResult loot)
    {
        var reference = new MapSceneLayer(new("extracts"), "Extracts", 10, true);
        var observations = new MapSceneLayer(new("companion-markers"), "Companion markers", 20, true);
        var hazards = new MapSceneLayer(new("hazards"), "Hazards", 25, true);
        var estimates = new MapSceneLayer(new("estimates"), "Historical estimates", 30, true);
        var objects = loot.Objects.Concat(
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
            Point("hazard:water", hazards.Id, MapSceneObjectKind.Risk, MapSceneTruthKind.StaticReference,
                "Deep water hazard", 88, 64),
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
                [
                    new(reference.Id, true),
                    new(observations.Id, true),
                    new(hazards.Id, true),
                    new(estimates.Id, true),
                    new(loot.Layer.Id, true),
                ]),
            [reference, observations, hazards, estimates, loot.Layer],
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

    private static MapSceneSnapshot ReplaceLoot(MapSceneSnapshot scene, HighValueLootLayerResult loot) => new(
        scene.Revision + 1,
        scene.LocationId,
        scene.VariantKey,
        scene.TransformVersion,
        scene.Bounds,
        scene.FloorIds,
        scene.Capabilities,
        scene.View,
        scene.Layers.Where(layer => layer.Id != HighValueLootLayerService.LayerId).Append(loot.Layer).ToArray(),
        scene.Objects.Where(item => item.LayerId != HighValueLootLayerService.LayerId).Concat(loot.Objects).ToArray(),
        scene.Assets);

    private static HighValueLootLayerResult BuildLoot(
        HighValueLootLayerService service,
        LootSpawnSnapshot snapshot,
        HighValueLootFilter filter) => service.Build(new(
        "renderer-gallery",
        "gallery-transform-1",
        new(0, 0, 100, 100),
        ObservedUtc,
        filter,
        snapshot,
        ["ground", "upper"]));

    private static HighValueLootLayerResult OfflineLoot(HighValueLootFilter filter) => new(
        HighValueLootLayerService.Layer,
        "renderer-gallery",
        "gallery-transform-1",
        filter,
        new(ResultCompleteness.Unavailable, FreshnessState.Unknown, "snapshot.offline-unavailable"),
        "Potential spawns · Offline data unavailable",
        null,
        null,
        [],
        [],
        [new(
            HighValueLootDiagnosticKind.SnapshotUnavailable,
            "snapshot.offline-unavailable",
            "No last-known-good loot-spawn snapshot is available while offline.")]);

    private static LootSpawnSnapshot LootSnapshot()
    {
        var dense = Enumerable.Range(0, 305)
            .Select(index => LootRecord(
                $"dense-{index:D3}",
                $"Potential loot {index + 1}",
                new(LootSpawnPrecision.ExactPoint, [new(18, 18)], ["ground"]),
                Candidate($"item-{index:D3}", $"Electronic part {index + 1}", "electronics", 100_000)))
            .ToArray();
        var records = dense.Concat(
        [
            LootRecord(
                "medical-exceptional",
                "Exceptional medical shelf",
                new(LootSpawnPrecision.BoundedArea, [new(68, 60), new(76, 60), new(76, 68), new(68, 68)], ["ground"]),
                Candidate("ledx", "LEDX skin transilluminator", "medical", 650_000),
                "Requires access to the sourced room."),
            LootRecord(
                "map-only-cache",
                "Map-only medical cache",
                new(LootSpawnPrecision.MapOnly, null),
                Candidate("medical-case", "Medical storage case", "medical", 550_000)),
            LootRecord(
                "floor-unresolved",
                "Floor-unresolved electronics table",
                new(LootSpawnPrecision.ExactPoint, [new(46, 30)]),
                Candidate("gpu", "Graphics card", "electronics", 420_000)),
            LootRecord(
                "profile-only-unknown-value",
                "Pinned quest shelf",
                new(LootSpawnPrecision.RoomOrRegion, [new(20, 66), new(30, 66), new(30, 75), new(20, 75)], ["upper"]),
                UnknownCandidate(
                    "quest-tool",
                    "Quest tool",
                    "electronics",
                    new(
                        LootSpawnProfileNeedKind.UserPin,
                        "gallery-pin",
                        "Pinned for the active profile",
                        CompleteStatus,
                        UserProvenance("gallery-pin"))))
        ]).ToArray();
        var positioned = records.Count(record => record.Location.Geometry is not null);
        var floorResolved = records.Count(record =>
            record.Location.Geometry is not null && record.Location.FloorIds.Count > 0);
        return new(
            "gallery-loot-snapshot",
            "gallery-loot-1",
            "renderer-gallery",
            "gallery-transform-1",
            ObservedUtc,
            new(ResultCompleteness.Partial, FreshnessState.Current, "gallery.partial"),
            new(records.Length, positioned, floorResolved, records.Length - positioned),
            EvidenceProvenance("gallery-loot-snapshot"),
            records);
    }

    private static LootSpawnRecord LootRecord(
        string id,
        string label,
        LootSpawnLocation location,
        LootSpawnCandidate candidate,
        string? accessNote = null) => new(
        id,
        "renderer-gallery",
        label,
        location,
        LootSpawnPoolKind.SingleKnownItem,
        [candidate],
        Unknown<double?>($"{id}-probability"),
        Unknown<string?>($"{id}-respawn"),
        "gallery-loot-1",
        "gallery-transform-1",
        CompleteStatus,
        EvidenceProvenance($"gallery-{id}"),
        accessNote);

    private static LootSpawnCandidate Candidate(
        string id,
        string name,
        string category,
        long value) => new(
        id,
        name,
        category,
        Complete<long?>($"{id}-gross", value + 25_000),
        Complete<long?>($"{id}-net", value),
        Complete<long?>($"{id}-trader", value / 2),
        Complete<int?>($"{id}-squares", 2));

    private static LootSpawnCandidate UnknownCandidate(
        string id,
        string name,
        string category,
        LootSpawnProfileNeed need) => new(
        id,
        name,
        category,
        Unknown<long?>($"{id}-gross"),
        Unknown<long?>($"{id}-net"),
        Unknown<long?>($"{id}-trader"),
        Complete<int?>($"{id}-squares", 2),
        [need]);

    private static ResultStatus CompleteStatus { get; } = new(
        ResultCompleteness.Complete,
        FreshnessState.Current);

    private static EvidencedValue<T> Complete<T>(string id, T value) => new(
        id,
        value,
        CompleteStatus,
        EvidenceProvenance(id));

    private static EvidencedValue<T> Unknown<T>(string id) => new(
        id,
        default,
        new(ResultCompleteness.Unknown, FreshnessState.Current),
        EvidenceProvenance(id));

    private static EvidenceProvenance EvidenceProvenance(string id) => new(
        EvidenceSourceClass.PublicStructuredData,
        $"fixture://{id}",
        ObservedUtc,
        new(EvidenceConfidenceKind.ProviderScore, 0.95),
        new("map-renderer-gallery", "1"));

    private static EvidenceProvenance UserProvenance(string id) => new(
        EvidenceSourceClass.UserEntered,
        $"local://{id}",
        ObservedUtc,
        EvidenceConfidence.Unscored,
        new("map-renderer-gallery", "1"));

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
