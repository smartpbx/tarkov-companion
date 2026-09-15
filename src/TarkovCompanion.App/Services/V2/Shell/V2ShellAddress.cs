namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>
/// Where the shell is: one route, the item it is addressed with, and the item Intel is open on.
/// </summary>
/// <param name="Route">The page's route.</param>
/// <param name="Item">The item the route itself is addressed with (Variant A's Intel details).</param>
/// <param name="IntelItem">The item Intel is open on beside the page (Variant B only).</param>
public sealed record V2ShellLocation(V2RouteId Route, string? Item = null, string? IntelItem = null)
{
    /// <summary>The same page with Intel closed.</summary>
    public V2ShellLocation WithoutIntel => this with { IntelItem = null };
}

/// <summary>A parsed address, or why it could not be.</summary>
public sealed record V2AddressParse(V2ShellLocation? Location, string? Failure)
{
    public bool Succeeded => Location is not null;

    public static V2AddressParse Refused(string failure) => new(null, failure);
}

/// <summary>
/// Turns a location into the address a variant gives it, and back.
/// </summary>
/// <remarks>
/// Addresses are the storyboards' <c>#/raid/loot/intel/electric-drill</c> form, because that is the
/// form #265 participants copy, reopen and send to a tablet, and a native shell with a different
/// vocabulary would make the two impossible to compare. They are opaque outside the variant that
/// produced them: Variant B's <c>#/home</c> is not an address in Variant A, and parsing it there is
/// refused rather than guessed.
///
/// Anything that could reach the file system or a URL handler is refused outright. An address is
/// only ever looked up in the variant's own table.
/// </remarks>
public sealed class V2AddressCodec
{
    public const string Prefix = "#/";
    public const int MaxAddressLength = 512;
    public const int MaxItemLength = 128;
    private const string ItemPlaceholder = "{item}";
    private const string IntelSegment = "intel";

    private readonly V2ShellVariantDefinition _variant;
    private readonly V2RouteRegistry _registry;
    private readonly IReadOnlyList<(V2RouteId Route, string[] Segments)> _templates;

    public V2AddressCodec(V2ShellVariantDefinition variant, V2RouteRegistry registry)
    {
        _variant = variant ?? throw new ArgumentNullException(nameof(variant));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _templates = variant.Addresses
            .Select(pair => (Route: pair.Key, Segments: pair.Value.Split('/')))
            .ToArray();

        foreach (var (route, segments) in _templates)
        {
            var definition = registry[route];
            if (definition.TakesItem != segments.Contains(ItemPlaceholder, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Route '{route}' has an address that does not match whether it takes an item.", nameof(variant));
            }

            if (variant.IntelPlacement == V2IntelPlacement.BesideCurrentPage &&
                !definition.TakesItem &&
                segments.Contains(IntelSegment, StringComparer.Ordinal))
            {
                // "intel" marks the start of the panel suffix in this placement, so no page may use it.
                throw new ArgumentException($"Route '{route}' uses the Intel segment, which is reserved beside a page.", nameof(variant));
            }
        }
    }

    public bool IsAddressable(V2RouteId route) => _variant.Addresses.ContainsKey(route);

    public string Format(V2ShellLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!_variant.Addresses.TryGetValue(location.Route, out var template))
        {
            throw new ArgumentException($"Route '{location.Route}' has no address in {_variant.Token}.", nameof(location));
        }

        var definition = _registry[location.Route];
        if (definition.TakesItem != (location.Item is not null))
        {
            throw new ArgumentException($"Route '{location.Route}' is addressed with an item exactly when it takes one.", nameof(location));
        }

        if (location.Item is { } item && !IsValidItem(item))
        {
            throw new ArgumentException("The route item is not a bounded address segment.", nameof(location));
        }

        var path = definition.TakesItem ? template.Replace(ItemPlaceholder, location.Item, StringComparison.Ordinal) : template;
        if (location.IntelItem is { } intelItem)
        {
            if (!IsValidItem(intelItem))
            {
                throw new ArgumentException("The Intel item is not a bounded address segment.", nameof(location));
            }

            if (_variant.IntelPlacement != V2IntelPlacement.BesideCurrentPage || definition.TakesItem)
            {
                throw new ArgumentException($"Intel cannot open beside '{location.Route}' in {_variant.Token}.", nameof(location));
            }

            path = $"{path}/{_variant.Addresses[V2Routes.Item].Replace(ItemPlaceholder, intelItem, StringComparison.Ordinal)}";
        }

        return Prefix + path;
    }

    public V2AddressParse Parse(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return V2AddressParse.Refused("An address is required.");
        }

        var text = address.Trim();
        if (text.Length > MaxAddressLength)
        {
            return V2AddressParse.Refused("That address is too long to be one of this shell's.");
        }

        if (text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            text = text[Prefix.Length..];
        }
        else if (text.StartsWith('/'))
        {
            text = text[1..];
        }

        var segments = text.Split('/');
        if (segments.Any(segment => segment.Length == 0 || !IsSafeSegment(segment)))
        {
            return V2AddressParse.Refused($"'{address}' is not an address in {_variant.Token}.");
        }

        string? intelItem = null;
        if (_variant.IntelPlacement == V2IntelPlacement.BesideCurrentPage)
        {
            var at = Array.FindIndex(segments, segment => string.Equals(segment, IntelSegment, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                if (at == 0 || at != segments.Length - 2)
                {
                    return V2AddressParse.Refused($"Intel opens beside a page in {_variant.Token}: '{address}' does not name one item beside one page.");
                }

                intelItem = segments[^1];
                segments = segments[..at];
            }
        }

        foreach (var (route, template) in _templates)
        {
            if (_registry[route].TakesItem && _variant.IntelPlacement == V2IntelPlacement.BesideCurrentPage)
            {
                continue;
            }

            if (Match(template, segments) is { } matched)
            {
                return new(new V2ShellLocation(route, matched.Item, intelItem), null);
            }
        }

        return V2AddressParse.Refused($"'{address}' is not an address in {_variant.Token}.");
    }

    /// <summary>Whether an item id is one this shell will put in an address.</summary>
    public static bool IsValidItem(string? item) => !string.IsNullOrEmpty(item) && IsSafeSegment(item);

    private static bool IsSafeSegment(string segment) =>
        segment.Length <= MaxItemLength &&
        segment is not "." and not ".." &&
        segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static (bool Matched, string? Item)? Match(string[] template, string[] segments)
    {
        if (template.Length != segments.Length)
        {
            return null;
        }

        string? item = null;
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] == ItemPlaceholder)
            {
                item = segments[index];
            }
            else if (!string.Equals(template[index], segments[index], StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return (true, item);
    }
}
