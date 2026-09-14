namespace TarkovCompanion.Application.Services.Catalogs;

/// <summary>What one barter would cost this player, if they can take it at all.</summary>
/// <param name="BarterId">Which trade.</param>
/// <param name="TraderId">Who makes it.</param>
/// <param name="Roubles">What the items it wants are worth, added up.</param>
/// <param name="Count">How many of the wanted item it hands over.</param>
public sealed record BarterRoute(string BarterId, string? TraderId, long Roubles, int Count)
{
    /// <summary>What one of the item costs by this route.</summary>
    public long PerItem => Count <= 0 ? Roubles : Roubles / Count;
}

/// <summary>
/// Whether bartering for something beats buying it on the flea.
/// </summary>
/// <remarks>
/// <para>
/// The half of Next 1 that the trader-level stepper unblocked. Every barter's loyalty
/// requirement was unanswerable while <c>PlayerProfile.TraderLevels</c> was written by nothing,
/// so a "cheapest route" line would have had to either ignore the requirement — offering routes
/// the player cannot take — or assume level 1 and hide most of them.
/// </para>
/// <para>
/// Only routes the player can actually take are considered. A cheaper barter behind loyalty
/// they do not have is not a cheaper route, it is a different game, and putting it on screen
/// sends somebody to a trader who will not serve them.
/// </para>
/// <para>
/// Every figure here is a flea price with an observation time behind it, the way the Flea page
/// already insists. A barter whose inputs have no price is not free: it is unpriced, and it is
/// left out rather than counted as zero — which is the arithmetic that would otherwise make
/// every unpriced barter look like the cheapest route available.
/// </para>
/// </remarks>
public static class BarterRouting
{
    /// <summary>
    /// The cheapest barter for <paramref name="itemId"/> this player could actually use.
    /// </summary>
    /// <param name="itemId">What they want.</param>
    /// <param name="barters">Every barter the sync stored.</param>
    /// <param name="traderLevels">The player's loyalty, by trader id.</param>
    /// <param name="priceOf">
    /// What one of an item costs, or null where nothing has priced it. Null anywhere in a
    /// barter's inputs disqualifies that barter rather than discounting it.
    /// </param>
    public static BarterRoute? Cheapest(
        string itemId,
        IReadOnlyList<BarterOffer> barters,
        IReadOnlyDictionary<string, int> traderLevels,
        Func<string, long?> priceOf)
    {
        ArgumentNullException.ThrowIfNull(barters);
        ArgumentNullException.ThrowIfNull(traderLevels);
        ArgumentNullException.ThrowIfNull(priceOf);
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        BarterRoute? best = null;
        foreach (var barter in barters)
        {
            if (!string.Equals(barter.Gives.ItemId, itemId, StringComparison.Ordinal) ||
                !CanTake(barter, traderLevels) ||
                Cost(barter, priceOf) is not { } roubles)
            {
                continue;
            }

            var route = new BarterRoute(barter.BarterId, barter.TraderId, roubles, barter.Gives.Count);
            if (best is null || route.PerItem < best.PerItem)
            {
                best = route;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether this player's loyalty is enough for this trade.
    /// </summary>
    /// <remarks>
    /// A stated requirement is checked; an unstated one is treated as met. The feed omits the
    /// field on trades that need nothing, and refusing everything it did not describe would
    /// hide most of the catalog to guard against a case the feed does not produce.
    ///
    /// A quest unlock is deliberately not checked here. Quest state lives behind a different
    /// service, and a route this returns is labelled with the unlock rather than silently
    /// dropped — "you need Debut for this" is an answer, and "no route exists" would be a lie.
    /// </remarks>
    public static bool CanTake(BarterOffer barter, IReadOnlyDictionary<string, int> traderLevels)
    {
        ArgumentNullException.ThrowIfNull(barter);
        ArgumentNullException.ThrowIfNull(traderLevels);
        if (barter.MinimumTraderLevel is not { } required || required <= 0)
        {
            return true;
        }

        return barter.TraderId is { Length: > 0 } trader &&
            traderLevels.GetValueOrDefault(trader) >= required;
    }

    /// <summary>
    /// What the items a barter wants are worth, or null if any of them is unpriced.
    /// </summary>
    /// <remarks>
    /// Null rather than a partial sum. A barter costed from half its inputs is cheaper than
    /// every barter costed from all of theirs, so partial sums do not produce an uncertain
    /// answer — they produce a confidently wrong one, always in the same direction.
    /// </remarks>
    public static long? Cost(BarterOffer barter, Func<string, long?> priceOf)
    {
        ArgumentNullException.ThrowIfNull(barter);
        ArgumentNullException.ThrowIfNull(priceOf);

        long total = 0;
        foreach (var want in barter.Wants)
        {
            if (priceOf(want.ItemId) is not { } each)
            {
                return null;
            }

            total += each * Math.Max(1, want.Count);
        }

        return total;
    }
}
