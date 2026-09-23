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
    RaidContext,
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
/// Minimum economic bands for taking ordinary loot as raid exposure rises. The thresholds are
/// monotonic so a more exposed context can never accidentally make an economic pick less strict.
/// </summary>
public sealed record RaidAdjustedLootThresholds
{
    public RaidAdjustedLootThresholds(
        EconomicValueBand normal,
        EconomicValueBand elevatedRisk,
        EconomicValueBand highRisk,
        EconomicValueBand criticalRisk,
        EconomicValueBand lateRaid,
        EconomicValueBand extracting)
    {
        Normal = Defined(normal, nameof(normal));
        ElevatedRisk = AtLeast(elevatedRisk, Normal, nameof(elevatedRisk));
        HighRisk = AtLeast(highRisk, ElevatedRisk, nameof(highRisk));
        CriticalRisk = AtLeast(criticalRisk, HighRisk, nameof(criticalRisk));
        LateRaid = AtLeast(lateRaid, Normal, nameof(lateRaid));
        Extracting = AtLeast(extracting, LateRaid, nameof(extracting));
    }

    public EconomicValueBand Normal { get; }

    public EconomicValueBand ElevatedRisk { get; }

    public EconomicValueBand HighRisk { get; }

    public EconomicValueBand CriticalRisk { get; }

    public EconomicValueBand LateRaid { get; }

    public EconomicValueBand Extracting { get; }

    public EconomicValueBand RequiredBand(
        RecommendationRaidPhase phase,
        RecommendationRaidRisk risk)
    {
        if (!Enum.IsDefined(phase))
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (!Enum.IsDefined(risk))
        {
            throw new ArgumentOutOfRangeException(nameof(risk));
        }

        var riskBand = RequiredBand(risk);
        var phaseBand = RequiredBand(phase);
        return (int)riskBand >= (int)phaseBand ? riskBand : phaseBand;
    }

    public EconomicValueBand RequiredBand(RecommendationRaidPhase phase) => phase switch
    {
        RecommendationRaidPhase.Early or RecommendationRaidPhase.Middle => Normal,
        RecommendationRaidPhase.Late => LateRaid,
        RecommendationRaidPhase.Extracting => Extracting,
        _ => throw new ArgumentOutOfRangeException(nameof(phase)),
    };

    public EconomicValueBand RequiredBand(RecommendationRaidRisk risk) => risk switch
    {
        RecommendationRaidRisk.Low => Normal,
        RecommendationRaidRisk.Elevated => ElevatedRisk,
        RecommendationRaidRisk.High => HighRisk,
        RecommendationRaidRisk.Critical => CriticalRisk,
        _ => throw new ArgumentOutOfRangeException(nameof(risk)),
    };

    private static EconomicValueBand Defined(EconomicValueBand value, string parameterName) =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(parameterName);

    private static EconomicValueBand AtLeast(
        EconomicValueBand value,
        EconomicValueBand minimum,
        string parameterName) =>
        (int)Defined(value, parameterName) >= (int)minimum
            ? value
            : throw new ArgumentException("Raid-context thresholds must become stricter monotonically.", parameterName);
}

/// <summary>
/// Versioning applies to precedence and thresholds together. Changing either produces a new
/// ruleset instead of silently changing an old recommendation's meaning.
/// </summary>
public sealed record ExplainableRecommendationPolicy
{
    public const string CurrentRulesetVersion = "recommendation-274.2";

    public ExplainableRecommendationPolicy(
        string rulesetVersion,
        int futureQuestSteps,
        int futureHideoutSteps,
        TimeSpan maximumInventoryAge,
        TimeSpan maximumPriceAge,
        TimeSpan maximumScarcityAge,
        TimeSpan maximumRaidContextAge,
        double minimumEvidenceConfidence,
        ValuePerSquareBands valueBands,
        RecommendationObtainabilityBand minimumScarcityToKeep,
        RaidAdjustedLootThresholds lootThresholds)
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

        if (maximumScarcityAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumScarcityAge));
        }

        if (maximumRaidContextAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRaidContextAge));
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
        MaximumScarcityAge = maximumScarcityAge;
        MaximumRaidContextAge = maximumRaidContextAge;
        MinimumEvidenceConfidence = minimumEvidenceConfidence;
        ValueBands = valueBands ?? throw new ArgumentNullException(nameof(valueBands));
        MinimumScarcityToKeep = Enum.IsDefined(minimumScarcityToKeep)
            ? minimumScarcityToKeep
            : throw new ArgumentOutOfRangeException(nameof(minimumScarcityToKeep));
        LootThresholds = lootThresholds ?? throw new ArgumentNullException(nameof(lootThresholds));
    }

    public string RulesetVersion { get; }

    public int FutureQuestSteps { get; }

    public int FutureHideoutSteps { get; }

    public TimeSpan MaximumInventoryAge { get; }

    public TimeSpan MaximumPriceAge { get; }

    public TimeSpan MaximumScarcityAge { get; }

    public TimeSpan MaximumRaidContextAge { get; }

    public double MinimumEvidenceConfidence { get; }

    public ValuePerSquareBands ValueBands { get; }

    public RecommendationObtainabilityBand MinimumScarcityToKeep { get; }

    public RaidAdjustedLootThresholds LootThresholds { get; }

    public ExplainableRecommendationPolicy WithHorizons(RecommendationHorizonSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalized();
        return new(
            RulesetVersion,
            RecommendationHorizonSettings.Steps(normalized.Quest),
            RecommendationHorizonSettings.Steps(normalized.Hideout),
            MaximumInventoryAge,
            MaximumPriceAge,
            MaximumScarcityAge,
            MaximumRaidContextAge,
            MinimumEvidenceConfidence,
            ValueBands,
            MinimumScarcityToKeep,
            LootThresholds);
    }

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
        ExplainableRecommendationRule.RaidContext => 550,
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
        maximumScarcityAge: TimeSpan.FromDays(7),
        maximumRaidContextAge: TimeSpan.FromMinutes(15),
        minimumEvidenceConfidence: 0.75,
        valueBands: new ValuePerSquareBands(10_000, 25_000, 50_000),
        minimumScarcityToKeep: RecommendationObtainabilityBand.Scarce,
        lootThresholds: new RaidAdjustedLootThresholds(
            EconomicValueBand.Moderate,
            EconomicValueBand.High,
            EconomicValueBand.Exceptional,
            EconomicValueBand.Exceptional,
            EconomicValueBand.High,
            EconomicValueBand.Exceptional));
}
