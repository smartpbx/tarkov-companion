using System.Collections.ObjectModel;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Core.Domain.Loot;

public enum LootScanVerdict
{
    Take = 1,
    Swap,
    Leave,
    Review,
}

public enum LootScanIssueKind
{
    CaptureChanged = 1,
    LootCoveragePartial,
    CarriedCoveragePartial,
    ItemEvidenceIncomplete,
    RecommendationMissing,
    RecommendationIncomplete,
    CapacityUnavailable,
    NoSupportedFit,
    SwapEvidenceIncomplete,
}

public sealed record LootScanCandidateRecommendation
{
    public LootScanCandidateRecommendation(GridCellAddress anchor, RecommendationResult recommendation)
    {
        Anchor = anchor;
        Recommendation = recommendation ?? throw new ArgumentNullException(nameof(recommendation));
    }

    public GridCellAddress Anchor { get; }

    public RecommendationResult Recommendation { get; }
}

/// <summary>Facts that decide whether one observed carried item may be displaced.</summary>
public sealed record LootScanCarriedPolicy
{
    public LootScanCarriedPolicy(
        GridCellAddress anchor,
        EvidencedValue<bool?> protectedItem,
        EvidencedValue<bool?> pinned,
        EvidencedValue<long?> replacementValueRoubles)
    {
        Anchor = anchor;
        ProtectedItem = protectedItem ?? throw new ArgumentNullException(nameof(protectedItem));
        Pinned = pinned ?? throw new ArgumentNullException(nameof(pinned));
        ReplacementValueRoubles = replacementValueRoubles ?? throw new ArgumentNullException(nameof(replacementValueRoubles));
        ValidateMoney(replacementValueRoubles, nameof(replacementValueRoubles));
    }

    public GridCellAddress Anchor { get; }

    public EvidencedValue<bool?> ProtectedItem { get; }

    public EvidencedValue<bool?> Pinned { get; }

    public EvidencedValue<long?> ReplacementValueRoubles { get; }

    public bool IsKnownDroppable =>
        IsCurrentComplete(ProtectedItem) && ProtectedItem.Value == false &&
        IsCurrentComplete(Pinned) && Pinned.Value == false;

    private static bool IsCurrentComplete<T>(EvidencedValue<T> value) =>
        value.Status.Completeness == ResultCompleteness.Complete &&
        value.Status.Freshness == FreshnessState.Current;

    private static void ValidateMoney(EvidencedValue<long?> field, string parameterName)
    {
        var values = new[] { field.Value }
            .Concat(field.Candidates.Select(candidate => candidate.Value))
            .Concat(field.Corrections.SelectMany(correction =>
                new[] { correction.OriginalValue, correction.CorrectedValue }));
        if (values.Any(value => value < 0))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Replacement values cannot be negative.");
        }
    }
}

public sealed record LootScanRequest
{
    public const int MaximumRecommendations = GridGeometry.MaxCells;

    public const int MaximumCarriedPolicies = GridGeometry.MaxCells;

    public LootScanRequest(
        string scanId,
        CaptureSessionId captureSessionId,
        string artifactId,
        int decodeRevision,
        string sourceContentSha256,
        string reviewedContentSha256,
        string initiatingDeviceId,
        DateTimeOffset evaluatedUtc,
        GridReconstructionResult visibleLoot,
        GridReconstructionResult carriedInventory,
        IReadOnlyList<LootScanCandidateRecommendation> recommendations,
        IReadOnlyList<LootScanCarriedPolicy> carriedPolicies)
    {
        ScanId = Required(scanId, nameof(scanId), 128);
        CaptureSessionId = captureSessionId.Value != Guid.Empty
            ? captureSessionId
            : throw new ArgumentException("A capture session is required.", nameof(captureSessionId));
        ArtifactId = Required(artifactId, nameof(artifactId), 128);
        if (decodeRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decodeRevision));
        }

        DecodeRevision = decodeRevision;
        SourceContentSha256 = Sha256(sourceContentSha256, nameof(sourceContentSha256));
        ReviewedContentSha256 = Sha256(reviewedContentSha256, nameof(reviewedContentSha256));
        InitiatingDeviceId = Required(initiatingDeviceId, nameof(initiatingDeviceId), 128);
        EvaluatedUtc = evaluatedUtc.Offset == TimeSpan.Zero
            ? evaluatedUtc
            : throw new ArgumentException("Loot-scan evaluation time must be UTC.", nameof(evaluatedUtc));
        VisibleLoot = visibleLoot ?? throw new ArgumentNullException(nameof(visibleLoot));
        CarriedInventory = carriedInventory ?? throw new ArgumentNullException(nameof(carriedInventory));
        if (visibleLoot.Surface != InventoryGridSurface.VisibleLoot)
        {
            throw new ArgumentException("The loot result must describe the visible-loot grid.", nameof(visibleLoot));
        }

        if (carriedInventory.Surface != InventoryGridSurface.CarriedInventory)
        {
            throw new ArgumentException("The carried result must describe carried inventory.", nameof(carriedInventory));
        }

        Recommendations = CopyDistinct(
            recommendations,
            MaximumRecommendations,
            item => item.Anchor,
            nameof(recommendations));
        CarriedPolicies = CopyDistinct(
            carriedPolicies,
            MaximumCarriedPolicies,
            item => item.Anchor,
            nameof(carriedPolicies));
    }

    public string ScanId { get; }

    public CaptureSessionId CaptureSessionId { get; }

    public string ArtifactId { get; }

    public int DecodeRevision { get; }

    public string SourceContentSha256 { get; }

    public string ReviewedContentSha256 { get; }

    public string InitiatingDeviceId { get; }

    public DateTimeOffset EvaluatedUtc { get; }

    public GridReconstructionResult VisibleLoot { get; }

    public GridReconstructionResult CarriedInventory { get; }

    public IReadOnlyList<LootScanCandidateRecommendation> Recommendations { get; }

    public IReadOnlyList<LootScanCarriedPolicy> CarriedPolicies { get; }

    public bool IsReviewedFrameCurrent =>
        string.Equals(SourceContentSha256, ReviewedContentSha256, StringComparison.Ordinal);

    private static ReadOnlyCollection<T> CopyDistinct<T>(
        IReadOnlyList<T> values,
        int maximum,
        Func<T, GridCellAddress> key,
        string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximum)
        {
            throw new ArgumentException($"A loot scan cannot contain more than {maximum} {parameterName}.", parameterName);
        }

        var copy = values
            .Select(value => value ?? throw new ArgumentException("Lists cannot contain null entries.", parameterName))
            .ToArray();
        if (copy.Select(key).Distinct().Count() != copy.Length)
        {
            throw new ArgumentException("Grid anchors must be unique within the list.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }

    private static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string Sha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : throw new ArgumentException("A content identity must be a SHA-256 hex digest.", parameterName);
    }
}

