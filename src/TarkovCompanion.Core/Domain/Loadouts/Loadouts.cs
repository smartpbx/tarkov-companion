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
/// <param name="Explanations">
/// Why each compatibility issue and warning was raised, keyed by its message: fixed wording that
/// says which fact the rule compared and, for the ammunition warning, the policy's own numbers.
/// Nothing here is a ballistic or economic judgement, and nothing needs the network.
/// </param>
public sealed record LoadoutEvaluation(
    long? ApproximateCostRoubles,
    double? ApproximateWeightKg,
    bool IsCompatible,
    IReadOnlyList<string> CompatibilityIssues,
    IReadOnlyList<string> Warnings,
    string AmmoTier,
    LoadoutCoverage CostCoverage = default,
    LoadoutCoverage WeightCoverage = default,
    long? KnownCostRoubles = null,
    double? KnownWeightKg = null,
    IReadOnlyDictionary<string, string>? Explanations = null);
