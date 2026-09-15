using System.Collections.Immutable;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Application.Services.CaptureSessions;

public readonly record struct CaptureCorrelationId
{
    public CaptureCorrelationId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A capture correlation id is required.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public bool IsDefined => Value != Guid.Empty;

    public static CaptureCorrelationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public enum CaptureDeliveryKind
{
    Desktop = 1,
    PairedDevice,
    Paste,
    Drop,
    Picker,
    WatchedFile,
    ExternalCapture,
    Batch,
}

public enum CaptureQueueDisposition
{
    Accepted = 1,
    Duplicate,
    Rejected,
    Cancelled,
}

public enum CaptureReviewAction
{
    UseDetected = 1,
    UseArmedIntent,
    Redecode,
    RetryCapture,
    Cancel,
}

public enum CaptureSessionNoticeKind
{
    Progress = 1,
    IntentExpired,
    Duplicate,
    QueueRejected,
    ReviewRequired,
    ReviewResolved,
    PixelsReleased,
}

/// <summary>The workspace facts frozen at intake rather than read again after recognition.</summary>
public sealed record CaptureContextMetadata
{
    public CaptureContextMetadata(
        string? activeWorkspace,
        string? activeProfile,
        string? activeMap,
        string? activePlan,
        string? selectedEntity,
        string? priorScan,
        string? initiatingDevice,
        ProfileContext? profileContext = null)
    {
        ActiveWorkspace = Optional(activeWorkspace, nameof(activeWorkspace));
        ActiveProfile = Optional(activeProfile, nameof(activeProfile));
        ActiveMap = Optional(activeMap, nameof(activeMap));
        ActivePlan = Optional(activePlan, nameof(activePlan));
        SelectedEntity = Optional(selectedEntity, nameof(selectedEntity));
        PriorScan = Optional(priorScan, nameof(priorScan));
        InitiatingDevice = Optional(initiatingDevice, nameof(initiatingDevice));
        ProfileContext = profileContext;
        if (profileContext is not null
            && ActiveProfile is not null
            && !string.Equals(
                ActiveProfile,
                profileContext.Identity.ProfileId.ToString("D"),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The profile label must agree with the canonical profile context.",
                nameof(activeProfile));
        }
    }

    public string? ActiveWorkspace { get; }

    public string? ActiveProfile { get; }

    public string? ActiveMap { get; }

    public string? ActivePlan { get; }

    public string? SelectedEntity { get; }

    public string? PriorScan { get; }

    public string? InitiatingDevice { get; }

    /// <summary>
    /// Canonical v2 profile identity and data-snapshot provenance. A legacy display id may be
    /// present without this value during composition migration, but no substitute DTO is made.
    /// </summary>
    public ProfileContext? ProfileContext { get; }

    public static CaptureContextMetadata Empty { get; } = new(null, null, null, null, null, null, null);

    private static string? Optional(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 256
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName, "Capture context values are limited to 256 characters.");
    }
}

public sealed record CaptureGuidance(string Code, string Message)
{
    public string Code { get; } = Required(Code, nameof(Code), 64);

    public string Message { get; } = Required(Message, nameof(Message), 512);

    private static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength
            ? trimmed
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

public sealed record CaptureArmRequest(
    CaptureSessionRequest Request,
    CaptureContextMetadata Context,
    CaptureGuidance Guidance)
{
    public CaptureSessionRequest Request { get; } = Request ?? throw new ArgumentNullException(nameof(Request));

    public CaptureContextMetadata Context { get; } = Context ?? throw new ArgumentNullException(nameof(Context));

    public CaptureGuidance Guidance { get; } = Guidance ?? throw new ArgumentNullException(nameof(Guidance));
}

public sealed record CaptureArmReceipt(bool Accepted, CaptureSessionId SessionId, string Code);

/// <summary>A local source of visible, user-requested pixels. It exposes no input operation.</summary>
public interface ICaptureContentSource : IDisposable
{
    CaptureSourceKind SourceKind { get; }

    ValueTask<CaptureSourceReadResult> ReadAsync(CancellationToken cancellationToken);
}

public sealed record CaptureSourceReadResult(CapturePixelLease? Pixels, bool Retryable, string? DiagnosticCode)
{
    public static CaptureSourceReadResult Success(CapturePixelLease pixels) =>
        new(pixels ?? throw new ArgumentNullException(nameof(pixels)), false, null);

    public static CaptureSourceReadResult Failure(string diagnosticCode, bool retryable = true) =>
        new(null, retryable, Required(diagnosticCode));

