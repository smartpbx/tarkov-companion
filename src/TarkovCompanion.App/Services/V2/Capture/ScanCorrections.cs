using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Services.V2.Capture;

public enum ScanCorrectionKind
{
    /// <summary>The same frame read again under another intent.</summary>
    ReadAs = 1,

    /// <summary>A different item picked from the recogniser's candidates.</summary>
    Candidate,
}

/// <summary>One thing the player said a scan got wrong.</summary>
/// <param name="ArtifactId">The capture that was corrected.</param>
/// <param name="From">What it had been read as: an intent name, or the candidate id.</param>
/// <param name="To">What the player said it was.</param>
/// <param name="Rereading">The capture the second reading was submitted as, for Read as.</param>
public sealed record ScanCorrection(
    ScanCorrectionKind Kind,
    string ArtifactId,
    string From,
    string To,
    DateTimeOffset CorrectedUtc,
    CaptureCorrelationId? Rereading = null);

/// <summary>
/// The corrections players made to scans this session, newest last, and one log line each.
/// </summary>
/// <remarks>
/// #287: a player who says "that was my stash, not loot" is telling the recogniser where it was
/// wrong. Kept pixel-free and bounded; the log line is what reaches a diagnostic bundle. It is
/// not persisted: nothing reads corrections back across a restart yet.
/// </remarks>
public sealed class ScanCorrectionLog(ILogger<ScanCorrectionLog>? logger = null)
{
    private const int Limit = 64;
    private readonly ILogger<ScanCorrectionLog> _logger = logger ?? NullLogger<ScanCorrectionLog>.Instance;
    private readonly Lock _gate = new();
    private readonly List<ScanCorrection> _entries = [];

    public IReadOnlyList<ScanCorrection> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    public void Record(ScanCorrection correction)
    {
        ArgumentNullException.ThrowIfNull(correction);
        lock (_gate)
        {
            _entries.Add(correction);
            if (_entries.Count > Limit)
            {
                _entries.RemoveAt(0);
            }
        }

        _logger.LogInformation(
            "Scan corrected: {Kind} {ArtifactId} from {From} to {To}.",
            correction.Kind,
            correction.ArtifactId,
            correction.From,
            correction.To);
    }

    /// <summary>The Read as correction that produced the capture <paramref name="correlationId"/>.</summary>
    public ScanCorrection? ForRereading(CaptureCorrelationId correlationId)
    {
        lock (_gate)
        {
            return _entries.LastOrDefault(entry => entry.Rereading == correlationId);
        }
    }
}

/// <summary>What a player can ask one captured frame to be read as.</summary>
public static class ScanReadAs
{
    /// <summary>The intents "Read as…" offers, in menu order.</summary>
    public static IReadOnlyList<ScanIntent> Intents { get; } =
        [ScanIntent.Loot, ScanIntent.Stash, ScanIntent.Flea, ScanIntent.QuestItems];

    public static string Label(ScanIntent intent) => TarkovCompanion.App.Localization.ShellText.ReadAsIntent(intent);
}

/// <param name="Started">Whether the frame went back in for a second reading.</param>
/// <param name="Message">One short line for the page.</param>
public sealed record ScanReadAsOutcome(bool Started, string Message, CaptureCorrelationId? Rereading = null);

/// <summary>
/// Reads one captured frame again under the intent the player says it was.
/// </summary>
/// <remarks>
/// #287 "Read as…". The coordinator has no action that re-reads an artifact as another intent,
/// and adding one would reopen its review state machine. This does what the player would: arms
/// the intent and hands the same frame in again, from <see cref="ScanFrameMemory"/>, in the
/// context it was first taken in and marked as a second reading so intake does not call it a
/// duplicate. Every handoff then treats it like any other capture.
/// </remarks>
public sealed class CaptureReanalysis(
    ICaptureSessionService sessions,
    ScanFrameMemory frames,
    ScanCorrectionLog corrections,
    TimeProvider? timeProvider = null)
{
    private readonly ICaptureSessionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly ScanFrameMemory _frames = frames ?? throw new ArgumentNullException(nameof(frames));
    private readonly ScanCorrectionLog _corrections = corrections ?? throw new ArgumentNullException(nameof(corrections));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ScanCorrectionLog Corrections => _corrections;

    public bool CanReadAgain(string? artifactId) => _frames.Describe(artifactId) is not null;

    /// <param name="arm">Arms <c>intent</c> in <c>context</c> and returns the new session.</param>
    public async Task<ScanReadAsOutcome> ReadAsAsync(
        string artifactId,
        ScanIntent intent,
        Func<ScanIntent, CaptureContextMetadata, CaptureSessionId> arm,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        ArgumentNullException.ThrowIfNull(arm);
        if (_frames.Describe(artifactId) is not { } hold || _frames.TakeCopy(artifactId) is not { } image)
        {
            return new(false, TarkovCompanion.App.Localization.ShellText.ReadAsImageReleased);
        }

        var original = _sessions.Snapshot.Sessions
            .SelectMany(session => session.Artifacts)
            .LastOrDefault(artifact => string.Equals(artifact.ArtifactId, artifactId, StringComparison.Ordinal));
        MemoryCaptureSource? source = new(image, original?.SourceKind ?? CaptureSourceKind.UserSelectedImage);
        try
        {
            var sessionId = arm(intent, hold.Context);
            var now = _timeProvider.GetUtcNow();
            var correlation = CaptureCorrelationId.New();
            var receipt = await _sessions.EnqueueAsync(
                    new(
                        original?.DeliveryKind is { } delivery && delivery != CaptureDeliveryKind.Batch
                            ? delivery
                            : CaptureDeliveryKind.Picker,
                        source,
                        hold.Context,
                        now,
                        correlation,
                        sessionId,
                        reanalysisOf: artifactId),
                    cancellationToken)
                .ConfigureAwait(false);
            source = null;
            if (receipt.Disposition != CaptureQueueDisposition.Accepted)
            {
                return new(false, TarkovCompanion.App.Localization.ShellText.ReadAsFailed(receipt.Code));
            }

            _corrections.Record(new(
                ScanCorrectionKind.ReadAs,
                artifactId,
                hold.ReadAs.ToString(),
                intent.ToString(),
                now,
                correlation));
            return new(true, TarkovCompanion.App.Localization.ShellText.ReadAsReadingAgain(ScanReadAs.Label(intent)), correlation);
        }
        finally
        {
            source?.Dispose();
        }
    }
}
