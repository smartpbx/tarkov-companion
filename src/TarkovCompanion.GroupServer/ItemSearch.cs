using System.Text.Json;

namespace TarkovCompanion.GroupServer;

/// <summary>One item, as a second screen needs to read it.</summary>
/// <param name="Name">The English name, resolved from the token the catalog stores.</param>
/// <param name="Flea">The 24-hour average flea price, or null where nothing has been traded.</param>
public sealed record FoundItem(string Id, string Name, string? ShortName, int? Flea, int? Base);

/// <summary>
/// Answers "what is this worth" against the catalog the relay already mirrors.
/// </summary>
/// <remarks>
/// <para>
/// Searched here rather than on the page. The items catalog is 16,713,367 bytes, 1,887,826
/// gzipped, for 5,320 items; a tablet on a sofa is not fetching that, and the landmark endpoint
/// beside this one exists for the same reason. The relay already holds the file, so the search
/// happens where the data is and the answer is a few hundred bytes.
/// </para>
/// <para>
/// Two files, because one of them has no names in it. Every item's <c>name</c> is a token —
/// literally <c>"{id} Name"</c> — and <c>items_en</c> is the dictionary that turns it into
/// "Colt M4A1 5.56x45 assault rifle". The desktop has always fetched both; the mirror only
/// carried the first, so anything searching it would have matched ids.
/// </para>
/// <para>
/// Ranked by where the match falls rather than by a score: an item whose name starts with what
/// was typed is what was meant, and one that merely contains it somewhere is a maybe. Shorter
/// names win ties, because "Salewa" should outrank "Salewa first aid kit (empty)".
/// </para>
/// </remarks>
public sealed class ItemSearch(CatalogMirror mirror)
{
    /// <summary>The mode searched, since prices are the same question in each.</summary>
    private const string Mode = "regular";

    /// <summary>How many answers are worth returning to a page being read on a sofa.</summary>
    private const int Most = 12;

    /// <summary>Shortest query worth running, so one keystroke does not return the catalog.</summary>
    public const int ShortestQuery = 2;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<FoundItem> _index = [];
    private string? _builtFrom;

    /// <summary>What matches, best first, or nothing when the catalog cannot be read.</summary>
    public async Task<IReadOnlyList<FoundItem>> FindAsync(string? query, CancellationToken cancellationToken)
    {
        var text = query?.Trim();
        if (text is not { Length: >= ShortestQuery })
        {
            return [];
        }

        return Find(await IndexAsync(cancellationToken).ConfigureAwait(false), text);
    }

