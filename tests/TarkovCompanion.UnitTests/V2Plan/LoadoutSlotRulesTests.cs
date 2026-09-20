using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.UnitTests.V2Plan;

public sealed class LoadoutSlotRulesTests
{
    [Theory]
    [InlineData(LoadoutSlot.Weapon, ItemCategory.Weapon, true)]
    [InlineData(LoadoutSlot.Weapon, ItemCategory.Ammunition, false)]
    [InlineData(LoadoutSlot.Weapon, ItemCategory.AmmunitionPack, false)]
    [InlineData(LoadoutSlot.Ammunition, ItemCategory.Ammunition, true)]
    [InlineData(LoadoutSlot.Ammunition, ItemCategory.AmmunitionPack, true)]
    [InlineData(LoadoutSlot.Ammunition, ItemCategory.Weapon, false)]
    [InlineData(LoadoutSlot.Magazine, ItemCategory.Attachment, true)]
    [InlineData(LoadoutSlot.Armor, ItemCategory.Rig, true)]
    [InlineData(LoadoutSlot.Rig, ItemCategory.Armor, true)]
    [InlineData(LoadoutSlot.Helmet, ItemCategory.Armor, true)]
    [InlineData(LoadoutSlot.Helmet, ItemCategory.Backpack, false)]
    [InlineData(LoadoutSlot.Medical, ItemCategory.Medicine, true)]
    [InlineData(LoadoutSlot.Medical, ItemCategory.Provision, true)]
    [InlineData(LoadoutSlot.Medical, ItemCategory.Key, false)]
    public void ASlotTakesItsOwnKindOfItem(LoadoutSlot slot, ItemCategory category, bool accepted)
    {
        Assert.Equal(accepted, LoadoutSlotRules.Accepts(slot, category));
    }

    [Fact]
    public void AnItemTheCatalogCannotClassifyIsTakenButDoesNotCountAsAFit()
    {
        // Every weapon preset is Unknown in the catalog; refusing those would empty the Weapon slot.
        foreach (var slot in Enum.GetValues<LoadoutSlot>())
        {
            Assert.True(LoadoutSlotRules.Accepts(slot, ItemCategory.Unknown));
            Assert.False(LoadoutSlotRules.Fits(slot, ItemCategory.Unknown));
        }
    }

    [Fact]
    public void ARefusalNamesTheSlotTheKindAndTheItem()
    {
        Assert.Equal(
            "Weapon doesn't take ammunition · 5.45x39mm BP not assigned",
            LoadoutSlotRules.Refusal("Weapon", "5.45x39mm BP", ItemCategory.Ammunition));
    }
}
