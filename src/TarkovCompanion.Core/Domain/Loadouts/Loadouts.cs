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

public sealed record LoadoutEvaluation(
    long? ApproximateCostRoubles,
    double? ApproximateWeightKg,
    bool IsCompatible,
    IReadOnlyList<string> CompatibilityIssues,
    IReadOnlyList<string> Warnings,
    string AmmoTier);
