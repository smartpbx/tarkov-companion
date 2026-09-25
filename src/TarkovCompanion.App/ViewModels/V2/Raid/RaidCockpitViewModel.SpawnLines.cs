using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Workspaces;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[#914] What one rebuild draws on the Spawn lines layer.</summary>
internal sealed record SpawnLineScene(
    IReadOnlyList<MapSceneObject> Objects,
    IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> Styles);

/// <summary>One press of the spawn radius picker.</summary>
public sealed class SpawnRadiusChoiceViewModel(int metres, bool isSelected, ICommand selectCommand)
{
    public int Metres { get; } = metres;

    public string Label { get; } = RaidText.SpawnRadiusChoice(metres);

    public bool IsSelected { get; } = isSelected;

    public ICommand SelectCommand { get; } = selectCommand;

    public string AutomationId => string.Create(CultureInfo.InvariantCulture, $"v2-raid-spawn-radius-{Metres}");
}

/// <summary>
/// [#914] V1's spawn threat lines on the V2 Raid map: a dashed red line from each nearby PMC
/// spawn area to the player's latest position in the first five minutes of a PMC raid, with a
/// radius chosen per map.
/// </summary>
/// <remarks>
/// V1 drew these (MapView's SpawnThreats) and the V2 scene never did; the cockpit forwarded
/// <c>SpawnThreats</c> and nothing read it. Built here from the same grouped, side-filtered areas
/// the Nearby spawns layer draws, so every line ends at a marker on that layer, one per area.
/// Everything is modelled from the static catalog: the label says "possible", never a player.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    internal static readonly MapSceneLayerId SpawnLinesLayerId = new("spawn-lines");

    /// <summary>
    /// V1's threat red. The V2 theme's Danger token is a text colour chosen per theme variant, and
    /// the map picture under these lines is dark in every variant, so the map keeps one red.
    /// </summary>
    internal const string SpawnLineColor = "#FFE05C5C";

    /// <summary>How often a fading line is redrawn; the raid clock ticks every second.</summary>
    private static readonly TimeSpan SpawnLineFadeStep = TimeSpan.FromSeconds(10);

    private IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> _spawnLineStyles =
        new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
    private DateTimeOffset? _spawnLinesRedrawUtc;
    private string? _spawnRadiusMapId;

    /// <summary>The radius on the open map, in metres.</summary>
    public int SpawnRadiusMetres => SpawnRadiusFor(_spawnRadiusMapId);

    /// <summary>The picker's chips, the open map's radius lit.</summary>
    public IReadOnlyList<SpawnRadiusChoiceViewModel> SpawnRadiusChoices =>
    [
        .. SpawnLines.RadiusChoices.Select(metres => new SpawnRadiusChoiceViewModel(
            metres,
            metres == SpawnRadiusMetres,
            new DelegateCommand(() => SetSpawnRadius(metres)))),
    ];

    /// <summary>Remembers a radius for the open map and redraws it.</summary>
    internal void SetSpawnRadius(int metres)
    {
        if (_spawnRadiusMapId is { Length: > 0 } mapId)
        {
            SetSpawnRadius(mapId, metres);
        }
    }

    /// <summary>Remembers a radius for one map.</summary>
    internal void SetSpawnRadius(string mapId, int metres)
    {
        if (!SpawnLines.RadiusChoices.Contains(metres))
        {
            return;
        }

        _layout?.Set(WorkspaceLayoutKeys.RaidSpawnRadius(mapId), metres.ToString(CultureInfo.InvariantCulture));
        RaiseSpawnRadius();
        _rebuildRequest.Request();
    }

    internal int SpawnRadiusFor(string? mapId) => string.IsNullOrEmpty(mapId)
        ? SpawnLines.DefaultRadiusMetres
        : SpawnLines.ParseRadius(_layout?.Get(WorkspaceLayoutKeys.RaidSpawnRadius(mapId)));

    private void RaiseSpawnRadius()
    {
        OnPropertyChanged(nameof(SpawnRadiusMetres));
        OnPropertyChanged(nameof(SpawnRadiusChoices));
    }

    /// <summary>The open map's areas inside its radius; also follows a map change for the picker.</summary>
    private IReadOnlyList<NearbySpawn> SpawnAreasWithinRadius(MapRenderModel model, IReadOnlyList<NearbySpawn> areas)
    {
        if (!string.Equals(_spawnRadiusMapId, model.Location.Id, StringComparison.OrdinalIgnoreCase))
        {
            _spawnRadiusMapId = model.Location.Id;
            RaiseSpawnRadius();
        }

        return SpawnLines.Within(areas, SpawnRadiusFor(model.Location.Id));
    }

    /// <summary>The Spawn lines row, always there: its objects exist only in the raid's opening window.</summary>
    private static MapSceneLayer SpawnLinesLayer(MapRenderModel model) =>
        new(SpawnLinesLayerId, RaidText.LayerSpawnLines, NearbySpawnsLayer(model).ZIndex, true);

    /// <summary>This rebuild's lines, and when to redraw them while they fade.</summary>
    private SpawnLineScene SpawnLinesFor(
        MapRenderModel model,
        IReadOnlyList<NearbySpawn> areas,
        EarlyRaidSpawnPhase phase,
        DateTimeOffset? startedUtc,
        DateTimeOffset nowUtc)
    {
        var strength = SpawnLineStrength(phase, startedUtc, nowUtc);
        // Nothing to redraw at full strength until the fade begins, then a step at a time.
        _spawnLinesRedrawUtc = areas.Count == 0 || strength <= 0 ? null
            : strength >= 1 ? startedUtc + SpawnLines.FullFor
            : nowUtc + SpawnLineFadeStep;
        var scene = BuildSpawnLineScene(
            areas,
            position => model.TryMapPosition(position, out var point) && double.IsFinite(point.X) && double.IsFinite(point.Y)
                ? new MapScenePoint(point.X, point.Y)
                : null,
            _map.PlayerPosition?.Position,
            strength,
            nowUtc);
        _spawnLineStyles = scene.Styles;
        return scene;
    }

    /// <summary>Drawn only while the PMC opening window is open; a scav or unknown side gets none.</summary>
    internal static double SpawnLineStrength(EarlyRaidSpawnPhase phase, DateTimeOffset? startedUtc, DateTimeOffset nowUtc) =>
        phase == EarlyRaidSpawnPhase.Active && startedUtc is { } started
            ? SpawnLines.Strength(nowUtc - started)
            : 0;

    /// <summary>Asks for a redraw on the raid clock's tick while the lines fade.</summary>
    private void TickSpawnLines()
    {
        if (_spawnLinesRedrawUtc is { } due && _timeProvider.GetUtcNow() >= due)
        {
            _spawnLinesRedrawUtc = null;
            _rebuildRequest.Request();
        }
    }

    /// <summary>
    /// One dashed line per area from the area to the player, and its distance written at the middle.
    /// </summary>
    /// <param name="areas">Grouped areas already inside the radius: one line each, never one per point.</param>
    /// <param name="toPlan">Projects a world position to plan units; null when it lands off the plan.</param>
    /// <param name="player">The player's latest position; no player, no lines.</param>
    /// <param name="strength">1 drawn fully, 0 not drawn (<see cref="SpawnLines.Strength"/>).</param>
    internal static SpawnLineScene BuildSpawnLineScene(
        IReadOnlyList<NearbySpawn> areas,
        Func<WorldPosition, MapScenePoint?> toPlan,
        WorldPosition? player,
        double strength,
        DateTimeOffset nowUtc)
    {
        var styles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
        if (strength <= 0 || areas.Count == 0 || player is not { } here || toPlan(here) is not { } end)
        {
            return new([], styles);
        }

        var objects = new List<MapSceneObject>();
        var provenance = new DataProvenance("map-catalog", nowUtc);
        var index = 0;
        foreach (var area in areas)
        {
            if (toPlan(area.Position) is not { } start)
            {
                continue;
            }

            var key = string.Create(CultureInfo.InvariantCulture, $"{index++}:{area.Position.X:F1}:{area.Position.Z:F1}");
            var distance = SpawnProximity.Describe(SpawnProximity.Distance(here, area.Position));
            var label = RaidText.SpawnLineLabel(distance);
            var line = new MapSceneObject(
                new($"spawn-line:{key}"),
                SpawnLinesLayerId,
                MapSceneObjectKind.Route,
                MapSceneTruthKind.PotentialSpawn,
                label,
                RaidText.SpawnLineDetail,
                new(MapSceneGeometryKind.Line, [start, end]),
                [],
                provenance,
                faction: area.Side);
            var text = new MapSceneObject(
                new($"spawn-line-label:{key}"),
                SpawnLinesLayerId,
                MapSceneObjectKind.Label,
                MapSceneTruthKind.PotentialSpawn,
                label,
                RaidText.SpawnLineDetail,
                MapSceneGeometry.At(new((start.X + end.X) / 2, (start.Y + end.Y) / 2)),
                [],
                provenance,
                faction: area.Side);
            objects.Add(line);
            objects.Add(text);
            styles[line.Id] = new(SpawnLineColor, LineThickness: 2.5, Opacity: 0.9 * strength, Dashed: true);
            // A label takes no opacity, so its fade rides in the colour's alpha.
            styles[text.Id] = new(WithAlpha(SpawnLineColor, strength));
        }

        return new(objects, styles);
    }

    internal static string WithAlpha(string argb, double strength)
    {
        var alpha = (int)Math.Round(Math.Clamp(strength, 0, 1) * 255);
        return string.Create(CultureInfo.InvariantCulture, $"#{alpha:X2}{argb[3..]}");
    }
}
