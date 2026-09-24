using System.Globalization;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Quests;

/// <summary>What the map can honestly say about where an objective is.</summary>
public enum QuestObjectivePlacement
{
    /// <summary>The catalog gives no place for it, or it is a kind of objective that has none.</summary>
    NoLocation,

    /// <summary>A spot the source authored.</summary>
    Point,

    /// <summary>A region the source authored, drawn as the region.</summary>
    Area,

    /// <summary>Several candidate spots, only one of which is where it happens.</summary>
    Candidates,

    /// <summary>
    /// A spot the player placed themselves for an objective the catalog gives no place for. It is
    /// theirs, not the quest data's, and is never presented as official.
    /// </summary>
    UserPlaced,
}

/// <summary>One objective as the map's list and its detail card describe it.</summary>
/// <param name="Number">The number written on its marker, which is also its row's number.</param>
/// <param name="Objective">Everything the read side knows about it.</param>
/// <param name="PlaceCount">How many places it has on this map (the candidates, the regions).</param>
/// <param name="PlacementLabel">"Area", "One of 5 places", "No location": what it is, in words.</param>
/// <param name="NoLocationReason">Why it has no place, when it has none.</param>
/// <param name="FloorIds">The floors it is on; empty when it is on every floor or has no height.</param>
/// <param name="FloorNames">The same floors by name, empty when the map has one floor or the objective is on all of them.</param>
/// <param name="ObjectIds">Every scene object that draws it: the marker, the area, the label point.</param>
public sealed record QuestObjectiveEntry(
    string ObjectiveId,
    string Number,
    QuestMapObjectiveReadModel Objective,
    QuestObjectivePlacement Placement,
    int PlaceCount,
    string PlacementLabel,
    string? NoLocationReason,
    IReadOnlyList<string> FloorIds,
    IReadOnlyList<string> FloorNames,
    IReadOnlyList<MapSceneObjectId> ObjectIds)
{
    public bool IsPlaced => Placement != QuestObjectivePlacement.NoLocation;

    /// <summary>The wiki page of the quest this belongs to, when the last sync had one.</summary>
    public string? WikiUri => Objective.WikiUri;

    /// <summary>"Ground floor" or "2nd Floor, 3rd Floor": which floor, and empty where the question has no answer.</summary>
    public string FloorLabel => FloorNames.Count == 0 ? string.Empty : string.Join(", ", FloorNames);
}

/// <summary>The scene objects for a set of objectives, and the entries that explain them.</summary>
public sealed record QuestObjectiveScene(
    IReadOnlyList<MapSceneObject> Objects,
    IReadOnlyList<QuestObjectiveEntry> Entries)
{
    public static QuestObjectiveScene Empty { get; } = new([], []);

    /// <summary>The objective a scene object belongs to, however many objects draw it.</summary>
    public QuestObjectiveEntry? EntryFor(MapSceneObjectId id) =>
        Entries.FirstOrDefault(entry => entry.ObjectIds.Contains(id));
}

/// <summary>
/// Turns projected quest objectives into the scene objects a map draws, for every screen that
/// draws them.
/// </summary>
/// <remarks>
/// <para>
/// The projection hands back points in Leaflet map units, and those are the units of
/// <see cref="MapSceneSnapshot.Bounds"/>: the cockpit's plan rectangle is stated in them. So a
/// projected point is a scene point as it stands. The Plan workspace used to convert them into a
/// 0 to 100 square first, which had been the plan's coordinate space before #413 gave it the
/// artwork's real rectangle, and placed objectives outside the artwork; there is deliberately no
/// second mapping here to get wrong.
/// </para>
/// <para>
/// What is drawn follows what the source said. A zone with an outline is an area and is drawn as
/// one, with its own number at a point inside it; a zone with only a position is a spot;
/// json.tarkov.dev's possibleLocations are several candidate spots for one objective and are drawn
/// as that many spots that each say they are one of them. An objective the projection could not
/// place — an unsupported kind, a hand-in, no geometry in the feed — produces an entry and no
/// object, so it is listed as having no location instead of being guessed onto the map.
/// </para>
/// </remarks>
public sealed class QuestObjectiveSceneBuilder
{
    private static readonly MapSceneLayerId QuestsLayer = MapSceneAssembler.IdFor(MapOverlayKind.QuestObjectives);

