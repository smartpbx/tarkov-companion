using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [#985] Possible PMC spawns from the moment the raid's map is known, without the player doing
/// anything: every PMC spawn area of the map at first, the nearby ones with lines once a
/// screenshot says where the player is, gone five minutes after the game started.
/// </summary>
/// <remarks>
/// <para>
/// Reported three times (#664, #914, #985) and each time something else hid them. Replayed
/// against the owner's real logs of 2026-09-23 to 25, the gates were, in the order they bit:
/// the PC clock four hours fast against the server-dated start, which closed the window before
/// the loading screen did (<see cref="Application.Services.Raids.RaidStartClock"/>); nothing at
/// all before the first screenshot; nothing in a raid whose side no line named; and a window
/// counted from the confirmation, a minute or more before the player could move.
/// </para>
/// <para>
/// Static catalog data, never a detection: every marker reads "possible PMC spawn", the lines
/// say "possible", and the status line on the map says what is shown and why.
/// </para>
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private IReadOnlyList<MapFeature>? _pmcAreasFrom;
    private IReadOnlyList<NearbySpawn> _pmcAreas = [];
    private string _openingSpawnStatus = string.Empty;

    /// <summary>One line on the map saying what the possible PMC spawns are and why they show; empty when none do.</summary>
    public string OpeningSpawnStatus
    {
        get => _openingSpawnStatus;
        private set
        {
            if (string.Equals(_openingSpawnStatus, value, StringComparison.Ordinal))
            {
                return;
            }

            _openingSpawnStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasOpeningSpawnStatus));
        }
    }

    public bool HasOpeningSpawnStatus => _openingSpawnStatus.Length > 0;

    /// <summary>This rebuild's opening-window spawns: nearby once a screenshot anchors them, every PMC area before.</summary>
    private EarlyRaidSpawnSelection SelectOpeningSpawns(RaidSnapshot raid, MapFeatureFaction raidSide)
    {
        if (raid.State != RaidLifecycleState.InRaid ||
            raid.MapId is not { } mapId ||
            !string.Equals(mapId, _map.RenderModel?.Location.Id, StringComparison.OrdinalIgnoreCase))
        {
            return new(EarlyRaidSpawnPhase.Unaffected, []);
        }

        var anchored = _map.PlayerTrailPositions.Count > 0;
        return _earlyRaidSpawns.Select(
            anchored ? PmcAreasFromFirstScreenshot() : PmcAreasOfThisMap(),
            raidSide,
            raid.StartedUtc,
            GameStartedUtc(),
            isNearby: anchored);
    }

    /// <summary>Grouped once per map's features, not on every rebuild.</summary>
    private IReadOnlyList<NearbySpawn> PmcAreasOfThisMap()
    {
        var features = _map.MapFeatures;
        if (!ReferenceEquals(features, _pmcAreasFrom))
        {
            _pmcAreasFrom = features;
            _pmcAreas = SpawnProximity.PmcAreas(features);
        }

        return _pmcAreas;
    }

    private (IReadOnlyList<MapFeature>? Features, ScreenshotPosition? Anchor, ScreenshotPosition? Player) _measuredFrom;
    private IReadOnlyList<NearbySpawn> _measured = [];

    /// <summary>
    /// Every PMC area measured from the first screenshot, nearest first, however far: the radius,
    /// or the nearest two beyond it, is applied by the caller (<see cref="SpawnLines.WithinOrNearest"/>).
    /// </summary>
    /// <remarks>
    /// Measured here as a PMC rather than read from the map's spawn panel, which follows the raid's
    /// side: an unknown side there lists scav areas too, and the panel stops at 300 m.
    /// </remarks>
    private IReadOnlyList<NearbySpawn> PmcAreasFromFirstScreenshot()
    {
        var key = (_map.MapFeatures, _map.PlayerTrailPositions[0], _map.PlayerPosition);
        if (!ReferenceEquals(key.Item1, _measuredFrom.Features) ||
            !ReferenceEquals(key.Item2, _measuredFrom.Anchor) ||
            !ReferenceEquals(key.Item3, _measuredFrom.Player))
        {
            _measuredFrom = key;
            _measured = SpawnProximity.Near(key.Item1, key.Item2.Position, key.Item3?.Position, MapFeatureFaction.Pmc, double.MaxValue);
        }

        return _measured;
    }

    /// <summary>When the log said the player could move in this raid, on this PC's clock (ADR 0022's stages).</summary>
    private DateTimeOffset? GameStartedUtc() =>
        _situation?.Current.Stages.LastOrDefault(stage => stage.Kind == RaidPhaseMarkerKind.GameStarted)?.ObservedUtc;

    private void PublishOpeningSpawnStatus(EarlyRaidSpawnSelection selection, IReadOnlyList<NearbySpawn> shown, bool beyondRadius, int radiusMetres) =>
        OpeningSpawnStatus = OpeningSpawnStatusFor(
            selection,
            shown,
            beyondRadius,
            radiusMetres,
            // A map whose catalog has not arrived, or has no PMC spawns, has nothing to say "none near you" about.
            RouteLayerSwitch.IsShown(Renderer, _layerVisibility, NearbySpawnsLayerId) && PmcAreasOfThisMap().Count > 0);

    /// <summary>The map's line: what is shown, for how long, and the one thing that would sharpen it.</summary>
    internal static string OpeningSpawnStatusFor(
        EarlyRaidSpawnSelection selection,
        IReadOnlyList<NearbySpawn> shown,
        bool beyondRadius,
        int radiusMetres,
        bool layerShown)
    {
        // Before a screenshot there is nothing to say on a map whose catalog has no PMC spawns.
        if (selection.Phase != EarlyRaidSpawnPhase.Active || !layerShown || (!selection.IsNearby && shown.Count == 0))
        {
            return string.Empty;
        }

        var line = !selection.IsNearby ? RaidText.OpeningSpawnsBeforeScreenshot
            : beyondRadius ? RaidText.OpeningSpawnsNearestBeyond(
                string.Join(", ", shown.Select(area => SpawnProximity.Describe(area.MetresFromStart))),
                radiusMetres)
            : shown.Count > 0 ? RaidText.OpeningSpawnsNearby(radiusMetres)
            : RaidText.OpeningSpawnsNoneNearby(radiusMetres);
        return selection.SideAssumed ? RaidText.OpeningSpawnsSideAssumed(line) : line;
    }
}
