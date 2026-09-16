using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Services.V2.Shell;

/// <summary>Why a capture is waiting for the player instead of publishing a result.</summary>
public enum V2CaptureAttentionKind
{
    IntentMismatch = 1,
    UnknownContext,
    StillWriting,
    Duplicate,
    DeviceRace,
    SourceUnavailable,
}

/// <summary>An action the shell can hand to #271 without attempting capture work itself.</summary>
public enum V2CaptureResolutionKind
{
    Skip = 1,
    AnalyzeAsArmed,
    AnalyzeAsDetected,
    AnalyzeAsSelected,
    Retry,
    AnalyzeAgain,
    KeepCurrentIntent,
    ArmSelectedIntent,
    Review,
    Correct,
}

/// <summary>
/// Typed facts needed to present a paused capture. The shell never infers one of these from prose.
/// </summary>
public sealed record V2CaptureAttention
{
    public V2CaptureAttention(
        V2CaptureAttentionKind kind,
        CaptureSessionId? sessionId,
        string? artifactId,
        int? captureOrdinal,
        ScanIntent boundIntent,
        StateRevision boundRevision,
        string settingDevice,
        RecognizedContext? detectedContext = null,
        string? detail = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!Enum.IsDefined(boundIntent))
        {
            throw new ArgumentOutOfRangeException(nameof(boundIntent));
        }

        if ((artifactId is null) != (captureOrdinal is null))
        {
            throw new ArgumentException("An attention item names both an artifact and its capture ordinal.", nameof(captureOrdinal));
        }

        if (kind != V2CaptureAttentionKind.DeviceRace && (sessionId is null || artifactId is null))
        {
            throw new ArgumentException("A capture attention item must name its session and artifact.", nameof(sessionId));
        }

        if (kind == V2CaptureAttentionKind.IntentMismatch && detectedContext is null)
        {
            throw new ArgumentException("An intent mismatch must name the context that was detected.", nameof(detectedContext));
        }

        if (kind == V2CaptureAttentionKind.UnknownContext && detectedContext is not null)
        {
            throw new ArgumentException("An unknown-context decision cannot also claim a detected context.", nameof(detectedContext));
        }

        if (sessionId is { } definedSession && definedSession == default)
        {
            throw new ArgumentException("A capture session id cannot be empty.", nameof(sessionId));
        }

        if (captureOrdinal is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureOrdinal));
        }

        Kind = kind;
        SessionId = sessionId;
        ArtifactId = Bounded(artifactId, nameof(artifactId), optional: true, maximumLength: 256);
        CaptureOrdinal = captureOrdinal;
        BoundIntent = boundIntent;
        BoundRevision = boundRevision;
        SettingDevice = Bounded(settingDevice, nameof(settingDevice), optional: false, maximumLength: 128)!;
        DetectedContext = detectedContext is null || Enum.IsDefined(detectedContext.Value)
            ? detectedContext
            : throw new ArgumentOutOfRangeException(nameof(detectedContext));
        Detail = Bounded(detail, nameof(detail), optional: true, maximumLength: 512);
    }

    public V2CaptureAttentionKind Kind { get; }
    public CaptureSessionId? SessionId { get; }
    public string? ArtifactId { get; }
    public int? CaptureOrdinal { get; }
    public ScanIntent BoundIntent { get; }
    public StateRevision BoundRevision { get; }
    public string SettingDevice { get; }
    public RecognizedContext? DetectedContext { get; }
    public string? Detail { get; }

    internal static string? Bounded(
        string? value,
        string parameterName,
        bool optional,
        int maximumLength = V2ShellIdentifier.MaxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return optional ? null : throw new ArgumentException("A value is required.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("A value must be bounded text without control characters.", parameterName);
        }

        return normalized;
    }
}