    /// <summary>
    /// What matches in an index already built, best first.
    /// </summary>
    /// <remarks>
    /// Separate from the fetching because the ranking is the part worth testing and it is pure:
    /// an index in, an order out, no catalog and no network anywhere near it.
    /// </remarks>
    public static IReadOnlyList<FoundItem> Find(IReadOnlyList<FoundItem> index, string? query)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (query?.Trim() is not { Length: >= ShortestQuery } text)
        {
            return [];
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return
        [
            .. index
                .Select(item => (Item: item, Rank: Rank(item, text, words)))
                .Where(match => match.Rank < int.MaxValue)
                .OrderBy(match => match.Rank)
                .ThenBy(match => match.Item.Name.Length)
                .ThenBy(match => match.Item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(Most)
                .Select(match => match.Item),
        ];
    }

    /// <summary>
    /// How well this item answers the query: lower is better, MaxValue is no match.
    /// </summary>
    /// <remarks>
    /// Word by word rather than as one string. Somebody looking for the Factory key types
    /// "factory key", and the item is called "Factory emergency exit key" — matching the typed
    /// text contiguously finds nothing, which is what the first version of this did. Every word
    /// has to appear somewhere; where they appear is what orders the results.
    ///
    /// The short name is what the game prints in a stash grid, so an exact one is somebody
    /// naming the thing rather than describing it.
    /// </remarks>
    private static int Rank(FoundItem item, string query, string[] words)
    {
        var shortName = item.ShortName ?? string.Empty;
        if (string.Equals(shortName, query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 0;
        }

        if (item.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 1;
        }

        // Every word, or it is not an answer. An item matching "factory" but not "key" is not
        // what "factory key" asked for, however well it matches the first half.
        var boundaries = 0;
        foreach (var word in words)
        {
            var where = Where(item.Name, word);
            if (where == Match.None)
            {
                where = Where(shortName, word);
                if (where == Match.None)
                {
                    return int.MaxValue;
                }
            }

            if (where == Match.Boundary)
            {
                boundaries++;
            }
        }

        // Every word starting a word of the name beats words buried inside longer ones, and
        // both beat a short name that merely begins with the same letters. Searching "kit"
        // means the word: "Sewing kit" is the answer and "KITECO SC-IV SA ballistic plate",
        // whose short name starts KIT, is not.
        if (boundaries == words.Length)
        {
            return 2;
        }

        return shortName.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 3 : 4;
    }

    private enum Match
    {
        None,
        Inside,
        Boundary,
    }

    /// <summary>Whether a word appears, and whether it starts a word where it does.</summary>
    private static Match Where(string text, string word)
    {
        if (text.Length == 0)
        {
            return Match.None;
        }

        var at = text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
        var inside = false;
        while (at >= 0)
        {
            if (at == 0 || !char.IsLetterOrDigit(text[at - 1]))
            {
                return Match.Boundary;
            }

            inside = true;
            at = text.IndexOf(word, at + 1, StringComparison.CurrentCultureIgnoreCase);
        }

        return inside ? Match.Inside : Match.None;
    }

    /// <summary>Builds the index once per catalog snapshot.</summary>
    private async Task<IReadOnlyList<FoundItem>> IndexAsync(CancellationToken cancellationToken)
    {
        var items = await mirror.GetAsync(Mode, "items", cancellationToken).ConfigureAwait(false);
        var names = await mirror.GetAsync(Mode, "items_en", cancellationToken).ConfigureAwait(false);
        if (items is null)
        {
            return _index;
        }

        var stamp = $"{items.ETag}/{names?.ETag}";
        if (string.Equals(_builtFrom, stamp, StringComparison.Ordinal))
        {
            return _index;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(_builtFrom, stamp, StringComparison.Ordinal))
            {
                return _index;
            }

            _index = Build(items.Body, names?.Body ?? []);
            _builtFrom = stamp;
            return _index;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // A shape this does not recognise costs the search, not the page.
            _index = [];
            _builtFrom = stamp;
            return _index;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Joins the catalog to its names.
    /// </summary>
    /// <remarks>
    /// An item whose name does not resolve keeps its token rather than being dropped, because a
    /// row reading "5447a9cd… Name" is a visible gap somebody can report, and a missing row is
    /// a search that quietly does not work.
    /// </remarks>
    public static IReadOnlyList<FoundItem> Build(ReadOnlySpan<byte> items, ReadOnlySpan<byte> names)
    {
        var translations = Translations(names);
        using var document = JsonDocument.Parse(items.ToArray());
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("items", out var array))
        {
            return [];
        }

        var found = new List<FoundItem>();
        foreach (var element in array.ValueKind == JsonValueKind.Object
            ? array.EnumerateObject().Select(entry => entry.Value)
            : array.EnumerateArray())
        {
            if (Text(element, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            var token = Text(element, "name");
            var shortToken = Text(element, "shortName");
            found.Add(new(
                id,
                Resolve(translations, token) ?? id,
                Resolve(translations, shortToken),
                Number(element, "avg24hPrice"),
                Number(element, "basePrice")));
        }

        return found;
    }

    private static string? Resolve(IReadOnlyDictionary<string, string> translations, string? token) =>
        token is { Length: > 0 } && translations.TryGetValue(token, out var name) ? name : token;

    /// <summary>The token-to-English dictionary, or empty when there is not one.</summary>
    private static IReadOnlyDictionary<string, string> Translations(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object)
            {
                root = data;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var built = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in root.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String &&
                    entry.Value.GetString() is { Length: > 0 } value)
                {
                    built[entry.Name] = value;
                }
            }

            return built;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) &&
        number > 0
            ? number
            : null;
}
