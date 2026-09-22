using System.Text.Json;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.IntegrationTests.DataV2;

/// <summary>
/// The catalog files a weapon preset ("M4A1 2k17 NY") as Unknown with only a baseItem, so Loadout
/// listed every built gun as "Unknown · No caliber recorded". Through the same refresh and catalog
/// code the app runs, a preset now carries its base weapon's kind, caliber and accepted rounds.
/// </summary>
public sealed class PresetLoadoutFactsTests
{
    [Fact]
    public async Task A_preset_takes_its_kind_caliber_and_rounds_from_its_base_weapon()
    {
        await using var database = await V2TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var items = new TarkovDevItemsData
        {
            Items = new Dictionary<string, TarkovDevItem>(StringComparer.Ordinal)
            {
                ["m4"] = Item("m4", "Colt M4A1", "gun",
                    """{"propertiesType":"ItemPropertiesWeapon","caliber":"Caliber556x45NATO","allowedAmmo":["round"]}"""),
                ["m4-preset"] = Item("m4-preset", "Colt M4A1 2k17 NY", "preset",
                    """{"propertiesType":"ItemPropertiesPreset","ergonomics":68,"baseItem":"m4"}"""),
                ["orphan-preset"] = Item("orphan-preset", "A preset of nothing we know", "preset",
                    """{"propertiesType":"ItemPropertiesPreset","baseItem":"missing"}"""),
                ["round"] = Item("round", "M855", "ammo",
                    """{"propertiesType":"ItemPropertiesAmmo","caliber":"Caliber556x45NATO"}"""),
            },
        };
        await new SqliteDataRefreshRepository(database.Factory)
            .RefreshItemsAsync(items, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        var facts = (await new SqliteItemFactCatalog(database.Factory)
            .GetLoadoutFactsAsync(TestContext.Current.CancellationToken))
            .ToDictionary(fact => fact.ItemId, StringComparer.Ordinal);

        Assert.Equal(facts["m4"].Category, facts["m4-preset"].Category);
        Assert.NotEqual(ItemCategory.Unknown, facts["m4-preset"].Category);
        Assert.Equal("Caliber556x45NATO", facts["m4-preset"].Caliber);
        Assert.Contains("m4-preset", facts["round"].CompatibleParentItemIds);
        Assert.Equal("Colt M4A1 2k17 NY", facts["m4-preset"].Name);
        Assert.Null(facts["orphan-preset"].Caliber);
    }

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
