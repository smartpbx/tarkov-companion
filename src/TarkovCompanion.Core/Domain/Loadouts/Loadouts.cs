using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Loadouts;

public sealed record LoadoutSelection(
    string? WeaponItemId,
    string? AmmunitionItemId,
    IReadOnlyList<string> MagazineItemIds,
    string? ArmorItemId,
    IReadOnlyList<string> PlateItemIds,
    string? HelmetItemId,
    string? HeadsetItemId,
    string? RigItemId,
    string? BackpackItemId,
    IReadOnlyList<string> MedicalItemIds);

/// <summary>How many of a kit's items a total was built from, out of how many are in it.</summary>
public readonly record struct LoadoutCoverage(int Known, int Total)
{
    public bool IsComplete => Known == Total;
}

/// <param name="ApproximateCostRoubles">The kit's cost when every item is priced, otherwise null.</param>
/// <param name="ApproximateWeightKg">The kit's weight when every item is weighed, otherwise null.</param>
/// <param name="KnownCostRoubles">
/// The sum of the prices that are known, or null when none is. It is a floor, never a total, and
/// is meaningful only beside <paramref name="CostCoverage"/>. An item listed twice counts twice.
/// </param>
/// <param name="KnownWeightKg">As <paramref name="KnownCostRoubles"/>, for weight.</param>
/// <param name="CompatibilityIssues">
/// What stops the kit going together. Each finding is a code and the names and numbers it is about;
/// the App says both the finding and why it was raised (#314), fixed wording that names which fact
/// the rule compared and, for the ammunition warning, the policy's own numbers. Nothing here is a
/// ballistic or economic judgement, and nothing needs the network.
/// </param>
public sealed record LoadoutEvaluation(
    long? ApproximateCostRoubles,
    double? ApproximateWeightKg,
    bool IsCompatible,
    IReadOnlyList<LoadoutFinding> CompatibilityIssues,
    IReadOnlyList<LoadoutFinding> Warnings,
    string AmmoTier,
    LoadoutCoverage CostCoverage = default,
    LoadoutCoverage WeightCoverage = default,
    long? KnownCostRoubles = null,
    double? KnownWeightKg = null);

/// <summary>Which check raised a loadout finding; the App has a sentence and a reason for each.</summary>
[PhraseCodes("Plan.Loadout.Finding")]
public enum LoadoutFindingKind
{
    /// <summary>The catalog has no entry for <see cref="LoadoutFinding.Item"/> (an id here).</summary>
    NotInCatalog,

    /// <summary><see cref="LoadoutFinding.Item"/> is filed under <see cref="LoadoutFinding.Category"/>, not what <see cref="LoadoutFinding.Slot"/> takes.</summary>
    WrongSlot,

    /// <summary>The round <see cref="LoadoutFinding.Item"/> and the weapon <see cref="LoadoutFinding.Other"/> differ in caliber.</summary>
    CaliberMismatch,

    /// <summary>The magazine <see cref="LoadoutFinding.Item"/> does not list the selected weapon.</summary>
    MagazineDoesNotFitWeapon,

    /// <summary>The magazine <see cref="LoadoutFinding.Item"/> holds another caliber than <see cref="LoadoutFinding.OtherCaliber"/>.</summary>
    MagazineCaliberMismatch,

    /// <summary>The plate <see cref="LoadoutFinding.Item"/> is not on the selected armor's list.</summary>
    PlateDoesNotFit,

    /// <summary>The active profile's rules say the selected round cannot be had yet.</summary>
    AmmunitionNotObtainable,

    /// <summary>A weak <see cref="LoadoutFinding.Tier"/> round in a kit that costs <see cref="LoadoutFinding.Roubles"/>.</summary>
    WeakAmmunitionForKit,

    /// <summary>Armor with plate slots and no plate chosen.</summary>
    ArmorWithoutPlates,
}

/// <summary>The loadout slot a finding names, said in lower case in the sentence.</summary>
[PhraseCodes("Plan.Loadout.SlotWord")]
public enum LoadoutSlotWord
{
    Weapon,
    Ammunition,
    Armor,
    Plate,
    Helmet,
    Headset,
    Rig,
    Backpack,
    Medical,
}

/// <summary>
/// One loadout finding: the check that raised it and the names and numbers it is about. Names are
/// the catalog's; the category is the catalog's own filing word.
/// </summary>
public sealed record LoadoutFinding(
    LoadoutFindingKind Kind,
    string? Item = null,
    string? ItemCaliber = null,
    string? Other = null,
    string? OtherCaliber = null,
    LoadoutSlotWord? Slot = null,
    string? Category = null,
    string? Tier = null,
    long? Roubles = null,
    IReadOnlyList<string>? WeakTiers = null,
    long? ThresholdRoubles = null);
