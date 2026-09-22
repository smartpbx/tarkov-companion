using TarkovCompanion.Application.Services;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.Application.Services.Intel;

public enum V2IntelKind
{
    /// <summary>Nothing in the catalog matches the id — the caller shows "not in the catalog yet".</summary>
    Unknown,
    Item,
    Key,
    Ammo,
}

/// <summary>The general price/need facts every catalog item has, regardless of kind.</summary>
/// <param name="ValueRoubles">The best of flea and trader value, when either is known.</param>
/// <param name="SaleChannelLabel">Which of those it came from ("Flea" or a trader name), if any.</param>
/// <param name="OutstandingItems">How many of it uncompleted quests still ask for (a quantity, not a quest count).</param>
/// <param name="OutstandingFoundInRaidItems">How many of those must be found in raid.</param>
public sealed record V2IntelValueFacts(
    long? ValueRoubles,
    string? SaleChannelLabel,
    int QuestsNeedingIt,
    int TrackedQuestsNeedingIt,
    int HideoutCount,
    int OutstandingItems = 0,
    int OutstandingFoundInRaidItems = 0);

/// <summary>What #308's key intelligence would call obtainability/associations, kept to real facts only.</summary>
/// <param name="MapId">The map the key's lock is on, when the catalog has one.</param>
/// <param name="Locks">What it opens, named as the catalog names them.</param>
/// <param name="MaximumUses">How many times it opens a door before it is spent, when the source says.</param>
/// <param name="AcquisitionCostRoubles">What buying it costs, when a trader or the flea sells it.</param>
/// <param name="MapName">The map's name where the synced maps table has one; the id otherwise.</param>
public sealed record V2IntelKeyFacts(
    string? MapId,
    IReadOnlyList<string> Locks,
    int? MaximumUses = null,
    long? AcquisitionCostRoubles = null,
    string? MapName = null);

/// <param name="ArmorDamagePercent">How much of an armour's durability a hit takes, when the source states it.</param>
/// <param name="FragmentationChance">The chance the round fragments on impact, when the source states it.</param>
public sealed record V2IntelAmmoFacts(
    int Damage,
    int Penetration,
    string Tier,
    IReadOnlyDictionary<int, ArmorEffectiveness> ArmorClassRatings,
    string PracticalAdvice,
    int? ArmorDamagePercent = null,
    double? FragmentationChance = null,
    double? VelocityMetresPerSecond = null,
    string Caliber = "Unknown caliber");

/// <summary>One trader's buy-back price, as the catalog names the trader.</summary>
public sealed record V2IntelTraderPrice(string TraderName, long ValueRoubles);

/// <summary>A held item's per-unit flea return beside its best certain trader sale.</summary>
public sealed record V2IntelSellingFacts(
    int HeldCount,
    long AskingRoubles,
    long FeeRoubles,
    long NetRoubles,
    string? TraderName,
    long? TraderRoubles);

/// <summary>
/// Every price the local catalog holds for the item, for the Intel workspace's price panel
/// (V2 rough package 17). Nothing here is a history: the 24-hour figures are the catalog's own
/// summary of its last sync, not a series this application recorded.
/// </summary>
/// <param name="FeeRoubles">
/// What listing it on the flea at the current flea price would cost, by the game's own fee
/// formula. Null where the base price or the live flea listing rates are not cached — never
/// zero, which would read as a free listing.
/// </param>
public sealed record V2IntelPriceFacts(
    long? FleaRoubles,
    long? Average24HourRoubles,
    long? Low24HourRoubles,
    long? High24HourRoubles,
    IReadOnlyList<V2IntelTraderPrice> Traders,
    DateTimeOffset UpdatedUtc,
    long? FeeRoubles = null,
    PriceHistorySummary? SevenDayHistory = null,
    V2IntelSellingFacts? Selling = null);

/// <summary>One active quest that still wants this item, by name.</summary>
public sealed record V2IntelQuestNeedRow(string TaskName, int? Remaining, bool FoundInRaidRequired);

/// <summary>One hideout station's next level that still wants this item, by name.</summary>
public sealed record V2IntelHideoutNeedRow(string StationName, int TargetLevel, int Remaining);

/// <summary>
/// Every reason a player might keep this rather than sell or drop it: the quests and hideout
/// levels asking for it, named rather than counted.
/// </summary>
public sealed record V2IntelKeepFacts(
    IReadOnlyList<V2IntelQuestNeedRow> Quests,
    IReadOnlyList<V2IntelHideoutNeedRow> Hideout);

