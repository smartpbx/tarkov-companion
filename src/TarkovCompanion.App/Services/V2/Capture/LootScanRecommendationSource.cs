using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Recommendations;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// Turns each item a scan named into the inputs the recommendation engine asks for, using only
/// what the companion actually holds.
/// </summary>
/// <remarks>
/// <para>
/// The handoff used to pass the decision service an empty list, so every named item came back
/// "no recommendation was produced". Then it passed catalog prices and nothing else, and said
/// three things it had not checked: that the item was not pinned, not protected and had no rule,
/// each as a known "false" at full confidence. The profile holds all three. A pinned item was
/// shown as unpinned and weighed on price. They are read from the profile now, and where no
/// profile is given they are unread, never false.
/// </para>
/// <para>
/// What else is read now, and from where: what quests and the hideout still need
/// (<see cref="LootScanNeedSource"/>), the wishlist, the flea fee (<see cref="FleaMarketFee"/>,
/// from the base price and the two rates json.tarkov.dev publishes beside the items), how
/// readily another copy is had, and the raid phase and risk
/// (<see cref="LootScanRaidContextSource"/>). Each is optional here so a caller that has only a
/// catalog still gets a valuation; whatever is not supplied stays unread and the engine
/// declines on it, which is what "valued, not decided" means.
/// </para>
/// <para>
/// How readily another copy is had is a band, from two published facts: a trader sells it for
/// money with no quest gating the offer (abundant), or the last market scan saw ten or more
/// listings (available); anything else is limited. "Scarce" is never claimed. A flea ban is a
/// rule about selling, not a measure of rarity, and nothing in the catalog measures rarity, so
/// scarcity alone never turns an item into a take.
/// </para>
/// </remarks>
public sealed class LootScanRecommendationSource(
    IItemRepository items,
    IItemMarketFactSource? market = null,
    LootScanNeedSource? needs = null,
    LootScanRaidContextSource? raidContext = null)
{
    /// <summary>Listings at the last market scan from which another copy counts as readily bought.</summary>
    internal const int ReadilyListedOffers = 10;

    private static readonly ProducerIdentity Producer = new("Tarkov Companion loot scan", "loot-scan-recommendation-source-2");

    private readonly IItemRepository _items = items ?? throw new ArgumentNullException(nameof(items));

    /// <summary>The raid phase and risk for this evaluation, or null where nothing supplies them.</summary>
    public async Task<RecommendationRaidContext?> ReadRaidContextAsync(DateTimeOffset evaluatedUtc, CancellationToken cancellationToken) =>
        raidContext is null
            ? null
            : await raidContext.ReadAsync(evaluatedUtc, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<LootScanCandidateRecommendation>> BuildAsync(
        GridReconstructionResult visibleLoot,
        CaptureSessionId sessionId,
        string artifactId,
        int decodeRevision,
        string contentSha256,
        InventoryProfileScope scope,
        string dataSnapshotId,
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken,
        ProfileRecord? profile = null)
    {
        ArgumentNullException.ThrowIfNull(visibleLoot);
        var cells = visibleLoot.Recognition?.Cells ?? [];
        if (cells.Count == 0)
        {
            return [];
        }

        var rates = await ReadRatesAsync(cancellationToken).ConfigureAwait(false);
        var needSnapshot = await ReadNeedsAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<LootScanCandidateRecommendation>();
        foreach (var cell in cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReadAsync(cell, rates, evaluatedUtc, cancellationToken).ConfigureAwait(false) is not { } read)
            {
                continue;
            }

            candidates.Add(new(
                new LootScanEvidenceBinding(sessionId, artifactId, decodeRevision, contentSha256, cell.Anchor, read.ItemId),
                $"loot-scan-{cell.Anchor.Row}-{cell.Anchor.Column}",
                scope,
                dataSnapshotId,
                ProfileFacts(profile, needSnapshot, read.ItemId, evaluatedUtc),
                read.Economics,
                new RecommendationScarcityFacts(Obtainability(read, evaluatedUtc))));
        }

        return candidates;
    }

    /// <summary>
    /// What may be dropped from the carried grid to make room, and what dropping it gives up.
    /// </summary>
    /// <remarks>
    /// A carried item the profile pins or protects is reported as such and the planner never
    /// offers it. One whose worth cannot be settled gets no value, and the planner then leaves
    /// it where it is rather than trade it away on a guess.
    /// </remarks>
    public async Task<IReadOnlyList<LootScanCarriedPolicy>> BuildCarriedPoliciesAsync(
        GridReconstructionResult carried,
        CaptureSessionId sessionId,
        string artifactId,
        int decodeRevision,
        string contentSha256,
        DateTimeOffset evaluatedUtc,
        ProfileRecord? profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(carried);
        var cells = carried.Recognition?.Cells ?? [];
        if (cells.Count == 0 || profile is null)
        {
            return [];
        }

        var rates = await ReadRatesAsync(cancellationToken).ConfigureAwait(false);
        var chosen = ProfileProvenance(profile, evaluatedUtc);
        var policies = new List<LootScanCarriedPolicy>();
        foreach (var cell in cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReadAsync(cell, rates, evaluatedUtc, cancellationToken).ConfigureAwait(false) is not { } read)
            {
                continue;
            }

            var net = read.Economics.FleaNetRoubles;
            var trader = read.Economics.TraderRoubles;
            var settled = Settled(net) && Settled(trader) && (net.Value is not null || trader.Value is not null);
            var worth = Math.Max(net.Value ?? 0, trader.Value ?? 0);
            policies.Add(new(
                new LootScanEvidenceBinding(sessionId, artifactId, decodeRevision, contentSha256, cell.Anchor, read.ItemId),
                Known<bool?>("carried.protected", LootScanProfileRules.IsProtected(profile.Progress, read.ItemId), chosen),
                Known<bool?>("carried.pinned", LootScanProfileRules.IsPinned(profile.Progress, read.ItemId), chosen),
                settled
                    ? Known<long?>(
                        "carried.replacement-value",
                        worth,
                        (net.Value ?? 0) >= (trader.Value ?? 0) && net.Value is not null ? net.Provenance : trader.Provenance)
                    : Unread<long?>("carried.replacement-value", "replacement-value.unsettled", read.Unknown)));
        }

        return policies;
    }

    private static bool Settled(EvidencedValue<long?> field) =>
        field.Status.Completeness is ResultCompleteness.Complete or ResultCompleteness.Unavailable;

    private async Task<FleaMarketRates?> ReadRatesAsync(CancellationToken cancellationToken)
    {
        if (market is null)
        {
            return null;
        }

        try
        {
            return await market.GetFleaRatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Rates that cannot be read leave the fee unread, which is what it was before.
            return null;
        }
    }

    private async Task<LootScanNeedSnapshot?> ReadNeedsAsync(CancellationToken cancellationToken)
    {
        if (needs is null)
        {
            return null;
        }

        try
        {
            return await needs.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The same facts for an item seen somewhere other than a loot grid: a stash tile.
    /// </summary>
    /// <remarks>
    /// #283's sort plan asks the engine the same questions about the same item, so it reads
    /// the same facts from the same places rather than keeping a second copy of the rules for
    /// what a price or a pin means. Null where the catalog does not know the item.
    /// </remarks>
    public async Task<ItemAdviceFacts?> ReadItemFactsAsync(
        ObservedStashItem item,
        ProfileRecord? profile,
        LootScanNeedSnapshot? needSnapshot,
        FleaMarketRates? rates,
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var read = await ReadAsync(
                item.ItemId,
                item.Width,
                item.Height,
                item.Quantity,
                item.Footprint,
                conditionApplies: null,
                rates,
                evaluatedUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return read is null
            ? null
            : new(
                ProfileFacts(profile, needSnapshot, read.ItemId, evaluatedUtc),
                read.Economics,
                new RecommendationScarcityFacts(Obtainability(read, evaluatedUtc)));
    }

    /// <summary>The flea rates and the player's needs, read once for a whole stash.</summary>
    public async Task<(FleaMarketRates? Rates, LootScanNeedSnapshot? Needs)> ReadSharedFactsAsync(CancellationToken cancellationToken) =>
        (await ReadRatesAsync(cancellationToken).ConfigureAwait(false),
         await ReadNeedsAsync(cancellationToken).ConfigureAwait(false));

    private Task<ItemRead?> ReadAsync(
        GridCellRecognition cell,
        FleaMarketRates? rates,
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        if (cell.Item.Value is not { } item ||
            cell.Item.Status.Completeness != ResultCompleteness.Complete ||
            item.CanonicalId.Value is not { } itemId ||
            item.WidthCells.Value is not { } width ||
            item.HeightCells.Value is not { } height ||
            item.Quantity.Value is not { } quantity)
        {
            return Task.FromResult<ItemRead?>(null);
        }

        return ReadAsync(
            itemId,
            width,
            height,
            quantity,
            item.WidthCells.Provenance,
            item.Condition.Value == ItemConditionReading.NotApplicable ? false : null,
            rates,
            evaluatedUtc,
            cancellationToken);
    }

    /// <param name="conditionApplies">False where the reading says the item has no condition; null where nothing says.</param>
    private async Task<ItemRead?> ReadAsync(
        string itemId,
        int width,
        int height,
        int quantity,
        EvidenceProvenance footprint,
        bool? conditionApplies,
        FleaMarketRates? rates,
        DateTimeOffset evaluatedUtc,
        CancellationToken cancellationToken)
    {
        var definition = await _items.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        var price = await _items.GetPriceAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (definition is null || price is null)
        {
            return null;
        }

        ItemMarketFacts? facts = null;
        if (market is not null)
        {
            try
            {
                facts = await market.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                facts = null;
            }
        }

        // A price synced after this evaluation began cannot be evidence for it.
        var observedUtc = Earlier(price.Provenance.ObservedUtc, evaluatedUtc);
        var catalog = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            $"json.tarkov.dev/items/{itemId}",
            observedUtc,
            EvidenceConfidence.Certain,
            Producer);
        var unknown = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            $"json.tarkov.dev/items/{itemId}",
            observedUtc,
            EvidenceConfidence.Unscored,
            Producer);

        // What a trader pays does not move with the market, and the source stamps an item only
        // when its market figures do, so a trader-only item carries a stamp weeks old. It is
        // as fresh as the last catalog sync that confirmed it, where that is known.
        var confirmedUtc = StaticFactsUtc(observedUtc, facts, evaluatedUtc);
        var confirmed = confirmedUtc == observedUtc
            ? catalog
            : new EvidenceProvenance(
                EvidenceSourceClass.PublicStructuredData,
                $"json.tarkov.dev/items/{itemId}/trader",
                confirmedUtc,
                EvidenceConfidence.Certain,
                Producer);

        var askingPrice = definition.FleaEligible ? price.Average24HourRoubles ?? price.FleaPriceRoubles : null;
        var trader = price.BestTrader?.ValueRoubles;
        EvidencedValue<long?> fleaGross;
        EvidencedValue<long?> fleaFee;
        EvidencedValue<long?> fleaNet;
        if (askingPrice is not { } asking || asking <= 0)
        {
            // "The flea does not sell this" is a fact about the catalog, not a market figure.
            fleaGross = Absent<long?>("economics.flea-gross", "flea-gross.not-sold", confirmed);
            fleaFee = Absent<long?>("economics.flea-fee", "flea-fee.not-sold", confirmed);
            fleaNet = Absent<long?>("economics.flea-net", "flea-net.not-sold", confirmed);
        }
        else
        {
            var gross = checked(asking * quantity);
            fleaGross = Known<long?>("economics.flea-gross", gross, catalog);
            if (rates is null || facts?.BasePriceRoubles is not { } basePrice)
            {
                fleaFee = Unread<long?>("economics.flea-fee", rates is null ? "flea-fee.rates-not-synced" : "flea-fee.base-price-unknown", unknown);
                fleaNet = Unread<long?>("economics.flea-net", "flea-net.fee-unread", unknown);
            }
            else
            {
                var fee = FleaMarketFee.Calculate(basePrice, asking, quantity, rates);
                var calculated = FeeProvenance(itemId, catalog, rates, facts, evaluatedUtc);
                fleaFee = Known<long?>("economics.flea-fee", fee, calculated);

                // A listing the fee would swallow is not a way to sell the item. That is a
                // settled "no" for the flea, so the trader's offer can be weighed on its own.
                fleaNet = fee >= gross
                    ? Absent<long?>("economics.flea-net", "flea-net.fee-exceeds-price", calculated)
                    : Known<long?>("economics.flea-net", gross - fee, calculated);
            }
        }

        var economics = new RecommendationEconomics(
            fleaGross,
            fleaFee,
            fleaNet,
            trader is { } traderValue
                ? Known<long?>("economics.trader", checked(traderValue * quantity), confirmed)
                : Absent<long?>("economics.trader", "trader.not-bought", confirmed),
            Known<int?>("economics.squares", checked(width * height), footprint),
            conditionApplies == false
                ? Known<double?>("economics.condition", 1, catalog)
                : Unread<double?>("economics.condition", "condition.unread", unknown));
        return new(itemId, definition, facts, economics, catalog, unknown, confirmedUtc);
    }

    private static DateTimeOffset StaticFactsUtc(DateTimeOffset stampedUtc, ItemMarketFacts? facts, DateTimeOffset evaluatedUtc) =>
        facts?.CatalogSyncedUtc is { } synced && synced > stampedUtc
            ? Earlier(synced, evaluatedUtc)
            : stampedUtc;

    private static RecommendationProfileFacts ProfileFacts(
        ProfileRecord? profile,
        LootScanNeedSnapshot? needSnapshot,
        string itemId,
        DateTimeOffset evaluatedUtc)
    {
        var read = new EvidenceProvenance(
            EvidenceSourceClass.UserEntered,
            profile is null ? "profile/none" : $"profile/{profile.Context.Identity.ProfileId:N}",
            evaluatedUtc,
            profile is null ? EvidenceConfidence.Unscored : EvidenceConfidence.Certain,
            Producer);
        var status = (profile, needSnapshot) switch
        {
            (null, _) => new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current, "profile.not-read"),
            (_, null) => new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current, "profile.needs-not-evaluated"),
            (_, { QuestBoardRead: false }) => new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current, "profile.quest-board-unread"),
            _ => new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "profile.read"),
        };
        if (profile is null)
        {
            return new(
                status,
                read,
                Unread<RecommendationExplicitActionState?>("profile.explicit-action", "profile.not-read", read),
                Unread<bool?>("profile.protected", "profile.not-read", read),
                Unread<bool?>("profile.pinned", "profile.not-read", read),
                Unread<bool?>("profile.wishlist", "profile.not-read", read),
                new RecommendationEventStateFacts(null, Unread<EventItemState?>("profile.event-state", "profile.not-read", read)),
                []);
        }

        var chosen = ProfileProvenance(profile, evaluatedUtc);
        var rule = LootScanProfileRules.RuleFor(profile.Progress, itemId);
        return new(
            status,
            read,
            rule == LootScanItemRule.Unrecognised
                ? Unread<RecommendationExplicitActionState?>("profile.explicit-action", "item-rule.unrecognised", chosen)
                : Known<RecommendationExplicitActionState?>(
                    "profile.explicit-action",
                    new RecommendationExplicitActionState(LootScanProfileRules.ActionFor(rule)),
                    chosen),
            Known<bool?>("profile.protected", LootScanProfileRules.IsProtected(profile.Progress, itemId), chosen),
            Known<bool?>("profile.pinned", LootScanProfileRules.IsPinned(profile.Progress, itemId), chosen),
            Known<bool?>("profile.wishlist", LootScanProfileRules.IsWishlisted(profile.Progress, itemId), chosen),
            new RecommendationEventStateFacts(null, Known<EventItemState?>("profile.event-state", EventItemState.Unknown, read)),
            needSnapshot?.NeedsFor(itemId, evaluatedUtc) ?? []);
    }

    /// <summary>The player's own choices, dated when the profile last changed.</summary>
    private static EvidenceProvenance ProfileProvenance(ProfileRecord profile, DateTimeOffset evaluatedUtc) =>
        new(
            EvidenceSourceClass.UserEntered,
            $"profile/{profile.Context.Identity.ProfileId:N}/item-rules",
            Earlier(profile.UpdatedUtc, evaluatedUtc),
            EvidenceConfidence.Certain,
            Producer);

    private static EvidencedValue<RecommendationObtainabilityBand?> Obtainability(ItemRead read, DateTimeOffset evaluatedUtc)
    {
        if (read.Facts is not { } facts)
        {
            return Unread<RecommendationObtainabilityBand?>("scarcity.obtainability", "scarcity.market-facts-unread", read.Unknown);
        }

        RecommendationObtainabilityBand? band = facts switch
        {
            { TraderSellsForCash: true } => RecommendationObtainabilityBand.Abundant,
            { LastOfferCount: >= ReadilyListedOffers } when read.Definition.FleaEligible => RecommendationObtainabilityBand.Available,
            { LastOfferCount: null } when read.Definition.FleaEligible => null,
            _ => RecommendationObtainabilityBand.Limited,
        };
        if (band is null)
        {
            return Unread<RecommendationObtainabilityBand?>("scarcity.obtainability", "scarcity.offer-count-unpublished", read.Unknown);
        }

        // A band read off the listing count is a market figure. One read off a trader's offer,
        // or off there being no flea to list on, is a catalog fact and as fresh as the last sync.
        var through = facts.TraderSellsForCash || !read.Definition.FleaEligible
            ? read.StaticFactsUtc
            : Earlier(facts.ObservedUtc, evaluatedUtc);
        return Known<RecommendationObtainabilityBand?>(
            "scarcity.obtainability",
            band,
            new EvidenceProvenance(
                EvidenceSourceClass.DerivedCalculation,
                $"loot-scan/obtainability/{read.ItemId}",
                evaluatedUtc,
                EvidenceConfidence.Certain,
                Producer,
                dataThroughUtc: through,
                generatedUtc: evaluatedUtc,
                inputs:
                [
                    new(
                        EvidenceSourceClass.PublicStructuredData,
                        $"json.tarkov.dev/items/{read.ItemId}/offers",
                        through,
                        EvidenceConfidence.Certain,
                        Producer),
                ]));
    }

    /// <summary>
    /// A fee is a calculation, and as old as the oldest thing it was calculated from, so a fee
    /// worked out now from yesterday's price is refused like yesterday's price.
    /// </summary>
    private static EvidenceProvenance FeeProvenance(
        string itemId,
        EvidenceProvenance catalog,
        FleaMarketRates rates,
        ItemMarketFacts facts,
        DateTimeOffset evaluatedUtc)
    {
        var ratesUtc = Earlier(rates.ObservedUtc, evaluatedUtc);
        var factsUtc = StaticFactsUtc(Earlier(facts.ObservedUtc, evaluatedUtc), facts, evaluatedUtc);
        var through = Earlier(Earlier(catalog.ObservedUtc, ratesUtc), factsUtc);
        return new(
            EvidenceSourceClass.DerivedCalculation,
            $"loot-scan/flea-fee/{itemId}",
            evaluatedUtc,
            EvidenceConfidence.Certain,
            Producer,
            dataThroughUtc: through,
            generatedUtc: evaluatedUtc,
            inputs:
            [
                catalog,
                new(
                    EvidenceSourceClass.PublicStructuredData,
                    $"json.tarkov.dev/items/{itemId}/base-price",
                    factsUtc,
                    EvidenceConfidence.Certain,
                    Producer),
                new(
                    EvidenceSourceClass.PublicStructuredData,
                    "json.tarkov.dev/items/flea-market-rates",
                    ratesUtc,
                    EvidenceConfidence.Certain,
                    Producer),
            ]);
    }

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static EvidencedValue<T> Known<T>(string fieldId, T value, EvidenceProvenance provenance) =>
        new(fieldId, value, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, fieldId + ".known"), provenance);

    /// <summary>The source says there is no such value: a channel that does not buy this item.</summary>
    private static EvidencedValue<T> Absent<T>(string fieldId, string code, EvidenceProvenance provenance) =>
        new(fieldId, default, new ResultStatus(ResultCompleteness.Unavailable, FreshnessState.Current, code), provenance);

    /// <summary>Nothing the companion holds answers this, which is not the same as "no".</summary>
    private static EvidencedValue<T> Unread<T>(string fieldId, string code, EvidenceProvenance provenance) =>
        new(fieldId, default, new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current, code), provenance);

    /// <summary>Everything the engine asks about one item, apart from what is asking.</summary>
    public sealed record ItemAdviceFacts(
        RecommendationProfileFacts Profile,
        RecommendationEconomics Economics,
        RecommendationScarcityFacts Scarcity);

    /// <summary>An item as a stash scan read it: what, how big, how many, and what saw it.</summary>
    public sealed record ObservedStashItem(string ItemId, int Width, int Height, int Quantity, EvidenceProvenance Footprint);

    private sealed record ItemRead(
        string ItemId,
        ItemDefinition Definition,
        ItemMarketFacts? Facts,
        RecommendationEconomics Economics,
        EvidenceProvenance Catalog,
        EvidenceProvenance Unknown,
        DateTimeOffset StaticFactsUtc);
}
