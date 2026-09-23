using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.CompanionProtocol;

/// <summary>The paired capture intents: the frozen #264 <see cref="ScanIntent"/> set.</summary>
public static class PairedScanIntents
{
    public static IReadOnlyList<ScanIntent> Allowed { get; } =
        Enum.GetValues<ScanIntent>();

    internal static ScanIntent Require(ScanIntent intent, string parameterName) =>
        Enum.IsDefined(intent)
            ? intent
            : throw new ArgumentOutOfRangeException(parameterName, intent, "The paired-device capture intent is not defined.");
}

public enum ContextualCaptureStatus
{
    Armed = 1,
    AwaitingUserCapture,
    InProgress,
    AwaitingReview,
    Complete,
    Cancelled,
    Failed,
    Expired,
}

public enum ContextualCaptureProgressPhase
{
    Armed = 1,
    AwaitingUserCapture,
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

public enum CaptureGuidanceKind
{
    TakeUserScreenshot = 1,
    OpenRelevantPanel,
    ScrollForOverlap,
    ReviewAmbiguity,
    ConfirmResult,
}

public enum CaptureReviewDisposition
{
    Accepted = 1,
    NeedsCorrection,
}

public enum CaptureCorrectionKind
{
    DetectedContext = 1,
    ItemIdentity,
    Quantity,
    GridPlacement,
    ExtractIdentity,
    CharacterRegion,
}

public sealed record CompanionCaptureContext
{
    public CompanionCaptureContext(
        string? mapId,
        string? floorId,
        string? profileId,
        string? previousResultId,
        IReadOnlyList<string> objectiveIds,
        IReadOnlyList<string> planIds,
        IReadOnlyList<MarkId> markIds)
    {
        MapId = ProtocolGuard.Optional(mapId, nameof(mapId), ProtocolBounds.MaxShortStringBytes);
        FloorId = ProtocolGuard.Optional(floorId, nameof(floorId), ProtocolBounds.MaxShortStringBytes);
        ProfileId = ProtocolGuard.Optional(profileId, nameof(profileId), ProtocolBounds.MaxShortStringBytes);
        PreviousResultId = ProtocolGuard.Optional(previousResultId, nameof(previousResultId), ProtocolBounds.MaxShortStringBytes);
        ObjectiveIds = Strings(objectiveIds, nameof(objectiveIds));
        PlanIds = Strings(planIds, nameof(planIds));
        MarkIds = ProtocolGuard.List(markIds, nameof(markIds));
    }

    public string? MapId { get; }

    public string? FloorId { get; }

    public string? ProfileId { get; }

    public string? PreviousResultId { get; }

    public IReadOnlyList<string> ObjectiveIds { get; }

    public IReadOnlyList<string> PlanIds { get; }

    public IReadOnlyList<MarkId> MarkIds { get; }

    private static IReadOnlyList<string> Strings(IReadOnlyList<string> values, string parameterName) =>
        ProtocolGuard.List(
            ProtocolGuard.List(values, parameterName)
                .Select(value => ProtocolGuard.Required(value, parameterName, ProtocolBounds.MaxShortStringBytes)),
            parameterName);
}

public sealed record ContextualCaptureProgress(
    long Sequence,
    ContextualCaptureProgressPhase Phase,
    DateTimeOffset ChangedUtc,
    int? Percent,
    string? ArtifactId,
    int? CaptureOrdinal,
    string? Detail)
{
    public long Sequence { get; } = ProtocolGuard.NonNegative(Sequence, nameof(Sequence));

    public ContextualCaptureProgressPhase Phase { get; } = ProtocolGuard.Defined(Phase, nameof(Phase));

    public DateTimeOffset ChangedUtc { get; } = ProtocolGuard.Utc(ChangedUtc, nameof(ChangedUtc));

    public int? Percent { get; } = Percent is null or (>= 0 and <= 100)
        ? Percent
        : throw new ArgumentOutOfRangeException(nameof(Percent));

    public string? ArtifactId { get; } = ProtocolGuard.Optional(
        ArtifactId,
        nameof(ArtifactId),
        ProtocolBounds.MaxShortStringBytes);

    public int? CaptureOrdinal { get; } = ValidateCorrelation(Phase, ArtifactId, CaptureOrdinal);

    public string? Detail { get; } = ProtocolGuard.Optional(Detail, nameof(Detail));

    private static int? ValidateCorrelation(
        ContextualCaptureProgressPhase phase,
        string? artifactId,
        int? captureOrdinal)
    {
        if (captureOrdinal is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureOrdinal));
        }

        var hasArtifact = !string.IsNullOrWhiteSpace(artifactId);
        if (hasArtifact != (captureOrdinal is not null))
        {
            throw new ArgumentException("An artifact and capture ordinal travel together.", nameof(captureOrdinal));
        }

