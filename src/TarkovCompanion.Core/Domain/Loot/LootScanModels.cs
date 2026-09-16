using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recommendations;

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

/// <summary>Binds derived advice to the exact pixels, decode, cell, and item it describes.</summary>
public sealed record LootScanEvidenceBinding
{
    public LootScanEvidenceBinding(
        CaptureSessionId captureSessionId,
        string artifactId,
        int decodeRevision,
        string contentSha256,
        GridCellAddress anchor,
        string canonicalItemId)
    {
        CaptureSessionId = captureSessionId.Value != Guid.Empty
            ? captureSessionId
            : throw new ArgumentException("A capture session is required.", nameof(captureSessionId));
        ArtifactId = Required(artifactId, nameof(artifactId), 128);
        if (decodeRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decodeRevision));
        }

        DecodeRevision = decodeRevision;
        ContentSha256 = Sha256(contentSha256, nameof(contentSha256));
        Anchor = anchor;
        CanonicalItemId = Required(canonicalItemId, nameof(canonicalItemId), 256);
    }

    public CaptureSessionId CaptureSessionId { get; }

    public string ArtifactId { get; }

    public int DecodeRevision { get; }

    public string ContentSha256 { get; }

    public GridCellAddress Anchor { get; }

    public string CanonicalItemId { get; }

    public bool Matches(
        CaptureSessionId captureSessionId,
        string artifactId,
        int decodeRevision,
        string contentSha256,
        GridCellAddress anchor,
        string canonicalItemId) =>
        CaptureSessionId == captureSessionId &&
        string.Equals(ArtifactId, artifactId, StringComparison.Ordinal) &&
        DecodeRevision == decodeRevision &&
        string.Equals(ContentSha256, contentSha256, StringComparison.Ordinal) &&
        Anchor == anchor &&
        string.Equals(CanonicalItemId, canonicalItemId, StringComparison.Ordinal);

    internal static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    internal static string Sha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Length == 64 && value.All(Uri.IsHexDigit)
            ? value.ToLowerInvariant()
            : throw new ArgumentException("A content identity must be a SHA-256 hex digest.", parameterName);
    }
}

public sealed record LootScanCandidateRecommendation
{
    public LootScanCandidateRecommendation(
        LootScanEvidenceBinding binding,
        RecommendationResult recommendation,
        RecommendationEconomics economics)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Recommendation = recommendation ?? throw new ArgumentNullException(nameof(recommendation));
        Economics = economics ?? throw new ArgumentNullException(nameof(economics));
        if (recommendation.CaptureSessionId != binding.CaptureSessionId)
        {
            throw new ArgumentException("Recommendation and loot evidence must name the same capture session.", nameof(recommendation));
        }
    }

    public LootScanEvidenceBinding Binding { get; }

    public GridCellAddress Anchor => Binding.Anchor;

    public RecommendationResult Recommendation { get; }

    public RecommendationEconomics Economics { get; }
}

/// <summary>Facts that decide whether one observed carried item may be displaced.</summary>
public sealed record LootScanCarriedPolicy
{
    public LootScanCarriedPolicy(
        LootScanEvidenceBinding binding,
        EvidencedValue<bool?> protectedItem,
        EvidencedValue<bool?> pinned,
        EvidencedValue<long?> replacementValueRoubles)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ProtectedItem = protectedItem ?? throw new ArgumentNullException(nameof(protectedItem));
        Pinned = pinned ?? throw new ArgumentNullException(nameof(pinned));
        ReplacementValueRoubles = replacementValueRoubles ?? throw new ArgumentNullException(nameof(replacementValueRoubles));
        ValidateMoney(replacementValueRoubles, nameof(replacementValueRoubles));
    }

    public LootScanEvidenceBinding Binding { get; }

    public GridCellAddress Anchor => Binding.Anchor;

    public EvidencedValue<bool?> ProtectedItem { get; }

    public EvidencedValue<bool?> Pinned { get; }

    public EvidencedValue<long?> ReplacementValueRoubles { get; }

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

public sealed record LootScanPlacement
{
    public LootScanPlacement(GridCellAddress anchor, int widthCells, int heightCells, bool rotateFromObserved)
    {
        if (widthCells is < 1 or > GridGeometry.MaxColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(widthCells));
        }

        if (heightCells is < 1 or > GridGeometry.MaxRows)
        {
            throw new ArgumentOutOfRangeException(nameof(heightCells));
        }

        if (anchor.Column > GridGeometry.MaxColumns - widthCells ||
            anchor.Row > GridGeometry.MaxRows - heightCells)
        {
            throw new ArgumentException("A loot placement must remain inside the bounded grid.", nameof(anchor));
        }

        Anchor = anchor;
        WidthCells = widthCells;
        HeightCells = heightCells;
        RotateFromObserved = rotateFromObserved;
    }

    public GridCellAddress Anchor { get; }

    public int WidthCells { get; }

    public int HeightCells { get; }

    public bool RotateFromObserved { get; }
}

public sealed record LootScanDropItem
{
    public LootScanDropItem(
        GridCellAddress anchor,
        EvidencedValue<RecognizedItem> item,
        long replacementValueRoubles,
        EvidenceProvenance valueProvenance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(replacementValueRoubles);
        Anchor = anchor;
        Item = item ?? throw new ArgumentNullException(nameof(item));
        ReplacementValueRoubles = replacementValueRoubles;
        ValueProvenance = valueProvenance ?? throw new ArgumentNullException(nameof(valueProvenance));
    }

    public GridCellAddress Anchor { get; }

    public EvidencedValue<RecognizedItem> Item { get; }

