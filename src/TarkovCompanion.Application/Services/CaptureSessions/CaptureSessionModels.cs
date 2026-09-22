using System.Collections.Immutable;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;

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

public enum CaptureArtifactDisposition
{
    Pending = 1,
    Accepted,
    NoChange,
    RetryRequested,
}

public enum CaptureHandoffDisposition
{
    NotAttempted = 1,
    Pending,
    DurablyAccepted,
    Rejected,
    AcknowledgementUnknown,
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
    NoChange,
    HandoffResolved,
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
        var sourceKind = source.SourceKind;
        SourceKind = Enum.IsDefined(sourceKind)
            ? sourceKind
            : throw new ArgumentOutOfRangeException(nameof(source), "The capture source kind is undefined.");
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

    /// <summary>The source classification frozen before admission takes ownership.</summary>
    public CaptureSourceKind SourceKind { get; }

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

/// <summary>
/// One catalog item a frame was read as, with the alternates it was chosen over.
/// </summary>
/// <remarks>
/// Pixel-free, like everything else on <see cref="CaptureAnalysis"/>: a name, an id, how sure the
/// resolver was, and the line it came from. The pixels are released the moment analysis returns,
/// so an identity that does not ride along here cannot be recovered afterwards — which is why the
/// Intel handoff could not say what a screenshot showed before this existed.
/// </remarks>
/// <param name="CanonicalId">The catalog id, which is what Intel is addressed by.</param>
/// <param name="DisplayName">The name to show, as the catalog spells it.</param>
/// <param name="Confidence">How sure the resolver was, unchanged from the recognizer.</param>
/// <param name="Evidence">The text it matched and the engine that read it.</param>
public sealed record CaptureIdentifiedItem(
    string CanonicalId,
    string DisplayName,
    Confidence Confidence,
    string Evidence);

/// <summary>
/// One row of the flea market screen the player opened and photographed: a price, and how many
/// units the offer holds where that was legible.
/// </summary>
/// <remarks>
/// Pixel-free, like the rest of <see cref="CaptureAnalysis"/>. It is read from a screenshot the
/// player took and nothing else; the companion never asks the market for anything.
/// </remarks>
public sealed record CaptureFleaListing(long PriceRoubles, int? Quantity, Confidence Confidence, string? SourceText);

public sealed record CaptureAnalysis(
    string ResultId,
    RecognizedContext? DetectedContext,
    bool IsAmbiguous,
    bool IsAvailable,
    string? DiagnosticCode,
    Confidence Confidence,
    GridReconstructionRequest? Grid = null,
    // Additive and defaulted: every existing producer and consumer predates it, and a capture
    // whose screen holds no single item legitimately identifies nothing.
    IReadOnlyList<CaptureIdentifiedItem>? Identified = null,
    // The player's own backpack where the same frame showed it: the in-raid Gear screen's
    // backpack grid, found by GearScreenLayoutReader. The Loot Scan plans a fit against it.
    GridReconstructionRequest? CarriedGrid = null,
    // The visible rows of a flea screen, where the frame was one. V1 parsed these and reduced
    // them to a count; V2 never parsed them at all.
    IReadOnlyList<CaptureFleaListing>? FleaListings = null)
{
    /// <summary>The flea rows this frame showed, top to bottom; empty when it was not a flea screen.</summary>
    public IReadOnlyList<CaptureFleaListing> FleaListings { get; } = FleaListings ?? [];

    public string ResultId { get; } = string.IsNullOrWhiteSpace(ResultId)
        ? throw new ArgumentException("A result id is required.", nameof(ResultId))
        : ResultId.Trim();

    public string? DiagnosticCode { get; } = string.IsNullOrWhiteSpace(DiagnosticCode)
        ? null
        : DiagnosticCode.Trim();

    /// <summary>What the frame was read as, most confident first; empty when nothing matched.</summary>
    public IReadOnlyList<CaptureIdentifiedItem> Identified { get; } = Identified ?? [];
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
    int DecodeRevision,
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
    bool PixelsRetained,
    int DecodeAttempts,
    int DecodeRevision,
    string? DiagnosticCode,
    EvidenceProvenance Provenance,
    CaptureArtifactDisposition Disposition,
    CaptureHandoffDisposition HandoffDisposition);

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

/// <summary>
/// Pixel-free reviewed result offered to the durable consumer. A consumer may report
/// <see cref="CaptureHandoffDisposition.DurablyAccepted"/> only after its own acceptance
/// boundary has succeeded; an event notification is not that boundary.
/// </summary>
public sealed class CaptureHandoffRequest
{
    public CaptureHandoffRequest(
        CaptureSessionId sessionId,
        string artifactId,
        CaptureAnalysis analysis,
        CaptureContextMetadata context,
        CaptureCorrelationId correlationId,
        CaptureSourceKind sourceKind,
        DateTimeOffset capturedUtc,
        DateTimeOffset submittedUtc,
        string? batchId,
        CaptureDeliveryKind deliveryKind,
        EvidenceProvenance provenance,
        int decodeRevision,
        CaptureReviewAction decision,
        ScanIntent effectiveIntent,
        CaptureCorrection correction)
    {
        SessionId = sessionId.Value != Guid.Empty
            ? sessionId
            : throw new ArgumentException("A capture session id is required.", nameof(sessionId));
        ArtifactId = Required(artifactId, nameof(artifactId), 128);
        Analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        CorrelationId = correlationId.IsDefined
            ? correlationId
            : throw new ArgumentException("A capture correlation id is required.", nameof(correlationId));
        SourceKind = Enum.IsDefined(sourceKind)
            ? sourceKind
            : throw new ArgumentOutOfRangeException(nameof(sourceKind));
        CapturedUtc = capturedUtc == default
            ? throw new ArgumentOutOfRangeException(nameof(capturedUtc))
            : capturedUtc.ToUniversalTime();
        SubmittedUtc = submittedUtc == default
            ? throw new ArgumentOutOfRangeException(nameof(submittedUtc))
            : submittedUtc.ToUniversalTime();
        BatchId = Optional(batchId, nameof(batchId), 128);
        DeliveryKind = Enum.IsDefined(deliveryKind)
            ? deliveryKind
            : throw new ArgumentOutOfRangeException(nameof(deliveryKind));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        if (Provenance.ObservedUtc < CapturedUtc)
        {
            throw new ArgumentException(
                "Capture provenance cannot predate the captured pixels.",
                nameof(provenance));
        }

        DecodeRevision = decodeRevision >= 0
            ? decodeRevision
            : throw new ArgumentOutOfRangeException(nameof(decodeRevision));
        Decision = Enum.IsDefined(decision)
            ? decision
            : throw new ArgumentOutOfRangeException(nameof(decision));
        EffectiveIntent = Enum.IsDefined(effectiveIntent)
            ? effectiveIntent
            : throw new ArgumentOutOfRangeException(nameof(effectiveIntent));
        Correction = correction ?? throw new ArgumentNullException(nameof(correction));
        if (Correction.DecodeRevision != DecodeRevision)
        {
            throw new ArgumentException(
                "The correction must target the handed-off decode revision.",
                nameof(correction));
        }
    }