    /// <param name="numberFor">The number an objective is called on the map, or null for the next free one.</param>
    public QuestObjectiveScene Build(
        IReadOnlyList<QuestMapObjectiveProjection> projections,
        IReadOnlyList<MapFloorDefinition> floors,
        Func<string, string?>? numberFor,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(floors);
        var provenance = new DataProvenance("quest-catalog", nowUtc.ToUniversalTime());
        var objects = new List<MapSceneObject>();
        var entries = new List<QuestObjectiveEntry>();
        var sequence = 0;
        foreach (var group in projections.GroupBy(projection => projection.ObjectiveId, StringComparer.Ordinal))
        {
            // json.tarkov.dev lists the very same zone twice for 77 objectives (Customs' dorm room
            // 214 is one), and drawing both would stack two identical areas and call them "2 areas".
            var placed = group
                .Where(item => item.HasExactGeometry && item.Points.Count > 0)
                .DistinctBy(item => $"{item.ZoneId}|{string.Join(';', item.Points.Select(point => FormattableString.Invariant($"{point.X:R},{point.Y:R}")))}")
                .ToArray();
            // Only what is on the map is numbered, so the letters on it run A, B, C with no gap
            // where an objective that has no place would have been. Letters, not digits: issue
            // 508. A caller that numbers its own rows (Plan's personal route order) is still
            // believed as given — that is a different, already-ordered sequence, not this one.
            var number = numberFor?.Invoke(group.Key) ??
                (placed.Length == 0 ? string.Empty : QuestObjectiveLetters.LetterFor(++sequence));
            var first = group.First();
            var objective = first.Source ?? FromProjection(first);
            if (placed.Length == 0)
            {
                entries.Add(new(
                    group.Key,
                    number,
                    objective,
                    QuestObjectivePlacement.NoLocation,
                    0,
                    "No location",
                    ReasonFor(group.ToArray()),
                    [],
                    [],
                    []));
                continue;
            }

            var candidates = placed.Where(item => item.IsPossibleLocation).ToArray();
            var authored = placed.Except(candidates).ToArray();
            var placement = authored.Length == 0 && candidates.Length > 1
                ? QuestObjectivePlacement.Candidates
                : authored.Any(item => item.GeometryKind == QuestMapGeometryKind.Region)
                    ? QuestObjectivePlacement.Area
                    : QuestObjectivePlacement.Point;
            var floorIds = FloorIdsOf(placed, floors);
            var floorNames = FloorNamesOf(placed, floors);
            var label = PlacementLabelFor(placement, placed.Length);
            var detail = DetailFor(objective, label, floorNames);
            // Issue 508: an objective can still be on the map after its own step is done (the
            // task itself runs on), and its marker says so rather than looking exactly like one
            // still outstanding.
            var isCompleted = objective.ObjectiveState == RecordedObjectiveState.Completed;
            var ids = new List<MapSceneObjectId>();
            var ordinal = 0;
            foreach (var item in placed)
            {
                ordinal++;
                var itemFloors = FloorIdsOf([item], floors);
                if (item.GeometryKind == QuestMapGeometryKind.Region && item.Points.Count >= 3)
                {
                    var outline = item.Points.Select(point => new MapScenePoint(point.X, point.Y)).ToArray();
                    var area = new MapSceneObject(
                        new($"quest:{group.Key}:{ordinal}:area"),
                        QuestsLayer,
                        MapSceneObjectKind.QuestObjective,
                        MapSceneTruthKind.PersonalPlan,
                        $"Area {number}",
                        detail,
                        new(MapSceneGeometryKind.Area, outline),
                        itemFloors,
                        provenance,
                        isCompleted: isCompleted);
                    var tag = new MapSceneObject(
                        new($"quest:{group.Key}:{ordinal}:label"),
                        QuestsLayer,
                        MapSceneObjectKind.QuestObjective,
                        MapSceneTruthKind.PersonalPlan,
                        number,
                        detail,
                        MapSceneGeometry.At(LabelPoint(outline)),
                        itemFloors,
                        provenance,
                        isCompleted: isCompleted);
                    objects.Add(area);
                    objects.Add(tag);
                    ids.Add(area.Id);
                    ids.Add(tag.Id);
                    continue;
                }

                var spot = new MapSceneObject(
                    new($"quest:{group.Key}:{ordinal}:spot"),
                    QuestsLayer,
                    MapSceneObjectKind.QuestObjective,
                    MapSceneTruthKind.PersonalPlan,
                    number,
                    detail,
                    MapSceneGeometry.At(new(item.Points[0].X, item.Points[0].Y)),
                    itemFloors,
                    provenance,
                    isCompleted: isCompleted);
                objects.Add(spot);
                ids.Add(spot.Id);
            }

            entries.Add(new(
                group.Key,
                number,
                objective,
                placement,
                placed.Length,
                label,
                null,
                floorIds,
                floorNames,
                ids));
        }

        return new(objects, entries);
    }

