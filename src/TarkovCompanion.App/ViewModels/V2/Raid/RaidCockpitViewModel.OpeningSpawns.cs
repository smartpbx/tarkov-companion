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
            anchored ? _map.NearbySpawnAreas : PmcAreasOfThisMap(),
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

    /// <summary>When the log said the player could move in this raid, on this PC's clock (ADR 0022's stages).</summary>
    private DateTimeOffset? GameStartedUtc() =>
        _situation?.Current.Stages.LastOrDefault(stage => stage.Kind == RaidPhaseMarkerKind.GameStarted)?.ObservedUtc;

    private void PublishOpeningSpawnStatus(EarlyRaidSpawnSelection selection, int shown, int radiusMetres) =>
        OpeningSpawnStatus = OpeningSpawnStatusFor(
            selection,
            shown,
            radiusMetres,
            RouteLayerSwitch.IsShown(Renderer, _layerVisibility, NearbySpawnsLayerId));

    /// <summary>The map's line: what is shown, for how long, and the one thing that would sharpen it.</summary>
    internal static string OpeningSpawnStatusFor(EarlyRaidSpawnSelection selection, int shown, int radiusMetres, bool layerShown)
    {
        // Before a screenshot there is nothing to say on a map whose catalog has no PMC spawns.
        if (selection.Phase != EarlyRaidSpawnPhase.Active || !layerShown || (!selection.IsNearby && shown == 0))
        {
            return string.Empty;
        }

        var line = !selection.IsNearby ? RaidText.OpeningSpawnsBeforeScreenshot
            : shown > 0 ? RaidText.OpeningSpawnsNearby(radiusMetres)
            : RaidText.OpeningSpawnsNoneNearby(radiusMetres);
        return selection.SideAssumed ? RaidText.OpeningSpawnsSideAssumed(line) : line;
    }
}
