namespace TarkovCompanion.Core.Domain.Recommendations;

/// <summary>Every rule with decision precedence in the versioned recommendation ruleset.</summary>
public enum ExplainableRecommendationRule
{
    ExplicitOverride = 1,
    EventAllergy,
    ProtectedItem,
    CurrentFoundInRaidQuest,
    CurrentQuest,
    FutureQuest,
    Hideout,
    CraftOrBarter,
    SpecialistUtility,
    Pin,
    Wishlist,
    EventUntested,
    EventSafe,
    Scarcity,
    Economics,
    EvidenceQuality,
}

public enum EconomicValueBand
{
    Low = 1,
    Moderate,
    High,
    Exceptional,
}

public sealed record ValuePerSquareBands
{
    public ValuePerSquareBands(long moderate, long high, long exceptional)
    {
        if (moderate < 1 || high <= moderate || exceptional <= high)
        {
            throw new ArgumentOutOfRangeException(nameof(moderate), "Value bands must be positive and strictly increasing.");
        }

        Moderate = moderate;
        High = high;
        Exceptional = exceptional;
    }

    public long Moderate { get; }

    public long High { get; }

    public long Exceptional { get; }

    public EconomicValueBand Classify(long valuePerSquare) => valuePerSquare switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(valuePerSquare)),
        var value when value >= Exceptional => EconomicValueBand.Exceptional,
        var value when value >= High => EconomicValueBand.High,
        var value when value >= Moderate => EconomicValueBand.Moderate,
        _ => EconomicValueBand.Low,
    };
}

/// <summary>
/// Versioning applies to precedence and thresholds together. Changing either produces a new
/// ruleset instead of silently changing an old recommendation's meaning.
/// </summary>
public sealed record ExplainableRecommendationPolicy
{
    public const string CurrentRulesetVersion = "recommendation-274.1";

    public ExplainableRecommendationPolicy(
        string rulesetVersion,
        int futureQuestSteps,
        int futureHideoutSteps,
        TimeSpan maximumInventoryAge,
        TimeSpan maximumPriceAge,
        double minimumEvidenceConfidence,
        ValuePerSquareBands valueBands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesetVersion);
        if (!string.Equals(rulesetVersion.Trim(), CurrentRulesetVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("This engine only supports its current explicit ruleset version.", nameof(rulesetVersion));
        }

        if (futureQuestSteps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(futureQuestSteps));
        }

        if (futureHideoutSteps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(futureHideoutSteps));
        }

        if (maximumInventoryAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumInventoryAge));
        }

        if (maximumPriceAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPriceAge));
        }

        if (!double.IsFinite(minimumEvidenceConfidence) || minimumEvidenceConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumEvidenceConfidence));
        }

        RulesetVersion = rulesetVersion.Trim();
        FutureQuestSteps = futureQuestSteps;
        FutureHideoutSteps = futureHideoutSteps;
        MaximumInventoryAge = maximumInventoryAge;
        MaximumPriceAge = maximumPriceAge;
        MinimumEvidenceConfidence = minimumEvidenceConfidence;
        ValueBands = valueBands ?? throw new ArgumentNullException(nameof(valueBands));
    }

    public string RulesetVersion { get; }

    public int FutureQuestSteps { get; }

    public int FutureHideoutSteps { get; }

    public TimeSpan MaximumInventoryAge { get; }

    public TimeSpan MaximumPriceAge { get; }

    public double MinimumEvidenceConfidence { get; }

    public ValuePerSquareBands ValueBands { get; }

    public int PriorityOf(ExplainableRecommendationRule rule) => rule switch
    {
        // A recorded allergic result is the one safety fact an override cannot weaken.
        ExplainableRecommendationRule.EventAllergy => 1600,
        ExplainableRecommendationRule.ExplicitOverride => 1500,
        ExplainableRecommendationRule.ProtectedItem => 1400,
        ExplainableRecommendationRule.CurrentFoundInRaidQuest => 1300,
        ExplainableRecommendationRule.CurrentQuest => 1200,
        ExplainableRecommendationRule.FutureQuest => 1100,
        ExplainableRecommendationRule.Hideout => 1000,
        ExplainableRecommendationRule.CraftOrBarter => 900,
        ExplainableRecommendationRule.SpecialistUtility => 800,
        ExplainableRecommendationRule.Pin => 700,
        ExplainableRecommendationRule.Wishlist => 690,
        ExplainableRecommendationRule.EventUntested => 680,
        ExplainableRecommendationRule.EventSafe => 670,
        ExplainableRecommendationRule.Scarcity => 600,
        ExplainableRecommendationRule.Economics => 500,
        ExplainableRecommendationRule.EvidenceQuality => 400,
        _ => throw new ArgumentOutOfRangeException(nameof(rule)),
    };

    public static ExplainableRecommendationPolicy Default { get; } = new(
        CurrentRulesetVersion,
        futureQuestSteps: 5,
        futureHideoutSteps: 3,
        maximumInventoryAge: TimeSpan.FromHours(24),
        maximumPriceAge: TimeSpan.FromHours(12),
        minimumEvidenceConfidence: 0.75,
        valueBands: new ValuePerSquareBands(10_000, 25_000, 50_000));
}
