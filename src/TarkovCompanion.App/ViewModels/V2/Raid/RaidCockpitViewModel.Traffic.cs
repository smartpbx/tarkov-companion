using System.Globalization;
using Avalonia.Media.Imaging;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [Issue 286] The modelled-traffic half of the cockpit: the prior, its picture, and what it says
/// about itself.
/// </summary>
/// <remarks>
/// The plan card read "no installed model yet" on every machine, because the only traffic source
/// was a governed snapshot of recorded raids and the project has none to publish. What every
/// machine does have is the map's own structure, so that is what is modelled and that is what it
/// is called, everywhere it appears: a prior from map structure, never a recorded or live thing.
/// An installed governed snapshot still takes the plan card's rows when there is one.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private static readonly MapSceneLayerId TrafficLayerId = MapSceneRendererViewModel.TrafficHeatLayerId;

    // V1's strategy model is what weighs spawns against extracts as the raid goes on.
    private readonly MapPriorTrafficModel _priorModel = new(new StrategyModel());
    private MapPriorTraffic? _prior;
    private string? _priorSignature;
    private Bitmap? _heatImage;

    // Null follows the raid clock; a value is the player asking "and what about late raid?".
    private RaidPhase? _chosenPriorPhase;
    private bool _priorPhaseFromClock;
    private IReadOnlyList<TrafficPhaseChoiceViewModel>? _trafficPhases;

    /// <summary>
    /// The concept's raid-phase scrubber, as the three steps the model has: Auto, Early, Mid, Late.
    /// </summary>
    /// <remarks>
    /// Not a minute slider with a play button. The prior weighs spawns, draws and extracts by
    /// thirds of a raid (V1's strategy model) and knows nothing finer; a slider would let a player
    /// read a difference between minute 14 and minute 16 that nothing here supports.
    /// </remarks>
    public IReadOnlyList<TrafficPhaseChoiceViewModel> TrafficPhases => _trafficPhases ??=
    [
        PhaseChoice("auto", RaidText.PhaseAuto, null),
        PhaseChoice("early", RaidText.PhaseEarly, RaidPhase.Early),
        PhaseChoice("mid", RaidText.PhaseMid, RaidPhase.Mid),
        PhaseChoice("late", RaidText.PhaseLate, RaidPhase.Late),
    ];

    public string TrafficPhaseTip => RaidText.TrafficPhaseTip;

    private TrafficPhaseChoiceViewModel PhaseChoice(string id, string label, RaidPhase? phase) =>
        new(label, _chosenPriorPhase == phase, new DelegateCommand(() => ChoosePriorPhase(phase))) { Id = id };

    private void ChoosePriorPhase(RaidPhase? phase)
    {
        if (_chosenPriorPhase == phase)
        {
            return;
        }

        _chosenPriorPhase = phase;
        _trafficPhases = null;
        OnPropertyChanged(nameof(TrafficPhases));
        _rebuildRequest.Request();
    }

    private string PhaseBasis => _chosenPriorPhase is not null
        ? RaidText.PhaseChosenByYou
        : _priorPhaseFromClock ? RaidText.PhaseFromRaidClock : RaidText.PhasePlanningDefault;

    public string TrafficBannerTitle => RaidText.TrafficBannerTitle;

    public string TrafficBannerBasis => RaidText.TrafficSourceClass;

    /// <summary>"Catalog through 14 Sep · includes 14 of your raids".</summary>
    public string TrafficBannerDetail => _prior is { HasField: true } prior
        ? string.Join(" · ", new[] { DataThroughLabel(prior.Basis), OwnRaidsLabel(prior.Basis) }.Where(text => text.Length > 0))
        : string.Empty;

    /// <summary>Only while the field is actually drawn: a banner over a map with no heat on it is noise.</summary>
    public bool ShowsTrafficBanner => _prior is { HasField: true } && Renderer is { ShowsTrafficHeat: true };

    /// <summary>
    /// [#591] The basis lines that used to sit beside the banner on the map, now the compact
    /// chip's tooltip and accessible help text instead — they are already spelled out on the Raid
    /// plan card, so the chip only needs to say what it is when a player asks.
    /// </summary>
    public string TrafficBannerTooltip => TrafficBannerDetail.Length > 0
        ? $"{TrafficBannerBasis}\n{TrafficBannerDetail}"
        : TrafficBannerBasis;

    private string? PriorNotice => _prior switch
    {
        null => null,
        { HasField: true } => RaidText.PriorModelled,
        _ => RaidText.PriorNone,
    };

    private IReadOnlyList<string> PriorRows
    {
        get
        {
            if (_prior is not { HasField: true } prior)
            {
                return [];
            }

            var basis = prior.Basis;
            var rows = new List<string>
            {
                RaidText.PhaseRaid(PhaseName(prior.Phase), PhaseBasis),
                RaidText.ModelVersion(MapPriorTraffic.ModelVersion),
                CoverageLabel(basis, SpawnAreas.Count),
                RaidText.DataThroughGenerated(DataThroughLabel(basis), LocalTime.ShortTime(basis.GeneratedUtc)),
                RaidText.ConfidenceLow(prior.Confidence.Value),
            };
            if (OwnRaidsLabel(basis) is { Length: > 0 } own)
            {
                rows.Add(char.ToUpper(own[0], CultureInfo.CurrentCulture) + own[1..]);
            }

            rows.AddRange(prior.Hotspots.Take(2).Select(hotspot => $"{Level(hotspot.Intensity)} · {RaidText.HotspotName(hotspot)}"));
            return [.. rows.Where(row => row.Length > 0)];
        }
    }

    private static string PhaseName(RaidPhase phase) => phase switch
    {
        RaidPhase.Early => RaidText.PhaseEarly,
        RaidPhase.Mid => RaidText.PhaseMid,
        _ => RaidText.PhaseLate,
    };

    private static string Level(double intensity) => TrafficLevel(intensity);

    /// <summary>Where a hotspot starts to read "High" and "Highest"; the legend quotes the same numbers.</summary>
    internal const double HighTrafficLevel = 0.65;

    internal const double HighestTrafficLevel = 0.8;

    internal static string TrafficLevel(double intensity) => intensity switch
    {
        >= HighestTrafficLevel => RaidText.LevelHighest,
        >= HighTrafficLevel => RaidText.LevelHigh,
        _ => RaidText.LevelRaised,
    };

    private static string CoverageLabel(TrafficPriorBasis basis, int spawnAreaCount) => string.Join(" · ", new[]
    {
        spawnAreaCount > 0 ? RaidText.SpawnAreaCount(spawnAreaCount) : RaidText.NoSpawnAreasLower,
        basis.Extracts > 0 ? RaidText.WaysOutSummary(basis.Extracts) : RaidText.NoWaysOut,
        basis.LootSpawns > 0 ? RaidText.LootSpawns(basis.LootSpawns) : RaidText.NoLootData,
    });

    private static string DataThroughLabel(TrafficPriorBasis basis) => basis.DataThroughUtc is { } through
        ? RaidText.CatalogThrough(LocalTime.ToLocal(through).ToString("d MMM", CultureInfo.CurrentCulture))
        : RaidText.CatalogDateUnknown;

    private static string OwnRaidsLabel(TrafficPriorBasis basis) => basis.OwnRaids switch
    {
        0 => string.Empty,
        _ => RaidText.IncludesOwnRaids(basis.OwnRaids),
    };

    /// <summary>Which third of the raid the clock says this is; the start of one when there is no raid.</summary>
    /// <remarks>
    /// Planning happens before the raid, so "early" is the default rather than a guess. Thirds of a
    /// nominal forty minutes: the model has three phases and no finer claim to make.
    /// </remarks>
    private RaidPhase CurrentPriorPhase(string mapId, DateTimeOffset nowUtc)
    {
        var raid = _stateStore.Current.Raid;
        _priorPhaseFromClock = false;
        if (raid.State != RaidLifecycleState.InRaid || raid.StartedUtc is not { } started ||
            !string.Equals(raid.MapId, mapId, StringComparison.OrdinalIgnoreCase))
        {
            return RaidPhase.Early;
        }

        _priorPhaseFromClock = true;

        return (nowUtc - started).TotalMinutes switch
        {
            < 13 => RaidPhase.Early,
            < 27 => RaidPhase.Mid,
            _ => RaidPhase.Late,
        };
    }

    /// <summary>
    /// The traffic layer and its hotspot objects for this scene, rebuilding the prior only when
    /// something it is built from has changed.
    /// </summary>
    private (IReadOnlyList<MapSceneLayer> Layers, IReadOnlyList<MapSceneObject> Objects) BuildTrafficLayers(
        MapRenderModel model,
        MapSceneBounds planBounds,
        string transformVersion,
        HighValueLootLayerResult shownLoot,
        IReadOnlyList<string> floorIds,
        DateTimeOffset nowUtc)
    {
        var clockPhase = CurrentPriorPhase(model.Location.Id, nowUtc);
        var phase = _chosenPriorPhase ?? clockPhase;
        var trails = _map.VisitedRaids;
        var signature = string.Create(
            CultureInfo.InvariantCulture,
            $"{model.Location.Id}|{transformVersion}|{phase}|{model.OverlayElements.Count}|{shownLoot.DataThroughUtc:O}|{_stateStore.Current.Data.UpdatedUtc:O}|{trails.Count}|{planBounds}");
        if (signature != _priorSignature)
        {
            _priorSignature = signature;
            _prior = _priorModel.Build(PriorInputs(model, planBounds, transformVersion, shownLoot, floorIds, nowUtc), phase);
            // The old picture is left to the collector rather than disposed: the renderer may be
            // half way through drawing it, and it is a quarter of a megabyte made once per map.
            _heatImage = _prior.Field is { } field ? TrafficHeatPicture.Draw(field) : null;
            RaiseTrafficChanged();
        }

        if (_prior is not { Field: not null } prior)
        {
            return ([], []);
        }

        var (estimate, provenance) = PriorEstimate(prior, transformVersion);
        var objects = prior.Hotspots
            .Select((hotspot, index) => new MapSceneObject(
                new($"traffic-prior:{index}"),
                TrafficLayerId,
                MapSceneObjectKind.Traffic,
                MapSceneTruthKind.HistoricalEstimate,
                RaidText.ModelledTrafficAt(Level(hotspot.Intensity), RaidText.HotspotName(hotspot)),
                RaidText.WhyDrivers(RaidText.HotspotDrivers(hotspot), RaidText.TrafficSourceClass),
                new(MapSceneGeometryKind.Region, Circle(hotspot.Position, hotspot.RadiusUnits, planBounds)),
                [],
                provenance,
                estimate))
            .ToArray();
        return ([new(TrafficLayerId, RaidText.LayerModelledTraffic, 5, true)], objects);
    }

    /// <summary>What every object drawn from the prior carries: through when, from what, how sure.</summary>
    private (MapSceneEstimateMetadata Estimate, DataProvenance Provenance) PriorEstimate(MapPriorTraffic prior, string transformVersion)
    {
        var dataThrough = (prior.Basis.DataThroughUtc ?? prior.Basis.GeneratedUtc).ToUniversalTime();
        dataThrough = dataThrough > prior.Basis.GeneratedUtc ? prior.Basis.GeneratedUtc : dataThrough;
        return (
            new(
                dataThrough,
                dataThrough,
                prior.Basis.GeneratedUtc,
                CoverageLabel(prior.Basis, SpawnAreas.Count),
                RaidText.NotValidated,
                transformVersion,
                MapPriorTraffic.ModelVersion),
            new("map-structure-prior", prior.Basis.GeneratedUtc, Confidence: prior.Confidence));
    }

    private TrafficPriorInputs PriorInputs(
        MapRenderModel model,
        MapSceneBounds planBounds,
        string transformVersion,
        HighValueLootLayerResult shownLoot,
        IReadOnlyList<string> floorIds,
        DateTimeOffset nowUtc)
    {
        var sources = TrafficPriorSources.FromOverlay(model.OverlayElements).ToList();

        // Every qualifying spawn, not the ones the player's loot filter happens to show: what
        // draws other players does not change when this player hides a category.
        var loot = ReferenceEquals(shownLoot.AppliedFilter, HighValueLootFilter.Default)
            ? shownLoot
            : _lootSource.Build(new(model.Location.Id, transformVersion, planBounds, nowUtc, HighValueLootFilter.Default, floorIds));
        var places = loot.Objects
            .Where(item => item.Geometry.Kind == MapSceneGeometryKind.Point)
            .ToDictionary(item => item.Id, item => item.Geometry.Points[0]);
        foreach (var entry in loot.Entries)
        {
            if (entry.SceneObjectId is { } id && places.TryGetValue(id, out var at) &&
                TrafficPriorSources.LootWeight(entry.Tier) is > 0 and var weight)
            {
                sources.Add(new(TrafficPriorSourceKind.HighValueLoot, new(at.X, at.Y), weight, "High-value loot"));
            }
        }

        var trails = _map.VisitedRaids
            .Select(trail => (IReadOnlyList<MapPoint>)[.. PlanPoints(model, trail.Positions.Select(step => step.Position))
                .Select(point => new MapPoint(point.X, point.Y))])
            .ToArray();
        return new(
            model.Location.Id,
            new(planBounds.MinimumX, planBounds.MinimumY),
            new(planBounds.MaximumX, planBounds.MaximumY),
            UnitsPerMetre(model),
            sources,
            trails,
            DataThrough(loot.DataThroughUtc, _stateStore.Current.Data.UpdatedUtc));
    }

    /// <summary>The older of the loot import and the catalog sync: the prior is only as new as both.</summary>
    private static DateTimeOffset? DataThrough(DateTimeOffset? loot, DateTimeOffset? catalog) => (loot, catalog) switch
    {
        ({ } a, { } b) => (a < b ? a : b).ToUniversalTime(),
        _ => (loot ?? catalog)?.ToUniversalTime(),
    };

    /// <summary>Plan units per metre of game world, measured through the transform itself.</summary>
    private static double UnitsPerMetre(MapRenderModel model)
    {
        const double Probe = 100;
        if (!model.TryMapPosition(new(0, 0, 0), out var origin) ||
            !model.TryMapPosition(new(Probe, 0, 0), out var east) ||
            !model.TryMapPosition(new(0, 0, Probe), out var north))
        {
            return 0;
        }

        var alongX = Math.Sqrt(Math.Pow(east.X - origin.X, 2) + Math.Pow(east.Y - origin.Y, 2)) / Probe;
        var alongZ = Math.Sqrt(Math.Pow(north.X - origin.X, 2) + Math.Pow(north.Y - origin.Y, 2)) / Probe;
        return (alongX + alongZ) / 2;
    }

    private static IReadOnlyList<MapScenePoint> Circle(MapPoint centre, double radius, MapSceneBounds bounds)
    {
        const int Points = 20;
        return [.. Enumerable.Range(0, Points).Select(index =>
        {
            var angle = 2 * Math.PI * index / Points;
            return new MapScenePoint(
                Math.Clamp(centre.X + (radius * Math.Cos(angle)), bounds.MinimumX, bounds.MaximumX),
                Math.Clamp(centre.Y + (radius * Math.Sin(angle)), bounds.MinimumY, bounds.MaximumY));
        })];
    }

    /// <summary>After the renderer exists or has been re-presented: hand it the current picture.</summary>
    private void ApplyTrafficToRenderer()
    {
        Renderer?.SetTrafficHeat(_heatImage);
        OnPropertyChanged(nameof(ShowsTrafficBanner));
    }

    private void RaiseTrafficChanged()
    {
        OnPropertyChanged(nameof(TrafficBannerDetail));
        OnPropertyChanged(nameof(TrafficBannerTooltip));
        OnPropertyChanged(nameof(ShowsTrafficBanner));
        OnPropertyChanged(nameof(TrafficLayerNotice));
        OnPropertyChanged(nameof(TrafficRows));
        OnPropertyChanged(nameof(HasTrafficRows));
        OnPropertyChanged(nameof(TrafficLegendPlaces));
        OnPropertyChanged(nameof(HasTrafficLegendPlaces));
    }
}

/// <summary>[Issue 286] One step of the traffic phase control.</summary>
public sealed class TrafficPhaseChoiceViewModel(string label, bool isSelected, System.Windows.Input.ICommand selectCommand)
{
    public string Label { get; } = label;

    public bool IsSelected { get; } = isSelected;

    public System.Windows.Input.ICommand SelectCommand { get; } = selectCommand;

    /// <summary>"auto", "early", "mid" or "late": what automation and the render tool press, whatever the label says.</summary>
    public string Id { get; init; } = string.Empty;

    public string AutomationId => $"v2-raid-traffic-phase-{Id}";
}
