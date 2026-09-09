using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Ammo;

public sealed record AmmoStats(
    string ItemId,
    string Caliber,
    int Damage,
    int Penetration,
    int? ArmorDamagePercent,
    double? FragmentationChance,
    int ProjectileCount,
    double? VelocityMetresPerSecond,
    double? RecoilModifier,
    double? AccuracyModifier,
    bool IsTracer,
    bool IsSubsonic,
    DataProvenance Provenance);

public enum ArmorEffectiveness
{
    Poor,
    Limited,
    Fair,
    Good,
    Excellent,
}

public sealed record AmmoIntelligence(
    AmmoStats Stats,
    string Tier,
    IReadOnlyDictionary<int, ArmorEffectiveness> ArmorClassRatings,
    string PracticalAdvice,
    string LearnModeExplanation,
    bool ObtainableForProfile,
    Confidence Confidence);

public sealed record AmmoPackContents(string PackItemId, string AmmoItemId, int Quantity, DataProvenance Provenance);
