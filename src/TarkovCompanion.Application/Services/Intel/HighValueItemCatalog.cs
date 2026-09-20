namespace TarkovCompanion.Application.Services.Intel;

/// <summary>One line of the catalog's own value ranking, with no per-profile need attached.</summary>
public sealed record HighValueItemSummary(string ItemId, long ValueRoubles);

/// <summary>
/// The catalog's own items ranked by worth, for the Intel landing page's "Highest value" row.
/// </summary>
/// <remarks>
/// Deliberately excludes anything profile-specific — a need, a pin, a recent visit — because
/// those already have their own places to come from; this is just "what is expensive," full
/// stop, the same fallback chain (flea, then the 24-hour average, then the base price) the key
/// intelligence catalog already uses for "what would this cost to replace."
/// </remarks>
public interface IHighValueItemCatalog
{
    Task<IReadOnlyList<HighValueItemSummary>> GetTopAsync(int limit, CancellationToken cancellationToken);
}