        var sessionPhase = phase is ContextualCaptureProgressPhase.Armed or
            ContextualCaptureProgressPhase.AwaitingUserCapture;
        var capturePhase = phase is >= ContextualCaptureProgressPhase.Settling and
            <= ContextualCaptureProgressPhase.AwaitingReview;
        if ((sessionPhase && hasArtifact) || (capturePhase && !hasArtifact))
        {
            throw new ArgumentException("Session phases have no artifact; capture phases identify one.", nameof(captureOrdinal));
        }

        return captureOrdinal;
    }
}

public sealed record ContextualCaptureResult
{
    public ContextualCaptureResult(
        string resultId,
        string artifactId,
        int captureOrdinal,
        ResultStatus status,
        RecognizedContext? detectedContext,
        DateTimeOffset completedUtc,
        EvidenceProvenance provenance)
    {
        ResultId = ProtocolGuard.Required(resultId, nameof(resultId), ProtocolBounds.MaxShortStringBytes);
        ArtifactId = ProtocolGuard.Required(artifactId, nameof(artifactId), ProtocolBounds.MaxShortStringBytes);
        CaptureOrdinal = captureOrdinal >= 0 ? captureOrdinal : throw new ArgumentOutOfRangeException(nameof(captureOrdinal));
        Status = ProtocolGuard.NotNull(status, nameof(status));
        DetectedContext = detectedContext is { } context && !Enum.IsDefined(context)
            ? throw new ArgumentOutOfRangeException(nameof(detectedContext))
            : detectedContext;
        CompletedUtc = ProtocolGuard.Utc(completedUtc, nameof(completedUtc));
        Provenance = ProtocolGuard.NotNull(provenance, nameof(provenance));

        // The full #264 lineage stays with the referenced result. A paired snapshot inside a
        // command acknowledgement reaches JSON depth 13 at this bound, inside the wire limit of 16;
        // Core's own depth of 8 would make a canonical state that no transport could deliver.
        if (Depth(Provenance) > ProtocolBounds.MaxCaptureProvenanceDepth)
        {
            throw new ArgumentException(
                $"A paired capture result carries at most {ProtocolBounds.MaxCaptureProvenanceDepth} provenance levels.",
                nameof(provenance));
        }
    }

    public string ResultId { get; }

    public string ArtifactId { get; }

    public int CaptureOrdinal { get; }

    public ResultStatus Status { get; }

    public RecognizedContext? DetectedContext { get; }

    public DateTimeOffset CompletedUtc { get; }

    public EvidenceProvenance Provenance { get; }

    private static int Depth(EvidenceProvenance provenance) =>
        1 + (provenance.Inputs.Count == 0 ? 0 : provenance.Inputs.Max(Depth));
}

public sealed record ContextualCaptureGuidance(
    CaptureGuidanceKind Kind,
    string Code,
    string Instruction,
    int Order)
{
    public CaptureGuidanceKind Kind { get; } = ProtocolGuard.Defined(Kind, nameof(Kind));

    public string Code { get; } = ProtocolGuard.Required(Code, nameof(Code), ProtocolBounds.MaxShortStringBytes);

    public string Instruction { get; } = ProtocolGuard.Required(Instruction, nameof(Instruction));

    public int Order { get; } = Order >= 0 ? Order : throw new ArgumentOutOfRangeException(nameof(Order));
}

public sealed record ContextualCaptureReview(
    CaptureReviewDisposition Disposition,
    CompanionDeviceId ReviewerDeviceId,
    DateTimeOffset ReviewedUtc,
    string? Note)
{
    public CaptureReviewDisposition Disposition { get; } = ProtocolGuard.Defined(Disposition, nameof(Disposition));

    public DateTimeOffset ReviewedUtc { get; } = ProtocolGuard.Utc(ReviewedUtc, nameof(ReviewedUtc));

    public string? Note { get; } = ProtocolGuard.Optional(Note, nameof(Note));
}

public sealed record ContextualCaptureCorrection(
    long Sequence,
    CaptureCorrectionKind Kind,
    string FieldId,
    string CorrectedValue,
    CompanionDeviceId ReviewerDeviceId,
    DateTimeOffset CorrectedUtc,
    string? Reason)
{
    public long Sequence { get; } = ProtocolGuard.Positive(Sequence, nameof(Sequence));

    public CaptureCorrectionKind Kind { get; } = ProtocolGuard.Defined(Kind, nameof(Kind));

    public string FieldId { get; } = ProtocolGuard.Required(FieldId, nameof(FieldId), ProtocolBounds.MaxShortStringBytes);

    public string CorrectedValue { get; } = ProtocolGuard.Required(CorrectedValue, nameof(CorrectedValue));

    public DateTimeOffset CorrectedUtc { get; } = ProtocolGuard.Utc(CorrectedUtc, nameof(CorrectedUtc));

    public string? Reason { get; } = ProtocolGuard.Optional(Reason, nameof(Reason));
}