public sealed record LootScanPlacement(
    GridCellAddress Anchor,
    int WidthCells,
    int HeightCells,
    bool RotateFromObserved);

public sealed record LootScanDropItem(
    GridCellAddress Anchor,
    EvidencedValue<RecognizedItem> Item,
    long ReplacementValueRoubles,
    EvidenceProvenance ValueProvenance);

public sealed record LootScanReason(string Code, string Explanation);

public sealed record LootScanDecision
{
    public LootScanDecision(
        GridCellAddress sourceAnchor,
        EvidencedValue<RecognizedItem> item,
        LootScanVerdict verdict,
        IReadOnlyList<LootScanReason> reasons,
        RecommendationResult? recommendation = null,
        LootScanPlacement? placement = null,
        IReadOnlyList<LootScanDropItem>? drops = null,
        long? replacementCostRoubles = null)
    {
        SourceAnchor = sourceAnchor;
        Item = item ?? throw new ArgumentNullException(nameof(item));
        Verdict = Enum.IsDefined(verdict) ? verdict : throw new ArgumentOutOfRangeException(nameof(verdict));
        ArgumentNullException.ThrowIfNull(reasons);
        var reasonCopy = reasons
            .Select(reason => reason ?? throw new ArgumentException("Reasons cannot contain null.", nameof(reasons)))
            .ToArray();
        if (reasonCopy.Length == 0)
        {
            throw new ArgumentException("A loot decision must explain itself.", nameof(reasons));
        }

        var dropCopy = (drops ?? [])
            .Select(drop => drop ?? throw new ArgumentException("Drops cannot contain null.", nameof(drops)))
            .ToArray();
        if (dropCopy.Length > LootScanPlannerLimits.MaximumSwapItems)
        {
            throw new ArgumentException("A loot decision exceeds the bounded swap size.", nameof(drops));
        }

        if (verdict == LootScanVerdict.Swap != (placement is not null && dropCopy.Length > 0))
        {
            throw new ArgumentException("Only a swap carries both a placement and displaced items.");
        }

        if (verdict == LootScanVerdict.Take && placement is null)
        {
            throw new ArgumentException("A take decision must name a supported placement.", nameof(placement));
        }

        if (replacementCostRoubles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(replacementCostRoubles));
        }

        Reasons = Array.AsReadOnly(reasonCopy);
        Recommendation = recommendation;
        Placement = placement;
        Drops = Array.AsReadOnly(dropCopy);
        ReplacementCostRoubles = replacementCostRoubles;
    }

    public GridCellAddress SourceAnchor { get; }

    public EvidencedValue<RecognizedItem> Item { get; }

    public LootScanVerdict Verdict { get; }

    public IReadOnlyList<LootScanReason> Reasons { get; }

    public RecommendationResult? Recommendation { get; }

    public LootScanPlacement? Placement { get; }

    public IReadOnlyList<LootScanDropItem> Drops { get; }

    public long? ReplacementCostRoubles { get; }
}

public sealed record LootScanIssue(
    LootScanIssueKind Kind,
    string Code,
    string Explanation,
    GridCellAddress? SourceAnchor = null);

public sealed record LootScanStageTiming(string Stage, long ElapsedMilliseconds);

public sealed record LootScanResult(
    string ScanId,
    CaptureSessionId CaptureSessionId,
    string ArtifactId,
    int DecodeRevision,
    string FocusDeviceId,
    ResultStatus Status,
    IReadOnlyList<LootScanDecision> Decisions,
    IReadOnlyList<LootScanIssue> Issues,
    IReadOnlyList<LootScanStageTiming> Timings);

public static class LootScanPlannerLimits
{
    public const int MaximumSwapItems = 3;
}
