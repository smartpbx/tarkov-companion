using System.Text.Json;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests.DataV2;

/// <summary>
/// The plate-to-armor fit check has always been in the loadout service, and on real data it never
/// fired: it reads the armors a plate fits, and nothing filled that in, so every plate abstained
/// and looked like a pass. A body armor states which plates fit each plate slot; this proves the
/// projection turns that into each plate's armors, through the same refresh and catalog code the
/// app runs.
/// </summary>
public sealed class PlateFitProjectionTests
{
    [Fact]
    public async Task A_plate_learns_the_armors_that_list_it_and_a_wrong_plate_is_reported()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var items = new TarkovDevItemsData
        {
            Items = new Dictionary<string, TarkovDevItem>(StringComparer.Ordinal)
            {
                ["armor-a"] = Item("armor-a", "Armor A", "armor",
                    """{"class":4,"armorSlots":[{"nameId":"Front_plate","allowedPlates":["plate-a","ammo-x"]},{"nameId":"Collar"}]}"""),
                ["armor-b"] = Item("armor-b", "Armor B", "armor",
                    """{"class":4,"armorSlots":[{"nameId":"Front_plate","allowedPlates":["plate-a","plate-b"]}]}"""),
                ["plate-a"] = Item("plate-a", "Plate A", "armorplate", """{"class":4,"durability":40}"""),
                ["plate-b"] = Item("plate-b", "Plate B", "armorplate", """{"class":4,"durability":40}"""),
                ["plate-lonely"] = Item("plate-lonely", "Plate nobody lists", "armorplate", """{"class":3}"""),
                ["ammo-x"] = Item("ammo-x", "Some round", "ammo", """{"caliber":"Caliber556x45NATO"}"""),
            },
        };
        await new SqliteDataRefreshRepository(database.Factory)
            .RefreshItemsAsync(items, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var facts = await new SqliteItemFactCatalog(database.Factory)
            .GetLoadoutFactsAsync(TestContext.Current.CancellationToken);
        var byId = facts.ToDictionary(fact => fact.ItemId, StringComparer.Ordinal);

        Assert.Equal(new[] { "armor-a", "armor-b" }, byId["plate-a"].CompatibleParentItemIds.Order());
        Assert.Equal(new[] { "armor-b" }, byId["plate-b"].CompatibleParentItemIds);
        // A plate nothing lists says nothing, so it abstains (as before); a round named in a plate
        // list is not a plate and gains no parent from a malformed list.
        Assert.Empty(byId["plate-lonely"].CompatibleParentItemIds);
        Assert.Empty(byId["ammo-x"].CompatibleParentItemIds);
        // The facts the page shows come through the same projection, with what the blob did not say unknown.
        Assert.Equal(4, byId["plate-a"].Gear!.ArmorClass);
        Assert.Equal(40, byId["plate-a"].Gear!.Durability);
        Assert.Null(byId["plate-lonely"].Gear!.Durability);
        Assert.Null(byId["ammo-x"].Gear);

        var service = new LoadoutIntelligenceService(facts, new AmmoIntelligenceService([]));
        var fits = await service.EvaluateAsync(Kit("armor-a", "plate-a"), null, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fits.CompatibilityIssues, issue => issue.Contains("armor", StringComparison.Ordinal));

        var wrong = await service.EvaluateAsync(Kit("armor-a", "plate-b"), null, TestContext.Current.CancellationToken);
        Assert.False(wrong.IsCompatible);
        Assert.Contains(wrong.CompatibilityIssues, issue => issue.Contains("Plate B is not compatible with the selected armor", StringComparison.Ordinal));
    }

    private static LoadoutSelection Kit(string armorId, string plateId) =>
        new(null, null, [], armorId, [plateId], null, null, null, null, []);

    private static TarkovDevItem Item(string id, string name, string type, string propertiesJson)
    {
        using var document = JsonDocument.Parse(propertiesJson);
        return new()
        {
            Id = id,
            Name = name,
            Width = 2,
            Height = 2,
            Types = [type],
            Properties = document.RootElement.Clone(),
        };
    }
}
