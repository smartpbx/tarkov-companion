namespace TarkovCompanion.Application.Services.Wiki;

/// <summary>
/// Opens a wiki page the catalog already gave us. Never builds a URL from user text, and never
/// opens anything off the allowed wiki hosts — see <see cref="WikiLinkPolicy"/>.
/// </summary>
/// <remarks>
/// Small and stable on purpose: the V2 Intel workspace and the Plan workspace (package 7) both
/// depend on this one method and nothing else.
/// </remarks>
public interface IWikiLinkOpener
{
    /// <summary>
    /// Opens <paramref name="wikiUrl"/> in the system browser. Returns false, without opening
    /// anything, for null/blank input or a URL that fails <see cref="WikiLinkPolicy.IsAllowed"/>.
    /// </summary>
    bool TryOpen(string? wikiUrl);
}
