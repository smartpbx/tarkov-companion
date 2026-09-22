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
                findings.Issue(
                    $"No compatibility data is available for '{itemId}'.",
                    "The loaded catalog has no entry for this item, so it could not be checked against the rest of the kit or priced. It counts as unpriced and unweighed in the totals.");
            }
        }

        ValidateCategory(selection.WeaponItemId, ItemCategory.Weapon, "weapon", findings);
        ValidateCategory(selection.AmmunitionItemId, ItemCategory.Ammunition, "ammunition", findings);
        ValidateCategory(selection.ArmorItemId, ItemCategory.Armor, "armor", findings);
        ValidateCategories(selection.PlateItemIds, ItemCategory.Plate, "plate", findings);
        ValidateCategory(selection.HelmetItemId, ItemCategory.Helmet, "helmet", findings);
        ValidateCategory(selection.HeadsetItemId, ItemCategory.Headset, "headset", findings);
        ValidateCategory(selection.RigItemId, ItemCategory.Rig, "rig", findings);
        ValidateCategory(selection.BackpackItemId, ItemCategory.Backpack, "backpack", findings);
        ValidateCategories(selection.MedicalItemIds, ItemCategory.Medicine, "medical", findings);
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
            findings.Warn(
                "The selected ammunition is not obtainable for the active profile rules.",
                "The rules of the active profile (its game mode and what it has unlocked) say this round cannot be bought or found yet, so a kit built around it cannot be assembled as shown.");
        }

        if (_ammoKitWarningPolicy.Warns(ammoTier, totalCost))
        {
            findings.Warn(
                FormattableString.Invariant($"{ammoTier}-tier ammunition is weak relative to this {totalCost:N0}-rouble kit."),
                FormattableString.Invariant($"A rule of thumb, not a ballistic result: ammunition in tier {string.Join(" or ", _ammoKitWarningPolicy.WeakTiers.Order(StringComparer.Ordinal))} is treated as weak, and a kit costing {_ammoKitWarningPolicy.KitCostThresholdRoubles:N0} roubles or more is a costly one to load with it. It is shown only when every item in the kit has a price."));
        }

        if (selection.ArmorItemId is not null && selection.PlateItemIds.Count == 0 && !StatesNoPlateSlots(selection.ArmorItemId))
        {
            findings.Warn(
                "Armor is selected without any known plate selection.",
                "This armor takes plates in its plate slots, and the protection it gives comes from them. With none selected the slots are empty.");
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
            knownWeight,
            findings.Why);
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
            findings.Issue(
                $"{ammo.Name} ({CaliberText.Describe(ammo.Caliber, ammo.Name)}) does not match {weapon.Name} ({CaliberText.Describe(weapon.Caliber, weapon.Name)}).",
                "A weapon fires only its own caliber. The catalog lists a caliber for both and they differ, so this weapon cannot use this ammunition.");
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
                findings.Issue(
                    $"{magazine.Name} is not compatible with the selected weapon.",
                    "The catalog lists the weapons this magazine fits, and the selected weapon is not one of them.");
            }

            if (TryGet(selection.AmmunitionItemId, out var ammo) &&
                magazine.Caliber is not null && ammo.Caliber is not null &&
                !CaliberKey.Matches(magazine.Caliber, ammo.Caliber))
            {
                findings.Issue(
                    $"{magazine.Name} does not accept {CaliberText.Describe(ammo.Caliber, ammo.Name)} ammunition.",
                    "A magazine holds one caliber. The catalog gives this magazine's caliber and the selected round's, and they differ.");
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
                findings.Issue(
                    $"{plate.Name} is not compatible with the selected armor.",
                    "A body armor lists the plates that fit each of its plate slots. This plate is not on the selected armor's list, so it would not fit.");
            }
        }
    }

    private void ValidateCategory(
        string? itemId,
        ItemCategory category,
        string slot,
        Findings findings)
    {
        if (TryGet(itemId, out var item) && item.Category != category)
        {
            findings.Issue(
                $"{item.Name} is not valid for the {slot} slot.",
                $"Each slot takes one kind of item. The catalog files this item under {item.Category}, which is not what the {slot} slot takes, so it belongs in a different slot.");
        }
    }

    private void ValidateCategories(
        IEnumerable<string> itemIds,
        ItemCategory category,
        string slot,
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

    /// <summary>The issues and warnings found so far, each with the plain reason it was raised.</summary>
    private sealed class Findings
    {
        public List<string> Issues { get; } = [];

        public List<string> Warnings { get; } = [];

        public Dictionary<string, string> Why { get; } = new(StringComparer.Ordinal);

        public void Issue(string message, string why)
        {
            Issues.Add(message);
            Why[message] = why;
        }

        public void Warn(string message, string why)
        {
            Warnings.Add(message);
            Why[message] = why;
        }
    }
}
