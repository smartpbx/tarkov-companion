using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.App.ViewModels;

/// <summary>
/// Which kinds of item a loadout slot takes. Before this a round could be assigned as the weapon
/// and the evaluation then priced a kit with a cartridge for a gun.
/// </summary>
/// <remarks>
/// The catalog's category is the first of the item's upstream types it recognises, so a plate
/// carrier can arrive as Armor or as Rig and a helmet can arrive as Armor; those slots take both.
/// <see cref="ItemCategory.Unknown"/> is taken everywhere: about 1,400 catalog items have it,
/// among them every weapon preset ("... Default"), and "the catalog does not say" is not "wrong".
/// </remarks>
public static class LoadoutSlotRules
{
    public static bool Accepts(LoadoutSlot slot, ItemCategory category) =>
        category == ItemCategory.Unknown || Known(slot).Contains(category);

    /// <summary>Whether the catalog positively says the item belongs in the slot.</summary>
    public static bool Fits(LoadoutSlot slot, ItemCategory category) => Known(slot).Contains(category);

    /// <summary>"Weapon takes a weapon, and 5.45x39mm BP is ammunition."</summary>
    public static string Refusal(string slotName, string itemName, ItemCategory category) =>
        $"{slotName} doesn't take {Describe(category)} · {itemName} not assigned";

    private static ItemCategory[] Known(LoadoutSlot slot) => slot switch
    {
        LoadoutSlot.Weapon => [ItemCategory.Weapon],
        LoadoutSlot.Ammunition => [ItemCategory.Ammunition, ItemCategory.AmmunitionPack],
        LoadoutSlot.Magazine => [ItemCategory.Attachment],
        LoadoutSlot.Armor => [ItemCategory.Armor, ItemCategory.Rig],
        LoadoutSlot.Plate => [ItemCategory.Plate],
        LoadoutSlot.Helmet => [ItemCategory.Helmet, ItemCategory.Armor],
        LoadoutSlot.Headset => [ItemCategory.Headset],
        LoadoutSlot.Rig => [ItemCategory.Rig, ItemCategory.Armor],
        LoadoutSlot.Backpack => [ItemCategory.Backpack],
        LoadoutSlot.Medical => [ItemCategory.Medicine, ItemCategory.Provision],
        _ => [],
    };

    private static string Describe(ItemCategory category) => category switch
    {
        ItemCategory.Ammunition => "ammunition",
        ItemCategory.AmmunitionPack => "an ammunition pack",
        ItemCategory.Weapon => "a weapon",
        ItemCategory.Attachment => "a weapon part",
        ItemCategory.Armor => "armor",
        ItemCategory.Plate => "a plate",
        ItemCategory.Helmet => "a helmet",
        ItemCategory.Headset => "a headset",
        ItemCategory.Rig => "a rig",
        ItemCategory.Backpack => "a backpack",
        ItemCategory.Medicine => "medicine",
        ItemCategory.Provision => "food or drink",
        ItemCategory.Key => "a key",
        ItemCategory.Container => "a container",
        _ => "a barter item",
    };
}