/// <summary>A finished result the shared Capture dialog can review or ask #271 to correct.</summary>
public sealed record V2CaptureReview
{
    public V2CaptureReview(
        CaptureSessionId sessionId,
        string artifactId,
        int captureOrdinal,
        ScanIntent analyzedAs,
        RecognizedContext? detectedContext,
        DateTimeOffset capturedUtc,
        string summary,
        string provenance,
        bool canCorrect = true)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("A review must name its capture session.", nameof(sessionId));
        }

        if (captureOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureOrdinal));
        }

        if (!Enum.IsDefined(analyzedAs))
        {
            throw new ArgumentOutOfRangeException(nameof(analyzedAs));
        }

        SessionId = sessionId;
        ArtifactId = V2CaptureAttention.Bounded(artifactId, nameof(artifactId), optional: false, maximumLength: 256)!;
        CaptureOrdinal = captureOrdinal;
        AnalyzedAs = analyzedAs;
        DetectedContext = detectedContext is null || Enum.IsDefined(detectedContext.Value)
            ? detectedContext
            : throw new ArgumentOutOfRangeException(nameof(detectedContext));
        CapturedUtc = capturedUtc == default
            ? throw new ArgumentOutOfRangeException(nameof(capturedUtc))
            : capturedUtc.ToUniversalTime();
        Summary = V2CaptureAttention.Bounded(summary, nameof(summary), optional: false, maximumLength: 512)!;
        Provenance = V2CaptureAttention.Bounded(provenance, nameof(provenance), optional: false, maximumLength: 512)!;
        CanCorrect = canCorrect;
    }

    public CaptureSessionId SessionId { get; }
    public string ArtifactId { get; }
    public int CaptureOrdinal { get; }
    public ScanIntent AnalyzedAs { get; }
    public RecognizedContext? DetectedContext { get; }
    public DateTimeOffset CapturedUtc { get; }
    public string Summary { get; }
    public string Provenance { get; }
    public bool CanCorrect { get; }
}

/// <summary>
/// The complete read-only capture projection consumed by shell chrome. #271 will own its source.
/// </summary>
public sealed record V2CaptureShellState
{
    public V2CaptureShellState(
        ScanIntent armedIntent,
        StateRevision intentRevision,
        string settingDevice,
        CaptureSessionSnapshot? session = null,
        V2CaptureAttention? attention = null,
        V2CaptureReview? review = null)
    {
        if (!Enum.IsDefined(armedIntent))
        {
            throw new ArgumentOutOfRangeException(nameof(armedIntent));
        }

        ArmedIntent = armedIntent;
        IntentRevision = intentRevision;
        SettingDevice = V2CaptureAttention.Bounded(settingDevice, nameof(settingDevice), optional: false, maximumLength: 128)!;
        Session = session;
        Attention = attention;
        Review = review;

        var sessionIds = new CaptureSessionId?[]
            {
                session?.Request.SessionId,
                attention?.SessionId,
                review?.SessionId,
            }
            .OfType<CaptureSessionId>()
            .Distinct()
            .ToArray();
        if (sessionIds.Length > 1)
        {
            throw new ArgumentException("Capture progress, attention, and review must share one correlation id.");
        }

        CorrelationId = sessionIds.FirstOrDefault() is { } id && id != default
            ? id.Value.ToString("D")
            : null;
    }

    public ScanIntent ArmedIntent { get; }
    public StateRevision IntentRevision { get; }
    public string SettingDevice { get; }
    public CaptureSessionSnapshot? Session { get; }
    public V2CaptureAttention? Attention { get; }
    public V2CaptureReview? Review { get; }
    public string? CorrelationId { get; }

    public static V2CaptureShellState Empty { get; } = new(
        ScanIntent.Auto,
        new StateRevision(0),
        V2NavigationContext.ThisDesktop);
}

public sealed record V2CaptureArmRequest(
    ScanIntent Intent,
    StateRevision BasedOnRevision,
    string RequestingDevice);

public sealed record V2CaptureResolutionRequest(
    CaptureSessionId? SessionId,
    string? ArtifactId,
    int? CaptureOrdinal,
    V2CaptureResolutionKind Resolution,
    ScanIntent? Intent,
    StateRevision BasedOnRevision,
    string RequestingDevice);