/// <summary>
/// Desktop-canonical scan context. Applying it arms companion recognition only; no field can
/// trigger a capture, control EFT, or generate input.
/// </summary>
public sealed record ContextualCaptureIntent
{
    public ContextualCaptureIntent(
        CaptureIntentId intentId,
        string correlationId,
        CaptureSessionId captureSessionId,
        CaptureIntentState state,
        CompanionDeviceId initiatingDeviceId,
        CompanionSurfaceKind initiatingSurface,
        ContextualCaptureStatus status,
        CompanionCaptureContext context,
        IReadOnlyList<ContextualCaptureProgress> progress,
        ContextualCaptureResult? result,
        IReadOnlyList<ContextualCaptureGuidance> guidance,
        ContextualCaptureReview? review,
        IReadOnlyList<ContextualCaptureCorrection> corrections)
    {
        IntentId = intentId.Value == Guid.Empty ? throw new ArgumentException("An intent id is required.", nameof(intentId)) : intentId;
        CorrelationId = ProtocolGuard.Required(correlationId, nameof(correlationId), ProtocolBounds.MaxShortStringBytes);
        CaptureSessionId = captureSessionId.Value == Guid.Empty
            ? throw new ArgumentException("A capture session id is required.", nameof(captureSessionId))
            : captureSessionId;
        State = ProtocolGuard.NotNull(state, nameof(state));
        PairedScanIntents.Require(State.Intent, nameof(state));
        ProtocolGuard.Utc(State.ArmedUtc, nameof(state));
        if (State.ExpiresUtc is not { } expires)
        {
            throw new ArgumentException("A paired capture intent always expires.", nameof(state));
        }

        ProtocolGuard.Utc(expires, nameof(state));
        InitiatingDeviceId = initiatingDeviceId.Value == Guid.Empty
            ? throw new ArgumentException("An initiating device is required.", nameof(initiatingDeviceId))
            : initiatingDeviceId;
        InitiatingSurface = ProtocolGuard.Defined(initiatingSurface, nameof(initiatingSurface));
        Status = ProtocolGuard.Defined(status, nameof(status));
        Context = ProtocolGuard.NotNull(context, nameof(context));
        Progress = ProtocolGuard.List(progress, nameof(progress));
        Result = result;
        Guidance = ProtocolGuard.List(guidance, nameof(guidance));
        Review = review;
        Corrections = ProtocolGuard.List(corrections, nameof(corrections));

        if (ExpiresUtc <= RequestedUtc || ExpiresUtc - RequestedUtc > ProtocolBounds.CaptureIntentLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(state), "A contextual capture intent is short lived.");
        }

