using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// The places on a map worth recognising, in world coordinates.
/// </summary>
/// <param name="Kind"><c>e</c> extract, <c>t</c> transit, <c>l</c> lock.</param>
/// <param name="Name">What to write beside it, or null for something with no name worth writing.</param>
/// <param name="Faction">Who may use it, for an extract. Null where it does not apply.</param>
/// <remarks>
/// The wire names are one character each, and so are the kinds. There are 468 of these and the
/// property names would otherwise be a third of the payload: spelling them out costs 11 KB of
/// 30 KB for something no person reads. A null name and a null faction are omitted entirely,
/// which is most of the locks.
/// </remarks>
public sealed record Landmark(
    [property: JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("n")] string? Name,
    [property: JsonPropertyName("f")] string? Faction,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z);

/// <summary>
/// Turns the mirrored map catalog into the few hundred points a schematic can draw.
/// </summary>
/// <remarks>
/// <para>
/// The tablet plots dots at world coordinates and says on the page that it is not the map,
/// which is honest and leaves it very hard to recognise where anybody is. Landmarks are what
/// turn a plot of dots into a place: the extract names are the words the group already uses.
/// </para>
/// <para>
/// Derived here rather than fetched whole by the page. The plan said the tablet should fetch
/// <c>/catalog/regular/maps</c> directly — it is already allowlisted and already carries what
/// is needed — and that was written before anybody measured it. That payload is 8,542,745 bytes,
/// 780,279 gzipped. What a schematic can actually draw is 19,829 bytes, 4,588 gzipped: every
/// landmark of every map, 170 times smaller than one map catalog. The difference is a page on
/// somebody's sofa over mobile data, and this page's whole argument is that it is small because
/// small is the right size.
/// </para>
/// <para>
/// Only extracts carry a name anybody would recognise. A transit's <c>description</c> is a
/// translation token (<c>CUS_TRANSIT_9_DESC</c>), so it is named for where it goes, which the
/// catalog does say. A lock is a <c>lockType</c> of "door" thirty-six times over, so it is drawn
/// and not labelled. Spawns are left out entirely: 278 nameless points on Customs alone is not
/// a landmark, it is a texture.
/// </para>
/// </remarks>
public sealed class Landmarks(CatalogMirror mirror)
{
    /// <summary>The mode whose catalog the landmarks come from.</summary>
    /// <remarks>
    /// Geometry does not differ by game mode — an extract is in the same place in PvE — so one
    /// mode is fetched rather than three kept in step.
    /// </remarks>
    private const string Mode = "regular";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, IReadOnlyList<Landmark>>? _cached;
    private string? _cachedFrom;

    /// <summary>
    /// Every map's landmarks, keyed by the map's normalised name.
    /// </summary>
    /// <remarks>
    /// Recomputed only when the mirror's snapshot changes, which it does about once an hour.
    /// An empty answer is returned rather than an error when the catalog cannot be read: the
    /// schematic drew nothing before this existed and is no worse without it, whereas a page
    /// that fails to load because the landmark fetch failed would be.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Landmark>>> GetAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await mirror.GetAsync(Mode, "maps", cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return _cached ?? new Dictionary<string, IReadOnlyList<Landmark>>(StringComparer.Ordinal);
        }

        if (_cached is { } held && string.Equals(_cachedFrom, snapshot.ETag, StringComparison.Ordinal))
        {
            return held;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } again && string.Equals(_cachedFrom, snapshot.ETag, StringComparison.Ordinal))
            {
                return again;
            }

            var built = Build(snapshot.Body);
            _cached = built;
            _cachedFrom = snapshot.ETag;
            return built;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A shape this does not recognise costs the landmarks, not the page.
            var empty = new Dictionary<string, IReadOnlyList<Landmark>>(StringComparer.Ordinal);
            _cached = empty;
            _cachedFrom = snapshot.ETag;
            return empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the catalog once and keeps what can be drawn.</summary>
    public static Dictionary<string, IReadOnlyList<Landmark>> Build(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("maps", out var maps) ||
            maps.ValueKind != JsonValueKind.Object)
        {
            return new(StringComparer.Ordinal);
        }

        // A transit says which map it leads to by id, and the name people use is the other
        // map's. Read first so a transit can be named on the single pass below.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in maps.EnumerateObject())
        {
            if (Text(entry.Value, "id") is { Length: > 0 } id &&
                Text(entry.Value, "normalizedName") is { Length: > 0 } normalized)
            {
                names[id] = normalized;
            }
        }

        var built = new Dictionary<string, IReadOnlyList<Landmark>>(StringComparer.Ordinal);
        foreach (var entry in maps.EnumerateObject())
        {
            if (Text(entry.Value, "normalizedName") is not { Length: > 0 } map)
            {
                continue;
            }

            var found = new List<Landmark>();
            Add(entry.Value, "extracts", found, element =>
                new("e", Text(element, "name"), Text(element, "faction"), 0, 0));
            Add(entry.Value, "transits", found, element =>
                new("t", names.TryGetValue(Text(element, "map") ?? string.Empty, out var to) ? $"Transit to {to}" : "Transit", null, 0, 0));
            Add(entry.Value, "locks", found, _ => new("l", null, null, 0, 0));

            if (found.Count > 0)
            {
                built[map] = found;
            }
        }

        return built;
    }

    /// <summary>
    /// Adds every member of one array that has a position, shaped by the caller.
    /// </summary>
    /// <remarks>
    /// The position is applied here rather than in each shaper, because "has a usable position"
    /// is the one rule all three share and the place a missing coordinate would otherwise become
    /// a zero that looks like a real place.
    /// </remarks>
    private static void Add(
        JsonElement map,
        string property,
        List<Landmark> into,
        Func<JsonElement, Landmark> shape)
    {
        if (!map.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("position", out var position) ||
                position.ValueKind != JsonValueKind.Object ||
                !Number(position, "x", out var x) ||
                !Number(position, "z", out var z))
            {
                continue;
            }

            into.Add(shape(element) with { X = Math.Round(x, 1), Z = Math.Round(z, 1) });
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Number(JsonElement element, string property, out double value)
    {
        value = 0;
        return element.TryGetProperty(property, out var found) &&
            found.ValueKind == JsonValueKind.Number &&
            found.TryGetDouble(out value) &&
            double.IsFinite(value);
    }
}
