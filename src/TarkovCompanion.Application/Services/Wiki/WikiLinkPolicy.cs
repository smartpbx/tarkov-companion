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

    /// <summary>True only for an absolute https URL on one of the allowed wiki hosts.</summary>
    public static bool IsAllowed(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
}
