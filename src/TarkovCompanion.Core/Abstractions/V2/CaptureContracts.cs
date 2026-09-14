using System.Text.Json.Serialization;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Abstractions.V2;

public enum ScanIntent
{
    Auto,
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
    GameWrittenScreenshot,
    UserSelectedImage,
    ClipboardImage,
    ExternalVisiblePixelCapture,
}

public enum CaptureSessionStage
{
    Armed,
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

    public WorkspaceOrigin Origin { get; } = V2ContractGuard.NotNull(Origin, nameof(Origin));

    public DateTimeOffset RequestedUtc { get; } = V2ContractGuard.Utc(RequestedUtc, nameof(RequestedUtc));

    public DateTimeOffset? ExpiresUtc { get; } = ExpiresUtc is not { } expires
        ? null
        : expires == default || expires < RequestedUtc
            ? throw new ArgumentOutOfRangeException(nameof(ExpiresUtc), "A capture request cannot expire before it was made.")
            : expires.ToUniversalTime();
}

public sealed record CaptureStageProgress(
    CaptureSessionId SessionId,
    long Sequence,
    CaptureSessionStage Stage,
    DateTimeOffset ChangedUtc,
    int? Percent = null,
    string? Detail = null)
{
    public CaptureSessionId SessionId { get; } = V2ContractGuard.Defined(SessionId, nameof(SessionId));

    public long Sequence { get; } = Sequence >= 0
        ? Sequence
        : throw new ArgumentOutOfRangeException(nameof(Sequence));

    public DateTimeOffset ChangedUtc { get; } = ChangedUtc == default
        ? throw new ArgumentException("A UTC timestamp is required.", nameof(ChangedUtc))
        : ChangedUtc.ToUniversalTime();

    public int? Percent { get; } = Percent is null or >= 0 and <= 100
        ? Percent
        : throw new ArgumentOutOfRangeException(nameof(Percent));
}

public sealed record CaptureSessionSnapshot
{
    public CaptureSessionSnapshot(
        CaptureSessionRequest request,
        IReadOnlyList<CaptureStageProgress> progress,
        ResultStatus status)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(status);
        progress = V2ContractGuard.List(progress, nameof(progress));

        if (progress.Any(item => item.SessionId != request.SessionId))
        {
            throw new ArgumentException("Every progress update must belong to the capture session.", nameof(progress));
        }

        for (var index = 0; index < progress.Count; index++)
        {
            if (progress[index].Sequence != index)
            {
                throw new ArgumentException("Progress sequences must be contiguous and start at zero.", nameof(progress));
            }

            if (index > 0 && progress[index].ChangedUtc < progress[index - 1].ChangedUtc)
            {
                throw new ArgumentException("Progress timestamps cannot move backwards.", nameof(progress));
            }
        }

        Request = request;
        Progress = progress.ToArray();
        Status = status;
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

    public EvidenceProvenance Provenance { get; } = V2ContractGuard.NotNull(Provenance, nameof(Provenance));

    public int PixelWidth { get; } = PixelWidth > 0
        ? PixelWidth
        : throw new ArgumentOutOfRangeException(nameof(PixelWidth));

    public int PixelHeight { get; } = PixelHeight > 0
        ? PixelHeight
        : throw new ArgumentOutOfRangeException(nameof(PixelHeight));

    public DateTimeOffset CapturedUtc { get; } = CapturedUtc == default
        ? throw new ArgumentException("A UTC timestamp is required.", nameof(CapturedUtc))
        : CapturedUtc.ToUniversalTime();
}

/// <summary>The reviewed game-facing seam: observe visible pixels only after a user request.</summary>
public interface IUserInitiatedVisibleCaptureSource
{
    Task<VisibleCaptureArtifact> CaptureAsync(
        CaptureSessionRequest request,
        CancellationToken cancellationToken);
}