    private static string Required(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim();
    }
}

public sealed record CaptureSubmission
{
    public CaptureSubmission(
        CaptureDeliveryKind deliveryKind,
        ICaptureContentSource source,
        CaptureContextMetadata context,
        DateTimeOffset submittedUtc,
        CaptureCorrelationId correlationId,
        CaptureSessionId? sessionId = null,
        bool endSessionAfterReview = true,
        string? batchId = null)
    {
        DeliveryKind = Enum.IsDefined(deliveryKind)
            ? deliveryKind
            : throw new ArgumentOutOfRangeException(nameof(deliveryKind));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        SubmittedUtc = submittedUtc == default
            ? throw new ArgumentOutOfRangeException(nameof(submittedUtc))
            : submittedUtc.ToUniversalTime();
        CorrelationId = correlationId.IsDefined
            ? correlationId
            : throw new ArgumentException("A capture correlation id is required.", nameof(correlationId));
        SessionId = sessionId;
        EndSessionAfterReview = endSessionAfterReview;
        BatchId = string.IsNullOrWhiteSpace(batchId) ? null : batchId.Trim();
        if (BatchId?.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(batchId));
        }

        if (deliveryKind == CaptureDeliveryKind.Batch && BatchId is null)
        {
            throw new ArgumentException("Batch delivery must name its batch.", nameof(batchId));
        }
    }

    public CaptureDeliveryKind DeliveryKind { get; }

    public ICaptureContentSource Source { get; }

    public CaptureContextMetadata Context { get; }

    public DateTimeOffset SubmittedUtc { get; }

    public CaptureCorrelationId CorrelationId { get; }

    public CaptureSessionId? SessionId { get; }

    public bool EndSessionAfterReview { get; }

    public string? BatchId { get; }
}

public sealed record CaptureQueueReceipt(
    long IntakeSequence,
    CaptureQueueDisposition Disposition,
    CaptureCorrelationId CorrelationId,
    DateTimeOffset ChangedUtc,
    string Code);

/// <summary>A recognizer-owned, non-persisting analysis seam used before review.</summary>
public interface ICaptureSessionPipeline
{
    Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken);
}

public sealed record CaptureAnalysisRequest(
    CaptureSessionId SessionId,
    string ArtifactId,
    int CaptureOrdinal,
    ScanIntent RequestedIntent,
    CaptureContextMetadata Context,
    CapturedImage Image,
    CaptureCorrelationId CorrelationId,
    int DecodeRevision);

public sealed record CaptureAnalysis(
    string ResultId,
    RecognizedContext? DetectedContext,
    bool IsAmbiguous,
    bool IsAvailable,
    string? DiagnosticCode,
    Confidence Confidence)
{
    public string ResultId { get; } = string.IsNullOrWhiteSpace(ResultId)
        ? throw new ArgumentException("A result id is required.", nameof(ResultId))
        : ResultId.Trim();

    public string? DiagnosticCode { get; } = string.IsNullOrWhiteSpace(DiagnosticCode)
        ? null
        : DiagnosticCode.Trim();
}

public sealed record CaptureReviewRequest(
    CaptureSessionId SessionId,
    string ArtifactId,
    int CaptureOrdinal,
    ScanIntent RequestedIntent,
    RecognizedContext? DetectedContext,
    bool HasIntentDisagreement,
    int DecodeRevision,
    DateTimeOffset ExpiresUtc,
    CaptureCorrelationId CorrelationId);

public sealed record CaptureCorrection(
    CaptureReviewAction Action,
    ScanIntent RequestedIntent,
    RecognizedContext? DetectedContext,
    DateTimeOffset CorrectedUtc,
    string Origin);

public sealed record CaptureArtifactSnapshot(
    string ArtifactId,
    int CaptureOrdinal,
    CaptureDeliveryKind DeliveryKind,
    CaptureCorrelationId CorrelationId,
    CaptureContextMetadata Context,
    CaptureSourceKind SourceKind,
    DateTimeOffset CapturedUtc,
    DateTimeOffset SubmittedUtc,
    string? BatchId,
    Confidence Confidence,
    CaptureAnalysis? Analysis,
    CaptureReviewRequest? Review,
    ImmutableArray<CaptureCorrection> Corrections,
    CaptureQueueDisposition Disposition,
    bool PixelsRetained,
    int DecodeAttempts,
    int DecodeRevision,
    string? DiagnosticCode);

public sealed record CaptureSessionState(
    CaptureSessionRequest Request,
    CaptureContextMetadata Context,
    CaptureGuidance Guidance,
    CaptureSessionSnapshot Snapshot,
    ImmutableArray<CaptureArtifactSnapshot> Artifacts,
    bool IntentClaimed,
    bool CancellationRequested,
    bool IsTerminal);