    /// <summary>What the objective is, in words, for the card and the list.</summary>
    public static string PlacementLabelFor(QuestObjectivePlacement placement, int places) => placement switch
    {
        QuestObjectivePlacement.Candidates => string.Create(CultureInfo.CurrentCulture, $"One of {places:N0} places"),
        // [#797] A zone is where to search, not a spot: the row must not read as an exact place.
        QuestObjectivePlacement.Area => places == 1
            ? "Somewhere in this area"
            : string.Create(CultureInfo.CurrentCulture, $"Somewhere in {places:N0} areas"),
        QuestObjectivePlacement.Point => places == 1
            ? "Marked spot"
            : string.Create(CultureInfo.CurrentCulture, $"{places:N0} spots"),
        QuestObjectivePlacement.UserPlaced => "Placed by you",
        _ => "No location",
    };

    private static string DetailFor(QuestMapObjectiveReadModel objective, string placement, IReadOnlyList<string> floorNames)
    {
        var where = floorNames.Count == 0 ? placement : $"{placement} · {string.Join(", ", floorNames)}";
        var detail = $"{objective.TaskName}\n{objective.Description}\n{where}";
        return detail.Length <= 2048 ? detail : detail[..2048];
    }

    /// <summary>
    /// Why an objective is not on the map, in words a player can use rather than the projection's
    /// own diagnostics.
    /// </summary>
    private static string ReasonFor(IReadOnlyList<QuestMapObjectiveProjection> group)
    {
        var first = group[0];
        if (first.IsUnsupported || first.Source?.IsUnsupported == true)
        {
            return "This kind of objective has no place on the map.";
        }

        if (group.All(item => item.Zone is null))
        {
            return "The catalog gives no position for it.";
        }

        if (group.Any(item => item.IsFloorFiltered))
        {
            return "It is on another floor.";
        }

        return first.Availability;
    }

