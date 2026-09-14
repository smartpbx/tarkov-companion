using System.Text.Json.Serialization;
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

/// <summary>The two named inputs every opportunity-cost figure is computed from.</summary>
/// <remarks>
/// A provenance tree says what was combined but not which input was the price, so the roles are
/// named here. Two identical provenances would leave the roles interchangeable, and a role this
/// record does not name would be one nothing checks, so both fail instead of being skipped.
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OpportunityCostLineage
{
    public OpportunityCostLineage(EvidenceProvenance price, EvidenceProvenance footprint)
    {
        Price = V2ContractGuard.NotNull(price, nameof(price));
        Footprint = V2ContractGuard.NotNull(footprint, nameof(footprint));

        if (price.SourceClass == EvidenceSourceClass.Unknown || footprint.SourceClass == EvidenceSourceClass.Unknown)
        {
            throw new ArgumentException("An opportunity-cost input must name its source class.");
        }

        if (price == footprint)
        {
            throw new ArgumentException("The price and footprint must be distinguishable inputs.", nameof(footprint));
        }
    }

    public EvidenceProvenance Price { get; }

    public EvidenceProvenance Footprint { get; }
}

/// <summary>
/// One decision. Opportunity cost is the value given up against the best economic alternative;
/// it is absent rather than zero when a price or footprint is unknown.
/// </summary>
/// <remarks>
/// Any figure (the value, a candidate, or a correction) is a computation, so it needs
/// <see cref="OpportunityCostLineage"/>, and the value and each candidate carry
/// <see cref="EvidenceSourceClass.DerivedCalculation"/> or
/// <see cref="EvidenceSourceClass.ModelledEstimate"/> provenance whose input tree holds the price
/// exactly once, then the footprint exactly once. Without that, a bare catalog price or a screenshot
/// reading could be presented as the cost of a choice.
/// </remarks>
public sealed record RecommendationDecision
{
    public RecommendationDecision(
        RecommendationAction action,
        string summary,
        IReadOnlyList<RecommendationReason> reasons,
        EvidencedValue<long?> opportunityCostRoubles,
        OpportunityCostLineage? opportunityCostLineage,
        IReadOnlyList<RecommendationSensitivity> changesTheAnswer)
    {
        Action = V2ContractGuard.Defined(action, nameof(action));
        Summary = V2ContractGuard.Required(summary, nameof(summary));
        Reasons = V2ContractGuard.List(
            V2ContractGuard.List(reasons, nameof(reasons)).OrderByDescending(reason => reason.Priority).ToArray(),
            nameof(reasons));
        OpportunityCostRoubles = V2ContractGuard.AtLeast(opportunityCostRoubles, 0, nameof(opportunityCostRoubles));
        OpportunityCostLineage = opportunityCostLineage;
        ChangesTheAnswer = V2ContractGuard.List(changesTheAnswer, nameof(changesTheAnswer));

        if (Reasons.Count == 0)
        {
            throw new ArgumentException("A decision must give at least one reason.", nameof(reasons));
        }

        ValidateOpportunityCost();
    }

    public RecommendationAction Action { get; }

    public string Summary { get; }

    /// <summary>Ordered by priority, highest first; ties keep the ruleset's order.</summary>
    public IReadOnlyList<RecommendationReason> Reasons { get; }

    public EvidencedValue<long?> OpportunityCostRoubles { get; }

    /// <summary>Null only when the opportunity cost carries no figure anywhere.</summary>
    public OpportunityCostLineage? OpportunityCostLineage { get; }

    public IReadOnlyList<RecommendationSensitivity> ChangesTheAnswer { get; }

    private void ValidateOpportunityCost()
    {
        var cost = OpportunityCostRoubles;
        if (cost.Value is null && cost.Candidates.Count == 0 && cost.Corrections.Count == 0)
        {
            return;
        }

        if (OpportunityCostLineage is not { } lineage)
        {
            throw new ArgumentException(
                "An opportunity-cost figure must name its price and footprint.",
                "opportunityCostLineage");
        }

        // A correction has no provenance of its own; it revises the value, whose provenance stands.
        foreach (var provenance in cost.Candidates.Select(candidate => candidate.Provenance).Prepend(cost.Provenance))
        {
            if (provenance.SourceClass is not (EvidenceSourceClass.DerivedCalculation or EvidenceSourceClass.ModelledEstimate))
            {
                throw new ArgumentException(
                    $"An opportunity cost is computed, so {provenance.SourceClass} provenance cannot carry one.",
                    "opportunityCostRoubles");
            }

            var inputs = provenance.DescendantInputs().ToList();
            var price = inputs.FindIndex(input => input == lineage.Price);
            var footprint = inputs.FindIndex(input => input == lineage.Footprint);
            if (price < 0 ||
                footprint < price ||
                inputs.FindLastIndex(input => input == lineage.Price) != price ||
                inputs.FindLastIndex(input => input == lineage.Footprint) != footprint)
            {
                throw new ArgumentException(
                    "An opportunity cost's inputs must hold its price once, then its footprint once.",
                    "opportunityCostLineage");
            }
        }
    }
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
