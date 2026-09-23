using System.Collections.ObjectModel;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Application.Services.LootScan;

/// <summary>
/// Large recommendation inputs shared by every item in one frozen scan. Keeping the inventory and
/// raid context here prevents a caller from pairing each item with a different view of the raid or
/// duplicating thousands of inventory facts hundreds of times.
/// </summary>
public sealed record LootScanRecommendationContext
{
    public const int MaximumDataSnapshotIdLength =
        LootScanCandidateRecommendation.MaximumDataSnapshotIdLength;

    public const int MaximumProfileDescriptorLength =
        LootScanCandidateRecommendation.MaximumProfileDescriptorLength;

    public LootScanRecommendationContext(
        InventoryProfileScope profileScope,
        string dataSnapshotId,
        ObservedInventoryEvidenceSnapshot? inventory,
        RecommendationRaidContext? raidContext)
    {
        ProfileScope = profileScope ?? throw new ArgumentNullException(nameof(profileScope));
        if (profileScope.Generation.Length > MaximumProfileDescriptorLength ||
            profileScope.GameMode.Length > MaximumProfileDescriptorLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(profileScope),
                $"Profile generation and game mode cannot exceed {MaximumProfileDescriptorLength} characters.");
        }

        DataSnapshotId = LootScanApplicationGuard.Required(
            dataSnapshotId,
            nameof(dataSnapshotId),
            MaximumDataSnapshotIdLength);
        if (inventory is not null &&
            (inventory.Scope != ProfileScope ||
             !string.Equals(inventory.DataSnapshotId, DataSnapshotId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The shared inventory must belong to the scan profile and data snapshot.",
                nameof(inventory));
        }

        Inventory = inventory;
        RaidContext = raidContext;
    }

    public InventoryProfileScope ProfileScope { get; }

    public string DataSnapshotId { get; }

    public ObservedInventoryEvidenceSnapshot? Inventory { get; }

    public RecommendationRaidContext? RaidContext { get; }
}

/// <summary>A bounded decision request tied to the capture context frozen at intake.</summary>
public sealed record LootScanRequest
{
    public LootScanRequest(
        string scanId,
        CaptureSessionId captureSessionId,
        CaptureCorrelationId correlationId,
        CaptureContextMetadata context,
        string artifactId,
        int decodeRevision,
        string sourceContentSha256,
        string reviewedContentSha256,
        string initiatingDeviceId,
        DateTimeOffset evaluatedUtc,
        LootScanRecommendationContext recommendationContext,
        GridReconstructionResult visibleLoot,
        GridReconstructionResult carriedInventory,
        IReadOnlyList<LootScanCandidateRecommendation> recommendations,
        IReadOnlyList<LootScanCarriedPolicy> carriedPolicies)
        : this(
            scanId,
            captureSessionId,
            correlationId,
            context,
            artifactId,
            decodeRevision,
            sourceContentSha256,
            reviewedContentSha256,
            initiatingDeviceId,
            evaluatedUtc,
            recommendationContext,
            visibleLoot,
            [new(CarriedGridIdentity.PrimaryBackpack, carriedInventory)],
            carriedCoverageComplete: true,
            recommendations,
            carriedPolicies)
    {
    }

