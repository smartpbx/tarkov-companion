using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [#780] Squadmates' open objectives on the Raid map, in their colour, quieter than the player's own.
/// </summary>
/// <remarks>
/// Off until the Objectives card's "Squad" toggle is pressed: a squad's objectives are help to
/// offer, not the player's own plan, and five people's quests at once would bury the map. They are
/// placed by the same quest layer as the player's own, from this player's own catalog; an objective
/// the player has open too is already a filled pin and is not drawn again.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private SquadQuestFeed? _squadQuests;
    private bool _showSquadObjectives;
    private bool _routeSquadStops;
    private ICommand? _toggleSquadObjectivesCommand;
    private ICommand? _toggleRouteSquadStopsCommand;
    private QuestMapProjectionReadModel? _squadProjection;
    private string? _squadProjectionKey;
    private string? _squadProjectingKey;
    private int _squadPictureVersion;
    private QuestObjectiveScene _squadScene = QuestObjectiveScene.Empty;
    private IReadOnlyDictionary<MapSceneObjectId, MapSceneObjectStyle> _squadStyles =
        new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
    private IReadOnlyList<ObjectiveRouteStop> _planRouteStops = [];
    private string? _planRouteLocationId;
    private string? _fedRouteSignature;

    /// <summary>A squadmate's colour, as their marker is drawn in ("#FFRRGGBB").</summary>
    internal string SquadColorFor(string name) => _map.GroupColorFor(name);

    /// <summary>Starts listening to the squad's quests; called once, from the constructor.</summary>
    private void AttachSquadQuests(SquadQuestFeed? feed)
    {
        if (feed is null)
        {
            return;
        }

        _squadQuests = feed;
        feed.Changed += SquadQuestsChanged;
    }

    private void SquadQuestsChanged()
    {
        _squadPictureVersion++;
        _rebuildRequest.Request();
    }

    /// <summary>Whether any squadmate has an open objective on the map shown.</summary>
    public bool HasSquadObjectivesHere => _squadProjection is { Objectives.Count: > 0 };

    /// <summary>The Objectives card shows for the player's own objectives or the squad's.</summary>
    public bool HasObjectivesCard => HasQuestObjectives || HasSquadObjectivesHere;

    /// <summary>"Squad": adds squadmates' open objectives on this map.</summary>
    public bool ShowSquadObjectives
    {
        get => _showSquadObjectives;
        set
        {
            if (SetProperty(ref _showSquadObjectives, value))
            {
                OnPropertyChanged(nameof(ShowsSquadObjectiveSummary));
                FeedObjectiveRouteStops();
                _rebuildRequest.Request();
            }
        }
    }

    public ICommand ToggleSquadObjectivesCommand =>
        _toggleSquadObjectivesCommand ??= new DelegateCommand(() => ShowSquadObjectives = !ShowSquadObjectives);

    /// <summary>"Route squad": the objective route also visits squadmates' stops on this map.</summary>
    public bool RouteSquadStops
    {
        get => _routeSquadStops;
        set
        {
            if (SetProperty(ref _routeSquadStops, value))
            {
                FeedObjectiveRouteStops();
            }
        }
    }

    public bool CanRouteSquadStops => HasObjectiveRoute && ShowSquadObjectives && HasSquadObjectivesHere;

    public ICommand ToggleRouteSquadStopsCommand =>
        _toggleRouteSquadStopsCommand ??= new DelegateCommand(() => RouteSquadStops = !RouteSquadStops);

    /// <summary>"4 from Geo, Riley" — whose objectives are on this map; empty when none.</summary>
    public string SquadObjectiveSummary
    {
        get
        {
            if (_squadProjection is not { Objectives.Count: > 0 } projection || _squadQuests is not { } feed)
            {
                return string.Empty;
            }

            var ids = projection.Objectives.Select(item => item.ObjectiveId).Distinct(StringComparer.Ordinal).ToArray();
            var names = ids.Select(feed.Picture.MemberFor).OfType<string>().Distinct(StringComparer.Ordinal);
            return RaidText.SquadObjectivesFrom(ids.Length, string.Join(", ", names));
        }
    }

    public bool ShowsSquadObjectiveSummary => ShowSquadObjectives && SquadObjectiveSummary.Length > 0;

    /// <summary>
    /// The squad's objectives for this rebuild, as scene objects styled in each squadmate's colour.
    /// </summary>
    /// <remarks>
    /// Placed off the interface thread when the map or the squad's quests change, then drawn on the
    /// next rebuild; an unchanged map and picture reuses the last placement.
    /// </remarks>
    private IReadOnlyList<MapSceneObject> BuildSquadObjectives(MapRenderModel model, DateTimeOffset nowUtc)
    {
        if (_squadQuests is not { } feed)
        {
            return [];
        }

        var key = $"{model.Location.Id}|{model.Variant.Key}|{_squadPictureVersion}|{string.Join(',', _questScene.Entries.Select(entry => entry.ObjectiveId))}";
        if (key != _squadProjectionKey && key != _squadProjectingKey)
        {
            _ = ProjectSquadAsync(feed, model.Location.Id, key);
        }

        var projection = _squadProjection;
        if (!_showSquadObjectives || projection is null ||
            !string.Equals(projection.LocationId, model.Location.Id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(projection.VariantKey, model.Variant.Key, StringComparison.OrdinalIgnoreCase))
        {
            _squadScene = QuestObjectiveScene.Empty;
            _squadStyles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
            return [];
        }

        var picture = feed.Picture;
        _squadScene = new QuestObjectiveSceneBuilder().Build(
            projection.Objectives,
            model.Floors,
            id => picture.MemberFor(id) is { Length: > 0 } name ? name[..1].ToUpperInvariant() : null,
            nowUtc);
        var styles = new Dictionary<MapSceneObjectId, MapSceneObjectStyle>();
        foreach (var entry in _squadScene.Entries)
        {
            if (picture.MemberFor(entry.ObjectiveId) is not { } member)
            {
                continue;
            }

            var colour = SquadColorFor(member);
            foreach (var id in entry.ObjectIds)
            {
                styles[id] = new MapSceneObjectStyle(colour, Opacity: 0.8, Outlined: true);
            }
        }

        _squadStyles = styles;
        // The squad's stops come from this scene, so the route hears about a new one now.
        FeedObjectiveRouteStops();
        return _squadScene.Objects;
    }

    private async Task ProjectSquadAsync(SquadQuestFeed feed, string locationId, string key)
    {
        _squadProjectingKey = key;
        try
        {
            var own = _questScene.Entries.Select(entry => entry.ObjectiveId).ToHashSet(StringComparer.Ordinal);
            var picture = feed.Picture;
            var projection = await _map
                .ProjectOtherObjectivesAsync(mapIds => picture.MapQuery(mapIds, own), CancellationToken.None)
                .ConfigureAwait(true);
            if (_disposed || _squadProjectingKey != key)
            {
                return;
            }

            _squadProjection = projection is { UnavailableReason: null } &&
                string.Equals(projection.LocationId, locationId, StringComparison.OrdinalIgnoreCase)
                    ? projection
                    : null;
            _squadProjectionKey = key;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WorkspaceFault.Record("raid", "place squad objectives", exception.Message);
            _squadProjection = null;
            _squadProjectionKey = key;
        }
        finally
        {
            if (_squadProjectingKey == key)
            {
                _squadProjectingKey = null;
            }
        }

        OnPropertyChanged(nameof(HasSquadObjectivesHere));
        OnPropertyChanged(nameof(HasObjectivesCard));
        OnPropertyChanged(nameof(SquadObjectiveSummary));
        OnPropertyChanged(nameof(ShowsSquadObjectiveSummary));
        FeedObjectiveRouteStops();
        _rebuildRequest.Request();
    }

    /// <summary>Plan's stops, kept so the squad's can be added to or taken off them.</summary>
    private void RememberPlanRouteStops(string locationId, IReadOnlyList<ObjectiveRouteStop> stops)
    {
        _planRouteLocationId = locationId;
        _planRouteStops = stops;
        FeedObjectiveRouteStops();
    }

    /// <summary>
    /// Plan's stops, and with "Route squad" the squadmates' single-spot objectives on this map too.
    /// </summary>
    /// <remarks>
    /// Only an objective with exactly one spot is a stop, the same rule Plan routes the player's
    /// own by: several candidates do not name one honest place to walk to.
    /// </remarks>
    private void FeedObjectiveRouteStops()
    {
        OnPropertyChanged(nameof(CanRouteSquadStops));
        if (!_objectiveRouteOpened || _planRouteLocationId is not { } locationId)
        {
            return;
        }

        IReadOnlyList<ObjectiveRouteStop> stops = _planRouteStops;
        if (_routeSquadStops && _showSquadObjectives && _squadQuests is { } feed && _squadProjection is { } projection &&
            string.Equals(projection.LocationId, locationId, StringComparison.OrdinalIgnoreCase))
        {
            var planned = stops.Select(stop => stop.ObjectiveId).ToHashSet(StringComparer.Ordinal);
            var objects = _squadScene.Objects.ToDictionary(item => item.Id);
            var squadStops = new List<ObjectiveRouteStop>();
            foreach (var entry in _squadScene.Entries.Where(entry => entry.IsPlaced && !planned.Contains(entry.ObjectiveId)))
            {
                var points = entry.ObjectIds
                    .Where(objects.ContainsKey)
                    .Select(id => objects[id].Geometry)
                    .Where(geometry => geometry.Kind == MapSceneGeometryKind.Point)
                    .Select(geometry => geometry.Points[0])
                    .Distinct()
                    .ToArray();
                if (points.Length == 1)
                {
                    squadStops.Add(new(entry.ObjectiveId, $"{feed.Picture.MemberFor(entry.ObjectiveId)}: {entry.Objective.Description}", points[0])
                    {
                        FloorIds = entry.FloorIds,
                    });
                }
            }

            stops = [.. stops, .. squadStops];
        }

        var signature = $"{locationId}|{string.Join(',', stops.Select(stop => FormattableString.Invariant($"{stop.ObjectiveId}@{stop.At.X:R},{stop.At.Y:R}")))}";
        if (signature == _fedRouteSignature)
        {
            return;
        }

        _fedRouteSignature = signature;
        Follower.SetStops(locationId, stops);
    }

    /// <summary>The squad's pins too, so a squad stop's step number rides on its own pin.</summary>
    private IReadOnlyList<ObjectiveRoutePin> SquadRoutePins() => ObjectiveRoutePins(_squadScene);
}
