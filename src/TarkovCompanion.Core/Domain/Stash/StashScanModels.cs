using System.Collections.ObjectModel;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Inventory;

namespace TarkovCompanion.Core.Domain.Stash;

public static class StashScanBounds
{
    // Two maximum-sized assembled roots plus their root nodes fit exactly beneath one comparison
    // provenance without exceeding EvidenceProvenance.MaxInputCount (2 + 127 + 127 = 256).
    public const int MaximumCaptures = 127;
    public const int MaximumIssues = 1_024;
    public const int MaximumPlanItems = 4_096;
    public const int MaximumSnapshotChanges = MaximumPlanItems * 3;
    public const int MaximumReasonsPerItem = 32;
    public const int MaximumOperationsPerItem = 8;
}

public enum StashScanIssueKind
{
    DuplicateCapture = 1,
    OutOfOrderCapture,
    OriginUnresolved,
    AmbiguousOverlap,
    MovementConflict,
    MissingCoverage,
    OccludedCells,
    ClosedContainer,
    PartialRecognition,
    ContainerGeometryConflict,
    IssueLimitReached,
}

/// <summary>
/// A suggested next capture or review step. Every action remains manual; this contract deliberately
/// has no input or inventory-mutation operation.
/// </summary>
public enum StashScanRetryAction
{
    None = 1,
    ReturnToContainerStart,
    CaptureWithMoreOverlap,
    CaptureMissingRange,
    ReopenAndCaptureContainer,
    ReviewConflictingCells,
    RetryOccludedRegion,
}

/// <summary>A bounded, localizable explanation of why a stash snapshot is not exact.</summary>
public sealed record StashScanIssue
{
    public StashScanIssue(
        StashScanIssueKind kind,
        string code,
        StashScanRetryAction retryAction,
        string containerPath,
        string? artifactId = null,
        int? captureOrdinal = null,
        int? affectedCells = null)
    {
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
        Code = Required(code, nameof(code), 128);
        RetryAction = Enum.IsDefined(retryAction)
            ? retryAction
            : throw new ArgumentOutOfRangeException(nameof(retryAction));
        ContainerPath = ContainerPaths.Validate(containerPath, nameof(containerPath));
        ArtifactId = Optional(artifactId, nameof(artifactId), 128);
        CaptureOrdinal = captureOrdinal is null or >= 0
            ? captureOrdinal
            : throw new ArgumentOutOfRangeException(nameof(captureOrdinal));
        AffectedCells = affectedCells is null or >= 0
            ? affectedCells
            : throw new ArgumentOutOfRangeException(nameof(affectedCells));
    }

    public StashScanIssueKind Kind { get; }

    public string Code { get; }

    public StashScanRetryAction RetryAction { get; }

    public string ContainerPath { get; }

    public string? ArtifactId { get; }

    public int? CaptureOrdinal { get; }

    public int? AffectedCells { get; }