    public LootScanRequest(
        string scanId,
        CaptureSessionId captureSessionId,
        CaptureCorrelationId correlationId,
        CaptureContextMetadata context,
        string artifactId,
        int decodeRevision,
        string sourceContentSha256,
        string reviewedContentSha256,
        string initiatingDeviceId,
        DateTimeOffset evaluatedUtc,
        LootScanRecommendationContext recommendationContext,
        GridReconstructionResult visibleLoot,
        IReadOnlyList<CarriedGridReconstructionResult> carriedGrids,
        bool carriedCoverageComplete,
        IReadOnlyList<LootScanCandidateRecommendation> recommendations,
        IReadOnlyList<LootScanCarriedPolicy> carriedPolicies)
    {
        ScanId = Required(scanId, nameof(scanId), 128);
        CaptureSessionId = captureSessionId.Value != Guid.Empty
            ? captureSessionId
            : throw new ArgumentException("A capture session is required.", nameof(captureSessionId));
        CorrelationId = correlationId.IsDefined
            ? correlationId
            : throw new ArgumentException("A capture correlation id is required.", nameof(correlationId));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ArtifactId = Required(artifactId, nameof(artifactId), 128);
        if (decodeRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decodeRevision));
        }

        DecodeRevision = decodeRevision;
        SourceContentSha256 = Sha256(sourceContentSha256, nameof(sourceContentSha256));
        ReviewedContentSha256 = Sha256(reviewedContentSha256, nameof(reviewedContentSha256));
        InitiatingDeviceId = Required(initiatingDeviceId, nameof(initiatingDeviceId), 128);
        if (context.InitiatingDevice is not null &&
            !string.Equals(context.InitiatingDevice, InitiatingDeviceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The initiating device must match the capture context.", nameof(initiatingDeviceId));
        }

        EvaluatedUtc = evaluatedUtc.Offset == TimeSpan.Zero
            ? evaluatedUtc
            : throw new ArgumentException("Loot-scan evaluation time must be UTC.", nameof(evaluatedUtc));
        RecommendationContext = recommendationContext ?? throw new ArgumentNullException(nameof(recommendationContext));
        VisibleLoot = visibleLoot ?? throw new ArgumentNullException(nameof(visibleLoot));
        ArgumentNullException.ThrowIfNull(carriedGrids);
        if (visibleLoot.Surface != InventoryGridSurface.VisibleLoot)
        {
            throw new ArgumentException("The loot result must describe the visible-loot grid.", nameof(visibleLoot));
        }

        if (carriedGrids.Any(carried => carried is null || carried.Reconstruction.Surface != InventoryGridSurface.CarriedInventory))
        {
            throw new ArgumentException("Every carried result must describe carried inventory.", nameof(carriedGrids));
        }

        if (carriedGrids.Select(carried => carried.Identity).Distinct().Count() != carriedGrids.Count)
        {
            throw new ArgumentException("Carried grid identities must be unique.", nameof(carriedGrids));
        }

        CarriedGrids = Array.AsReadOnly(carriedGrids.ToArray());
        HasCompleteCarriedCoverage = carriedCoverageComplete;
        CarriedInventory = CarriedGrids
            .FirstOrDefault(carried => carried.Identity == CarriedGridIdentity.PrimaryBackpack)
            ?.Reconstruction
            ?? new(
                GridReconstructionOutcome.NoChange,
                InventoryGridSurface.CarriedInventory,
                recognition: null,
                unresolvedCells: [],
                issues: []);

        EnsureBounded(visibleLoot, LootScanPlannerLimits.MaximumVisibleItems, nameof(visibleLoot));
        var carriedItemCount = CarriedGrids.Sum(carried =>
            (carried.Reconstruction.Recognition?.Cells.Count ?? 0) + carried.Reconstruction.UnresolvedCells.Count);
        if (carriedItemCount > LootScanPlannerLimits.MaximumCarriedItems)
        {
            throw new ArgumentException(
                $"A loot scan cannot contain more than {LootScanPlannerLimits.MaximumCarriedItems} carried items.",
                nameof(carriedGrids));
        }
        Recommendations = CopyDistinct(
            recommendations,
            LootScanPlannerLimits.MaximumVisibleItems,
            item => item.Anchor,
            nameof(recommendations));
        CarriedPolicies = CopyDistinct(
            carriedPolicies,
            LootScanPlannerLimits.MaximumCarriedItems,
            item => (item.CarriedGrid, item.Anchor),
            nameof(carriedPolicies));
    }

    public string ScanId { get; }

    public CaptureSessionId CaptureSessionId { get; }

    public CaptureCorrelationId CorrelationId { get; }

    public CaptureContextMetadata Context { get; }

    public string ArtifactId { get; }

    public int DecodeRevision { get; }

    public string SourceContentSha256 { get; }

    public string ReviewedContentSha256 { get; }

    public string InitiatingDeviceId { get; }

    public DateTimeOffset EvaluatedUtc { get; }

    public LootScanRecommendationContext RecommendationContext { get; }

    public GridReconstructionResult VisibleLoot { get; }

    public GridReconstructionResult CarriedInventory { get; }

    public IReadOnlyList<CarriedGridReconstructionResult> CarriedGrids { get; }

    public bool HasCompleteCarriedCoverage { get; }

    public IReadOnlyList<LootScanCandidateRecommendation> Recommendations { get; }

    public IReadOnlyList<LootScanCarriedPolicy> CarriedPolicies { get; }

    public bool IsReviewedFrameCurrent =>
        string.Equals(SourceContentSha256, ReviewedContentSha256, StringComparison.Ordinal);

    private static void EnsureBounded(GridReconstructionResult result, int maximum, string parameterName)
    {
        var count = (result.Recognition?.Cells.Count ?? 0) + result.UnresolvedCells.Count;
        if (count > maximum)
        {
            throw new ArgumentException($"A loot scan cannot contain more than {maximum} observed items.", parameterName);
        }
    }

    private static ReadOnlyCollection<T> CopyDistinct<T, TKey>(
        IReadOnlyList<T> values,
        int maximum,
        Func<T, TKey> key,
        string parameterName)
        where T : class
        where TKey : notnull
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

    private static string Required(string value, string parameterName, int maximumLength) =>
        LootScanApplicationGuard.Required(value, parameterName, maximumLength);

    private static string Sha256(string value, string parameterName) =>
        LootScanApplicationGuard.Sha256(value, parameterName);
}