public sealed record V2ItemIntelResult(
    V2IntelKind Kind,
    string ItemId,
    string Name,
    string ShortName,
    string? WikiUri,
    ItemCategory Category,
    int Width,
    int Height,
    bool FleaEligible,
    V2IntelValueFacts? Value = null,
    V2IntelKeyFacts? Key = null,
    V2IntelAmmoFacts? Ammo = null,
    string Description = "",
    V2IntelPriceFacts? Prices = null,
    V2IntelKeepFacts? Keep = null)
{
    public static V2ItemIntelResult NotFound(string itemId) =>
        new(V2IntelKind.Unknown, itemId, itemId, itemId, null, ItemCategory.Unknown, 0, 0, false);
}

/// <summary>
/// Resolves one item id to a rough Intel card: what kind of thing it is, and the plain facts
/// that kind has. Reuses the fact catalog and quest progress service exactly as the legacy
/// Ammo/Keys pages do; adds no new fact sources.
/// </summary>
public interface IItemIntelService
{
    Task<V2ItemIntelResult> GetAsync(string itemId, CancellationToken cancellationToken);

    /// <summary>
    /// The card plus the gear and weight facts a side-by-side comparison (#287) needs. The default
    /// leaves those unknown, so a service that has no fact catalog still compares on what it has.
    /// </summary>
    async Task<ItemComparisonFacts> GetComparisonFactsAsync(string itemId, CancellationToken cancellationToken) =>
        new(await GetAsync(itemId, cancellationToken).ConfigureAwait(false), Gear: null, WeightKg: null);
}

