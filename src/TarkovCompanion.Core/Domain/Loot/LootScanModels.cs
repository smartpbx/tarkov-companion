using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using RecommendationResult = TarkovCompanion.Core.Abstractions.V2.RecommendationResult;

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

/// <summary>
/// Compact, evidenced inputs for one bound item. The application service produces the actual
/// recommendation result so callers cannot pair an item with independently evaluated advice.
/// </summary>
public sealed record LootScanCandidateRecommendation
{
    public const int MaximumRecommendationIdLength = 128;

    public const int MaximumDataSnapshotIdLength = 256;

    public const int MaximumProfileDescriptorLength = 256;

    public const int MaximumNeedIdLength = 256;

    public const int MaximumNeedDisplayNameLength = 512;

    public LootScanCandidateRecommendation(
        LootScanEvidenceBinding binding,
        string recommendationId,
        InventoryProfileScope profileScope,
        string dataSnapshotId,
        RecommendationProfileFacts profile,
        RecommendationEconomics economics,
        RecommendationScarcityFacts scarcity,
        RecommendationEventScope? eventScope = null)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        RecommendationId = LootScanEvidenceBinding.Required(
            recommendationId,
            nameof(recommendationId),
            MaximumRecommendationIdLength);
        ProfileScope = profileScope ?? throw new ArgumentNullException(nameof(profileScope));
        if (profileScope.Generation.Length > MaximumProfileDescriptorLength ||
            profileScope.GameMode.Length > MaximumProfileDescriptorLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profileScope),
                $"Profile generation and game mode cannot exceed {MaximumProfileDescriptorLength} characters.");
        }

        DataSnapshotId = LootScanEvidenceBinding.Required(
            dataSnapshotId,
            nameof(dataSnapshotId),
            MaximumDataSnapshotIdLength);
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Economics = economics ?? throw new ArgumentNullException(nameof(economics));
        Scarcity = scarcity ?? throw new ArgumentNullException(nameof(scarcity));
        if (eventScope is { } scope &&
            (scope.ProfileScope != profileScope ||
             !string.Equals(scope.ItemId, binding.CanonicalItemId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The recommendation event scope must name the bound profile and item.",
                nameof(eventScope));
        }

        if (profile.EventState.Scope != eventScope)
        {
            throw new ArgumentException(
                "The profile event state must match the recommendation event scope exactly.",
                nameof(profile));
        }

        foreach (var need in profile.Needs)
        {
            _ = LootScanEvidenceBinding.Required(need.NeedId, nameof(profile), MaximumNeedIdLength);
            _ = LootScanEvidenceBinding.Required(
                need.DisplayName,
                nameof(profile),
                MaximumNeedDisplayNameLength);
        }

        EventScope = eventScope;
    }

    public LootScanEvidenceBinding Binding { get; }

    public GridCellAddress Anchor => Binding.Anchor;

    public string RecommendationId { get; }

    public InventoryProfileScope ProfileScope { get; }

    public string DataSnapshotId { get; }

    public RecommendationProfileFacts Profile { get; }

    public RecommendationEconomics Economics { get; }

    public RecommendationScarcityFacts Scarcity { get; }

    public RecommendationEventScope? EventScope { get; }
}

/// <summary>Facts that decide whether one observed carried item may be displaced.</summary>
public sealed record LootScanCarriedPolicy
{
    public LootScanCarriedPolicy(
        LootScanEvidenceBinding binding,
        EvidencedValue<bool?> protectedItem,
        EvidencedValue<bool?> pinned,
        EvidencedValue<long?> replacementValueRoubles,
        CarriedGridIdentity? carriedGrid = null)
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        ProtectedItem = protectedItem ?? throw new ArgumentNullException(nameof(protectedItem));
        Pinned = pinned ?? throw new ArgumentNullException(nameof(pinned));
        ReplacementValueRoubles = replacementValueRoubles ?? throw new ArgumentNullException(nameof(replacementValueRoubles));
        CarriedGrid = carriedGrid ?? CarriedGridIdentity.PrimaryBackpack;
        ValidateMoney(replacementValueRoubles, nameof(replacementValueRoubles));
    }

    public LootScanEvidenceBinding Binding { get; }

    public GridCellAddress Anchor => Binding.Anchor;

    public EvidencedValue<bool?> ProtectedItem { get; }

    public EvidencedValue<bool?> Pinned { get; }

    public EvidencedValue<long?> ReplacementValueRoubles { get; }

    public CarriedGridIdentity CarriedGrid { get; }

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
    public LootScanPlacement(
        GridCellAddress anchor,
        int widthCells,
        int heightCells,
        bool rotateFromObserved,
        CarriedGridIdentity? carriedGrid = null)
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
        CarriedGrid = carriedGrid ?? CarriedGridIdentity.PrimaryBackpack;
    }

    public GridCellAddress Anchor { get; }

    public int WidthCells { get; }

    public int HeightCells { get; }

    public bool RotateFromObserved { get; }

    public CarriedGridIdentity CarriedGrid { get; }
}

