using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.Application.Services.Intelligence;

public sealed record LoadoutItemFacts(
    string ItemId,
    string Name,
    ItemCategory Category,
    long ApproximateCostRoubles,
    double WeightKg,
    string? Caliber,
    IReadOnlySet<string> CompatibleWeaponItemIds,
    IReadOnlySet<string> CompatibleParentItemIds);

public sealed class LoadoutIntelligenceService : ILoadoutService
{
    private readonly IReadOnlyDictionary<string, LoadoutItemFacts> _catalog;
    private readonly IAmmoIntelligenceService _ammoIntelligenceService;

    public LoadoutIntelligenceService(
        IEnumerable<LoadoutItemFacts> catalog,
        IAmmoIntelligenceService ammoIntelligenceService)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(ammoIntelligenceService);

        _catalog = catalog.ToDictionary(x => x.ItemId, StringComparer.Ordinal);
        _ammoIntelligenceService = ammoIntelligenceService;
        if (_catalog.Values.Any(x => x.ApproximateCostRoubles < 0 || x.WeightKg < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(catalog), "Loadout cost and weight cannot be negative.");
        }
    }

    public async Task<LoadoutEvaluation> EvaluateAsync(
        LoadoutSelection selection,
        PlayerProfile? profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();

        var issues = new List<string>();
        var warnings = new List<string>();
        var selectedIds = EnumerateSelectedIds(selection).ToArray();
        var knownItems = new List<LoadoutItemFacts>();
        foreach (var itemId in selectedIds)
        {
            if (_catalog.TryGetValue(itemId, out var item))
            {
                knownItems.Add(item);
            }
            else
            {
                issues.Add($"No compatibility data is available for '{itemId}'.");
            }
        }

        ValidateCategory(selection.WeaponItemId, ItemCategory.Weapon, "weapon", issues);
        ValidateCategory(selection.AmmunitionItemId, ItemCategory.Ammunition, "ammunition", issues);
        ValidateCategory(selection.ArmorItemId, ItemCategory.Armor, "armor", issues);
        ValidateCategories(selection.PlateItemIds, ItemCategory.Plate, "plate", issues);
        ValidateCategory(selection.HelmetItemId, ItemCategory.Helmet, "helmet", issues);
        ValidateCategory(selection.HeadsetItemId, ItemCategory.Headset, "headset", issues);
        ValidateCategory(selection.RigItemId, ItemCategory.Rig, "rig", issues);
        ValidateCategory(selection.BackpackItemId, ItemCategory.Backpack, "backpack", issues);
        ValidateCategories(selection.MedicalItemIds, ItemCategory.Medicine, "medical", issues);
        CheckWeaponAndAmmunition(selection, issues);
        CheckMagazines(selection, issues);
        CheckPlates(selection, issues);

        var ammo = selection.AmmunitionItemId is null
            ? null
            : await _ammoIntelligenceService.GetAsync(selection.AmmunitionItemId, profile, cancellationToken).ConfigureAwait(false);
        var ammoTier = ammo?.Tier ?? "Unknown";
        var totalCost = knownItems.Sum(x => x.ApproximateCostRoubles);
        double? totalWeight = knownItems.Count == selectedIds.Length ? knownItems.Sum(x => x.WeightKg) : null;

        if (ammo is not null && !ammo.ObtainableForProfile)
        {
            warnings.Add("The selected ammunition is not obtainable for the active profile rules.");
        }

        if (ammoTier is "C" or "D" && totalCost >= 150_000)
        {
            warnings.Add(FormattableString.Invariant($"{ammoTier}-tier ammunition is weak relative to this {totalCost:N0}-rouble kit."));
        }

        if (selection.ArmorItemId is not null && selection.PlateItemIds.Count == 0)
        {
            warnings.Add("Armor is selected without any known plate selection.");
        }

        return new(totalCost, totalWeight, issues.Count == 0, issues, warnings, ammoTier);
    }

    private void CheckWeaponAndAmmunition(LoadoutSelection selection, List<string> issues)
    {
        if (!TryGet(selection.WeaponItemId, out var weapon) || !TryGet(selection.AmmunitionItemId, out var ammo))
        {
            return;
        }

        if (weapon.Caliber is not null && ammo.Caliber is not null &&
            !StringComparer.OrdinalIgnoreCase.Equals(weapon.Caliber, ammo.Caliber))
        {
            issues.Add($"{ammo.Name} ({ammo.Caliber}) does not match {weapon.Name} ({weapon.Caliber}).");
        }
    }

    private void CheckMagazines(LoadoutSelection selection, List<string> issues)
    {
        foreach (var magazineId in selection.MagazineItemIds)
        {
            if (!TryGet(magazineId, out var magazine))
            {
                continue;
            }

            if (selection.WeaponItemId is not null &&
                magazine.CompatibleWeaponItemIds.Count > 0 &&
                !magazine.CompatibleWeaponItemIds.Contains(selection.WeaponItemId))
            {
                issues.Add($"{magazine.Name} is not compatible with the selected weapon.");
            }

            if (TryGet(selection.AmmunitionItemId, out var ammo) &&
                magazine.Caliber is not null && ammo.Caliber is not null &&
                !StringComparer.OrdinalIgnoreCase.Equals(magazine.Caliber, ammo.Caliber))
            {
                issues.Add($"{magazine.Name} does not accept {ammo.Caliber} ammunition.");
            }
        }
    }

    private void CheckPlates(LoadoutSelection selection, List<string> issues)
    {
        foreach (var plateId in selection.PlateItemIds)
        {
            if (!TryGet(plateId, out var plate) || selection.ArmorItemId is null)
            {
                continue;
            }

            if (plate.CompatibleParentItemIds.Count > 0 &&
                !plate.CompatibleParentItemIds.Contains(selection.ArmorItemId))
            {
                issues.Add($"{plate.Name} is not compatible with the selected armor.");
            }
        }
    }

    private void ValidateCategory(
        string? itemId,
        ItemCategory category,
        string slot,
        List<string> issues)
    {
        if (TryGet(itemId, out var item) && item.Category != category)
        {
            issues.Add($"{item.Name} is not valid for the {slot} slot.");
        }
    }

    private void ValidateCategories(
        IEnumerable<string> itemIds,
        ItemCategory category,
        string slot,
        List<string> issues)
    {
        foreach (var itemId in itemIds.Distinct(StringComparer.Ordinal))
        {
            ValidateCategory(itemId, category, slot, issues);
        }
    }

    private bool TryGet(string? itemId, out LoadoutItemFacts item)
    {
        if (itemId is not null && _catalog.TryGetValue(itemId, out var value))
        {
            item = value;
            return true;
        }

        item = null!;
        return false;
    }

    private static IEnumerable<string> EnumerateSelectedIds(LoadoutSelection selection)
    {
        if (selection.WeaponItemId is not null) yield return selection.WeaponItemId;
        if (selection.AmmunitionItemId is not null) yield return selection.AmmunitionItemId;
        foreach (var itemId in selection.MagazineItemIds) yield return itemId;
        if (selection.ArmorItemId is not null) yield return selection.ArmorItemId;
        foreach (var itemId in selection.PlateItemIds) yield return itemId;
        if (selection.HelmetItemId is not null) yield return selection.HelmetItemId;
        if (selection.HeadsetItemId is not null) yield return selection.HeadsetItemId;
        if (selection.RigItemId is not null) yield return selection.RigItemId;
        if (selection.BackpackItemId is not null) yield return selection.BackpackItemId;
        foreach (var itemId in selection.MedicalItemIds) yield return itemId;
    }
}
