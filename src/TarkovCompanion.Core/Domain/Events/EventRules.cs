using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Events;

/// <summary>One effect from a hand-authored event definition that the companion can reason about.</summary>
public abstract record EventRuleEffect;

public sealed record TraderPriceMultiplierRule(
    string TraderId,
    string TraderName,
    decimal Multiplier) : EventRuleEffect;

public sealed record FleaAvailabilityRule(bool Enabled) : EventRuleEffect;

public sealed record MapAvailabilityRule(
    string MapId,
    string MapName,
    bool Available) : EventRuleEffect;

public sealed record BossSpawnMultiplierRule(
    string BossId,
    string BossName,
    string? MapId,
    decimal Multiplier) : EventRuleEffect;

public sealed record QuestAvailabilityWindowRule(
    string QuestId,
    string QuestName,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc) : EventRuleEffect;

public sealed record EventRuleSet(IReadOnlyList<EventRuleEffect> Effects)
{
    public static EventRuleSet Empty { get; } = new([]);
}

/// <summary>An active effect together with the local definition and provenance that asserted it.</summary>
public sealed record ActiveEventRule(
    string EventId,
    string EventName,
    EventRuleEffect Effect,
    DataProvenance Provenance);

/// <summary>
/// The effects in force at one instant. Restrictive availability wins when local definitions
/// disagree, while trader multipliers compose in stable event-id order.
/// </summary>
public sealed record ActiveEventRules(IReadOnlyList<ActiveEventRule> Rules)
{
    public static ActiveEventRules Empty { get; } = new([]);

    public bool FleaEnabled => !Rules.Any(rule => rule.Effect is FleaAvailabilityRule { Enabled: false });

    public bool IsMapAvailable(string mapId) => !Rules.Any(rule =>
        rule.Effect is MapAvailabilityRule { Available: false } map &&
        string.Equals(map.MapId, mapId, StringComparison.OrdinalIgnoreCase));

    public decimal TraderMultiplier(string traderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traderId);
        var multiplier = 1m;
        foreach (var rule in Rules.OrderBy(rule => rule.EventId, StringComparer.Ordinal))
        {
            if (rule.Effect is TraderPriceMultiplierRule trader &&
                string.Equals(trader.TraderId, traderId, StringComparison.OrdinalIgnoreCase))
            {
                multiplier = multiplier > 100m / trader.Multiplier
                    ? 100m
                    : Math.Min(100m, multiplier * trader.Multiplier);
            }
        }

        return multiplier;
    }
}
