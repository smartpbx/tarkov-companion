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
    public string Code { get; } = Required(Code, nameof(Code));

    public string Explanation { get; } = Required(Explanation, nameof(Explanation));

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}

public sealed record RecommendationDecision(
    RecommendationAction Action,
    string Summary,
    IReadOnlyList<RecommendationReason> Reasons)
{
    public string Summary { get; } = Required(Summary, nameof(Summary));

    public IReadOnlyList<RecommendationReason> Reasons { get; } =
        Reasons?.OrderByDescending(reason => reason.Priority).ToArray()
        ?? throw new ArgumentNullException(nameof(Reasons));

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
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
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesetVersion);
        ArgumentNullException.ThrowIfNull(decision);

        RecommendationId = recommendationId.Trim();
        ContractVersion = contractVersion;
        RulesetVersion = rulesetVersion.Trim();
        CaptureSessionId = captureSessionId;
        Decision = decision;
    }

    public string RecommendationId { get; }

    public V2ContractVersion ContractVersion { get; }

    public string RulesetVersion { get; }

    public CaptureSessionId? CaptureSessionId { get; }

    public EvidencedValue<RecommendationDecision> Decision { get; }
}
