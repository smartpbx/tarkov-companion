using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Application.Services.Recommendations;

/// <summary>
/// Every sentence the recommendation engine gives as a reason, a caveat, an evidence gap or a
/// fact that would change the answer (#314). The App says each through its string table; the
/// English there is the engine's old sentence word for word, which the engine still writes beside
/// the code so what is stored or relayed stays the same in every language.
/// </summary>
[PhraseCodes("Intel.Advice.Sentence")]
public enum AdviceSentence
{
    ProfileIncomplete = 1,
    OverrideUntrusted,
    OverrideExplicit,
    OverrideRemoved,
    EventStateUntrusted,
    EventAllergic,
    EventStateCorrected,
    ProtectionUntrusted,
    ItemProtected,
    ProtectionRemoved,
    NeedKeep,
    NeedKeepAfterHoldings,
    NeedKeepFoundInRaid,
    NeedKeepFoundInRaidAfterHoldings,
    HorizonCurrent,
    HorizonAhead,
    NeedCompleted,
    PinnedUntrusted,
    Pinned,
    PinRemoved,
    WishlistUntrusted,
    Wishlist,
    WishlistRemoved,
    EventUntested,
    EventTested,
    EventSafe,
    Scarcity,
    Economics,
    EconomicsDetailed,
    DetailFleaGross,
    DetailFee,
    DetailFleaNet,
    DetailTrader,
    DetailCondition,
    DetailList,
    PriceOrFootprintUpdated,
    EvidenceInsufficient,
    IdentityUntrusted,
    OfferPriceUntrusted,
    Offer,
    OfferAtCondition,
    ChannelFleaAfterFee,
    ChannelBestTrader,
    MarginMore,
    MarginSame,
    MarginLess,
    OfferEvidenceInsufficient,
    OfferOrResaleUpdated,
    InventoryMissing,
    InventoryIncompatible,
    InventoryUntrusted,
    InventoryUnseen,
    InventoryTotalUntrusted,
    InventoryFirUntrusted,
    InventoryPartial,
    NeedUntrusted,
    CandidateFirUntrusted,
    ScarcityUnknown,
    ScarcityUntrusted,
    RaidContextMissing,
    RaidPhaseUntrusted,
    RaidRiskUntrusted,
    RaidPhase,
    RaidRisk,
    ObtainabilityImproved,
    ObtainabilityWorsened,
    RaidRiskReduced,
    RaidRiskIncreased,
    RaidPhaseEarlier,
    RaidPhaseLater,
    FootprintMissing,
    FleaNetUntrusted,
    TraderUntrusted,
    PriceMissing,
    PriceDoubt,
}

/// <summary>The one-line summary, "Keep: {reason}", one code per action the engine can give.</summary>
/// <remarks>Member names mirror <see cref="Core.Abstractions.V2.RecommendationAction"/>.</remarks>
[PhraseCodes("Intel.Advice.Summary")]
public enum AdviceSummary
{
    Keep = 1,
    Take,
    Swap,
    Leave,
    SellOnFlea,
    SellToTrader,
    UseSoon,
    Organize,
    Review,
    AvoidConsume,
}

/// <summary>An action named inside a sentence ("your explicit item rule says keep").</summary>
/// <remarks>Member names mirror <see cref="Core.Abstractions.V2.RecommendationAction"/>.</remarks>
[PhraseCodes("Intel.Advice.ActionWord")]
public enum AdviceActionWord
{
    Keep = 1,
    Take,
    Swap,
    Leave,
    SellOnFlea,
    SellToTrader,
    UseSoon,
    Organize,
    Review,
    AvoidConsume,
}

/// <summary>A value-per-square band named inside a sentence.</summary>
/// <remarks>Member names mirror <see cref="Core.Domain.Recommendations.EconomicValueBand"/>.</remarks>
[PhraseCodes("Intel.Advice.Band")]
public enum AdviceBandWord
{
    Low = 1,
    Moderate,
    High,
    Exceptional,
}

/// <summary>How hard an item is to obtain, named inside a sentence.</summary>
/// <remarks>Member names mirror <see cref="Core.Domain.Recommendations.RecommendationObtainabilityBand"/>.</remarks>
[PhraseCodes("Intel.Advice.Obtainability")]
public enum AdviceObtainabilityWord
{
    Abundant = 1,
    Available,
    Limited,
    Scarce,
}

/// <summary>A raid phase named inside a sentence.</summary>
/// <remarks>Member names mirror <see cref="Core.Domain.Recommendations.RecommendationRaidPhase"/>.</remarks>
[PhraseCodes("Intel.Advice.Phase")]
public enum AdvicePhaseWord
{
    Early = 1,
    Middle,
    Late,
    Extracting,
}

/// <summary>A raid-risk setting named inside a sentence.</summary>
/// <remarks>Member names mirror <see cref="Core.Domain.Recommendations.RecommendationRaidRisk"/>.</remarks>
[PhraseCodes("Intel.Advice.Risk")]
public enum AdviceRiskWord
{
    Low = 1,
    Elevated,
    High,
    Critical,
}

/// <summary>Turns an engine enum into the word code that names it; the names mirror one another.</summary>
public static class AdviceWords
{
    /// <summary>The word code of the same name, e.g. <c>EconomicValueBand.High</c> to <see cref="AdviceBandWord.High"/>.</summary>
    public static TWord Of<TWord>(Enum value)
        where TWord : struct, Enum =>
        Enum.Parse<TWord>(value.ToString(), ignoreCase: false);
}
