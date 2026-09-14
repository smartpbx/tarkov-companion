using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Abstractions.V2;

public enum RecommendationAction
{
    Unknown,
    Keep,
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

public sealed record RecommendationReason(
    string Code,
    string Explanation,
    int Priority,
    EvidenceProvenance Provenance)
{
    public string Code { get; } = V2ContractGuard.Required(Code, nameof(Code));

    public string Explanation { get; } = V2ContractGuard.Required(Explanation, nameof(Explanation));

    public EvidenceProvenance Provenance { get; } = V2ContractGuard.NotNull(Provenance, nameof(Provenance));
}

public sealed record RecommendationDecision(
    RecommendationAction Action,
    string Summary,
    IReadOnlyList<RecommendationReason> Reasons)
{
    public string Summary { get; } = V2ContractGuard.Required(Summary, nameof(Summary));

    public IReadOnlyList<RecommendationReason> Reasons { get; } =
        V2ContractGuard.List(Reasons, nameof(Reasons)).OrderByDescending(reason => reason.Priority).ToArray();
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
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesetVersion);
        ArgumentNullException.ThrowIfNull(decision);

        RecommendationId = recommendationId.Trim();
        ContractVersion = V2ContractGuard.Defined(contractVersion, nameof(contractVersion));
        RulesetVersion = rulesetVersion.Trim();
        CaptureSessionId = captureSessionId is { } session
            ? V2ContractGuard.Defined(session, nameof(captureSessionId))
            : null;
        Decision = decision;
    }

    public string RecommendationId { get; }

    public V2ContractVersion ContractVersion { get; }

    public string RulesetVersion { get; }

    public CaptureSessionId? CaptureSessionId { get; }

    public EvidencedValue<RecommendationDecision> Decision { get; }
}
