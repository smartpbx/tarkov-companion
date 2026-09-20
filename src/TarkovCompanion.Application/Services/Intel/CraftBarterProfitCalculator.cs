namespace TarkovCompanion.Application.Services.Intel;

/// <summary>How many of one priced item a craft or barter's input line wants.</summary>
/// <param name="Count">How many the line asks for. Zero or negative is treated as one.</param>
/// <param name="UnitPriceRoubles">
/// What buying one costs, or null where the catalog has never priced it.
/// </param>
public readonly record struct IntelTradeInput(int Count, long? UnitPriceRoubles);

/// <summary>
/// What a craft or barter is worth, in the same "flea fee included" terms the Intel item detail
/// already prices a single item by.
/// </summary>
/// <remarks>
/// <para>
/// Pure and DB-free on purpose, so the arithmetic itself — not a repository mock — is what a
/// test exercises. <see cref="IntelTradeCatalogService"/> is the only caller; it resolves item
/// ids to prices and hands the numbers here.
/// </para>
/// <para>
/// The catalog has one "buy" price per item (the flea market's current lowest listing, the same
/// figure <c>HideoutBarterRoutes</c> already calls "buying") and, separately, a sell side: the
/// flea price again, net of the game's own listing fee, compared against the best trader
/// buy-back. Selling always takes the better of those two, exactly like the item detail's own
/// "best sale" figure.
/// </para>
/// <para>
/// Missing prices are never counted as zero. A craft priced from half its inputs would look
/// artificially cheap — cheaper than every craft priced from all of theirs — which is a
/// confidently wrong answer, not an uncertain one. So one unpriced input, or an unpriced output,
/// takes the whole trade's cost, value and profit to null ("unknown"), never to zero.
/// </para>
/// </remarks>
public static class CraftBarterProfitCalculator
{
    /// <summary>What buying every input costs, or null the moment any one of them is unpriced.</summary>
    public static long? InputCost(IEnumerable<IntelTradeInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        long total = 0;
        foreach (var input in inputs)
        {
            if (input.UnitPriceRoubles is not { } price)
            {
                return null;
            }

            total += price * Math.Max(1, input.Count);
        }

        return total;
    }

    /// <summary>
    /// What selling the output is worth: the better of the flea price net of its listing fee and
    /// the best trader buy-back, times how many the trade yields. Null when neither channel is
    /// priced at all.
    /// </summary>
    /// <param name="count">How many of the output the trade yields. Zero or negative is treated as one.</param>
    /// <param name="fleaPriceRoubles">The flea market's price for one, before the listing fee.</param>
    /// <param name="fleaFeeRoubles">
    /// What listing one on the flea at that price would cost. Null is "the fee is not known",
    /// never "the fee is zero" — the gross flea price stands in for the net one rather than
    /// pretending the listing is free.
    /// </param>
    /// <param name="bestTraderRoubles">The best trader buy-back for one, where any trader takes it.</param>
    public static long? OutputValue(int count, long? fleaPriceRoubles, long? fleaFeeRoubles, long? bestTraderRoubles)
    {
        var netFlea = fleaPriceRoubles is { } flea ? flea - (fleaFeeRoubles ?? 0) : (long?)null;
        long? best = (netFlea, bestTraderRoubles) switch
        {
            ({ } f, { } t) => Math.Max(f, t),
            ({ } f, null) => f,
            (null, { } t) => t,
            _ => null,
        };

        return best is { } value ? value * Math.Max(1, count) : null;
    }

    /// <summary>The output's value minus the inputs' cost, or null the moment either half is unknown.</summary>
    public static long? Profit(long? inputCost, long? outputValue) =>
        inputCost is { } cost && outputValue is { } value ? value - cost : null;
}
