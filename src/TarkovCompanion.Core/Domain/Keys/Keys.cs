using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Keys;

public sealed record KeyScoreComponents(
    double Quest,
    double Economy,
    double Uses,
    double LockUtility,
    double UniqueAccess,
    double RiskAdjustedLoot)
{
    public double WeightedTotal =>
        (Quest * 0.30) +
        (Economy * 0.15) +
        (Uses * 0.10) +
        (LockUtility * 0.20) +
        (UniqueAccess * 0.15) +
        (RiskAdjustedLoot * 0.10);
}

public sealed record KeyIntelligence(
    string ItemId,
    string? MapId,
    int? MaximumUses,
    int? RemainingUses,
    IReadOnlyList<string> Locks,
    IReadOnlyList<string> RelevantTaskIds,
    KeyScoreComponents Score,
    string Tier,
    string Advice,
    string Explanation,
    bool IsCurated,
    DataProvenance Provenance);
