using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Inventory;
using V2RecommendationAction = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;

namespace TarkovCompanion.Core.Domain.Recommendations;

public enum RecommendationUseCase
{
    ItemCard = 1,
    Loot,
    Stash,
}

public enum RecommendationNeedPurpose
{
    Quest = 1,
    Hideout,
    CraftOrBarter,
    SpecialistUtility,
}

/// <summary>A requirement before compatible observed holdings are allocated to it.</summary>
public sealed record RecommendationNeed
{
    public RecommendationNeed(
        string needId,
        string displayName,
        RecommendationNeedPurpose purpose,
        int stepsAhead,
        int requiredQuantity,
        bool requiresFoundInRaid,
        ResultStatus status,
        EvidenceProvenance provenance)
    {
        NeedId = V2ContractGuard.Required(needId, nameof(needId));
        DisplayName = V2ContractGuard.Required(displayName, nameof(displayName));
        Purpose = V2ContractGuard.Defined(purpose, nameof(purpose));
        if (stepsAhead < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepsAhead));
        }

        if (requiredQuantity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredQuantity));
        }

        if (requiresFoundInRaid && purpose != RecommendationNeedPurpose.Quest)
        {
            throw new ArgumentException("Only a quest requirement can require found-in-raid items.", nameof(requiresFoundInRaid));
        }

        StepsAhead = stepsAhead;
        RequiredQuantity = requiredQuantity;
        RequiresFoundInRaid = requiresFoundInRaid;
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    public string NeedId { get; }

    public string DisplayName { get; }

    public RecommendationNeedPurpose Purpose { get; }

    public int StepsAhead { get; }

    public int RequiredQuantity { get; }

    public bool RequiresFoundInRaid { get; }

    public ResultStatus Status { get; }

    public EvidenceProvenance Provenance { get; }
}

public sealed record RecommendationProfileFacts
{
    // The decision provenance tree is bounded by the frozen V2 contract. Sixty-four needs leave
    // room for holdings calculations, protections, event state, economics, and quality reasons.
    public const int MaximumNeeds = 64;

    public RecommendationProfileFacts(
        ResultStatus status,
        EvidenceProvenance provenance,
        EvidencedValue<V2RecommendationAction?> explicitAction,
        EvidencedValue<bool?> protectedItem,
        EvidencedValue<bool?> pinned,
        EvidencedValue<bool?> wishlist,
        EvidencedValue<EventItemState?> eventState,
        IReadOnlyList<RecommendationNeed> needs)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        ExplicitAction = V2ContractGuard.Defined(explicitAction, nameof(explicitAction));
        ProtectedItem = protectedItem ?? throw new ArgumentNullException(nameof(protectedItem));
        Pinned = pinned ?? throw new ArgumentNullException(nameof(pinned));
        Wishlist = wishlist ?? throw new ArgumentNullException(nameof(wishlist));
        EventState = V2ContractGuard.Defined(eventState, nameof(eventState));
        ArgumentNullException.ThrowIfNull(needs);
        var copied = needs
            .Select(need => need ?? throw new ArgumentException("Needs cannot contain null.", nameof(needs)))
            .OrderBy(need => need.NeedId, StringComparer.Ordinal)
            .ToArray();
        if (copied.Length > MaximumNeeds)
        {
            throw new ArgumentException($"A profile cannot provide more than {MaximumNeeds} recommendation needs.", nameof(needs));
        }

        if (copied.Select(need => need.NeedId).Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException("Need identifiers must be unique.", nameof(needs));
        }

        Needs = Array.AsReadOnly(copied);
    }

    public ResultStatus Status { get; }

    public EvidenceProvenance Provenance { get; }

    public EvidencedValue<V2RecommendationAction?> ExplicitAction { get; }

    public EvidencedValue<bool?> ProtectedItem { get; }

    public EvidencedValue<bool?> Pinned { get; }

    public EvidencedValue<bool?> Wishlist { get; }

    public EvidencedValue<EventItemState?> EventState { get; }

    public IReadOnlyList<RecommendationNeed> Needs { get; }
}

