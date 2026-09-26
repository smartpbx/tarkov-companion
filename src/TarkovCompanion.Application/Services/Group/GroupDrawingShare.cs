using System.Text.Json.Serialization;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.Application.Services.Group;

/// <summary>
/// [#286] One line drawn on a map, as it crosses the relay: world x and z, in the order drawn.
/// </summary>
/// <param name="Id">The sender's own id for the line, so a receiver can tell two apart.</param>
/// <param name="MapId">The map it was drawn on.</param>
/// <param name="FloorId">The catalog floor it was drawn on, or null for the whole map.</param>
/// <param name="Points">World positions (x, z), at most <see cref="RaidDrawingLimits.MaximumPoints"/>.</param>
/// <param name="Width">[#919] The line's width in pixels, or null when the sender predates the choice.</param>
public sealed record GroupDrawingView(string Id, string MapId, string? FloorId, IReadOnlyList<(double X, double Z)> Points, int? Width = null);

/// <summary>
/// [#286] The lines this player shares with the squad, set by the Raid map and read by the
/// group session on each exchange.
/// </summary>
/// <remarks>
/// The member publishes its own lines with its own state, rather than posting each one like a
/// waypoint: a line belongs to whoever drew it, so nobody else can remove it, and it leaves the
/// room with them. No lifetime crosses the wire — the sender stops publishing a line when it
/// expires here, and two machines' clocks never have to agree (Clayton's ran four hours apart).
/// </remarks>
public sealed class GroupDrawingShare
{
    private IReadOnlyList<GroupDrawingView> _current = [];

    public IReadOnlyList<GroupDrawingView> Current => Volatile.Read(ref _current);

    /// <summary>Raised after <see cref="Current"/> changes, so the group hears now rather than on a tick.</summary>
    public event Action? Changed;

    public void Set(IReadOnlyList<GroupDrawingView> drawings)
    {
        ArgumentNullException.ThrowIfNull(drawings);
        var previous = Volatile.Read(ref _current);
        if (SameLines(previous, drawings))
        {
            return;
        }

        Volatile.Write(ref _current, drawings);
        Changed?.Invoke();
    }

    private static bool SameLines(IReadOnlyList<GroupDrawingView> left, IReadOnlyList<GroupDrawingView> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair =>
            pair.First.Id == pair.Second.Id &&
            pair.First.MapId == pair.Second.MapId &&
            pair.First.FloorId == pair.Second.FloorId &&
            pair.First.Width == pair.Second.Width &&
            pair.First.Points.SequenceEqual(pair.Second.Points));
}

/// <summary>[#286] One shared line: <c>points</c> is x0, z0, x1, z1, … in world metres.</summary>
public sealed record GroupDrawingDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mapId")] string MapId,
    [property: JsonPropertyName("points")] IReadOnlyList<double>? Points)
{
    [JsonPropertyName("floor")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Floor { get; init; }

    /// <summary>
    /// [#919] The line's width in pixels. Optional both ways: an older companion neither sends nor
    /// reads it, and an older relay drops it, so the line arrives at the old width.
    /// </summary>
    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; init; }
}

/// <summary>[#286] Shared lines to and from the relay's member state.</summary>
public static class GroupDrawingWire
{
    /// <summary>[#286] Our shared lines as the wire carries them: a flat x, z list at 0.1 m.</summary>
    public static IReadOnlyList<GroupDrawingDto>? Describe(IReadOnlyList<GroupDrawingView> drawings)
    {
        if (drawings.Count == 0)
        {
            return null;
        }

        return [.. drawings
            .Take(RaidDrawingLimits.MaximumSharedStrokes)
            .Where(drawing => drawing.Points.Count is >= 2 and <= RaidDrawingLimits.MaximumPoints)
            .Select(drawing => new GroupDrawingDto(
                drawing.Id,
                drawing.MapId,
                [.. drawing.Points.SelectMany(point => new[] { Math.Round(point.X, 1), Math.Round(point.Z, 1) })])
            {
                Floor = drawing.FloorId,
                Width = RaidDrawingWidths.Read(drawing.Width),
            })];
    }

    /// <summary>
    /// [#286] A squadmate's lines, held to the relay's bounds again here: an older relay passes
    /// nothing, and a relay is not the only thing that could have written this.
    /// </summary>
    public static IReadOnlyList<GroupDrawingView> Read(IReadOnlyList<GroupDrawingDto>? drawings)
    {
        if (drawings is null)
        {
            return [];
        }

        var result = new List<GroupDrawingView>();
        foreach (var drawing in drawings.Take(RaidDrawingLimits.MaximumSharedStrokes))
        {
            if (drawing is not { Id.Length: > 0 and <= 64, MapId.Length: > 0 and <= 64 } ||
                drawing.Points is not { Count: >= 4 } points ||
                points.Count % 2 != 0 ||
                points.Count > RaidDrawingLimits.MaximumPoints * 2 ||
                points.Any(value => !double.IsFinite(value)))
            {
                continue;
            }

            var pairs = new (double X, double Z)[points.Count / 2];
            for (var index = 0; index < pairs.Length; index++)
            {
                pairs[index] = (points[index * 2], points[index * 2 + 1]);
            }

            result.Add(new(
                drawing.Id,
                drawing.MapId,
                drawing.Floor is { Length: > 0 and <= 64 } floor ? floor : null,
                pairs,
                RaidDrawingWidths.Read(drawing.Width)));
        }

        return result;
    }
}
