using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;

namespace TarkovCompanion.Core.Domain.Recommendations;

public enum RecommendationAction
{
    EssentialKeep,
    Keep,
    Use,
    SellFlea,
    SellTrader,
    DropFirst,
    AvoidConsume,
    EventTestCandidate,
    Unknown,
}

public enum RecommendationReasonCode
{
    UserOverride,
    KnownAllergy,
    ActiveEventRule,
    OutstandingQuestFoundInRaid,
    OutstandingQuest,
    HideoutRequirement,
    Wishlist,
    KeyUtility,
    AmmoQuality,
    BestFleaValue,
    BestTraderValue,
    HighValuePerSlot,
    LowValuePerSlot,
    MissingPriceData,
    AmbiguousRecognition,
}

public sealed record RecommendationReason(
    RecommendationReasonCode Code,
    string Explanation,
    int Priority);

public sealed record RecommendationContext(
    bool IsFoundInRaid,
    int OutstandingQuestCount,
    int OutstandingFoundInRaidQuestCount,
    int OutstandingHideoutCount,
    bool IsWishlisted,
    EventItemState EventState,
    string? UserOverride,
    string? SpecializedAdvice,
    Confidence ContextConfidence);

public sealed record RecommendationResult(
    RecommendationAction Action,
    string Rating,
    Confidence Confidence,
    IReadOnlyList<RecommendationReason> Reasons,
    string Explanation,
    long SelectedEconomicValue,
    long ValuePerSlot,
    DateTimeOffset DataTimestampUtc,
    SaleChannel SaleChannel);

public sealed record ValueTierThresholds(long S, long A, long B, long C)
{
    public static ValueTierThresholds Default { get; } = new(100_000, 60_000, 35_000, 20_000);

    public string GetTier(long valuePerSlot) => valuePerSlot switch
    {
        var value when value > S => "S",
        var value when value >= A => "A",
        var value when value >= B => "B",
        var value when value >= C => "C",
        _ => "D",
    };
}