public sealed class ItemIntelService(
    IItemRepository itemRepository,
    IQuestProgressService questProgress,
    IItemFactCatalog factCatalog,
    IMapDataService? maps = null,
    // Package 33 (#287): "should I keep it" needs the quests and hideout levels asking for the
    // item by name, not just the counts questProgress already carries. All optional so every
    // existing composition of this class keeps working; without them the keep facts are null and
    // the caller falls back to the counts it already had.
    IQuestReadService? questRead = null,
    IPlayerProfileService? profileService = null,
    ProfileNeedAggregationService? needAggregation = null,
    IRequirementCatalog? requirements = null,
    IItemMarketFactSource? marketFacts = null,
    IPriceHistoryService? priceHistory = null) : IItemIntelService
{
    public async Task<V2ItemIntelResult> GetAsync(string itemId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        var item = await itemRepository.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return V2ItemIntelResult.NotFound(itemId);
        }

        var price = await itemRepository.GetPriceAsync(itemId, cancellationToken).ConfigureAwait(false);
        var needs = await questProgress.GetItemNeedsAsync(itemId, cancellationToken).ConfigureAwait(false);
        var value = ValueFacts(price, needs);
        var prices = await PriceFactsAsync(itemId, price, cancellationToken).ConfigureAwait(false);
        var keep = await KeepFactsAsync(itemId, cancellationToken).ConfigureAwait(false);

        return item.Category switch
        {
            ItemCategory.Ammunition or ItemCategory.AmmunitionPack =>
                await BuildAmmoAsync(item, value, cancellationToken).ConfigureAwait(false),
            ItemCategory.Key => await BuildKeyAsync(item, value, cancellationToken).ConfigureAwait(false),
            _ => Base(V2IntelKind.Item, item, value),
        } with { Description = item.Description, Prices = prices, Keep = keep };
    }

    public async Task<ItemComparisonFacts> GetComparisonFactsAsync(string itemId, CancellationToken cancellationToken)
    {
        var intel = await GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (intel.Kind == V2IntelKind.Unknown)
        {
            return new(intel, Gear: null, WeightKg: null);
        }

        // Cached by the catalog after its first read, so a comparison of three costs three lookups.
        var facts = await factCatalog.GetLoadoutFactsAsync(cancellationToken).ConfigureAwait(false);
        var match = facts.FirstOrDefault(fact => string.Equals(fact.ItemId, itemId, StringComparison.Ordinal));
        return new(intel, match?.Gear, match?.WeightKg);
    }

    private async Task<V2ItemIntelResult> BuildAmmoAsync(
        ItemDefinition item,
        V2IntelValueFacts value,
        CancellationToken cancellationToken)
    {
        var stats = await factCatalog.GetAmmoAsync(cancellationToken).ConfigureAwait(false);
        var packs = await factCatalog.GetAmmoPacksAsync(cancellationToken).ConfigureAwait(false);
        var intelligence = await new AmmoIntelligenceService(stats, packs)
            .GetAsync(item.Id, profile: null, cancellationToken)
            .ConfigureAwait(false);
        var ammo = intelligence is null
            ? null
            : new V2IntelAmmoFacts(
                intelligence.Stats.Damage,
                intelligence.Stats.Penetration,
                intelligence.Tier,
                intelligence.ArmorClassRatings,
                intelligence.PracticalAdvice,
                intelligence.Stats.ArmorDamagePercent,
                intelligence.Stats.FragmentationChance,
                intelligence.Stats.VelocityMetresPerSecond,
                CaliberText.Describe(intelligence.Stats.Caliber, item.Name));
        return Base(V2IntelKind.Ammo, item, value) with { Ammo = ammo };
    }

    private async Task<V2ItemIntelResult> BuildKeyAsync(
        ItemDefinition item,
        V2IntelValueFacts value,
        CancellationToken cancellationToken)
    {
        var facts = await factCatalog.GetKeyFactsAsync(cancellationToken).ConfigureAwait(false);
        var match = facts.FirstOrDefault(fact => string.Equals(fact.ItemId, item.Id, StringComparison.Ordinal));
        var key = match is null
            ? null
            : new V2IntelKeyFacts(
                match.MapId,
                match.Locks,
                match.MaximumUses,
                match.AcquisitionCostRoubles,
                await NameOfMapAsync(match.MapId, cancellationToken).ConfigureAwait(false));
        return Base(V2IntelKind.Key, item, value) with { Key = key };
    }

    /// <summary>
    /// The map's name, or the id when nothing names it. The key projection carries the game's own
    /// map id, and printing that read as a hash rather than a place; the id stays the answer when
    /// the maps table has nothing, so a key never loses its map to a failed lookup.
    /// </summary>
    private async Task<string?> NameOfMapAsync(string? mapId, CancellationToken cancellationToken)
    {
        if (mapId is null || maps is null)
        {
            return mapId;
        }

        try
        {
            return (await maps.GetAsync(mapId, cancellationToken).ConfigureAwait(false))?.Name ?? mapId;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return mapId;
        }
    }

    private static V2ItemIntelResult Base(V2IntelKind kind, ItemDefinition item, V2IntelValueFacts value) => new(
        kind,
        item.Id,
        item.Name,
        item.ShortName,
        item.WikiUri,
        item.Category,
        item.Dimensions.Width,
        item.Dimensions.Height,
        item.FleaEligible,
        value);

    private async Task<V2IntelPriceFacts?> PriceFactsAsync(
        string itemId,
        ItemPriceSnapshot? price,
        CancellationToken cancellationToken)
    {
        if (price is null)
        {
            return null;
        }

        var fee = await FeeRoublesAsync(itemId, price.FleaPriceRoubles, cancellationToken).ConfigureAwait(false);
        var held = await HeldCountAsync(itemId, cancellationToken).ConfigureAwait(false);
        var bestTrader = price.TraderOffers.OrderByDescending(offer => offer.ValueRoubles).FirstOrDefault();
        var selling = held is > 0 && price.FleaPriceRoubles is > 0 and var asking && fee is { } listingFee
            ? new V2IntelSellingFacts(
                held.Value,
                asking,
                listingFee,
                asking - listingFee,
                bestTrader?.TraderName,
                bestTrader?.ValueRoubles)
            : null;

        return new(
            price.FleaPriceRoubles,
            price.Average24HourRoubles,
            price.Low24HourRoubles,
            price.High24HourRoubles,
            price.TraderOffers
                .OrderByDescending(offer => offer.ValueRoubles)
                .Select(offer => new V2IntelTraderPrice(offer.TraderName, offer.ValueRoubles))
                .ToArray(),
            price.Provenance.SourceUpdatedUtc ?? price.Provenance.ObservedUtc,
            fee,
            await HistoryAsync(itemId, cancellationToken).ConfigureAwait(false),
            selling);
    }

    /// <summary>
    /// The active profile's recorded holding. Zero and unknown deliberately draw no selling call:
    /// this is advice about an item the player has, never an invitation to acquire one.
    /// </summary>
    private async Task<int?> HeldCountAsync(string itemId, CancellationToken cancellationToken)
    {
        if (profileService is null)
        {
            return null;
        }

        try
        {
            var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            return profile.OwnedItemCounts.TryGetValue(itemId, out var held) && held >= 0 ? held : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<PriceHistorySummary?> HistoryAsync(string itemId, CancellationToken cancellationToken)
    {
        if (priceHistory is null)
        {
            return null;
        }

        try
        {
            var points = await priceHistory.GetAsync(itemId, TimeSpan.FromDays(7), cancellationToken).ConfigureAwait(false);
            return PriceHistorySummary.From(points);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The flea listing fee at the item's own current price, by the game's published formula.
    /// </summary>
    /// <remarks>
    /// Null wherever a factor is missing — no base price cached, nothing currently asking on
    /// flea, or the live fee rates have not synced — rather than guessed at 5%, which is only
    /// this week's published rate and not a promise.
    /// </remarks>
    private async Task<long?> FeeRoublesAsync(string itemId, long? fleaPriceRoubles, CancellationToken cancellationToken)
    {
        if (marketFacts is null || fleaPriceRoubles is not > 0)
        {
            return null;
        }

        try
        {
            var facts = await marketFacts.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            var rates = await marketFacts.GetFleaRatesAsync(cancellationToken).ConfigureAwait(false);
            if (facts?.BasePriceRoubles is not > 0 || rates is null)
            {
                return null;
            }

            return FleaMarketFee.Calculate(facts.BasePriceRoubles.Value, fleaPriceRoubles.Value, 1, rates);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Named quests and hideout levels asking for this item — "should I keep it," in words.</summary>
    private async Task<V2IntelKeepFacts?> KeepFactsAsync(string itemId, CancellationToken cancellationToken)
    {
        if (questRead is null && (needAggregation is null || requirements is null || profileService is null))
        {
            return null;
        }

        var quests = await QuestNeedRowsAsync(itemId, cancellationToken).ConfigureAwait(false);
        var hideout = await HideoutNeedRowsAsync(itemId, cancellationToken).ConfigureAwait(false);
        return new(quests, hideout);
    }

    private async Task<IReadOnlyList<V2IntelQuestNeedRow>> QuestNeedRowsAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        if (questRead is null || profileService is null)
        {
            return [];
        }

        try
        {
            var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var scope = new QuestProfileScope(profile.Id, profile.GameMode, profile.ProfileGeneration);
            var needs = await questRead.GetItemNeedsAsync(scope, itemId, cancellationToken).ConfigureAwait(false);

            // One task can list the same item on two objectives (find-in-raid, then hand over),
            // and each read separately. Grouped by task rather than shown twice under its own
            // name with nothing to tell the rows apart; the larger of the two is kept rather than
            // summed, the same reasoning the hideout requirement catalog collapses a duplicate row
            // by — two objectives are not reliably an additive amount, and overstating what is
            // needed is the worse of the two wrong answers.
            return needs.Requirements
                .Where(requirement => requirement.RemainingCount is not 0)
                .GroupBy(requirement => requirement.TaskName, StringComparer.Ordinal)
                .Select(group => new V2IntelQuestNeedRow(
                    group.Key,
                    MaxOrNull(group.Select(requirement => requirement.RemainingCount ?? requirement.TargetCount)),
                    group.Any(requirement => requirement.FoundInRaidRequired == true)))
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// The largest of a set of possibly-unknown quantities, or null when every one of them is
    /// unknown — never zero, which would read as nothing outstanding.
    /// </summary>
    private static int? MaxOrNull(IEnumerable<decimal?> values)
    {
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return known.Length == 0 ? null : (int)Math.Ceiling(known.Max());
    }

    private async Task<IReadOnlyList<V2IntelHideoutNeedRow>> HideoutNeedRowsAsync(
        string itemId,
        CancellationToken cancellationToken)
    {
        if (needAggregation is null || requirements is null || profileService is null)
        {
            return [];
        }

        try
        {
            var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            var outstanding = needAggregation.GetOutstandingRequirements(profile, itemId).Hideout;
            if (outstanding.Count == 0)
            {
                return [];
            }

            var stations = await requirements.GetStationsAsync(cancellationToken).ConfigureAwait(false);
            var names = stations.ToDictionary(station => station.StationId, station => station.Name, StringComparer.Ordinal);
            return outstanding
                .Select(row => new V2IntelHideoutNeedRow(
                    names.GetValueOrDefault(row.Requirement.StationId, row.Requirement.StationId),
                    row.Requirement.TargetLevel,
                    row.Remaining))
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    private static V2IntelValueFacts ValueFacts(ItemPriceSnapshot? price, ItemNeedSummary needs)
    {
        if (price is null)
        {
            return new(null, null, needs.QuestsNeedingIt, needs.TrackedQuestsNeedingIt, needs.HideoutCount,
                needs.OutstandingItems, needs.OutstandingFoundInRaidItems);
        }

        var (valueRoubles, channelLabel) = price.BestSaleChannel switch
        {
            SaleChannel.Flea => (price.FleaPriceRoubles, "Flea"),
            SaleChannel.Trader => (price.BestTrader?.ValueRoubles, price.BestTrader?.TraderName),
            _ => ((long?)null, (string?)null),
        };
        return new(valueRoubles, channelLabel, needs.QuestsNeedingIt, needs.TrackedQuestsNeedingIt, needs.HideoutCount,
            needs.OutstandingItems, needs.OutstandingFoundInRaidItems);
    }
}