        ValidateProgress();
        ValidateReviewAndCorrections();
    }

    public CaptureIntentId IntentId { get; }

    public string CorrelationId { get; }

    public CaptureSessionId CaptureSessionId { get; }

    /// <summary>The Core v2 capture intent: the scan intent, armed time, and expiry.</summary>
    public CaptureIntentState State { get; }

    public CompanionDeviceId InitiatingDeviceId { get; }

    public CompanionSurfaceKind InitiatingSurface { get; }

    public ContextualCaptureStatus Status { get; }

    public CompanionCaptureContext Context { get; }

    public IReadOnlyList<ContextualCaptureProgress> Progress { get; }

    public ContextualCaptureResult? Result { get; }

    public IReadOnlyList<ContextualCaptureGuidance> Guidance { get; }

    public ContextualCaptureReview? Review { get; }

    public IReadOnlyList<ContextualCaptureCorrection> Corrections { get; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ScanIntent Intent => State.Intent;

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset RequestedUtc => State.ArmedUtc;

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset ExpiresUtc => State.ExpiresUtc!.Value;

    private void ValidateProgress()
    {
        var captures = new List<(string ArtifactId, ContextualCaptureProgressPhase Phase, DateTimeOffset ChangedUtc)>();
        for (var index = 0; index < Progress.Count; index++)
        {
            var item = Progress[index];
            if (item.Sequence != index || item.ChangedUtc < RequestedUtc || item.ChangedUtc > ExpiresUtc)
            {
                throw new ArgumentException("Capture progress is contiguous and remains within the intent lifetime.", nameof(Progress));
            }

            if (index > 0 && item.ChangedUtc < Progress[index - 1].ChangedUtc)
            {
                throw new ArgumentException("Capture progress timestamps do not move backwards.", nameof(Progress));
            }

            if (item.CaptureOrdinal is not { } ordinal)
            {
                continue;
            }

            if (ordinal > captures.Count)
            {
                throw new ArgumentException("Capture ordinals enter the session contiguously from zero.", nameof(Progress));
            }

            if (ordinal == captures.Count)
            {
                if (captures.Any(capture => string.Equals(capture.ArtifactId, item.ArtifactId, StringComparison.Ordinal)))
                {
                    throw new ArgumentException("An artifact belongs to one capture ordinal.", nameof(Progress));
                }

                captures.Add((item.ArtifactId!, item.Phase, item.ChangedUtc));
                continue;
            }

            var previous = captures[ordinal];
            var terminal = previous.Phase is ContextualCaptureProgressPhase.Complete or
                ContextualCaptureProgressPhase.Cancelled or ContextualCaptureProgressPhase.Failed;
            var retry = previous.Phase == ContextualCaptureProgressPhase.Decoding &&
                        item.Phase == ContextualCaptureProgressPhase.Settling;
            if (!string.Equals(previous.ArtifactId, item.ArtifactId, StringComparison.Ordinal) ||
                terminal || (item.Phase < previous.Phase && !retry))
            {
                throw new ArgumentException("A capture artifact moves forward once and cannot be rebound.", nameof(Progress));
            }

            captures[ordinal] = (previous.ArtifactId, item.Phase, item.ChangedUtc);
        }

        if (Result is not null &&
            (Result.CompletedUtc < RequestedUtc ||
             Result.CompletedUtc > ExpiresUtc ||
             Result.Provenance.ObservedUtc > Result.CompletedUtc))
        {
            throw new ArgumentException("A capture result follows its request and evidence observation and precedes intent expiry.", nameof(Result));
        }

        var resultRule = ResultRule(Status);
        if ((resultRule == true && Result is null) || (resultRule == false && Result is not null))
        {
            throw new ArgumentException(
                "Awaiting-review and complete captures carry a result, unfinished ones do not, and a terminal failure keeps any result it had.",
                nameof(Result));
        }

        if (Result is not null &&
            (Result.CaptureOrdinal >= captures.Count ||
             !string.Equals(captures[Result.CaptureOrdinal].ArtifactId, Result.ArtifactId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("A result identifies an artifact already correlated in progress.", nameof(Result));
        }

        if (Result is not null)
        {
            var correlated = Progress
                .Where(item => item.CaptureOrdinal == Result.CaptureOrdinal &&
                               string.Equals(item.ArtifactId, Result.ArtifactId, StringComparison.Ordinal))
                .ToArray();
            var awaitingReview = correlated.LastOrDefault(item => item.Phase == ContextualCaptureProgressPhase.AwaitingReview);
            if (awaitingReview is null)
            {
                throw new ArgumentException("A result has an awaiting-review transition.", nameof(Result));
            }

            var evidence = correlated.LastOrDefault(item =>
                item.Sequence < awaitingReview.Sequence && item.Phase != ContextualCaptureProgressPhase.AwaitingReview);
            if (evidence is null ||
                Result.CompletedUtc < evidence.ChangedUtc || Result.CompletedUtc > awaitingReview.ChangedUtc)
            {
                throw new ArgumentException("A result completes after correlated evidence and before its awaiting-review transition.", nameof(Result));
            }
        }
    }

    private void ValidateReviewAndCorrections()
    {
        if (Review is not null && (Result is null || Review.ReviewedUtc < Result.CompletedUtc || Review.ReviewedUtc > ExpiresUtc))
        {
            throw new ArgumentException("A review follows a result and precedes intent expiry.", nameof(Review));
        }

        if (Status == ContextualCaptureStatus.Complete && Review?.Disposition != CaptureReviewDisposition.Accepted)
        {
            throw new ArgumentException("A complete contextual result has been accepted.", nameof(Review));
        }

        if (Corrections.Count > 0 && Result is null)
        {
            throw new ArgumentException("A correction follows a contextual capture result.", nameof(Corrections));
        }

        for (var index = 0; index < Corrections.Count; index++)
        {
            var correction = Corrections[index];
            var previousUtc = index == 0 ? Result!.CompletedUtc : Corrections[index - 1].CorrectedUtc;
            if (correction.Sequence != index + 1 ||
                correction.CorrectedUtc < previousUtc ||
                correction.CorrectedUtc > ExpiresUtc)
            {
                throw new ArgumentException("Corrections are append-only, ordered, and within the intent lifetime.", nameof(Corrections));
            }
        }
    }

    // Expiry, cancellation, and failure can arrive after a result was published; they keep that
    // result as history instead of making server-time maintenance unable to expire the intent.
    private static bool? ResultRule(ContextualCaptureStatus status) => status switch
    {
        ContextualCaptureStatus.AwaitingReview or ContextualCaptureStatus.Complete => true,
        ContextualCaptureStatus.Armed or ContextualCaptureStatus.AwaitingUserCapture or ContextualCaptureStatus.InProgress => false,
        _ => null,
    };
}
