using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Items;

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
    double? FragmentationChance = null);

/// <summary>One trader's buy-back price, as the catalog names the trader.</summary>
public sealed record V2IntelTraderPrice(string TraderName, long ValueRoubles);

/// <summary>
/// Every price the local catalog holds for the item, for the Intel workspace's price panel
/// (V2 rough package 17). Nothing here is a history: the 24-hour figures are the catalog's own
/// summary of its last sync, not a series this application recorded.
/// </summary>
public sealed record V2IntelPriceFacts(
    long? FleaRoubles,
    long? Average24HourRoubles,
    long? Low24HourRoubles,
    long? High24HourRoubles,
    IReadOnlyList<V2IntelTraderPrice> Traders,
    DateTimeOffset UpdatedUtc);

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
    V2IntelPriceFacts? Prices = null)
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
}

public sealed class ItemIntelService(
    IItemRepository itemRepository,
    IQuestProgressService questProgress,
    IItemFactCatalog factCatalog,
    IMapDataService? maps = null) : IItemIntelService
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
        var prices = PriceFacts(price);

        return item.Category switch
        {
            ItemCategory.Ammunition or ItemCategory.AmmunitionPack =>
                await BuildAmmoAsync(item, value, cancellationToken).ConfigureAwait(false),
            ItemCategory.Key => await BuildKeyAsync(item, value, cancellationToken).ConfigureAwait(false),
            _ => Base(V2IntelKind.Item, item, value),
        } with { Description = item.Description, Prices = prices };
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
                intelligence.Stats.FragmentationChance);
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

    private static V2IntelPriceFacts? PriceFacts(ItemPriceSnapshot? price) => price is null
        ? null
        : new(
            price.FleaPriceRoubles,
            price.Average24HourRoubles,
            price.Low24HourRoubles,
            price.High24HourRoubles,
            price.TraderOffers
                .OrderByDescending(offer => offer.ValueRoubles)
                .Select(offer => new V2IntelTraderPrice(offer.TraderName, offer.ValueRoubles))
                .ToArray(),
            price.Provenance.SourceUpdatedUtc ?? price.Provenance.ObservedUtc);

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
