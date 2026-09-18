namespace TarkovCompanion.Application.Services.Wiki;

/// <summary>
/// The only hosts a wiki link may point at, checked before anything is ever launched.
/// </summary>
/// <remarks>
/// json.tarkov.dev's <c>wikiLink</c> field for items and tasks resolves to
/// escapefromtarkov.fandom.com today (confirmed by this repo's own fixtures in
/// <c>ReviewedExtractCatalogTests</c>). Kept as an explicit allowlist rather than "any https URL"
/// so a compromised or malformed upstream field can never make this application launch an
/// arbitrary link.
/// </remarks>
public static class WikiLinkPolicy
{
    private static readonly string[] AllowedHosts =
    [
        "escapefromtarkov.fandom.com",
    ];

    /// <summary>
    /// Who the page belongs to and what pressing the link does, for the small line beside it. The
    /// app links out and nothing else: it does not fetch, embed or copy anything from the page.
    /// </summary>
    public const string Attribution = "Escape from Tarkov Wiki · opens in your browser";

    /// <summary>True only for an absolute https URL on one of the allowed wiki hosts.</summary>
    public static bool IsAllowed(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
}
