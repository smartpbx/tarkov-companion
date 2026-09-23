using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.UnitTests.V2Intel;

/// <summary>The catalog and item fakes the V1 Ammo, Keys and Flea pages read, for the V2 workspaces over them.</summary>
internal static class IntelWorkspaceFakes
{
    public static readonly DataProvenance Provenance = new("test", DateTimeOffset.UnixEpoch);

    public static ItemDefinition Item(string id, string name, string shortName = "", ItemCategory category = ItemCategory.Barter) => new(
        id,
        name,
        shortName.Length == 0 ? name : shortName,
        string.Empty,
        category,
        new ItemDimensions(1, 2),
        FleaEligible: true,
        IconUri: null,
        ImageUri: null,
        WikiUri: null,
        PropertiesType: null,
        PropertiesJson: null,
        CategoryIds: new HashSet<string>(),
        Provenance: Provenance);

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}

internal sealed class FakeFactCatalog : IItemFactCatalog
{
    public IReadOnlyList<AmmoStats> Ammo { get; init; } = [];

    public IReadOnlyList<KeyFacts> Keys { get; init; } = [];

    public IReadOnlyList<AmmoPackContents> Packs { get; init; } = [];

    public Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken) => Task.FromResult(Ammo);

    public Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Packs);

    public Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LoadoutItemFacts>>([]);

    public Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken) => Task.FromResult(Keys);

    public void Invalidate()
    {
    }
}

internal sealed class FakeItemRepository(params ItemDefinition[] items) : IItemRepository
{
    public ItemPriceSnapshot? Price { get; init; }

    public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
        Task.FromResult(items.FirstOrDefault(item => item.Id == itemId));

    public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ItemSearchHit>>(
        [
            .. items
                .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .Select(item => new ItemSearchHit(item, 1, item.Name)),
        ]);

    public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
        Task.FromResult(Price);
}

/// <summary>A profile whose owned counts a test sets, as a stash or case scan would.</summary>
internal sealed class OwnedCountsProfile(IReadOnlyDictionary<string, int> owned) : IPlayerProfileService
{
    public IReadOnlyDictionary<string, int> Owned { get; set; } = owned;

    public Task<TarkovCompanion.Core.Domain.Profile.PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new TarkovCompanion.Core.Domain.Profile.PlayerProfile(
            Guid.Empty,
            "Tester",
            GameMode.Regular,
            Level: 10,
            TarkovCompanion.Core.Domain.Profile.Faction.Usec,
            Edition: null,
            TraderLevels: new Dictionary<string, int>(StringComparer.Ordinal),
            CompletedTaskIds: new HashSet<string>(StringComparer.Ordinal),
            ObjectiveProgress: new Dictionary<string, int>(StringComparer.Ordinal),
            HideoutStationLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            WishlistItemIds: new HashSet<string>(StringComparer.Ordinal),
            OwnedItemCounts: Owned,
            EventItemStates: new Dictionary<string, TarkovCompanion.Core.Domain.Events.EventItemState>(StringComparer.Ordinal),
            ItemOverrides: new Dictionary<string, string>(StringComparer.Ordinal),
            UpdatedUtc: DateTimeOffset.UnixEpoch));

    public Task SaveAsync(TarkovCompanion.Core.Domain.Profile.PlayerProfile profile, CancellationToken cancellationToken)
    {
        Owned = profile.OwnedItemCounts;
        return Task.CompletedTask;
    }

    public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult(string.Empty);

    public Task<TarkovCompanion.Core.Domain.Profile.PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) =>
        GetActiveAsync(cancellationToken);
}