    public CaptureSessionId SessionId { get; }

    public string ArtifactId { get; }

    public CaptureAnalysis Analysis { get; }

    public CaptureContextMetadata Context { get; }

    public CaptureCorrelationId CorrelationId { get; }

    public CaptureSourceKind SourceKind { get; }

    public DateTimeOffset CapturedUtc { get; }

    public DateTimeOffset SubmittedUtc { get; }

    public string? BatchId { get; }

    public CaptureDeliveryKind DeliveryKind { get; }

    public EvidenceProvenance Provenance { get; }

    public int DecodeRevision { get; }

    public CaptureReviewAction Decision { get; }

    /// <summary>
    /// The intent consumers must apply after review. For UseArmedIntent this is the explicit
    /// user correction, not the detector's disagreed context.
    /// </summary>
    public ScanIntent EffectiveIntent { get; }

    public CaptureCorrection Correction { get; }

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

public sealed record CaptureHandoffResult
{
    public CaptureHandoffResult(CaptureHandoffDisposition disposition, string code)
    {
        if (disposition is not (CaptureHandoffDisposition.DurablyAccepted
            or CaptureHandoffDisposition.Rejected
            or CaptureHandoffDisposition.AcknowledgementUnknown))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        var trimmed = code.Trim();
        if (trimmed.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(code));
        }

        Disposition = disposition;
        Code = trimmed;
    }

    public CaptureHandoffDisposition Disposition { get; }

    public string Code { get; }

    public static CaptureHandoffResult Accepted { get; } =
        new(CaptureHandoffDisposition.DurablyAccepted, "capture_handoff_accepted");
}

public interface ICaptureResultHandoff
{
    ValueTask<CaptureHandoffResult> AcceptAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken);
}

public sealed class CaptureAcceptedEventArgs(CaptureHandoffRequest request) : EventArgs
{
    public CaptureHandoffRequest Request { get; } = request ?? throw new ArgumentNullException(nameof(request));

    public CaptureSessionId SessionId => Request.SessionId;

    public string ArtifactId => Request.ArtifactId;

    public CaptureAnalysis Analysis => Request.Analysis;

    public CaptureContextMetadata Context => Request.Context;

    public CaptureCorrelationId CorrelationId => Request.CorrelationId;

    public CaptureSourceKind SourceKind => Request.SourceKind;

    public CaptureDeliveryKind DeliveryKind => Request.DeliveryKind;

    public DateTimeOffset CapturedUtc => Request.CapturedUtc;

    public DateTimeOffset SubmittedUtc => Request.SubmittedUtc;

    public string? BatchId => Request.BatchId;

    public EvidenceProvenance Provenance => Request.Provenance;

    public int DecodeRevision => Request.DecodeRevision;

    public CaptureReviewAction Decision => Request.Decision;

    public ScanIntent EffectiveIntent => Request.EffectiveIntent;

    public CaptureCorrection Correction => Request.Correction;

    public CaptureHandoffDisposition HandoffDisposition => CaptureHandoffDisposition.DurablyAccepted;
}

public interface ICaptureSessionService : IAsyncDisposable
{
    event EventHandler? Changed;

    event EventHandler<CaptureReviewRequestedEventArgs>? ReviewRequested;

    event EventHandler<CaptureAcceptedEventArgs>? Accepted;

    CaptureSessionServiceSnapshot Snapshot { get; }

    CaptureArmReceipt Arm(CaptureArmRequest request);

    ValueTask<CaptureQueueReceipt> EnqueueAsync(CaptureSubmission submission, CancellationToken cancellationToken);

    bool TryReview(
        CaptureSessionId sessionId,
        string artifactId,
        int decodeRevision,
        CaptureReviewAction action,
        string origin);

    bool Cancel(CaptureSessionId sessionId, string origin);
}