    /// <summary>
    /// The floors a placed zone is on. Height decides, and a floor whose extents name areas
    /// (Customs' dorms, Interchange's mall) only claims a zone that is inside one of them, so a
    /// hill at the same height as a dorm room is not on the dorm's floor.
    /// </summary>
    private static IReadOnlyList<string> FloorIdsOf(
        IReadOnlyList<QuestMapObjectiveProjection> placed,
        IReadOnlyList<MapFloorDefinition> floors)
    {
        var ids = new List<string>();
        foreach (var item in placed)
        {
            if (item.Zone is not { } zone || HeightsOf(zone) is not { } span)
            {
                continue;
            }

            var position = PositionOf(zone);
            foreach (var floor in floors)
            {
                var onFloor = floor.Extents.Count == 0 ||
                    floor.Extents.Any(extent =>
                        InHeightRange(extent, span) &&
                        (extent.Bounds.Count == 0 || position is null ||
                         extent.Bounds.Any(bounds => bounds.Contains(position.Value.X, position.Value.Z))));
                if (onFloor && !ids.Contains(floor.Id, StringComparer.OrdinalIgnoreCase))
                {
                    ids.Add(floor.Id);
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// A point at one height must sit inside the range; a span must overlap it. The same rule
    /// <see cref="MapSceneAssembler.FloorsForHeights"/> applies to the catalog's own elements.
    /// </summary>
    private static bool InHeightRange(MapLayerExtent extent, (double Minimum, double Maximum) span) =>
        span.Minimum == span.Maximum
            ? (extent.MinimumHeight is null || span.Minimum >= extent.MinimumHeight) &&
              (extent.MaximumHeight is null || span.Minimum < extent.MaximumHeight)
            : (extent.MinimumHeight is null || span.Maximum > extent.MinimumHeight) &&
              (extent.MaximumHeight is null || span.Minimum < extent.MaximumHeight);

    /// <summary>
    /// The upper floors an objective is on, named for the player. The floor the plan opens on
    /// claims every height and is no answer to "which floor", so it is left out: an objective in a
    /// dorm room is on the 2nd Floor, and one in a field is on none of them.
    /// </summary>
    /// <remarks>
    /// Decided at the zone's middle rather than over its whole span. A trigger volume thirty metres
    /// tall (Interchange's exits) overlaps every floor and is drawn on each of them, but it is not
    /// "on the 2nd and 3rd floors" in any sense a player means.
    /// </remarks>
    private static IReadOnlyList<string> FloorNamesOf(
        IReadOnlyList<QuestMapObjectiveProjection> placed,
        IReadOnlyList<MapFloorDefinition> floors)
    {
        var names = new List<string>();
        foreach (var item in placed)
        {
            if (item.Zone is not { } zone || MiddleOf(zone) is not { } middle)
            {
                continue;
            }

            foreach (var floor in floors)
            {
                if (string.Equals(floor.Id, BaseFloorId, StringComparison.OrdinalIgnoreCase) ||
                    floor.Extents.Count == 0 ||
                    names.Contains(floor.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                if (floor.Extents.Any(extent => extent.Contains(middle)))
                {
                    names.Add(floor.Name);
                }
            }
        }

        return names;
    }

    private const string BaseFloorId = "base";

    /// <summary>Where a zone is: its position, or the middle of its outline, at its own height.</summary>
    private static WorldPosition? MiddleOf(QuestObjectiveZone zone)
    {
        if (zone.Position is { } position)
        {
            return position;
        }

        if (zone.Outline.Count == 0)
        {
            return null;
        }

        return new(
            zone.Outline.Average(point => point.X),
            zone.Outline.Average(point => point.Y),
            zone.Outline.Average(point => point.Z));
    }

    private static (double Minimum, double Maximum)? HeightsOf(QuestObjectiveZone zone)
    {
        if (zone.BottomElevation is { } bottom && zone.TopElevation is { } top)
        {
            return (bottom, top);
        }

        var heights = (zone.Outline.Count > 0 ? zone.Outline.Select(point => point.Y) : zone.Position is { } position ? [position.Y] : [])
            .Where(double.IsFinite)
            .ToArray();
        return heights.Length == 0 ? null : (heights.Min(), heights.Max());
    }

    /// <summary>Where in the world a zone is: its position, or the middle of its outline.</summary>
    private static (double X, double Z)? PositionOf(QuestObjectiveZone zone)
    {
        if (zone.Position is { } position)
        {
            return (position.X, position.Z);
        }

        return zone.Outline.Count == 0
            ? null
            : (zone.Outline.Average(point => point.X), zone.Outline.Average(point => point.Z));
    }

    /// <summary>
    /// A point inside a region to write its number at: the centroid where that is inside, and
    /// otherwise the middle of the widest stretch of the region on a level through the centroid.
    /// </summary>
    /// <remarks>
    /// The mean of an outline's vertices lands inside every convex zone and outside some
    /// concave ones, and a number sitting beside its warehouse reads as another place.
    /// </remarks>
    public static MapScenePoint LabelPoint(IReadOnlyList<MapScenePoint> outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        var centroid = Centroid(outline);
        if (Contains(outline, centroid))
        {
            return centroid;
        }

        var best = (Length: -1d, Middle: outline[0]);
        var minimumY = outline.Min(point => point.Y);
        var maximumY = outline.Max(point => point.Y);
        for (var step = 1; step < 20; step++)
        {
            var y = minimumY + ((maximumY - minimumY) * step / 20d);
            var crossings = new List<double>();
            for (var index = 0; index < outline.Count; index++)
            {
                var a = outline[index];
                var b = outline[(index + 1) % outline.Count];
                if ((a.Y > y) != (b.Y > y))
                {
                    crossings.Add(a.X + ((y - a.Y) / (b.Y - a.Y) * (b.X - a.X)));
                }
            }

            crossings.Sort();
            for (var pair = 0; pair + 1 < crossings.Count; pair += 2)
            {
                var length = crossings[pair + 1] - crossings[pair];
                if (length > best.Length)
                {
                    best = (length, new((crossings[pair] + crossings[pair + 1]) / 2, y));
                }
            }
        }

        return best.Middle;
    }

    private static MapScenePoint Centroid(IReadOnlyList<MapScenePoint> outline)
    {
        double area = 0, x = 0, y = 0;
        for (var index = 0; index < outline.Count; index++)
        {
            var a = outline[index];
            var b = outline[(index + 1) % outline.Count];
            var cross = (a.X * b.Y) - (b.X * a.Y);
            area += cross;
            x += (a.X + b.X) * cross;
            y += (a.Y + b.Y) * cross;
        }

        return Math.Abs(area) < 1e-12
            ? new(outline.Average(point => point.X), outline.Average(point => point.Y))
            : new(x / (3 * area), y / (3 * area));
    }

    private static bool Contains(IReadOnlyList<MapScenePoint> outline, MapScenePoint point)
    {
        var inside = false;
        for (int index = 0, previous = outline.Count - 1; index < outline.Count; previous = index++)
        {
            var a = outline[index];
            var b = outline[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>What a projection alone can say about its objective, for a caller that had no read model.</summary>
    private static QuestMapObjectiveReadModel FromProjection(QuestMapObjectiveProjection projection) => new(
        projection.TaskId,
        projection.TaskName,
        null,
        projection.ObjectiveId,
        0,
        projection.Description,
        projection.ObjectiveKind,
        projection.IsUnsupported,
        null,
        RecordedTaskState.Unknown,
        RecordedObjectiveState.Unknown,
        projection.IsPinned,
        false,
        null,
        string.Empty,
        null,
        projection.FoundInRaidRequired,
        [],
        [],
        projection.ItemTargets);
}