/// <summary>A review result that keeps its initiating device and frozen capture context.</summary>
public sealed record LootScanResult
{
    public LootScanResult(
        string scanId,
        CaptureSessionId captureSessionId,
        CaptureCorrelationId correlationId,
        CaptureContextMetadata context,
        string artifactId,
        int decodeRevision,
        string sourceContentSha256,
        string reviewedContentSha256,
        string focusDeviceId,
        DateTimeOffset evaluatedUtc,
        ResultStatus status,
        IReadOnlyList<LootScanDecision> decisions,
        IReadOnlyList<LootScanIssue> issues,
        IReadOnlyList<LootScanStageTiming> timings)
    {
        ScanId = LootScanApplicationGuard.Required(scanId, nameof(scanId), 128);
        CaptureSessionId = captureSessionId.Value != Guid.Empty
            ? captureSessionId
            : throw new ArgumentException("A capture session is required.", nameof(captureSessionId));
        CorrelationId = correlationId.IsDefined
            ? correlationId
            : throw new ArgumentException("A capture correlation id is required.", nameof(correlationId));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        ArtifactId = LootScanApplicationGuard.Required(artifactId, nameof(artifactId), 128);
        ArgumentOutOfRangeException.ThrowIfNegative(decodeRevision);
        DecodeRevision = decodeRevision;
        SourceContentSha256 = LootScanApplicationGuard.Sha256(sourceContentSha256, nameof(sourceContentSha256));
        ReviewedContentSha256 = LootScanApplicationGuard.Sha256(reviewedContentSha256, nameof(reviewedContentSha256));
        FocusDeviceId = LootScanApplicationGuard.Required(focusDeviceId, nameof(focusDeviceId), 128);
        if (context.InitiatingDevice is not null &&
            !string.Equals(context.InitiatingDevice, FocusDeviceId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Result focus must return to the device that initiated the capture.", nameof(focusDeviceId));
        }

        EvaluatedUtc = evaluatedUtc.Offset == TimeSpan.Zero
            ? evaluatedUtc
            : throw new ArgumentException("Loot-scan evaluation time must be UTC.", nameof(evaluatedUtc));
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Decisions = Copy(decisions, LootScanPlannerLimits.MaximumVisibleItems, nameof(decisions));
        if (Decisions.Select(decision => decision.SourceAnchor).Distinct().Count() != Decisions.Count)
        {
            throw new ArgumentException("A loot result must contain at most one decision per source anchor.", nameof(decisions));
        }

        Issues = Copy(issues, LootScanPlannerLimits.MaximumVisibleItems * 4, nameof(issues));
        Timings = Copy(timings, 32, nameof(timings));
        if (Status.Completeness == ResultCompleteness.Complete &&
            (Issues.Count > 0 || Decisions.Any(decision => decision.Verdict == LootScanVerdict.Review)))
        {
            throw new ArgumentException("A complete loot result cannot contain review decisions or unresolved issues.", nameof(status));
        }
    }

    public string ScanId { get; }
    public CaptureSessionId CaptureSessionId { get; }
    public CaptureCorrelationId CorrelationId { get; }
    public CaptureContextMetadata Context { get; }
    public string ArtifactId { get; }
    public int DecodeRevision { get; }
    public string SourceContentSha256 { get; }
    public string ReviewedContentSha256 { get; }
    public string FocusDeviceId { get; }
    public DateTimeOffset EvaluatedUtc { get; }
    public ResultStatus Status { get; }
    public IReadOnlyList<LootScanDecision> Decisions { get; }
    public IReadOnlyList<LootScanIssue> Issues { get; }
    public IReadOnlyList<LootScanStageTiming> Timings { get; }

    /// <summary>
    /// The reviewed loot and carried grids the decisions were planned against, when read. The
    /// decisions alone name only the items they move; a review surface that draws the container
    /// and the backpack as grids needs every occupied footprint and the grid size as well.
    /// </summary>
    public GridRecognition? VisibleLootGrid { get; init; }

    public GridRecognition? CarriedGrid { get; init; }

    public IReadOnlyList<CarriedGridRecognition> CarriedGrids { get; init; } = [];

    private static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> values, int maximum, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximum)
        {
            throw new ArgumentException($"The result cannot contain more than {maximum} {parameterName}.", parameterName);
        }

        return Array.AsReadOnly(values
            .Select(value => value ?? throw new ArgumentException("Result lists cannot contain null.", parameterName))
            .ToArray());
    }
}

internal static class LootScanApplicationGuard
{
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