/// <summary>Economic inputs remain distinct so gross, fees, net, and condition cannot collapse.</summary>
public sealed record RecommendationEconomics
{
    public RecommendationEconomics(
        EvidencedValue<long?> fleaGrossRoubles,
        EvidencedValue<long?> fleaFeeRoubles,
        EvidencedValue<long?> fleaNetRoubles,
        EvidencedValue<long?> traderRoubles,
        EvidencedValue<int?> occupiedSquares,
        EvidencedValue<double?> conditionFraction)
    {
        FleaGrossRoubles = V2ContractGuard.AtLeast(fleaGrossRoubles, 0, nameof(fleaGrossRoubles));
        FleaFeeRoubles = V2ContractGuard.AtLeast(fleaFeeRoubles, 0, nameof(fleaFeeRoubles));
        FleaNetRoubles = V2ContractGuard.AtLeast(fleaNetRoubles, 0, nameof(fleaNetRoubles));
        TraderRoubles = V2ContractGuard.AtLeast(traderRoubles, 0, nameof(traderRoubles));
        OccupiedSquares = V2ContractGuard.AtLeast(occupiedSquares, 1, nameof(occupiedSquares));
        ConditionFraction = conditionFraction ?? throw new ArgumentNullException(nameof(conditionFraction));

        foreach (var value in new[] { conditionFraction.Value }
                     .Concat(conditionFraction.Candidates.Select(candidate => candidate.Value))
                     .Concat(conditionFraction.Corrections.SelectMany(correction =>
                         new[] { correction.OriginalValue, correction.CorrectedValue })))
        {
            if (value is { } present && (!double.IsFinite(present) || present is < 0 or > 1))
            {
                throw new ArgumentOutOfRangeException(nameof(conditionFraction), "Condition must be between zero and one.");
            }
        }

        if (FleaGrossRoubles.Value is { } gross && FleaFeeRoubles.Value is { } fee && FleaNetRoubles.Value is { } net &&
            (fee > gross || net != gross - fee))
        {
            throw new ArgumentException("Flea net must equal gross minus fee when all are known.", nameof(fleaNetRoubles));
        }
    }

    public EvidencedValue<long?> FleaGrossRoubles { get; }

    public EvidencedValue<long?> FleaFeeRoubles { get; }

    public EvidencedValue<long?> FleaNetRoubles { get; }

    public EvidencedValue<long?> TraderRoubles { get; }

    public EvidencedValue<int?> OccupiedSquares { get; }

    public EvidencedValue<double?> ConditionFraction { get; }
}

public sealed record ExplainableRecommendationRequest
{
    public ExplainableRecommendationRequest(
        string recommendationId,
        string itemId,
        RecommendationUseCase useCase,
        DateTimeOffset evaluatedUtc,
        InventoryProfileScope profileScope,
        string dataSnapshotId,
        EvidencedValue<bool?> candidateFoundInRaid,
        RecommendationProfileFacts profile,
        RecommendationEconomics economics,
        ObservedInventoryEvidenceSnapshot? inventory = null,
        CaptureSessionId? captureSessionId = null)
    {
        RecommendationId = V2ContractGuard.Required(recommendationId, nameof(recommendationId));
        ItemId = V2ContractGuard.Required(itemId, nameof(itemId));
        UseCase = V2ContractGuard.Defined(useCase, nameof(useCase));
        EvaluatedUtc = V2ContractGuard.Utc(evaluatedUtc, nameof(evaluatedUtc));
        ProfileScope = profileScope ?? throw new ArgumentNullException(nameof(profileScope));
        DataSnapshotId = V2ContractGuard.Required(dataSnapshotId, nameof(dataSnapshotId));
        CandidateFoundInRaid = candidateFoundInRaid ?? throw new ArgumentNullException(nameof(candidateFoundInRaid));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Economics = economics ?? throw new ArgumentNullException(nameof(economics));
        Inventory = inventory;
        CaptureSessionId = captureSessionId is { } session
            ? V2ContractGuard.Defined(session, nameof(captureSessionId))
            : null;
    }

    public string RecommendationId { get; }

    public string ItemId { get; }

    public RecommendationUseCase UseCase { get; }

    public DateTimeOffset EvaluatedUtc { get; }

    public InventoryProfileScope ProfileScope { get; }

    public string DataSnapshotId { get; }

    public EvidencedValue<bool?> CandidateFoundInRaid { get; }

    public RecommendationProfileFacts Profile { get; }

    public RecommendationEconomics Economics { get; }

    public ObservedInventoryEvidenceSnapshot? Inventory { get; }

    public CaptureSessionId? CaptureSessionId { get; }
}
