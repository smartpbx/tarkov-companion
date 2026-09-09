using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Keys;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.Application.Services.Intelligence;

public sealed record KeyFacts(
    string ItemId,
    string? MapId,
    int? MaximumUses,
    IReadOnlyList<string> Locks,
    IReadOnlyList<string> RelevantTaskIds,
    long AcquisitionCostRoubles,
    long ExpectedLootRoubles,
    double Utility,
    bool GrantsUniqueAccess,
    double RouteRisk,
    DataProvenance Provenance);

public sealed record CuratedKeyOverride(
    string ItemId,
    KeyScoreComponents? Score,
    string? Tier,
    string? Advice,
    string? Explanation,
    DataProvenance Provenance);

public sealed class KeyIntelligenceService : IKeyIntelligenceService
{
    private readonly IReadOnlyDictionary<string, KeyFacts> _facts;
    private readonly IReadOnlyDictionary<string, CuratedKeyOverride> _overrides;

    public KeyIntelligenceService(
        IEnumerable<KeyFacts> facts,
        IEnumerable<CuratedKeyOverride>? curatedOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(facts);

        _facts = facts.ToDictionary(x => x.ItemId, StringComparer.Ordinal);
        _overrides = (curatedOverrides ?? []).ToDictionary(x => x.ItemId, StringComparer.Ordinal);
        foreach (var item in _facts.Values)
        {
            if (item.AcquisitionCostRoubles < 0 || item.ExpectedLootRoubles < 0 ||
                item.Utility is < 0 or > 100 || item.RouteRisk is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(facts), "Key facts contain an out-of-range score input.");
            }
        }

        foreach (var item in _overrides.Values)
        {
            if (string.IsNullOrWhiteSpace(item.Provenance.Source) ||
                string.IsNullOrWhiteSpace(item.Provenance.Reference) ||
                item.Provenance.ObservedUtc == default ||
                item.Provenance.Confidence is null ||
                (item.Score is not null && !IsNormalized(item.Score)) ||
                (item.Tier is not null && item.Tier is not ("S" or "A" or "B" or "C" or "D")))
            {
                throw new ArgumentException(
                    "Curated key overrides require valid scores/tier plus source, reference, date, and confidence.",
                    nameof(curatedOverrides));
            }
        }
    }

    public Task<KeyIntelligence?> GetAsync(
        string itemId,
        PlayerProfile? profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_facts.TryGetValue(itemId, out var facts))
        {
            return Task.FromResult<KeyIntelligence?>(null);
        }

        var score = Score(facts, profile);
        var explanation = Explain(facts, profile, score);
        var tier = Tier(score.WeightedTotal);
        var advice = Advice(tier, facts);
        var isCurated = _overrides.TryGetValue(itemId, out var curated);
        if (curated is not null)
        {
            score = curated.Score ?? score;
            tier = curated.Tier ?? Tier(score.WeightedTotal);
            advice = curated.Advice ?? advice;
            explanation = curated.Explanation ?? explanation;
        }

        var result = new KeyIntelligence(
            itemId,
            facts.MapId,
            facts.MaximumUses,
            facts.MaximumUses,
            facts.Locks,
            facts.RelevantTaskIds,
            score,
            tier,
            advice,
            explanation,
            isCurated,
            curated?.Provenance ?? facts.Provenance);
        return Task.FromResult<KeyIntelligence?>(result);
    }

    private static KeyScoreComponents Score(KeyFacts facts, PlayerProfile? profile)
    {
        var incompleteTasks = profile is null
            ? facts.RelevantTaskIds.Count
            : facts.RelevantTaskIds.Count(x => !profile.CompletedTaskIds.Contains(x));
        var quest = facts.RelevantTaskIds.Count == 0
            ? 0
            : profile is null
                ? 75
                : 100d * incompleteTasks / facts.RelevantTaskIds.Count;
        var economy = NormalizeRatio(facts.ExpectedLootRoubles, facts.AcquisitionCostRoubles, 3);
        var uses = facts.MaximumUses switch
        {
            null => 100,
            >= 40 => 85,
            >= 10 => 65,
            > 0 => 40,
            _ => 0,
        };
        var lockBreadth = Math.Min(100, facts.Locks.Count * 25d);
        var lockUtility = (lockBreadth + facts.Utility) / 2;
        var uniqueAccess = facts.GrantsUniqueAccess ? 100 : 0;
        var riskAdjustedLoot = NormalizeRatio(
            (long)Math.Round(facts.ExpectedLootRoubles * (1 - facts.RouteRisk)),
            facts.AcquisitionCostRoubles,
            2);

        return new(quest, economy, uses, lockUtility, uniqueAccess, riskAdjustedLoot);
    }

    private static double NormalizeRatio(long value, long cost, double fullScoreRatio)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (cost <= 0)
        {
            return 100;
        }

        return Math.Clamp((value / (double)cost) / fullScoreRatio * 100, 0, 100);
    }

    private static string Explain(KeyFacts facts, PlayerProfile? profile, KeyScoreComponents score)
    {
        var personalQuestCount = profile is null
            ? facts.RelevantTaskIds.Count
            : facts.RelevantTaskIds.Count(x => !profile.CompletedTaskIds.Contains(x));
        var uses = facts.MaximumUses?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "reusable";
        return FormattableString.Invariant($"Weighted score {score.WeightedTotal:F1}: {personalQuestCount} personal quest locks, {uses} maximum uses, {facts.Locks.Count} locks, {facts.ExpectedLootRoubles} expected loot versus {facts.AcquisitionCostRoubles} cost, utility {facts.Utility:F0}, unique access {(facts.GrantsUniqueAccess ? "yes" : "no")}, route risk {facts.RouteRisk:P0}.");
    }

    private static string Advice(string tier, KeyFacts facts) => tier switch
    {
        "S" or "A" => $"Prioritize this {facts.MapId ?? "unknown-map"} key when its locks fit the raid plan.",
        "B" or "C" => "Situational: compare the lock route with the current raid objective.",
        _ => "Low general priority unless a personal objective specifically requires it.",
    };

    private static string Tier(double total) => total switch
    {
        >= 80 => "S",
        >= 65 => "A",
        >= 50 => "B",
        >= 35 => "C",
        _ => "D",
    };

    private static bool IsNormalized(KeyScoreComponents score) =>
        score.Quest is >= 0 and <= 100 &&
        score.Economy is >= 0 and <= 100 &&
        score.Uses is >= 0 and <= 100 &&
        score.LockUtility is >= 0 and <= 100 &&
        score.UniqueAccess is >= 0 and <= 100 &&
        score.RiskAdjustedLoot is >= 0 and <= 100;
}
