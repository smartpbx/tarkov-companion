using System.Text.Json;
using System.Text.Json.Serialization;

namespace TarkovCompanion.GroupServer;

/// <summary>
/// The places on a map worth recognising, in world coordinates.
/// </summary>
/// <param name="Kind"><c>e</c> extract, <c>t</c> transit, <c>l</c> lock, <c>p</c> place.</param>
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
/// <para>
/// Places come from a second file. The names a player actually says — Power Station, Main
/// Office, Dorms — are in the map artwork's label layer, which lives in the-hideout's
/// <c>maps.json</c> and not in the game-data catalog this server mirrors. The desktop has read
/// that file since the map was drawn, and <c>WaypointNaming</c> names a mark from it, so without
/// it a mark made on the tablet could never read the same as one made at the desk. It is 109,867
/// bytes and yields 303 labels across ten maps; losing it costs the place names and nothing else.
/// </para>
/// </remarks>
public sealed class Landmarks(CatalogMirror mirror, IHttpClientFactory? clients = null)
{
    /// <summary>The name this class's own HTTP client is registered under.</summary>
    public const string HttpClientName = "landmark-places";

    /// <summary>The mode whose catalog the landmarks come from.</summary>
    /// <remarks>
    /// Geometry does not differ by game mode — an extract is in the same place in PvE — so one
    /// mode is fetched rather than three kept in step.
    /// </remarks>
    private const string Mode = "regular";

    /// <summary>Where the map artwork's label layer lives.</summary>
    /// <remarks>
    /// The same file, from the same place, that the desktop's map catalog reads. A second copy
    /// of these names somewhere else would be a second thing to be wrong.
    /// </remarks>
    private const string PlacesUrl =
        "https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json";

    /// <summary>How long the label layer is held before asking again.</summary>
    /// <remarks>
    /// The same hour the catalog mirror holds its snapshot, so the two halves of an answer are
    /// never more than an hour apart in age.
    /// </remarks>
    private static readonly TimeSpan PlacesFor = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, IReadOnlyList<Landmark>>? _cached;
    private string? _cachedFrom;
    private IReadOnlyDictionary<string, IReadOnlyList<Landmark>> _places =
        new Dictionary<string, IReadOnlyList<Landmark>>(StringComparer.Ordinal);
    private DateTimeOffset _placesUtc = DateTimeOffset.MinValue;

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
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var places = await PlacesAsync(timeProvider, cancellationToken).ConfigureAwait(false);
        var snapshot = await mirror.GetAsync(Mode, "maps", cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            // The label layer alone is still worth drawing: it is the half with the names on it.
            return _cached ?? places;
        }

        var stamp = $"{snapshot.ETag}/{places.Count}";
        if (_cached is { } held && string.Equals(_cachedFrom, stamp, StringComparison.Ordinal))
        {
            return held;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } again && string.Equals(_cachedFrom, stamp, StringComparison.Ordinal))
            {
                return again;
            }

            var built = Build(snapshot.Body);
            Merge(built, places);
            _cached = built;
            _cachedFrom = stamp;
            return built;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // A shape this does not recognise costs the landmarks, not the page.
            var empty = new Dictionary<string, IReadOnlyList<Landmark>>(StringComparer.Ordinal);
            Merge(empty, places);
            _cached = empty;
            _cachedFrom = stamp;
            return empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds each map's place names to whatever the catalog gave it.</summary>
    private static void Merge(
        Dictionary<string, IReadOnlyList<Landmark>> into,
        IReadOnlyDictionary<string, IReadOnlyList<Landmark>> places)
    {
        foreach (var (map, named) in places)
        {
            into[map] = into.TryGetValue(map, out var existing) ? [.. existing, .. named] : named;
        }
    }

    /// <summary>
    /// The map artwork's label layer, held for an hour.
    /// </summary>
    /// <remarks>
    /// Its own fetch rather than the mirror's, because it is a different file from a different
    /// host and the mirror's allowlist exists to keep it that way. A failure returns whatever
    /// was last read, or nothing, and costs the place names alone.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<Landmark>>> PlacesAsync(
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now - _placesUtc < PlacesFor || clients is null)
        {
            return _places;
        }

        try
        {
            using var client = clients.CreateClient(HttpClientName);
            var body = await client.GetByteArrayAsync(PlacesUrl, cancellationToken).ConfigureAwait(false);
            _places = BuildPlaces(body);
            _placesUtc = now;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Asked again next hour rather than every request, so an upstream that is down does
            // not turn every page load into a failed fetch.
            _placesUtc = now;
        }

        return _places;
    }

    /// <summary>
    /// Reads the label layer out of the map artwork file.
    /// </summary>
    /// <remarks>
    /// A label's position is two numbers and the second is world Z, which is how the desktop's
    /// projection reads it when it draws them. A label is laid out as artwork, so several carry
    /// line breaks to sit inside a building; those are flattened, because a name goes in a list
    /// where a newline is a hole.
    /// </remarks>
    public static Dictionary<string, IReadOnlyList<Landmark>> BuildPlaces(ReadOnlySpan<byte> body)
    {
        using var document = JsonDocument.Parse(body.ToArray());
        var root = document.RootElement;
        // The artwork file's root is a bare array. TryGetProperty on an array throws rather
        // than returning false, so the kind is checked first -- which is how the first version
        // of this read nothing at all and said nothing about it.
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("maps", out var inner))
        {
            root = inner;
        }

        var built = new Dictionary<string, IReadOnlyList<Landmark>>(StringComparer.Ordinal);
        var maps = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToArray(),
            JsonValueKind.Object => root.EnumerateObject().Select(entry => entry.Value).ToArray(),
            _ => [],
        };

        foreach (var map in maps)
        {
            if (map.ValueKind != JsonValueKind.Object ||
                Text(map, "normalizedName") is not { Length: > 0 } name ||
                !map.TryGetProperty("maps", out var variants) ||
                variants.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var found = new List<Landmark>();
            foreach (var variant in variants.EnumerateArray())
            {
                if (variant.ValueKind != JsonValueKind.Object ||
                    !variant.TryGetProperty("labels", out var labels) ||
                    labels.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var label in labels.EnumerateArray())
                {
                    if (label.ValueKind != JsonValueKind.Object ||
                        Text(label, "text") is not { Length: > 0 } text ||
                        !label.TryGetProperty("position", out var position) ||
                        position.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var pair = position.EnumerateArray().ToArray();
                    if (pair.Length < 2 ||
                        !pair[0].TryGetDouble(out var x) ||
                        !pair[1].TryGetDouble(out var z) ||
                        !double.IsFinite(x) ||
                        !double.IsFinite(z))
                    {
                        continue;
                    }

                    found.Add(new("p", Tidy(text), null, Math.Round(x, 1), Math.Round(z, 1)));
                }
            }

            if (found.Count > 0)
            {
                built[name] = found;
            }
        }

        return built;
    }

    /// <summary>Flattens a label the map draws across two lines into one a list can print.</summary>
    private static string Tidy(string text) => string.Join(
        ' ',
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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