public sealed record CaptureTimingSnapshot(
    long IntakeSequence,
    CaptureCorrelationId CorrelationId,
    CaptureSessionId? SessionId,
    string? ArtifactId,
    long QueueMilliseconds,
    long DecodeMilliseconds,
    long AnalysisMilliseconds,
    long ReviewMilliseconds);

public sealed record CaptureSessionNotice(
    long Sequence,
    CaptureSessionNoticeKind Kind,
    DateTimeOffset ChangedUtc,
    CaptureCorrelationId? CorrelationId,
    CaptureSessionId? SessionId,
    string? ArtifactId,
    string Code);

public sealed record CaptureSessionServiceSnapshot(
    bool IsStopping,
    int QueueDepth,
    int QueueCapacity,
    long Accepted,
    long Duplicate,
    long Rejected,
    long PixelsInUse,
    ImmutableArray<CaptureSessionState> Sessions,
    ImmutableArray<CaptureTimingSnapshot> Timings,
    ImmutableArray<CaptureSessionNotice> Notices)
{
    public static CaptureSessionServiceSnapshot Empty { get; } = new(
        false,
        0,
        0,
        0,
        0,
        0,
        0,
        [],
        [],
        []);
}

public sealed class CaptureReviewRequestedEventArgs(CaptureReviewRequest review) : EventArgs
{
    public CaptureReviewRequest Review { get; } = review ?? throw new ArgumentNullException(nameof(review));
}

public sealed class CaptureAcceptedEventArgs(
    CaptureSessionId sessionId,
    string artifactId,
    CaptureAnalysis analysis,
    CaptureContextMetadata context,
    CaptureCorrelationId correlationId,
    CaptureSourceKind sourceKind,
    DateTimeOffset capturedUtc,
    DateTimeOffset submittedUtc,
    string? batchId,
    CaptureReviewAction decision,
    ScanIntent effectiveIntent,
    CaptureCorrection correction) : EventArgs
{
    public CaptureSessionId SessionId { get; } = sessionId;

    public string ArtifactId { get; } = artifactId;

    public CaptureAnalysis Analysis { get; } = analysis ?? throw new ArgumentNullException(nameof(analysis));

    public CaptureContextMetadata Context { get; } = context ?? throw new ArgumentNullException(nameof(context));

    public CaptureCorrelationId CorrelationId { get; } = correlationId;

    public CaptureSourceKind SourceKind { get; } = Enum.IsDefined(sourceKind)
        ? sourceKind
        : throw new ArgumentOutOfRangeException(nameof(sourceKind));

    public DateTimeOffset CapturedUtc { get; } = capturedUtc == default
        ? throw new ArgumentOutOfRangeException(nameof(capturedUtc))
        : capturedUtc.ToUniversalTime();

    public DateTimeOffset SubmittedUtc { get; } = submittedUtc == default
        ? throw new ArgumentOutOfRangeException(nameof(submittedUtc))
        : submittedUtc.ToUniversalTime();

    public string? BatchId { get; } = string.IsNullOrWhiteSpace(batchId) ? null : batchId.Trim();

    public CaptureReviewAction Decision { get; } = Enum.IsDefined(decision)
        ? decision
        : throw new ArgumentOutOfRangeException(nameof(decision));

    /// <summary>
    /// The intent consumers must apply after review. For UseArmedIntent this is the explicit
    /// user correction, not the detector's disagreed context.
    /// </summary>
    public ScanIntent EffectiveIntent { get; } = Enum.IsDefined(effectiveIntent)
        ? effectiveIntent
        : throw new ArgumentOutOfRangeException(nameof(effectiveIntent));

    public CaptureCorrection Correction { get; } = correction ?? throw new ArgumentNullException(nameof(correction));
}

public interface ICaptureSessionService : IAsyncDisposable
{
    event EventHandler? Changed;

    event EventHandler<CaptureReviewRequestedEventArgs>? ReviewRequested;

    event EventHandler<CaptureAcceptedEventArgs>? Accepted;

    CaptureSessionServiceSnapshot Snapshot { get; }

    CaptureArmReceipt Arm(CaptureArmRequest request);

    ValueTask<CaptureQueueReceipt> EnqueueAsync(CaptureSubmission submission, CancellationToken cancellationToken);

    bool TryReview(CaptureSessionId sessionId, string artifactId, CaptureReviewAction action, string origin);

    bool Cancel(CaptureSessionId sessionId, string origin);
}