public sealed record LootScanDropItem
{
    public LootScanDropItem(
        GridCellAddress anchor,
        EvidencedValue<RecognizedItem> item,
        long replacementValueRoubles,
        EvidenceProvenance valueProvenance,
        CarriedGridIdentity? carriedGrid = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(replacementValueRoubles);
        Anchor = anchor;
        Item = item ?? throw new ArgumentNullException(nameof(item));
        ReplacementValueRoubles = replacementValueRoubles;
        ValueProvenance = valueProvenance ?? throw new ArgumentNullException(nameof(valueProvenance));
        CarriedGrid = carriedGrid ?? CarriedGridIdentity.PrimaryBackpack;
    }

    public GridCellAddress Anchor { get; }

    public EvidencedValue<RecognizedItem> Item { get; }

    public long ReplacementValueRoubles { get; }

    public EvidenceProvenance ValueProvenance { get; }

    public CarriedGridIdentity CarriedGrid { get; }
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

    private readonly PhraseAside _words;

    /// <summary>
    /// The same sentence as <see cref="Explanation"/>, as a code the App says in the interface
    /// language. Never stored or sent: a record read back has none and shows its English.
    /// </summary>
    [JsonIgnore]
    public Phrase? Words { get => _words.Value; private init => _words = new(value); }

    /// <summary>This record, carrying <paramref name="words"/> beside its English.</summary>
    public LootScanReason WithWords(Phrase? words) => this with { Words = words };
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
        if (reasons.Count is < 1 or > LootScanPlannerLimits.MaximumReasonsPerDecision)
        {
            throw new ArgumentException(
                $"A loot decision must contain between one and {LootScanPlannerLimits.MaximumReasonsPerDecision} reasons.",
                nameof(reasons));
        }

        var reasonCopy = new LootScanReason[reasons.Count];
        for (var index = 0; index < reasonCopy.Length; index++)
        {
            reasonCopy[index] = reasons[index] ??
                throw new ArgumentException("Reasons cannot contain null.", nameof(reasons));
        }

        var suppliedDrops = drops ?? [];
        if (suppliedDrops.Count > LootScanPlannerLimits.MaximumSwapItems)
        {
            throw new ArgumentException("A loot decision exceeds the bounded swap size.", nameof(drops));
        }

        var dropCopy = new LootScanDropItem[suppliedDrops.Count];
        for (var index = 0; index < dropCopy.Length; index++)
        {
            dropCopy[index] = suppliedDrops[index] ??
                throw new ArgumentException("Drops cannot contain null.", nameof(drops));
        }

        if (dropCopy.Select(drop => (drop.CarriedGrid, drop.Anchor)).Distinct().Count() != dropCopy.Length)
        {
            throw new ArgumentException("A carried item can be displaced at most once.", nameof(drops));
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

        var summedReplacementCost = SumReplacementCost(dropCopy);
        if (verdict == LootScanVerdict.Swap &&
            (replacementCostRoubles is null || replacementCostRoubles != summedReplacementCost))
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

    private static long SumReplacementCost(IReadOnlyList<LootScanDropItem> drops)
    {
        long sum = 0;
        foreach (var drop in drops)
        {
            if (drop.ReplacementValueRoubles > long.MaxValue - sum)
            {
                throw new ArgumentException("Displaced item values exceed the supported replacement-cost range.", nameof(drops));
            }

            sum += drop.ReplacementValueRoubles;
        }

        return sum;
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

    private readonly PhraseAside _words;

    /// <summary>
    /// The same sentence as <see cref="Explanation"/>, as a code the App says in the interface
    /// language. Never stored or sent: a record read back has none and shows its English.
    /// </summary>
    [JsonIgnore]
    public Phrase? Words { get => _words.Value; private init => _words = new(value); }

    /// <summary>This record, carrying <paramref name="words"/> beside its English.</summary>
    public LootScanIssue WithWords(Phrase? words) => this with { Words = words };
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

    public const int MaximumReasonsPerDecision = 16;

    public const int MaximumVisibleItems = 512;

    public const int MaximumCarriedItems = 2048;

    public const int MaximumRecommendationReasons = 64;

    public const int MaximumRecommendationSensitivities = 64;

    public const int MaximumRecommendationEvidenceVisitsPerCandidate = 4096;

    public const int MaximumRecommendationWorkVisits =
        MaximumVisibleItems * MaximumRecommendationEvidenceVisitsPerCandidate;

    /// <summary>
    /// Shared deterministic ceiling for carried-grid cell inspections in one scan. The planner
    /// returns review-only advice when legal but adversarial dimensions would exceed this work.
    /// </summary>
    public const int MaximumPlacementCellVisits = 2_000_000;
}
