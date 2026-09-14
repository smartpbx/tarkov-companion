using System.Text.Json.Serialization;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Abstractions.V2;

public enum ScanIntent
{
    Auto = 1,
    Loot,
    Stash,
    Ammo,
    Keys,
    QuestItems,
    ExtractsAndMap,
    HealthAndCharacter,
    Flea,
}

public enum CaptureSourceKind
{
    GameWrittenScreenshot = 1,
    UserSelectedImage,
    ClipboardImage,
    ExternalVisiblePixelCapture,
}

/// <summary>
/// Session stages in pipeline order. <see cref="Armed"/> and <see cref="AwaitingCapture"/>
/// belong to the session; <see cref="Settling"/> through <see cref="AwaitingReview"/> belong to
/// one capture; the three terminal stages end either one capture or, without an artifact, the
/// whole session.
/// </summary>
public enum CaptureSessionStage
{
    Armed = 1,
    AwaitingCapture,
    Settling,
    Decoding,
    DetectingContext,
    DetectingRegions,
    Matching,
    EnrichingProfile,
    Recommending,
    AwaitingReview,
    Complete,
    Cancelled,
    Failed,
}

public readonly record struct CaptureSessionId
{
    // System.Text.Json builds a struct through its implicit parameterless constructor unless told
    // otherwise, which silently round-tripped ids to Guid.Empty and addresses to (0, 0).
    [JsonConstructor]
    public CaptureSessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Capture session id cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }
}

public sealed record CaptureSessionRequest(
    CaptureSessionId SessionId,
    ScanIntent Intent,
    WorkspaceOrigin Origin,
    DateTimeOffset RequestedUtc,
    string? ProfileId = null,
    string? MapId = null,
    DateTimeOffset? ExpiresUtc = null)
{
    public CaptureSessionId SessionId { get; } = V2ContractGuard.Defined(SessionId, nameof(SessionId));

    public ScanIntent Intent { get; } = V2ContractGuard.Defined(Intent, nameof(Intent));

    public WorkspaceOrigin Origin { get; } = V2ContractGuard.NotNull(Origin, nameof(Origin));

    public DateTimeOffset RequestedUtc { get; } = V2ContractGuard.Utc(RequestedUtc, nameof(RequestedUtc));

    public string? ProfileId { get; } = V2ContractGuard.Optional(ProfileId);

    public string? MapId { get; } = V2ContractGuard.Optional(MapId);

    public DateTimeOffset? ExpiresUtc { get; } = ExpiresUtc is not { } expires
        ? null
        : expires == default || expires < RequestedUtc
            ? throw new ArgumentOutOfRangeException(nameof(ExpiresUtc), "A capture request cannot expire before it was made.")
            : expires.ToUniversalTime();
}

public sealed record CaptureStageProgress
{
    public CaptureStageProgress(
        CaptureSessionId sessionId,
        long sequence,
        CaptureSessionStage stage,
        DateTimeOffset changedUtc,
        string? artifactId = null,
        int? captureOrdinal = null,
        int? percent = null,
        string? detail = null)
    {
        SessionId = V2ContractGuard.Defined(sessionId, nameof(sessionId));
        Sequence = sequence >= 0 ? sequence : throw new ArgumentOutOfRangeException(nameof(sequence));
        Stage = V2ContractGuard.Defined(stage, nameof(stage));
        ChangedUtc = V2ContractGuard.Utc(changedUtc, nameof(changedUtc));
        ArtifactId = V2ContractGuard.Optional(artifactId);
        CaptureOrdinal = captureOrdinal is null or >= 0
            ? captureOrdinal
            : throw new ArgumentOutOfRangeException(nameof(captureOrdinal));
        Percent = percent is null or (>= 0 and <= 100) ? percent : throw new ArgumentOutOfRangeException(nameof(percent));
        Detail = V2ContractGuard.Optional(detail);

        if ((ArtifactId is null) != (CaptureOrdinal is null))
        {
            throw new ArgumentException("An artifact and its capture ordinal travel together.", nameof(captureOrdinal));
        }

        if (IsSessionStage(stage) && ArtifactId is not null)
        {
            throw new ArgumentException($"{stage} belongs to the session, not a capture.", nameof(artifactId));
        }

        if (IsCaptureStage(stage) && ArtifactId is null)
        {
            throw new ArgumentException($"{stage} belongs to one capture and must name its artifact.", nameof(artifactId));
        }
    }

