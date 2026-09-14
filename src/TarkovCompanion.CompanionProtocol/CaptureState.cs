using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.CompanionProtocol;

public enum ContextualCapturePurpose
{
    LootDecision = 1,
    FullStash,
    Ammo,
    Keys,
    QuestAndFutureQuestItems,
    MapAndExtracts,
    HealthAndCharacter,
    AutoDetect,
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
    }

    public string ResultId { get; }

    public string ArtifactId { get; }

    public int CaptureOrdinal { get; }

    public ResultStatus Status { get; }

    public RecognizedContext? DetectedContext { get; }

    public DateTimeOffset CompletedUtc { get; }

    public EvidenceProvenance Provenance { get; }
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
        ContextualCapturePurpose purpose,
        CompanionDeviceId initiatingDeviceId,
        CompanionSurfaceKind initiatingSurface,
        DateTimeOffset requestedUtc,
        DateTimeOffset expiresUtc,
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
        Purpose = ProtocolGuard.Defined(purpose, nameof(purpose));
        InitiatingDeviceId = initiatingDeviceId.Value == Guid.Empty
            ? throw new ArgumentException("An initiating device is required.", nameof(initiatingDeviceId))
            : initiatingDeviceId;
        InitiatingSurface = ProtocolGuard.Defined(initiatingSurface, nameof(initiatingSurface));
        RequestedUtc = ProtocolGuard.Utc(requestedUtc, nameof(requestedUtc));
        ExpiresUtc = ProtocolGuard.Utc(expiresUtc, nameof(expiresUtc));
        Status = ProtocolGuard.Defined(status, nameof(status));
        Context = ProtocolGuard.NotNull(context, nameof(context));
        Progress = ProtocolGuard.List(progress, nameof(progress));
        Result = result;
        Guidance = ProtocolGuard.List(guidance, nameof(guidance));
        Review = review;
        Corrections = ProtocolGuard.List(corrections, nameof(corrections));

        if (ExpiresUtc <= RequestedUtc || ExpiresUtc - RequestedUtc > ProtocolBounds.CaptureIntentLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresUtc), "A contextual capture intent is short lived.");
        }

        ValidateProgress();
        ValidateReviewAndCorrections();
    }

    public CaptureIntentId IntentId { get; }

    public string CorrelationId { get; }

    public CaptureSessionId CaptureSessionId { get; }

    public ContextualCapturePurpose Purpose { get; }

    public CompanionDeviceId InitiatingDeviceId { get; }

    public CompanionSurfaceKind InitiatingSurface { get; }

    public DateTimeOffset RequestedUtc { get; }

    public DateTimeOffset ExpiresUtc { get; }

    public ContextualCaptureStatus Status { get; }

    public CompanionCaptureContext Context { get; }

    public IReadOnlyList<ContextualCaptureProgress> Progress { get; }

    public ContextualCaptureResult? Result { get; }

    public IReadOnlyList<ContextualCaptureGuidance> Guidance { get; }

    public ContextualCaptureReview? Review { get; }

    public IReadOnlyList<ContextualCaptureCorrection> Corrections { get; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ScanIntent CoreIntent => Purpose switch
    {
        ContextualCapturePurpose.LootDecision => ScanIntent.Loot,
        ContextualCapturePurpose.FullStash => ScanIntent.Stash,
        ContextualCapturePurpose.Ammo => ScanIntent.Ammo,
        ContextualCapturePurpose.Keys => ScanIntent.Keys,
        ContextualCapturePurpose.QuestAndFutureQuestItems => ScanIntent.QuestItems,
        ContextualCapturePurpose.MapAndExtracts => ScanIntent.ExtractsAndMap,
        ContextualCapturePurpose.HealthAndCharacter => ScanIntent.HealthAndCharacter,
        ContextualCapturePurpose.AutoDetect => ScanIntent.Auto,
        _ => throw new InvalidOperationException("Unsupported capture purpose."),
    };

    private void ValidateProgress()
    {
        var captures = new List<(string ArtifactId, ContextualCaptureProgressPhase Phase)>();
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

                captures.Add((item.ArtifactId!, item.Phase));
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

            captures[ordinal] = (previous.ArtifactId, item.Phase);
        }

        if (Result is not null && Result.CompletedUtc > ExpiresUtc)
        {
            throw new ArgumentException("A capture result cannot complete after its intent expired.", nameof(Result));
        }

        if (StatusRequiresResult(Status) != (Result is not null))
        {
            throw new ArgumentException("Awaiting-review and complete capture states carry a result; other states do not.", nameof(Result));
        }

        if (Result is not null &&
            (Result.CaptureOrdinal >= captures.Count ||
             !string.Equals(captures[Result.CaptureOrdinal].ArtifactId, Result.ArtifactId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("A result identifies an artifact already correlated in progress.", nameof(Result));
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

    private static bool StatusRequiresResult(ContextualCaptureStatus status) =>
        status is ContextualCaptureStatus.AwaitingReview or ContextualCaptureStatus.Complete;
}
