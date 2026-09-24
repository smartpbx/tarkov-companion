using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Gear;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Loadouts;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.Application.Services.Intelligence;

public sealed record LoadoutItemFacts(
    string ItemId,
    string Name,
    ItemCategory Category,
    long? ApproximateCostRoubles,
    double? WeightKg,
    string? Caliber,
    IReadOnlySet<string> CompatibleWeaponItemIds,
    IReadOnlySet<string> CompatibleParentItemIds,
    GearFacts? Gear = null);

public sealed class LoadoutIntelligenceService
{
    private readonly IReadOnlyDictionary<string, LoadoutItemFacts> _catalog;
    private readonly AmmoIntelligenceService _ammoIntelligenceService;
    private readonly AmmoKitWarningPolicy _ammoKitWarningPolicy;

    public LoadoutIntelligenceService(
        IEnumerable<LoadoutItemFacts> catalog,
        AmmoIntelligenceService ammoIntelligenceService,
        AmmoKitWarningPolicy? ammoKitWarningPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(ammoIntelligenceService);
        _ammoKitWarningPolicy = ammoKitWarningPolicy ?? AmmoKitWarningPolicy.Default;

        _catalog = catalog.ToDictionary(x => x.ItemId, StringComparer.Ordinal);
        _ammoIntelligenceService = ammoIntelligenceService;
        if (_catalog.Values.Any(x => x.ApproximateCostRoubles is < 0 || x.WeightKg is < 0))
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

        var findings = new Findings();
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
                findings.Issue(new(LoadoutFindingKind.NotInCatalog, Item: itemId));
            }
        }

        ValidateCategory(selection.WeaponItemId, ItemCategory.Weapon, LoadoutSlotWord.Weapon, findings);
        ValidateCategory(selection.AmmunitionItemId, ItemCategory.Ammunition, LoadoutSlotWord.Ammunition, findings);
        ValidateCategory(selection.ArmorItemId, ItemCategory.Armor, LoadoutSlotWord.Armor, findings);
        ValidateCategories(selection.PlateItemIds, ItemCategory.Plate, LoadoutSlotWord.Plate, findings);
        ValidateCategory(selection.HelmetItemId, ItemCategory.Helmet, LoadoutSlotWord.Helmet, findings);
        ValidateCategory(selection.HeadsetItemId, ItemCategory.Headset, LoadoutSlotWord.Headset, findings);
        ValidateCategory(selection.RigItemId, ItemCategory.Rig, LoadoutSlotWord.Rig, findings);
        ValidateCategory(selection.BackpackItemId, ItemCategory.Backpack, LoadoutSlotWord.Backpack, findings);
        ValidateCategories(selection.MedicalItemIds, ItemCategory.Medicine, LoadoutSlotWord.Medical, findings);
        CheckWeaponAndAmmunition(selection, findings);
        CheckMagazines(selection, findings);
        CheckPlates(selection, findings);

        var ammo = selection.AmmunitionItemId is null
            ? null
            : await _ammoIntelligenceService.GetAsync(selection.AmmunitionItemId, profile, cancellationToken).ConfigureAwait(false);
        var ammoTier = ammo?.Tier ?? "Unknown";
        // Every occurrence counts: three of the same magazine are three magazines' weight and price.
        // An id the catalog does not know is one more item with no figures, so it lowers coverage.
        var priced = knownItems.Where(item => item.ApproximateCostRoubles is not null).ToArray();
        var weighed = knownItems.Where(item => item.WeightKg is not null).ToArray();
        var costCoverage = new LoadoutCoverage(priced.Length, selectedIds.Length);
        var weightCoverage = new LoadoutCoverage(weighed.Length, selectedIds.Length);
        long? knownCost = priced.Length == 0 ? null : priced.Sum(item => item.ApproximateCostRoubles!.Value);
        double? knownWeight = weighed.Length == 0 ? null : weighed.Sum(item => item.WeightKg!.Value);
        long? totalCost = costCoverage.IsComplete ? knownCost : null;
        double? totalWeight = weightCoverage.IsComplete ? knownWeight : null;

        if (ammo is not null && !ammo.ObtainableForProfile)
        {
            findings.Warn(new(LoadoutFindingKind.AmmunitionNotObtainable));
        }

        if (_ammoKitWarningPolicy.Warns(ammoTier, totalCost))
        {
            findings.Warn(new(
                LoadoutFindingKind.WeakAmmunitionForKit,
                Tier: ammoTier,
                Roubles: totalCost,
                WeakTiers: [.. _ammoKitWarningPolicy.WeakTiers.Order(StringComparer.Ordinal)],
                ThresholdRoubles: _ammoKitWarningPolicy.KitCostThresholdRoubles));
        }

        if (selection.ArmorItemId is not null && selection.PlateItemIds.Count == 0 && !StatesNoPlateSlots(selection.ArmorItemId))
        {
            findings.Warn(new(LoadoutFindingKind.ArmorWithoutPlates));
        }

        return new(
            totalCost,
            totalWeight,
            findings.Issues.Count == 0,
            findings.Issues,
            findings.Warnings,
            ammoTier,
            costCoverage,
            weightCoverage,
            knownCost,
            knownWeight);
    }

    // True only when the catalog says so: an armor with a stated class and no plate slots (a soft
    // vest) has nothing to put plates in. An armor the catalog knows nothing about keeps the warning,
    // because no plate slots is not the same as no facts.
    private bool StatesNoPlateSlots(string armorItemId) =>
        TryGet(armorItemId, out var armor) && armor.Gear is { ArmorClass: not null, PlateSlots.Count: 0 };

    private void CheckWeaponAndAmmunition(LoadoutSelection selection, Findings findings)
    {
        if (!TryGet(selection.WeaponItemId, out var weapon) || !TryGet(selection.AmmunitionItemId, out var ammo))
        {
            return;
        }

        // Through CaliberKey, not as the strings come: the PP-9 Klin's caliber is written
        // differently from every round it fires, and was told it could use none of them.
        if (weapon.Caliber is not null && ammo.Caliber is not null &&
            !CaliberKey.Matches(weapon.Caliber, ammo.Caliber))
        {
            findings.Issue(new(
                LoadoutFindingKind.CaliberMismatch,
                Item: ammo.Name,
                ItemCaliber: CaliberText.Describe(ammo.Caliber, ammo.Name),
                Other: weapon.Name,
                OtherCaliber: CaliberText.Describe(weapon.Caliber, weapon.Name)));
        }
    }

    private void CheckMagazines(LoadoutSelection selection, Findings findings)
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
                findings.Issue(new(LoadoutFindingKind.MagazineDoesNotFitWeapon, Item: magazine.Name));
            }

            if (TryGet(selection.AmmunitionItemId, out var ammo) &&
                magazine.Caliber is not null && ammo.Caliber is not null &&
                !CaliberKey.Matches(magazine.Caliber, ammo.Caliber))
            {
                findings.Issue(new(
                    LoadoutFindingKind.MagazineCaliberMismatch,
                    Item: magazine.Name,
                    OtherCaliber: CaliberText.Describe(ammo.Caliber, ammo.Name)));
            }
        }
    }

    private void CheckPlates(LoadoutSelection selection, Findings findings)
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
                findings.Issue(new(LoadoutFindingKind.PlateDoesNotFit, Item: plate.Name));
            }
        }
    }

    private void ValidateCategory(
        string? itemId,
        ItemCategory category,
        LoadoutSlotWord slot,
        Findings findings)
    {
        if (TryGet(itemId, out var item) && item.Category != category)
        {
            findings.Issue(new(LoadoutFindingKind.WrongSlot, Item: item.Name, Slot: slot, Category: item.Category.ToString()));
        }
    }

    private void ValidateCategories(
        IEnumerable<string> itemIds,
        ItemCategory category,
        LoadoutSlotWord slot,
        Findings findings)
    {
        foreach (var itemId in itemIds.Distinct(StringComparer.Ordinal))
        {
            ValidateCategory(itemId, category, slot, findings);
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

    /// <summary>The issues and warnings found so far; the App says each and why it was raised.</summary>
    private sealed class Findings
    {
        public List<LoadoutFinding> Issues { get; } = [];

        public List<LoadoutFinding> Warnings { get; } = [];

        public void Issue(LoadoutFinding finding) => Issues.Add(finding);

        public void Warn(LoadoutFinding finding) => Warnings.Add(finding);
    }
}
