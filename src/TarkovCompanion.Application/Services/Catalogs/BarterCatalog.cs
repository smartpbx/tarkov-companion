namespace TarkovCompanion.Application.Services.Catalogs;

/// <summary>One thing a barter asks for, or hands over.</summary>
/// <param name="ItemId">What it is.</param>
/// <param name="Count">How many.</param>
public sealed record BarterItem(string ItemId, int Count);

/// <summary>
/// One trade a trader will make, as the last sync recorded it.
/// </summary>
/// <remarks>
/// 789 of these are rewritten on every sync and no query has ever read one. They were hidden
/// from the unread-table sweep by its DELETE blind spot: the refresh clears the table before
/// repopulating it, and "DELETE FROM barters" contains "FROM barters", so the sweep counted the
/// clear as a read and excused the table to itself.
/// </remarks>
/// <param name="BarterId">The feed's id for the trade.</param>
/// <param name="TraderId">Who makes it, which is what the loyalty requirement is against.</param>
/// <param name="MinimumTraderLevel">
/// The loyalty needed, where the feed states one. Null is not "no requirement" — it is "the
/// feed did not say", and the difference matters because offering somebody a route they cannot
/// take is worse than not offering one.
/// </param>
/// <param name="TaskUnlock">A quest that unlocks it, where the feed names one.</param>
/// <param name="Gives">What the trader hands over.</param>
/// <param name="Wants">What it costs.</param>
public sealed record BarterOffer(
    string BarterId,
    string? TraderId,
    int? MinimumTraderLevel,
    string? TaskUnlock,
    BarterItem Gives,
    IReadOnlyList<BarterItem> Wants);

/// <summary>The barters the last sync stored.</summary>
public interface IBarterCatalog
{
    /// <summary>Every barter, whatever it trades.</summary>
    Task<IReadOnlyList<BarterOffer>> GetAsync(CancellationToken cancellationToken);
}
