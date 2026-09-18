using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests.Intel;

public sealed class ItemIntelServiceTests
{
    private static readonly DataProvenance Provenance = new("test", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task UnknownItemReturnsNotFound()
    {
        var service = new ItemIntelService(
            new FakeItemRepository([]),
            new FakeQuestProgressService(new(0, 0, 0)),
            new FakeItemFactCatalog());

        var result = await service.GetAsync("missing-item", CancellationToken.None);

        Assert.Equal(V2IntelKind.Unknown, result.Kind);
        Assert.Equal("missing-item", result.ItemId);
    }

    [Fact]
    public async Task BarterItemReportsPriceAndNeed()
    {
        var item = Item("item-bandage", "Bandage", ItemCategory.Medicine);
        var price = new ItemPriceSnapshot(1000, [], 900, 800, 1100, Provenance);
        var repository = new FakeItemRepository([item], price);
        var needs = new ItemNeedSummary(3, 1, 2) { QuestsNeedingIt = 3, TrackedQuestsNeedingIt = 1 };

        var service = new ItemIntelService(repository, new FakeQuestProgressService(needs), new FakeItemFactCatalog());

        var result = await service.GetAsync(item.Id, CancellationToken.None);

        Assert.Equal(V2IntelKind.Item, result.Kind);
        Assert.Equal("Bandage", result.Name);
        Assert.NotNull(result.Value);
        Assert.Equal(1000, result.Value!.ValueRoubles);
        Assert.Equal("Flea", result.Value.SaleChannelLabel);
        Assert.Equal(3, result.Value.QuestsNeedingIt);
        Assert.Equal(1, result.Value.TrackedQuestsNeedingIt);
        Assert.Equal(2, result.Value.HideoutCount);
        Assert.Null(result.Key);
        Assert.Null(result.Ammo);
    }

    [Fact]
    public async Task PriceFactsListEveryTraderBestFirstAlongsideTheFleaFigures()
    {
        var item = Item("item-gpu", "Graphics card", ItemCategory.Barter) with { Description = "A video card." };
        var updated = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        var price = new ItemPriceSnapshot(
            1_200_000,
            [
                new TraderOffer("therapist", "Therapist", 90_000, Provenance),
                new TraderOffer("mechanic", "Mechanic", 118_000, Provenance),
            ],
            1_180_000,
            1_100_000,
            1_260_000,
            Provenance with { SourceUpdatedUtc = updated });
        var service = new ItemIntelService(
            new FakeItemRepository([item], price),
            new FakeQuestProgressService(new(0, 0, 0)),
            new FakeItemFactCatalog());

        var result = await service.GetAsync(item.Id, CancellationToken.None);

        Assert.Equal("A video card.", result.Description);
        var prices = Assert.IsType<V2IntelPriceFacts>(result.Prices);
        Assert.Equal(1_200_000, prices.FleaRoubles);
        Assert.Equal(1_100_000, prices.Low24HourRoubles);
        Assert.Equal(1_260_000, prices.High24HourRoubles);
        Assert.Equal(["Mechanic", "Therapist"], prices.Traders.Select(trader => trader.TraderName));
        Assert.Equal(updated, prices.UpdatedUtc);
    }

    [Fact]
    public async Task AnItemWithNoPriceHasNoPriceFacts()
    {
        var item = Item("item-unpriced", "Unpriced", ItemCategory.Barter);
        var service = new ItemIntelService(
            new FakeItemRepository([item]),
            new FakeQuestProgressService(new(0, 0, 0)),
            new FakeItemFactCatalog());

        var result = await service.GetAsync(item.Id, CancellationToken.None);

        Assert.Null(result.Prices);
    }

    [Fact]
    public async Task KeyItemReportsWhatItOpensWhenTheCatalogHasFacts()
    {
        var item = Item("item-key", "Dorm 114 key", ItemCategory.Key);
        var repository = new FakeItemRepository([item]);
        var factCatalog = new FakeItemFactCatalog
        {
            KeyFacts = [new KeyFacts(item.Id, "customs", null, ["Room 114"], [], null, 0, 0, false, 0, Provenance)],
        };

        var service = new ItemIntelService(repository, new FakeQuestProgressService(new(0, 0, 0)), factCatalog);

        var result = await service.GetAsync(item.Id, CancellationToken.None);

        Assert.Equal(V2IntelKind.Key, result.Kind);
        Assert.NotNull(result.Key);
        Assert.Equal("customs", result.Key!.MapId);
        Assert.Equal(["Room 114"], result.Key.Locks);
    }

    [Fact]
    public async Task KeyItemCarriesUsesCostAndTheMapsName()
    {
        var item = Item("item-key", "Dorm 114 key", ItemCategory.Key);
        var factCatalog = new FakeItemFactCatalog
        {
            KeyFacts = [new KeyFacts(item.Id, "map-customs", 12, ["Room 114"], [], 150_000, 0, 0, false, 0, Provenance)],
        };

        var service = new ItemIntelService(
            new FakeItemRepository([item]),
            new FakeQuestProgressService(new(0, 0, 0)),
            factCatalog,
            new FakeMapData(("map-customs", "Customs")));

        var key = (await service.GetAsync(item.Id, CancellationToken.None)).Key!;

        Assert.Equal(12, key.MaximumUses);
        Assert.Equal(150_000, key.AcquisitionCostRoubles);
        Assert.Equal("Customs", key.MapName);
        Assert.Equal("map-customs", key.MapId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeyKeepsItsMapIdWhenNothingNamesTheMap(bool mapLookupThrows)
    {
        var item = Item("item-key", "Dorm 114 key", ItemCategory.Key);
        var factCatalog = new FakeItemFactCatalog
        {
            KeyFacts = [new KeyFacts(item.Id, "map-unknown", null, [], [], null, 0, 0, false, 0, Provenance)],
        };

        var service = new ItemIntelService(
            new FakeItemRepository([item]),
            new FakeQuestProgressService(new(0, 0, 0)),
            factCatalog,
            new FakeMapData(throws: mapLookupThrows));

        var key = (await service.GetAsync(item.Id, CancellationToken.None)).Key!;

        Assert.Equal("map-unknown", key.MapName);
        Assert.Null(key.MaximumUses);
        Assert.Null(key.AcquisitionCostRoubles);
    }

    [Fact]
    public async Task KeyItemDegradesHonestlyWhenTheCatalogHasNoFacts()
    {
        var item = Item("item-key-unknown", "Unlabelled key", ItemCategory.Key);
        var service = new ItemIntelService(
            new FakeItemRepository([item]),
            new FakeQuestProgressService(new(0, 0, 0)),
            new FakeItemFactCatalog());

        var result = await service.GetAsync(item.Id, CancellationToken.None);

        Assert.Equal(V2IntelKind.Key, result.Kind);
        Assert.Null(result.Key);
    }

    [Fact]
    public async Task AmmoItemReportsDamagePenetrationAndTier()
    {
        var item = Item("item-ammo", "M995", ItemCategory.Ammunition);
        var stats = new AmmoStats("item-ammo", "7.62x51", 60, 55, null, null, 1, null, null, null, false, false, Provenance);
        var factCatalog = new FakeItemFactCatalog { AmmoStats = [stats] };

        var service = new ItemIntelService(
            new FakeItemRepository([item]),
            new FakeQuestProgressService(new(0, 0, 0)),
            factCatalog);

        var result = await service.GetAsync(item.Id, CancellationToken.None);

        Assert.Equal(V2IntelKind.Ammo, result.Kind);
        Assert.NotNull(result.Ammo);
        Assert.Equal(60, result.Ammo!.Damage);
        Assert.Equal(55, result.Ammo.Penetration);
        Assert.False(string.IsNullOrWhiteSpace(result.Ammo.Tier));
    }

    [Fact]
    public async Task AmmoItemCarriesArmorDamageFragmentationAndTheSixArmorClassRatings()
    {
        var item = Item("item-ammo", "M995", ItemCategory.Ammunition);
        var stats = new AmmoStats("item-ammo", "7.62x51", 60, 55, 68, 0.25, 1, null, null, null, false, false, Provenance);
        var service = new ItemIntelService(
            new FakeItemRepository([item]),
            new FakeQuestProgressService(new(0, 0, 0)),
            new FakeItemFactCatalog { AmmoStats = [stats] });

        var ammo = (await service.GetAsync(item.Id, CancellationToken.None)).Ammo!;

        Assert.Equal(68, ammo.ArmorDamagePercent);
        Assert.Equal(0.25, ammo.FragmentationChance);
        Assert.Equal(6, ammo.ArmorClassRatings.Count);
    }

    private static ItemDefinition Item(string id, string name, ItemCategory category) => new(
        id,
        name,
        name,
        string.Empty,
        category,
        new ItemDimensions(1, 1),
        FleaEligible: true,
        IconUri: null,
        ImageUri: null,
        WikiUri: "https://escapefromtarkov.fandom.com/wiki/" + id,
        PropertiesType: null,
        PropertiesJson: null,
        CategoryIds: new HashSet<string>(),
        Provenance: Provenance);

    private sealed class FakeItemRepository(IReadOnlyList<ItemDefinition> items, ItemPriceSnapshot? price = null) : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(items.FirstOrDefault(item => item.Id == itemId));

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(price);
    }

    private sealed class FakeMapData(params (string Id, string Name)[] maps) : IMapDataService
    {
        private readonly bool _throws;

        public FakeMapData(bool throws)
            : this() => _throws = throws;

        public Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken)
        {
            if (_throws)
            {
                throw new InvalidOperationException("The maps table is not readable.");
            }

            var match = maps.FirstOrDefault(map => map.Id == mapId);
            return Task.FromResult<MapDefinition?>(match.Id is null
                ? null
                : new MapDefinition(match.Id, match.Name, null, null, [], [], null, Provenance));
        }
    }

    private sealed class FakeQuestProgressService(ItemNeedSummary summary) : IQuestProgressService
    {
        public Task<ItemNeedSummary> GetItemNeedsAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(summary);
    }

    private sealed class FakeItemFactCatalog : IItemFactCatalog
    {
        public IReadOnlyList<AmmoStats> AmmoStats { get; init; } = [];

        public IReadOnlyList<AmmoPackContents> AmmoPacks { get; init; } = [];

        public IReadOnlyList<KeyFacts> KeyFacts { get; init; } = [];

        public Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken) =>
            Task.FromResult(AmmoStats);

        public Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken) =>
            Task.FromResult(AmmoPacks);

        public Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LoadoutItemFacts>>([]);

        public Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(KeyFacts);

        public void Invalidate()
        {
        }
    }
}