    private static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? Optional(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>
/// Pixel-free outcome of one guided session. Content identities used for in-memory deduplication
/// are intentionally absent, as are source paths and screenshots.
/// </summary>
public sealed record StashScanSessionReport
{
    public StashScanSessionReport(
        CaptureSessionId sessionId,
        string snapshotId,
        ResultStatus status,
        EvidenceCoverage coverage,
        IReadOnlyList<string> acceptedArtifactIds,
        IReadOnlyList<string> duplicateArtifactIds,
        IReadOnlyList<StashScanIssue> issues)
    {
        SessionId = sessionId.Value != Guid.Empty
            ? sessionId
            : throw new ArgumentException("A capture session is required.", nameof(sessionId));
        SnapshotId = Required(snapshotId, nameof(snapshotId), 256);
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        AcceptedArtifactIds = CopyStrings(
            acceptedArtifactIds,
            StashScanBounds.MaximumCaptures,
            nameof(acceptedArtifactIds));
        DuplicateArtifactIds = CopyStrings(
            duplicateArtifactIds,
            StashScanBounds.MaximumCaptures,
            nameof(duplicateArtifactIds));
        Issues = Copy(issues, StashScanBounds.MaximumIssues, nameof(issues));
    }

    public CaptureSessionId SessionId { get; }

    public string SnapshotId { get; }

    public ResultStatus Status { get; }

    public EvidenceCoverage Coverage { get; }

    public IReadOnlyList<string> AcceptedArtifactIds { get; }

    public IReadOnlyList<string> DuplicateArtifactIds { get; }

    public IReadOnlyList<StashScanIssue> Issues { get; }

    private static ReadOnlyCollection<string> CopyStrings(
        IReadOnlyList<string> values,
        int maximum,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximum)
        {
            throw new ArgumentException($"The list cannot exceed {maximum} entries.", parameterName);
        }

        var copy = values
            .Select(value => Required(value, parameterName, 128))
            .ToArray();
        if (copy.Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("Artifact identifiers must be unique.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }

    private static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> values, int maximum, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximum)
        {
            throw new ArgumentException($"The list cannot exceed {maximum} entries.", parameterName);
        }

        return Array.AsReadOnly(values
            .Select(value => value ?? throw new ArgumentException("Lists cannot contain null entries.", parameterName))
            .ToArray());
    }

    private static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public enum StashSnapshotChangeKind
{
    Added = 1,
    Removed,
    Moved,
    QuantityChanged,
    EvidenceChanged,
    Resolved,
    BecameUnresolved,
}

public enum StashObservationState
{
    Absent = 1,
    Observed,
    Inferred,
    Unresolved,
}

/// <summary>One evidence-honest change between two reviewed snapshots.</summary>
public sealed record StashSnapshotChange
{
    public StashSnapshotChange(
        StashSnapshotChangeKind kind,
        string comparisonKey,
        StashObservationState previousState,
        StashObservationState currentState,
        string? itemId,
        string? previousContainerPath,
        GridCellAddress? previousAnchor,
        int? previousQuantity,
        string? currentContainerPath,
        GridCellAddress? currentAnchor,
        int? currentQuantity,
        EvidenceProvenance provenance)
    {
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
        ComparisonKey = Required(comparisonKey, nameof(comparisonKey), 256);
        PreviousState = Enum.IsDefined(previousState)
            ? previousState
            : throw new ArgumentOutOfRangeException(nameof(previousState));
        CurrentState = Enum.IsDefined(currentState)
            ? currentState
            : throw new ArgumentOutOfRangeException(nameof(currentState));
        ItemId = Optional(itemId, nameof(itemId), 256);
        PreviousContainerPath = OptionalPath(previousContainerPath, nameof(previousContainerPath));
        PreviousAnchor = previousAnchor;
        PreviousQuantity = previousQuantity is null or >= 0
            ? previousQuantity
            : throw new ArgumentOutOfRangeException(nameof(previousQuantity));
        CurrentContainerPath = OptionalPath(currentContainerPath, nameof(currentContainerPath));
        CurrentAnchor = currentAnchor;
        CurrentQuantity = currentQuantity is null or >= 0
            ? currentQuantity
            : throw new ArgumentOutOfRangeException(nameof(currentQuantity));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));

        var statesAreCoherent = kind switch
        {
            StashSnapshotChangeKind.Added =>
                previousState == StashObservationState.Absent && currentState != StashObservationState.Absent,
            StashSnapshotChangeKind.Removed =>
                previousState != StashObservationState.Absent && currentState == StashObservationState.Absent,
            _ => previousState != StashObservationState.Absent && currentState != StashObservationState.Absent,
        };
        if (!statesAreCoherent)
        {
            throw new ArgumentException("Snapshot change states do not match the change kind.");
        }
    }

    public StashSnapshotChangeKind Kind { get; }
    public string ComparisonKey { get; }
    public StashObservationState PreviousState { get; }
    public StashObservationState CurrentState { get; }
    public string? ItemId { get; }
    public string? PreviousContainerPath { get; }
    public GridCellAddress? PreviousAnchor { get; }
    public int? PreviousQuantity { get; }
    public string? CurrentContainerPath { get; }
    public GridCellAddress? CurrentAnchor { get; }
    public int? CurrentQuantity { get; }
    public EvidenceProvenance Provenance { get; }

    private static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? Optional(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? OptionalPath(string? value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : ContainerPaths.Validate(value, parameterName);
}

public enum StashPlanGroup
{
    Keep = 1,
    Sell,
    UseSoon,
    Organize,
    Review,
}

/// <summary>Manual-only checklist operations; none can be executed against the game.</summary>
public enum StashManualOperation
{
    Group = 1,
    Consolidate,
    Stage,
    AddToSellQueue,
    Pin,
    Ignore,
    Rescan,
    ReviewEvidence,
}

/// <summary>
/// One item in a manual organization checklist. Economic channels and utility facts stay separate
/// so a missing fee, footprint, scarcity, or obtainability value cannot masquerade as a zero.
/// </summary>
public sealed record StashOrganizationItem
{
    public StashOrganizationItem(
        string itemKey,
        string canonicalItemId,
        string containerPath,
        GridCellAddress anchor,
        StashPlanGroup group,
        IReadOnlyList<StashManualOperation> operations,
        IReadOnlyList<string> reasonCodes,
        EvidencedValue<long?> fleaFeeRoubles,
        EvidencedValue<long?> netValueRoubles,
        EvidencedValue<long?> valuePerSquareRoubles,
        EvidencedValue<TimeSpan?> age,
        EvidencedValue<string> scarcity,
        EvidencedValue<string> obtainability,
        ResultStatus recommendationStatus,
        string recommendationVersion,
        DateTimeOffset evaluatedUtc)
    {
        ItemKey = Required(itemKey, nameof(itemKey), 256);
        CanonicalItemId = Required(canonicalItemId, nameof(canonicalItemId), 256);
        ContainerPath = ContainerPaths.Validate(containerPath, nameof(containerPath));
        Anchor = anchor;
        Group = Enum.IsDefined(group) ? group : throw new ArgumentOutOfRangeException(nameof(group));
        Operations = CopyEnums(operations, StashScanBounds.MaximumOperationsPerItem, nameof(operations));
        ReasonCodes = CopyReasons(reasonCodes, nameof(reasonCodes));
        FleaFeeRoubles = fleaFeeRoubles ?? throw new ArgumentNullException(nameof(fleaFeeRoubles));
        NetValueRoubles = netValueRoubles ?? throw new ArgumentNullException(nameof(netValueRoubles));
        ValuePerSquareRoubles = valuePerSquareRoubles ?? throw new ArgumentNullException(nameof(valuePerSquareRoubles));
        if (EvidenceValues(fleaFeeRoubles).Concat(EvidenceValues(netValueRoubles))
            .Concat(EvidenceValues(valuePerSquareRoubles)).Any(value => value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(fleaFeeRoubles), "Economic facts cannot be negative.");
        }

        Age = age ?? throw new ArgumentNullException(nameof(age));
        if (EvidenceValues(age).Any(value => value < TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(age), "An item age cannot be negative.");
        }

        Scarcity = scarcity ?? throw new ArgumentNullException(nameof(scarcity));
        Obtainability = obtainability ?? throw new ArgumentNullException(nameof(obtainability));
        RecommendationStatus = recommendationStatus ?? throw new ArgumentNullException(nameof(recommendationStatus));
        RecommendationVersion = Required(recommendationVersion, nameof(recommendationVersion), 128);
        EvaluatedUtc = evaluatedUtc.Offset == TimeSpan.Zero && evaluatedUtc != default
            ? evaluatedUtc
            : throw new ArgumentException("Evaluation time must be a defined UTC instant.", nameof(evaluatedUtc));
    }

    public string ItemKey { get; }
    public string CanonicalItemId { get; }
    public string ContainerPath { get; }
    public GridCellAddress Anchor { get; }
    public StashPlanGroup Group { get; }
    public IReadOnlyList<StashManualOperation> Operations { get; }
    public IReadOnlyList<string> ReasonCodes { get; }
    public EvidencedValue<long?> FleaFeeRoubles { get; }
    public EvidencedValue<long?> NetValueRoubles { get; }
    public EvidencedValue<long?> ValuePerSquareRoubles { get; }
    public EvidencedValue<TimeSpan?> Age { get; }
    public EvidencedValue<string> Scarcity { get; }
    public EvidencedValue<string> Obtainability { get; }
    public ResultStatus RecommendationStatus { get; }
    public string RecommendationVersion { get; }
    public DateTimeOffset EvaluatedUtc { get; }

    private static ReadOnlyCollection<StashManualOperation> CopyEnums(
        IReadOnlyList<StashManualOperation> values,
        int maximum,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximum || values.Any(value => !Enum.IsDefined(value)))
        {
            throw new ArgumentException("Manual operations are invalid or exceed their bound.", parameterName);
        }

        var copy = values.Distinct().ToArray();
        return Array.AsReadOnly(copy);
    }

    private static ReadOnlyCollection<string> CopyReasons(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > StashScanBounds.MaximumReasonsPerItem)
        {
            throw new ArgumentException("Recommendation reasons exceed their bound.", parameterName);
        }

        return Array.AsReadOnly(values
            .Select(value => Required(value, parameterName, 128))
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }

    private static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static IEnumerable<T> EvidenceValues<T>(EvidencedValue<T?> value)
        where T : struct
    {
        if (value.Value is { } current)
        {
            yield return current;
        }

        foreach (var candidate in value.Candidates)
        {
            if (candidate.Value is { } possible)
            {
                yield return possible;
            }
        }

        foreach (var correction in value.Corrections)
        {
            if (correction.OriginalValue is { } original)
            {
                yield return original;
            }

            if (correction.CorrectedValue is { } corrected)
            {
                yield return corrected;
            }
        }
    }
}

public sealed record StashOrganizationPlan
{
    public StashOrganizationPlan(
        string planId,
        string snapshotId,
        long revision,
        DateTimeOffset generatedUtc,
        IReadOnlyList<StashOrganizationItem> items)
    {
        PlanId = Required(planId, nameof(planId));
        SnapshotId = Required(snapshotId, nameof(snapshotId));
        Revision = revision >= 1 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
        GeneratedUtc = generatedUtc.Offset == TimeSpan.Zero && generatedUtc != default
            ? generatedUtc
            : throw new ArgumentException("Generation time must be a defined UTC instant.", nameof(generatedUtc));
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > StashScanBounds.MaximumPlanItems)
        {
            throw new ArgumentException("A stash plan exceeds its item bound.", nameof(items));
        }

        var copy = items
            .Select(item => item ?? throw new ArgumentException("Plan items cannot contain null.", nameof(items)))
            .ToArray();
        if (copy.Select(item => item.ItemKey).Distinct(StringComparer.Ordinal).Count() != copy.Length)
        {
            throw new ArgumentException("Plan item keys must be unique.", nameof(items));
        }

        Items = Array.AsReadOnly(copy);
    }

    public string PlanId { get; }
    public string SnapshotId { get; }
    public long Revision { get; }
    public DateTimeOffset GeneratedUtc { get; }
    public IReadOnlyList<StashOrganizationItem> Items { get; }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= 256
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>A bounded comparison whose states distinguish absence from uncertain evidence.</summary>
public sealed record StashSnapshotComparison
{
    public StashSnapshotComparison(
        string previousSnapshotId,
        string currentSnapshotId,
        DateTimeOffset comparedUtc,
        ResultStatus status,
        IReadOnlyList<StashSnapshotChange> changes)
    {
        PreviousSnapshotId = Required(previousSnapshotId, nameof(previousSnapshotId));
        CurrentSnapshotId = Required(currentSnapshotId, nameof(currentSnapshotId));
        ComparedUtc = comparedUtc.Offset == TimeSpan.Zero && comparedUtc != default
            ? comparedUtc
            : throw new ArgumentException("Comparison time must be a defined UTC instant.", nameof(comparedUtc));
        Status = status ?? throw new ArgumentNullException(nameof(status));
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count > StashScanBounds.MaximumSnapshotChanges)
        {
            throw new ArgumentException("Snapshot changes exceed their bound.", nameof(changes));
        }

        Changes = Array.AsReadOnly(changes
            .Select(change => change ?? throw new ArgumentException("Changes cannot contain null.", nameof(changes)))
            .ToArray());
    }

    public string PreviousSnapshotId { get; }
    public string CurrentSnapshotId { get; }
    public DateTimeOffset ComparedUtc { get; }
    public ResultStatus Status { get; }
    public IReadOnlyList<StashSnapshotChange> Changes { get; }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= 256 ? trimmed : throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>
/// A durable, pixel-free snapshot record. The profile scope prevents holdings from one wipe or
/// mode from satisfying needs in another.
/// </summary>
public sealed record StashSnapshotRecord
{
    public StashSnapshotRecord(
        Guid snapshotId,
        InventoryProfileScope profileScope,
        string dataSnapshotId,
        DateTimeOffset recordedUtc,
        bool isCurrent,
        RecognitionResultEnvelope<StashRecognition> recognition)
    {
        SnapshotId = snapshotId != Guid.Empty
            ? snapshotId
            : throw new ArgumentException("A durable snapshot id is required.", nameof(snapshotId));
        ProfileScope = profileScope ?? throw new ArgumentNullException(nameof(profileScope));
        DataSnapshotId = Required(dataSnapshotId, nameof(dataSnapshotId));
        RecordedUtc = recordedUtc.Offset == TimeSpan.Zero && recordedUtc != default
            ? recordedUtc
            : throw new ArgumentException("Recording time must be a defined UTC instant.", nameof(recordedUtc));
        Recognition = recognition ?? throw new ArgumentNullException(nameof(recognition));
        if (recognition.Header.ContractVersion != V2ContractVersion.Current || recognition.Result.Value is null)
        {
            throw new ArgumentException("A stash record requires a current V2 recognition value.", nameof(recognition));
        }

        if (recordedUtc < recognition.Result.Provenance.ObservedUtc)
        {
            throw new ArgumentException("Recording cannot predate the assembled observation.", nameof(recordedUtc));
        }

        IsCurrent = isCurrent;
    }

    public Guid SnapshotId { get; }
    public InventoryProfileScope ProfileScope { get; }
    public string DataSnapshotId { get; }
    public DateTimeOffset RecordedUtc { get; }
    public bool IsCurrent { get; }
    public RecognitionResultEnvelope<StashRecognition> Recognition { get; }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= 256 ? trimmed : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record StashSnapshotSummary(
    Guid SnapshotId,
    string RecognitionSnapshotId,
    string DataSnapshotId,
    DateTimeOffset RecordedUtc,
    bool IsCurrent,
    ResultStatus Status,
    EvidenceCoverage Coverage)
{
    public Guid SnapshotId { get; } = SnapshotId != Guid.Empty
        ? SnapshotId
        : throw new ArgumentException("A snapshot id is required.", nameof(SnapshotId));

    public string RecognitionSnapshotId { get; } = string.IsNullOrWhiteSpace(RecognitionSnapshotId)
        ? throw new ArgumentException("A recognition snapshot id is required.", nameof(RecognitionSnapshotId))
        : RecognitionSnapshotId.Trim().Length <= 256
            ? RecognitionSnapshotId.Trim()
            : throw new ArgumentOutOfRangeException(nameof(RecognitionSnapshotId));

    public string DataSnapshotId { get; } = string.IsNullOrWhiteSpace(DataSnapshotId)
        ? throw new ArgumentException("A data snapshot id is required.", nameof(DataSnapshotId))
        : DataSnapshotId.Trim().Length <= 256
            ? DataSnapshotId.Trim()
            : throw new ArgumentOutOfRangeException(nameof(DataSnapshotId));

    public DateTimeOffset RecordedUtc { get; } = RecordedUtc.Offset == TimeSpan.Zero && RecordedUtc != default
        ? RecordedUtc
        : throw new ArgumentException("Recording time must be a defined UTC instant.", nameof(RecordedUtc));

    public ResultStatus Status { get; } = Status ?? throw new ArgumentNullException(nameof(Status));

    public EvidenceCoverage Coverage { get; } = Coverage ?? throw new ArgumentNullException(nameof(Coverage));
}

public sealed record StashSnapshotDeleteResult(bool Deleted, Guid? PromotedSnapshotId);

public sealed record StashSnapshotRetentionResult(int MatchedSnapshots, int DeletedSnapshots, bool DryRun)
{
    public int MatchedSnapshots { get; } = MatchedSnapshots >= 0
        ? MatchedSnapshots
        : throw new ArgumentOutOfRangeException(nameof(MatchedSnapshots));

    public int DeletedSnapshots { get; } = DeletedSnapshots >= 0 && DeletedSnapshots <= MatchedSnapshots
        ? DeletedSnapshots
        : throw new ArgumentOutOfRangeException(nameof(DeletedSnapshots));
}

/// <summary>Persistence port over the existing observed-inventory schema.</summary>
public interface IStashSnapshotStore
{
    Task SaveAsync(StashSnapshotRecord snapshot, CancellationToken cancellationToken);

    Task<StashSnapshotRecord?> ReadCurrentAsync(
        InventoryProfileScope scope,
        CancellationToken cancellationToken);

    Task<StashSnapshotRecord?> ReadAsync(
        InventoryProfileScope scope,
        Guid snapshotId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StashSnapshotSummary>> ListAsync(
        InventoryProfileScope scope,
        int maximumCount,
        CancellationToken cancellationToken);

    Task<StashSnapshotDeleteResult> DeleteAsync(
        InventoryProfileScope scope,
        Guid snapshotId,
        CancellationToken cancellationToken);

    Task<StashSnapshotRetentionResult> ApplyRetentionAsync(
        InventoryProfileScope scope,
        DateTimeOffset retainFromUtc,
        bool dryRun,
        CancellationToken cancellationToken);
}

public enum StashReviewActionKind
{
    CorrectItemIdentity = 1,
    CorrectQuantity,
    MergeEntries,
    SplitEntry,
    Pin,
    Ignore,
    Unpin,
    Unignore,
    Rescan,
}

/// <summary>
/// Append-only manual review intent. Applying a correction is a separate consumer responsibility;
/// this command never rewrites the captured evidence or performs an inventory action.
/// </summary>
public sealed record StashReviewCommand
{
    public StashReviewCommand(
        Guid commandId,
        string snapshotId,
        StashReviewActionKind action,
        IReadOnlyList<string> targetItemKeys,
        DateTimeOffset createdUtc,
        string originIdentifier,
        string? correctedItemId = null,
        int? correctedQuantity = null,
        string? reason = null)
    {
        CommandId = commandId != Guid.Empty
            ? commandId
            : throw new ArgumentException("A review command id is required.", nameof(commandId));
        SnapshotId = Required(snapshotId, nameof(snapshotId), 256);
        Action = Enum.IsDefined(action) ? action : throw new ArgumentOutOfRangeException(nameof(action));
        ArgumentNullException.ThrowIfNull(targetItemKeys);
        var targets = targetItemKeys.Select(value => Required(value, nameof(targetItemKeys), 256)).ToArray();
        if (targets.Length is < 1 or > 256 || targets.Distinct(StringComparer.Ordinal).Count() != targets.Length)
        {
            throw new ArgumentException("Review targets must contain 1-256 unique item keys.", nameof(targetItemKeys));
        }

        if (action == StashReviewActionKind.MergeEntries && targets.Length < 2)
        {
            throw new ArgumentException("A merge requires at least two entries.", nameof(targetItemKeys));
        }

        if (action != StashReviewActionKind.MergeEntries && targets.Length != 1)
        {
            throw new ArgumentException("Only merge accepts more than one target.", nameof(targetItemKeys));
        }

        CorrectedItemId = Optional(correctedItemId, nameof(correctedItemId), 256);
        CorrectedQuantity = correctedQuantity is null or >= 1
            ? correctedQuantity
            : throw new ArgumentOutOfRangeException(nameof(correctedQuantity));
        if ((action == StashReviewActionKind.CorrectItemIdentity) != (CorrectedItemId is not null) ||
            (action == StashReviewActionKind.CorrectQuantity) != (CorrectedQuantity is not null))
        {
            throw new ArgumentException("Correction values must match the selected correction action.");
        }

        CreatedUtc = createdUtc.Offset == TimeSpan.Zero && createdUtc != default
            ? createdUtc
            : throw new ArgumentException("Review time must be a defined UTC instant.", nameof(createdUtc));
        OriginIdentifier = Required(originIdentifier, nameof(originIdentifier), 256);
        Reason = Optional(reason, nameof(reason), 1024);
        TargetItemKeys = Array.AsReadOnly(targets);
    }

    public Guid CommandId { get; }
    public string SnapshotId { get; }
    public StashReviewActionKind Action { get; }
    public IReadOnlyList<string> TargetItemKeys { get; }
    public DateTimeOffset CreatedUtc { get; }
    public string OriginIdentifier { get; }
    public string? CorrectedItemId { get; }
    public int? CorrectedQuantity { get; }
    public string? Reason { get; }

    private static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : throw new ArgumentOutOfRangeException(parameterName);
    }

    private static string? Optional(string? value, string parameterName, int maximumLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Required(value, parameterName, maximumLength);
}

public interface IStashReviewCommandSink
{
    Task AppendAsync(StashReviewCommand command, CancellationToken cancellationToken);
}
