using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>One floor in a stacked view: where it sits and how prominent it is.</summary>
/// <param name="Floor">The floor itself.</param>
/// <param name="Offset">
/// How far up the canvas to draw it, in canvas units. Zero is the lowest floor in the stack.
/// </param>
/// <param name="Opacity">How solid to draw it. The chosen floor is solid; the rest are context.</param>
/// <param name="IsSelected">Whether this is the floor everything else is being read against.</param>
public sealed record FloorPlacement(
    MapFloorDefinition Floor,
    double Offset,
    double Opacity,
    bool IsSelected);

/// <summary>
/// Arranges a map's floors as a stack seen from above and to one side.
/// </summary>
/// <remarks>
/// <para>
/// The flat map answers "where", and on a map with floors it cannot answer "which floor", which
/// is the question that matters inside a building. A stack answers both at once: the floors are
/// drawn one above another in the order they are in the building, the one being read is solid,
/// and the rest are there to say what is above and below it.
/// </para>
/// <para>
/// Deliberately not a 3D scene. There is no camera, no perspective and no geometry: the floors
/// are the pictures the flat map already draws, offset and faded. That makes it a view of the
/// same data rather than a second renderer, and it means the flat view is always one toggle
/// away and always correct.
/// </para>
/// <para>
/// The order comes from the floors' own height bands rather than from the order they are listed
/// in, because a catalog that lists "Underground" last would otherwise stack it on the roof.
/// Floors with no band keep their listed order beneath everything that has one, which is where
/// a floor nobody could measure belongs.
/// </para>
/// </remarks>
public static class FloorStack
{
    /// <summary>How far apart two floors are drawn, in canvas units.</summary>
    /// <remarks>
    /// Fixed rather than proportional to the real height difference. The catalog's bands are
    /// metres of game world and they are wildly uneven -- Streets has one floor spanning five
    /// metres and another spanning nine thousand -- so a proportional stack would put two
    /// floors on top of each other and one off the screen.
    /// </remarks>
    public const double Separation = 140;

    /// <summary>How solid a floor that is not being read is drawn.</summary>
    private const double Context = 0.28;

    /// <summary>
    /// Arranges the floors, lowest first.
    /// </summary>
    /// <param name="floors">The map's floors, in the order the catalog lists them.</param>
    /// <param name="selected">The floor being read, or null for none.</param>
    public static IReadOnlyList<FloorPlacement> Arrange(
        IReadOnlyList<MapFloorDefinition> floors,
        MapFloorDefinition? selected)
    {
        ArgumentNullException.ThrowIfNull(floors);
        if (floors.Count == 0)
        {
            return [];
        }

        var ordered = floors
            .Select((floor, index) => (Floor: floor, Index: index))
            .OrderBy(entry => Elevation(entry.Floor) ?? double.NegativeInfinity)
            .ThenBy(entry => entry.Index)
            .ToArray();

        var placements = new List<FloorPlacement>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var floor = ordered[index].Floor;
            var isSelected = selected is not null &&
                string.Equals(floor.Id, selected.Id, StringComparison.OrdinalIgnoreCase);
            placements.Add(new(floor, index * Separation, isSelected ? 1 : Context, isSelected));
        }

        return placements;
    }

    /// <summary>
    /// Where a floor sits in the building, from whichever of its bands says anything.
    /// </summary>
    /// <remarks>
    /// The lowest minimum across the floor's extents, because a floor is several rectangles at
    /// several heights and the building's own ground is the lowest of them. A sentinel like
    /// -10000, which the catalog uses for "everything below", is ignored: it is a bound rather
    /// than a height, and taking it literally would drop that floor far below the rest and
    /// leave the real floors bunched at the top.
    /// </remarks>
    public static double? Elevation(MapFloorDefinition floor)
    {
        ArgumentNullException.ThrowIfNull(floor);
        double? lowest = null;
        foreach (var extent in floor.Extents)
        {
            double? candidate = null;
            if (extent.MinimumHeight is { } minimum && minimum > -1000)
            {
                candidate = minimum;
            }
            else if (extent.MaximumHeight is { } maximum && maximum < 1000)
            {
                candidate = maximum;
            }

            if (candidate is { } value && (lowest is null || value < lowest))
            {
                lowest = value;
            }
        }

        return lowest;
    }

    /// <summary>
    /// How far up the canvas the chosen floor sits, so everything else can be drawn with it.
    /// </summary>
    /// <remarks>
    /// The markers belong to the floor being read and have to move with it. Without this they
    /// would stay on the lowest plane while the map they describe rose above them.
    /// </remarks>
    public static double OffsetOf(IReadOnlyList<FloorPlacement> placements, MapFloorDefinition? selected)
    {
        ArgumentNullException.ThrowIfNull(placements);
        if (selected is null)
        {
            return 0;
        }

        foreach (var placement in placements)
        {
            if (string.Equals(placement.Floor.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
            {
                return placement.Offset;
            }
        }

        return 0;
    }
}