    public CaptureSessionId SessionId { get; }

    public long Sequence { get; }

    public CaptureSessionStage Stage { get; }

    public DateTimeOffset ChangedUtc { get; }

    public string? ArtifactId { get; }

    /// <summary>The capture's position in the session's ordered queue, starting at zero.</summary>
    public int? CaptureOrdinal { get; }

    public int? Percent { get; }

    public string? Detail { get; }

    internal static bool IsSessionStage(CaptureSessionStage stage) =>
        stage is CaptureSessionStage.Armed or CaptureSessionStage.AwaitingCapture;

    internal static bool IsCaptureStage(CaptureSessionStage stage) =>
        stage is >= CaptureSessionStage.Settling and <= CaptureSessionStage.AwaitingReview;

    internal static bool IsTerminal(CaptureSessionStage stage) =>
        stage is CaptureSessionStage.Complete or CaptureSessionStage.Cancelled or CaptureSessionStage.Failed;
}

/// <summary>
/// An ordered session history. Each capture moves forward through its stages (a decode retry
/// may return from Decoding to Settling) and nothing follows its terminal stage. A session
/// terminal stage is final for everything. Capture ordinals number the queue from zero without
/// gaps, in the order captures first appear. The status cannot claim more than the history shows.
/// </summary>
public sealed record CaptureSessionSnapshot
{
    public CaptureSessionSnapshot(
        CaptureSessionRequest request,
        IReadOnlyList<CaptureStageProgress> progress,
        ResultStatus status)
    {
        Request = V2ContractGuard.NotNull(request, nameof(request));
        Progress = V2ContractGuard.List(progress, nameof(progress));
        Status = V2ContractGuard.NotNull(status, nameof(status));

        // Ordinals are contiguous from zero, so the ordinal is the capture's index here, and the
        // unfinished count is kept as it changes rather than recounted for every session update.
        var sessionStage = (CaptureSessionStage?)null;
        var captures = new List<(string ArtifactId, CaptureSessionStage Stage)>();
        var artifacts = new HashSet<string>(StringComparer.Ordinal);
        var unfinished = 0;
        for (var index = 0; index < Progress.Count; index++)
        {
            var item = Progress[index];
            if (item.SessionId != request.SessionId)
            {
                throw new ArgumentException("Every progress update must belong to the capture session.", nameof(progress));
            }

            if (item.Sequence != index)
            {
                throw new ArgumentException("Progress sequences must be contiguous and start at zero.", nameof(progress));
            }

            var previousUtc = index == 0 ? request.RequestedUtc : Progress[index - 1].ChangedUtc;
            if (item.ChangedUtc < previousUtc)
            {
                throw new ArgumentException("Progress cannot predate the request or move backwards.", nameof(progress));
            }

            if (sessionStage is { } ended && CaptureStageProgress.IsTerminal(ended))
            {
                throw new ArgumentException("Nothing follows the session's terminal stage.", nameof(progress));
            }

            if (item.CaptureOrdinal is not { } ordinal)
            {
                if (sessionStage is { } current && item.Stage < current)
                {
                    throw new ArgumentException("Session stages cannot move backwards.", nameof(progress));
                }

                if (item.Stage == CaptureSessionStage.Complete && unfinished > 0)
                {
                    throw new ArgumentException("A session cannot complete while a capture is unfinished.", nameof(progress));
                }

                sessionStage = item.Stage;
                continue;
            }

            if (ordinal < captures.Count)
            {
                var capture = captures[ordinal];
                if (!string.Equals(capture.ArtifactId, item.ArtifactId, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A capture ordinal names exactly one artifact.", nameof(progress));
                }

                var retry = capture.Stage == CaptureSessionStage.Decoding && item.Stage == CaptureSessionStage.Settling;
                if (CaptureStageProgress.IsTerminal(capture.Stage) || (item.Stage < capture.Stage && !retry))
                {
                    throw new ArgumentException("A capture's stages move forward and end at its terminal stage.", nameof(progress));
                }

                if (CaptureStageProgress.IsTerminal(item.Stage))
                {
                    unfinished--;
                }

                captures[ordinal] = (capture.ArtifactId, item.Stage);
                continue;
            }

            // The queue is numbered from zero with no holes, and a capture first appears in queue
            // order. Uniqueness alone let one capture arrive as ordinal 99, which reads as a
            // normal history while ninety-nine queued captures vanished without a single stage.
            if (ordinal != captures.Count)
            {
                throw new ArgumentException(
                    $"Capture ordinals start at zero and enter the queue in order; expected {captures.Count}, not {ordinal}.",
                    nameof(progress));
            }

            if (!artifacts.Add(item.ArtifactId!))
            {
                throw new ArgumentException("An artifact belongs to exactly one capture ordinal.", nameof(progress));
            }

            if (!CaptureStageProgress.IsTerminal(item.Stage))
            {
                unfinished++;
            }

            captures.Add((item.ArtifactId!, item.Stage));
        }

        var allowed = sessionStage switch
        {
            CaptureSessionStage.Failed => status.Completeness == ResultCompleteness.Unavailable,
            CaptureSessionStage.Cancelled => status.Completeness != ResultCompleteness.Complete,
            CaptureSessionStage.Complete => status.Completeness is ResultCompleteness.Partial or ResultCompleteness.Complete,
            _ => status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Partial,
        };

        if (!allowed)
        {
            throw new ArgumentException(
                $"Status {status.Completeness} is inconsistent with session stage {sessionStage?.ToString() ?? "none"}.",
                nameof(status));
        }
    }

    public CaptureSessionRequest Request { get; }

    public IReadOnlyList<CaptureStageProgress> Progress { get; }

    public ResultStatus Status { get; }
}

public sealed record VisibleCaptureArtifact(
    string ArtifactId,
    CaptureSourceKind SourceKind,
    int PixelWidth,
    int PixelHeight,
    DateTimeOffset CapturedUtc,
    EvidenceProvenance Provenance)
{
    public string ArtifactId { get; } = V2ContractGuard.Required(ArtifactId, nameof(ArtifactId));

    public CaptureSourceKind SourceKind { get; } = V2ContractGuard.Defined(SourceKind, nameof(SourceKind));

    public int PixelWidth { get; } = PixelWidth > 0
        ? PixelWidth
        : throw new ArgumentOutOfRangeException(nameof(PixelWidth));

    public int PixelHeight { get; } = PixelHeight > 0
        ? PixelHeight
        : throw new ArgumentOutOfRangeException(nameof(PixelHeight));

    public DateTimeOffset CapturedUtc { get; } = V2ContractGuard.Utc(CapturedUtc, nameof(CapturedUtc));

    public EvidenceProvenance Provenance { get; } =
        V2ContractGuard.NotNull(Provenance, nameof(Provenance)).ObservedUtc < CapturedUtc
            ? throw new ArgumentException("A capture cannot be acquired before it was taken.", nameof(Provenance))
            : Provenance;
}

/// <summary>The reviewed game-facing seam: observe visible pixels only after a user request.</summary>
public interface IUserInitiatedVisibleCaptureSource
{
    Task<VisibleCaptureArtifact> CaptureAsync(
        CaptureSessionRequest request,
        CancellationToken cancellationToken);
}
