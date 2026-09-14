using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Abstractions.V2;

public enum RecommendationAction
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

/// <summary>
/// Why advice was given. The ruleset version decides precedence between categories; the
/// category keeps a quest need from being displayed or compared as a price.
/// </summary>
public enum RecommendationReasonCategory
{
    ExplicitOverride = 1,
    Safety,
    CurrentFoundInRaidQuest,
    CurrentQuest,
    FutureQuest,
    Hideout,
    CraftOrBarter,
    SpecialistUtility,
    PinOrWishlist,
    ScarcityOrObtainability,
    Economics,
    EvidenceQuality,
}

public sealed record RecommendationReason(
    RecommendationReasonCategory Category,
    string Code,
    string Explanation,
    int Priority,
    EvidenceProvenance Provenance)
{
    public RecommendationReasonCategory Category { get; } = V2ContractGuard.Defined(Category, nameof(Category));

    public string Code { get; } = V2ContractGuard.Required(Code, nameof(Code));

    public string Explanation { get; } = V2ContractGuard.Required(Explanation, nameof(Explanation));

    public int Priority { get; } = Priority;

    public EvidenceProvenance Provenance { get; } = V2ContractGuard.NotNull(Provenance, nameof(Provenance));
}

/// <summary>A fact that would change the answer, e.g. "if the quest is already turned in: Sell".</summary>
public sealed record RecommendationSensitivity(
    string FactCode,
    string Explanation,
    RecommendationAction? AlternativeAction)
{
    public string FactCode { get; } = V2ContractGuard.Required(FactCode, nameof(FactCode));

    public string Explanation { get; } = V2ContractGuard.Required(Explanation, nameof(Explanation));

    public RecommendationAction? AlternativeAction { get; } =
        V2ContractGuard.DefinedOptional(AlternativeAction, nameof(AlternativeAction));
}

/// <summary>
/// One decision. Opportunity cost is the value given up against the best economic alternative;
/// it is absent rather than zero when a price or footprint is unknown. Combined figures carry
/// <see cref="EvidenceSourceClass.DerivedCalculation"/> provenance naming every input.
/// </summary>
public sealed record RecommendationDecision
{
    public RecommendationDecision(
        RecommendationAction action,
        string summary,
        IReadOnlyList<RecommendationReason> reasons,
        EvidencedValue<long?> opportunityCostRoubles,
        IReadOnlyList<RecommendationSensitivity> changesTheAnswer)
    {
        Action = V2ContractGuard.Defined(action, nameof(action));
        Summary = V2ContractGuard.Required(summary, nameof(summary));
        Reasons = V2ContractGuard.List(
            V2ContractGuard.List(reasons, nameof(reasons)).OrderByDescending(reason => reason.Priority).ToArray(),
            nameof(reasons));
        OpportunityCostRoubles = V2ContractGuard.AtLeast(opportunityCostRoubles, 0, nameof(opportunityCostRoubles));
        ChangesTheAnswer = V2ContractGuard.List(changesTheAnswer, nameof(changesTheAnswer));

        if (Reasons.Count == 0)
        {
            throw new ArgumentException("A decision must give at least one reason.", nameof(reasons));
        }
    }

    public RecommendationAction Action { get; }

    public string Summary { get; }

    /// <summary>Ordered by priority, highest first; ties keep the ruleset's order.</summary>
    public IReadOnlyList<RecommendationReason> Reasons { get; }

    public EvidencedValue<long?> OpportunityCostRoubles { get; }

    public IReadOnlyList<RecommendationSensitivity> ChangesTheAnswer { get; }
}

/// <summary>A recommendation is advice for review; it cannot perform the advised action.</summary>
public sealed record RecommendationResult
{
    public RecommendationResult(
        string recommendationId,
        V2ContractVersion contractVersion,
        string rulesetVersion,
        CaptureSessionId? captureSessionId,
        EvidencedValue<RecommendationDecision> decision)
    {
        RecommendationId = V2ContractGuard.Required(recommendationId, nameof(recommendationId));
        ContractVersion = V2ContractGuard.Defined(contractVersion, nameof(contractVersion));
        RulesetVersion = V2ContractGuard.Required(rulesetVersion, nameof(rulesetVersion));
        CaptureSessionId = captureSessionId is { } session
            ? V2ContractGuard.Defined(session, nameof(captureSessionId))
            : null;
        Decision = V2ContractGuard.NotNull(decision, nameof(decision));
    }

    public string RecommendationId { get; }

    public V2ContractVersion ContractVersion { get; }

    public string RulesetVersion { get; }

    public CaptureSessionId? CaptureSessionId { get; }

    public EvidencedValue<RecommendationDecision> Decision { get; }
}