    public long ReplacementValueRoubles { get; }

    public EvidenceProvenance ValueProvenance { get; }
}

public sealed record LootScanReason
{
    public LootScanReason(string code, string explanation)
    {
        Code = LootScanEvidenceBinding.Required(code, nameof(code), 128);
        Explanation = LootScanEvidenceBinding.Required(explanation, nameof(explanation), 1024);
    }

    public string Code { get; }

    public string Explanation { get; }
}

/// <summary>Explicit raw inputs plus the evidence-gated value projection shown to the user.</summary>
public sealed record LootScanEconomicProjection
{
    public LootScanEconomicProjection(
        RecommendationEconomics inputs,
        ResultStatus status,
        long? bestNetValueRoubles,
        long? valuePerSquareRoubles,
        EconomicValueBand? valueBand,
        string? selectedPriceBasis,
        EvidenceProvenance? calculationProvenance)
    {
        Inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        Status = status ?? throw new ArgumentNullException(nameof(status));
        if (bestNetValueRoubles < 0 || valuePerSquareRoubles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bestNetValueRoubles));
        }

        if (valueBand is { } band && !Enum.IsDefined(band))
        {
            throw new ArgumentOutOfRangeException(nameof(valueBand));
        }

        var complete = status.Completeness == ResultCompleteness.Complete;
        if (complete != (bestNetValueRoubles is not null && valuePerSquareRoubles is not null &&
                         valueBand is not null && selectedPriceBasis is not null && calculationProvenance is not null))
        {
            throw new ArgumentException("A complete economic projection must carry its value, band, basis, and provenance.");
        }

        if (complete && status.Freshness != FreshnessState.Current)
        {
            throw new ArgumentException("A decisive economic projection must be current.", nameof(status));
        }

        Inputs = inputs;
        BestNetValueRoubles = bestNetValueRoubles;
        ValuePerSquareRoubles = valuePerSquareRoubles;
        ValueBand = valueBand;
        SelectedPriceBasis = selectedPriceBasis is null
            ? null
            : LootScanEvidenceBinding.Required(selectedPriceBasis, nameof(selectedPriceBasis), 64);
        CalculationProvenance = calculationProvenance;
    }

    public RecommendationEconomics Inputs { get; }

    public ResultStatus Status { get; }

    public long? BestNetValueRoubles { get; }

    public long? ValuePerSquareRoubles { get; }

    public EconomicValueBand? ValueBand { get; }

    public string? SelectedPriceBasis { get; }

    public EvidenceProvenance? CalculationProvenance { get; }
}

public sealed record LootScanDecision
{
    public LootScanDecision(
        GridCellAddress sourceAnchor,
        EvidencedValue<RecognizedItem> item,
        LootScanVerdict verdict,
        IReadOnlyList<LootScanReason> reasons,
        RecommendationResult? recommendation = null,
        LootScanEconomicProjection? economics = null,
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

        if ((verdict == LootScanVerdict.Swap) != (placement is not null && dropCopy.Length > 0))
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

        if (verdict == LootScanVerdict.Swap &&
            (replacementCostRoubles is null || replacementCostRoubles != dropCopy.Sum(drop => drop.ReplacementValueRoubles)))
        {
            throw new ArgumentException("A swap replacement cost must equal the displaced item values.", nameof(replacementCostRoubles));
        }

        if (verdict != LootScanVerdict.Swap && (dropCopy.Length > 0 || replacementCostRoubles is not null))
        {
            throw new ArgumentException("Only a swap can carry displaced items or replacement cost.", nameof(drops));
        }

        if (verdict is LootScanVerdict.Leave or LootScanVerdict.Review && placement is not null)
        {
            throw new ArgumentException("Leave and review decisions cannot claim a placement.", nameof(placement));
        }

        Reasons = Array.AsReadOnly(reasonCopy);
        Recommendation = recommendation;
        Economics = economics;
        Placement = placement;
        Drops = Array.AsReadOnly(dropCopy);
        ReplacementCostRoubles = replacementCostRoubles;
    }

    public GridCellAddress SourceAnchor { get; }

    public EvidencedValue<RecognizedItem> Item { get; }

    public LootScanVerdict Verdict { get; }

    public IReadOnlyList<LootScanReason> Reasons { get; }

    public RecommendationResult? Recommendation { get; }

    public LootScanEconomicProjection? Economics { get; }

    public LootScanPlacement? Placement { get; }

    public IReadOnlyList<LootScanDropItem> Drops { get; }

    public long? ReplacementCostRoubles { get; }
}

public sealed record LootScanIssue
{
    public LootScanIssue(LootScanIssueKind kind, string code, string explanation, GridCellAddress? sourceAnchor = null)
    {
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
        Code = LootScanEvidenceBinding.Required(code, nameof(code), 128);
        Explanation = LootScanEvidenceBinding.Required(explanation, nameof(explanation), 1024);
        SourceAnchor = sourceAnchor;
    }

    public LootScanIssueKind Kind { get; }

    public string Code { get; }

    public string Explanation { get; }

    public GridCellAddress? SourceAnchor { get; }
}

public sealed record LootScanStageTiming
{
    public LootScanStageTiming(string stage, long elapsedMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        Stage = LootScanEvidenceBinding.Required(stage, nameof(stage), 128);
        ElapsedMilliseconds = elapsedMilliseconds;
    }

    public string Stage { get; }

    public long ElapsedMilliseconds { get; }
}

public static class LootScanPlannerLimits
{
    public const int MaximumSwapItems = 3;

    public const int MaximumVisibleItems = 512;

    public const int MaximumCarriedItems = 2048;
}
