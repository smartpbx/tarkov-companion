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

/// <summary>
/// A coarse, evidence-backed description of how readily another copy can be obtained. Higher
/// values mean harder to replace; a band is used instead of a fabricated spawn probability.
/// </summary>
public enum RecommendationObtainabilityBand
{
    Abundant = 1,
    Available,
    Limited,
    Scarce,
}

/// <summary>Scarcity facts stay separate from economics so price cannot masquerade as rarity.</summary>
public sealed record RecommendationScarcityFacts
{
    public RecommendationScarcityFacts(EvidencedValue<RecommendationObtainabilityBand?> obtainability)
    {
        Obtainability = V2ContractGuard.Defined(obtainability, nameof(obtainability));
    }

    public EvidencedValue<RecommendationObtainabilityBand?> Obtainability { get; }
}

/// <summary>A user-visible raid phase, never a claim about unseen activity.</summary>
public enum RecommendationRaidPhase
{
    Early = 1,
    Middle,
    Late,
    Extracting,
}

/// <summary>
/// The user's current tolerance/risk context. It is an input to advice, not enemy, traffic, or
/// live-world detection.
/// </summary>
public enum RecommendationRaidRisk
{
    Low = 1,
    Elevated,
    High,
    Critical,
}

/// <summary>Ephemeral raid context used only when evaluating loot advice.</summary>
public sealed record RecommendationRaidContext
{
    public RecommendationRaidContext(
        EvidencedValue<RecommendationRaidPhase?> phase,
        EvidencedValue<RecommendationRaidRisk?> risk)
    {
        Phase = V2ContractGuard.Defined(phase, nameof(phase));
        Risk = V2ContractGuard.Defined(risk, nameof(risk));
    }

    public EvidencedValue<RecommendationRaidPhase?> Phase { get; }

    public EvidencedValue<RecommendationRaidRisk?> Risk { get; }
}

/// <summary>
/// The identity boundary for one seasonal event-item state. The profile generation carries the
/// wipe/season boundary; event and ruleset identifiers prevent a confirmed outcome from being
/// reused by another event with the same item.
/// </summary>
public sealed record RecommendationEventScope
{
    public const int MaximumIdentifierLength = 256;

    public RecommendationEventScope(
        InventoryProfileScope profileScope,
        string eventId,
        string eventRulesetVersion,
        string itemId)
    {
        ProfileScope = profileScope ?? throw new ArgumentNullException(nameof(profileScope));
        EventId = RequiredIdentifier(eventId, nameof(eventId));
        EventRulesetVersion = RequiredIdentifier(eventRulesetVersion, nameof(eventRulesetVersion));
        ItemId = RequiredIdentifier(itemId, nameof(itemId));
    }

    public InventoryProfileScope ProfileScope { get; }

    public string EventId { get; }

    public string EventRulesetVersion { get; }

    public string ItemId { get; }

    private static string RequiredIdentifier(string value, string parameterName)
    {
        var normalized = V2ContractGuard.Required(value, parameterName);
        return normalized.Length <= MaximumIdentifierLength
            ? normalized
            : throw new ArgumentOutOfRangeException(
                parameterName,
                $"An event-scope identifier cannot exceed {MaximumIdentifierLength} characters.");
    }
}

/// <summary>An event state together with the exact profile/event/item boundary that produced it.</summary>
public sealed record RecommendationEventStateFacts
{
    public RecommendationEventStateFacts(
        RecommendationEventScope? scope,
        EvidencedValue<EventItemState?> state)
    {
        State = V2ContractGuard.Defined(state, nameof(state));
        if (State.Value is { } currentState && currentState != EventItemState.Unknown && scope is null)
        {
            throw new ArgumentException(
                "An applicable or confirmed event-item state must carry its profile, event, and item scope.",
                nameof(scope));
        }

        Scope = scope;
    }

    public RecommendationEventScope? Scope { get; }

    public EvidencedValue<EventItemState?> State { get; }
}

