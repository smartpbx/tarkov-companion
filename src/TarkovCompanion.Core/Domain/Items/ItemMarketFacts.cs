namespace TarkovCompanion.Core.Domain.Items;

/// <summary>
/// The two rates the game charges on a flea listing, as json.tarkov.dev publishes them.
/// </summary>
/// <remarks>
/// They sit beside the items in the same payload, under <c>data.fleaMarket</c>, as
/// <c>sellOfferFeeRate</c> and <c>sellRequirementFeeRate</c>. Both read 0.05 on 2026-09-19. The
/// Loot Scan used to say the source published neither, which was never checked, and every flea
/// item was left "valued, not decided" on the strength of it.
/// </remarks>
public sealed record FleaMarketRates
{
    public FleaMarketRates(double sellOfferFeeRate, double sellRequirementFeeRate, DateTimeOffset observedUtc)
    {
        SellOfferFeeRate = Rate(sellOfferFeeRate, nameof(sellOfferFeeRate));
        SellRequirementFeeRate = Rate(sellRequirementFeeRate, nameof(sellRequirementFeeRate));
        ObservedUtc = observedUtc.ToUniversalTime();
    }

    public double SellOfferFeeRate { get; }

    public double SellRequirementFeeRate { get; }

    public DateTimeOffset ObservedUtc { get; }

    private static double Rate(double value, string parameterName) =>
        double.IsFinite(value) && value is >= 0 and <= 1
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "A fee rate is a fraction between zero and one.");
}

/// <summary>
/// What the catalog says about getting and selling one item, beyond its price.
/// </summary>
/// <param name="BasePriceRoubles">The game's own base price, which the flea fee is charged against.</param>
/// <param name="LastOfferCount">How many flea listings the last scan of the market saw. Null where unpublished.</param>
/// <param name="TraderSellsForCash">A trader sells it for money without a quest unlocking the offer.</param>
/// <param name="ObservedUtc">
/// When the source last updated this item, which is when its market figures were taken.
/// </param>
/// <param name="CatalogSyncedUtc">
/// When the items catalog was last synced, where that is recorded. The source stamps an item
/// only when its market figures move, and 1,476 of 5,442 items on 2026-09-19 carried a stamp
/// over a week old: everything with no flea listing. What a trader pays and the base price do
/// not move with the market, so they are as fresh as the last sync that confirmed them, and
/// dating them by the stamp would call a quarter of the catalog expired for ever.
/// </param>
public sealed record ItemMarketFacts(
    string ItemId,
    long? BasePriceRoubles,
    int? LastOfferCount,
    bool TraderSellsForCash,
    DateTimeOffset ObservedUtc,
    DateTimeOffset? CatalogSyncedUtc = null);

/// <summary>
/// The fee the game takes for listing an item on the flea market.
/// </summary>
/// <remarks>
/// <para>
/// The game does not publish this formula. It is the one the community worked out and the EFT
/// wiki documents, and tarkov.dev's own calculator uses the same one:
/// </para>
/// <code>
/// fee = Q * (VO * Ti * 4^PO + VR * Tr * 4^PR)
/// </code>
/// <para>
/// VO is the item's base price and VR the asking price, both for one item; Q is how many. PO is
/// log10(VO / VR) and PR is log10(VR / VO). Whichever of the two is positive is raised to the
/// power 1.08: PO when asking under the base price, PR when asking at or over it. Ti and Tr are
/// the two published rates.
/// </para>
/// <para>
/// One anchor needs no trust in the curve: asking exactly the base price makes both exponents
/// zero, and the fee is base price times the sum of the rates. The tests hold that.
/// </para>
/// <para>
/// Not modelled, and both only ever lower the fee: the Intelligence Center's discount and the
/// Hideout Management skill. So this figure is the most the game would charge, and a net price
/// taken from it is the least the listing would return.
/// </para>
/// </remarks>
public static class FleaMarketFee
{
    private const double PenaltyExponent = 1.08;

    public static long Calculate(long basePriceRoubles, long askingPriceRoubles, int quantity, FleaMarketRates rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(basePriceRoubles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(askingPriceRoubles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);

        var offerExponent = Math.Log10((double)basePriceRoubles / askingPriceRoubles);
        var requirementExponent = Math.Log10((double)askingPriceRoubles / basePriceRoubles);
        if (askingPriceRoubles < basePriceRoubles)
        {
            offerExponent = Math.Pow(offerExponent, PenaltyExponent);
        }
        else
        {
            requirementExponent = Math.Pow(requirementExponent, PenaltyExponent);
        }

        var perItem =
            (basePriceRoubles * rates.SellOfferFeeRate * Math.Pow(4, offerExponent)) +
            (askingPriceRoubles * rates.SellRequirementFeeRate * Math.Pow(4, requirementExponent));
        return checked((long)Math.Round(perItem * quantity, MidpointRounding.AwayFromZero));
    }
}
