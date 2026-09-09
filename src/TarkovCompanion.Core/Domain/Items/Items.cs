using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Items;

public readonly record struct ItemDimensions
{
    public ItemDimensions(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public int Slots => checked(Width * Height);
}

public enum ItemCategory
{
    Unknown,
    Barter,
    Ammunition,
    AmmunitionPack,
    Key,
    Provision,
    Medicine,
    Weapon,
    Attachment,
    Armor,
    Plate,
    Helmet,
    Headset,
    Rig,
    Backpack,
    Container,
}

public enum SaleChannel
{
    None,
    Flea,
    Trader,
}

public sealed record TraderOffer(string TraderId, string TraderName, long ValueRoubles, DataProvenance Provenance);

public sealed record ItemPriceSnapshot(
    long? FleaPriceRoubles,
    IReadOnlyList<TraderOffer> TraderOffers,
    long? Average24HourRoubles,
    long? Low24HourRoubles,
    long? High24HourRoubles,
    DataProvenance Provenance)
{
    public TraderOffer? BestTrader => TraderOffers.OrderByDescending(x => x.ValueRoubles).FirstOrDefault();

    public long BestEconomicValue => Math.Max(FleaPriceRoubles ?? 0, BestTrader?.ValueRoubles ?? 0);

    public SaleChannel BestSaleChannel => FleaPriceRoubles.GetValueOrDefault() >= (BestTrader?.ValueRoubles ?? 0)
        ? FleaPriceRoubles.HasValue ? SaleChannel.Flea : SaleChannel.None
        : SaleChannel.Trader;
}

public sealed record ItemDefinition(
    string Id,
    string Name,
    string ShortName,
    string Description,
    ItemCategory Category,
    ItemDimensions Dimensions,
    bool FleaEligible,
    string? IconUri,
    string? ImageUri,
    string? WikiUri,
    string? PropertiesType,
    string? PropertiesJson,
    IReadOnlySet<string> CategoryIds,
    DataProvenance Provenance)
{
    public long ValuePerSlot(ItemPriceSnapshot price) => price.BestEconomicValue / Dimensions.Slots;
}

public sealed record ItemSearchHit(ItemDefinition Item, double Score, string MatchedText);

public sealed record ItemQuantity(string ItemId, int Required, int Owned, bool FoundInRaidRequired)
{
    public int Remaining => Math.Max(0, Required - Owned);
}