/// <summary>
/// The configured explicit action, including a first-class <see cref="None"/> state. The outer
/// evidence status distinguishes a confirmed absence from an action field that was not resolved.
/// </summary>
public readonly record struct RecommendationExplicitActionState
{
    public RecommendationExplicitActionState(V2RecommendationAction? action)
    {
        Action = V2ContractGuard.DefinedOptional(action, nameof(action));
    }

    public V2RecommendationAction? Action { get; }

    public static RecommendationExplicitActionState None { get; } = new(null);
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
        EvidencedValue<RecommendationExplicitActionState?> explicitAction,
        EvidencedValue<bool?> protectedItem,
        EvidencedValue<bool?> pinned,
        EvidencedValue<bool?> wishlist,
        RecommendationEventStateFacts eventState,
        IReadOnlyList<RecommendationNeed> needs)
    {
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        ExplicitAction = explicitAction ?? throw new ArgumentNullException(nameof(explicitAction));
        ProtectedItem = protectedItem ?? throw new ArgumentNullException(nameof(protectedItem));
        Pinned = pinned ?? throw new ArgumentNullException(nameof(pinned));
        Wishlist = wishlist ?? throw new ArgumentNullException(nameof(wishlist));
        EventState = eventState ?? throw new ArgumentNullException(nameof(eventState));
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

    public EvidencedValue<RecommendationExplicitActionState?> ExplicitAction { get; }

    public EvidencedValue<bool?> ProtectedItem { get; }

    public EvidencedValue<bool?> Pinned { get; }

    public EvidencedValue<bool?> Wishlist { get; }

    public RecommendationEventStateFacts EventState { get; }

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
        RecommendationScarcityFacts scarcity,
        ObservedInventoryEvidenceSnapshot? inventory = null,
        CaptureSessionId? captureSessionId = null,
        RecommendationRaidContext? raidContext = null,
        RecommendationEventScope? eventScope = null)
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
        Scarcity = scarcity ?? throw new ArgumentNullException(nameof(scarcity));
        Inventory = inventory;
        CaptureSessionId = captureSessionId is { } session
            ? V2ContractGuard.Defined(session, nameof(captureSessionId))
            : null;
        RaidContext = raidContext;
        if (eventScope is { } suppliedEventScope &&
            (suppliedEventScope.ProfileScope != ProfileScope ||
             !string.Equals(suppliedEventScope.ItemId, ItemId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The event scope must belong to the recommendation profile and item.",
                nameof(eventScope));
        }

        if (profile.EventState.Scope != eventScope)
        {
            throw new ArgumentException(
                "The event-item state must match the recommendation event scope exactly.",
                nameof(profile));
        }

        EventScope = eventScope;
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

    public RecommendationScarcityFacts Scarcity { get; }

    public ObservedInventoryEvidenceSnapshot? Inventory { get; }

    public CaptureSessionId? CaptureSessionId { get; }

    public RecommendationRaidContext? RaidContext { get; }

    public RecommendationEventScope? EventScope { get; }
}

/// <summary>
/// One photographed flea offer evaluated against the catalog's two resale alternatives.
/// </summary>
/// <remarks>
/// Offer ranking is deliberately a recommendation-engine input rather than a UI calculation:
/// identity confidence, OCR price confidence, fee/net evidence, condition, ruleset version and
/// explanation provenance then travel together in the same <see cref="RecommendationResult"/>
/// shape used by Loot and Intel.
/// </remarks>
public sealed record FleaOfferRecommendationRequest
{
    public FleaOfferRecommendationRequest(
        string recommendationId,
        EvidencedValue<string> itemIdentity,
        DateTimeOffset evaluatedUtc,
        CaptureSessionId captureSessionId,
        EvidencedValue<long?> offerPriceRoubles,
        RecommendationEconomics resaleEconomics)
    {
        RecommendationId = V2ContractGuard.Required(recommendationId, nameof(recommendationId));
        ItemIdentity = V2ContractGuard.NotNull(itemIdentity, nameof(itemIdentity));
        EvaluatedUtc = V2ContractGuard.Utc(evaluatedUtc, nameof(evaluatedUtc));
        CaptureSessionId = V2ContractGuard.Defined(captureSessionId, nameof(captureSessionId));
        OfferPriceRoubles = V2ContractGuard.AtLeast(offerPriceRoubles, 1, nameof(offerPriceRoubles));
        ResaleEconomics = resaleEconomics ?? throw new ArgumentNullException(nameof(resaleEconomics));
    }

    public string RecommendationId { get; }

    public EvidencedValue<string> ItemIdentity { get; }

    public DateTimeOffset EvaluatedUtc { get; }

    public CaptureSessionId CaptureSessionId { get; }

    public EvidencedValue<long?> OfferPriceRoubles { get; }

    public RecommendationEconomics ResaleEconomics { get; }
}
