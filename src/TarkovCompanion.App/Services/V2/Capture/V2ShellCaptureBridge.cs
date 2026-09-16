using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>
/// Connects the already-built shell chrome (arm/resolve events, <see cref="V2ShellViewModel.UpdateCaptureState"/>)
/// to the real #271 <see cref="ICaptureSessionService"/> and the #274/#282 Loot Scan decision path.
/// Neither #271's coordinator nor the shell reference each other directly; this composition-owned
/// adapter is the seam both of their designs left for wiring.
/// </summary>
/// <remarks>
/// Scope for this rough pass: every capture review is resolved automatically (preferring the
/// detected context, falling back to the armed intent) so a screenshot always reaches handoff
/// without a blocking dialog. The richer manual intent-mismatch/unknown-context attention UI the
/// shell already renders (<see cref="V2ShellViewModel.CaptureAttentionActions"/>) is intentionally
/// left unset here; deciding when to interrupt the player instead of auto-resolving is a UX call
/// for a later pass, not a wiring gap. Only one capture session is tracked at a time, matching the
/// shell's single global Capture affordance.
/// </remarks>
public sealed class V2ShellCaptureBridge : IDisposable
{
    private readonly V2ShellViewModel _shell;
    private readonly ICaptureSessionService _captureSessions;
    private readonly LootScanCaptureHandoff _lootScanHandoff;
    private readonly WorkspaceOrigin _origin;
    private readonly ILogger<V2ShellCaptureBridge> _logger;
    private readonly Lock _gate = new();
    private long _intentRevision;
    private ScanIntent _armedIntent = ScanIntent.Auto;
    private string _settingDevice = V2NavigationContext.ThisDesktop;
    private CaptureSessionId? _currentSessionId;
    private V2CaptureReview? _review;
    private bool _disposed;

    public V2ShellCaptureBridge(
        V2ShellViewModel shell,
        ICaptureSessionService captureSessions,
        LootScanCaptureHandoff lootScanHandoff,
        WorkspaceOrigin origin,
        ILogger<V2ShellCaptureBridge>? logger = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _captureSessions = captureSessions ?? throw new ArgumentNullException(nameof(captureSessions));
        _lootScanHandoff = lootScanHandoff ?? throw new ArgumentNullException(nameof(lootScanHandoff));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _logger = logger ?? NullLogger<V2ShellCaptureBridge>.Instance;

        _shell.CaptureArmRequested += OnCaptureArmRequested;
        _shell.CaptureResolutionRequested += OnCaptureResolutionRequested;
        _captureSessions.Changed += OnCaptureSessionsChanged;
        _captureSessions.ReviewRequested += OnReviewRequested;
        _lootScanHandoff.LootScanEvaluated += OnLootScanEvaluated;
        Push();
    }

    private void OnCaptureArmRequested(object? sender, V2CaptureArmRequest request)
    {
        lock (_gate)
        {
            if (request.BasedOnRevision.Value != _intentRevision)
            {
                throw new InvalidOperationException("The armed intent changed since this request was built.");
            }

            var sessionId = new CaptureSessionId(Guid.NewGuid());
            var now = TimeProvider.System.GetUtcNow();
            var context = new CaptureContextMetadata(
                activeWorkspace: null,
                activeProfile: null,
                activeMap: null,
                activePlan: null,
                selectedEntity: null,
                priorScan: null,
                initiatingDevice: request.RequestingDevice);
            var receipt = _captureSessions.Arm(new(
                new(
                    sessionId,
                    request.Intent,
                    _origin,
                    now,
                    ProfileId: null,
                    MapId: null,
                    ExpiresUtc: now.AddSeconds(30)),
                context,
                new("hold_screen", "Hold the screen steady until the capture completes.")));
            if (!receipt.Accepted)
            {
                throw new InvalidOperationException($"Capture could not be armed: {receipt.Code}.");
            }

            _armedIntent = request.Intent;
            _intentRevision++;
            _settingDevice = request.RequestingDevice;
            _currentSessionId = sessionId;
            _review = null;
        }

        Push();
    }

    private void OnCaptureResolutionRequested(object? sender, V2CaptureResolutionRequest request)
    {
        if (request is { SessionId: { } sessionId, ArtifactId: { } artifactId, CaptureOrdinal: { } ordinal })
        {
            switch (request.Resolution)
            {
                case V2CaptureResolutionKind.Skip:
                    _captureSessions.Cancel(sessionId, request.RequestingDevice);
                    return;
                case V2CaptureResolutionKind.Retry:
                    _captureSessions.TryReview(sessionId, artifactId, ordinal, CaptureReviewAction.RetryCapture, request.RequestingDevice);
                    return;
                case V2CaptureResolutionKind.AnalyzeAgain:
                    _captureSessions.TryReview(sessionId, artifactId, ordinal, CaptureReviewAction.Redecode, request.RequestingDevice);
                    return;
                case V2CaptureResolutionKind.AnalyzeAsArmed:
                    _captureSessions.TryReview(sessionId, artifactId, ordinal, CaptureReviewAction.UseArmedIntent, request.RequestingDevice);
                    return;
                case V2CaptureResolutionKind.AnalyzeAsDetected:
                    _captureSessions.TryReview(sessionId, artifactId, ordinal, CaptureReviewAction.UseDetected, request.RequestingDevice);
                    return;
            }
        }

        // Review/Correct/AnalyzeAsSelected/KeepCurrentIntent/ArmSelectedIntent: the result is
        // already visible on the Loot route once evaluated, and this pass does not yet offer a
        // corrected re-analysis. Acknowledged rather than rejected, since nothing failed.
    }

    private void OnReviewRequested(object? sender, CaptureReviewRequestedEventArgs eventArgs)
    {
        var review = eventArgs.Review;
        var action = review.DetectedContext is not null
            ? CaptureReviewAction.UseDetected
            : CaptureReviewAction.UseArmedIntent;
        _captureSessions.TryReview(review.SessionId, review.ArtifactId, review.DecodeRevision, action, "system-auto-resolve");
    }

    private void OnLootScanEvaluated(object? sender, LootScanResult result)
    {
        var viewModel = new LootScanViewModel(result);
        lock (_gate)
        {
            // The frame that reached handoff is the only capture this session's single-review
            // lifecycle produces, so ordinal 0 names it without inventing a count.
            _review = new V2CaptureReview(
                result.CaptureSessionId,
                result.ArtifactId,
                0,
                ScanIntent.Loot,
                null,
                result.EvaluatedUtc,
                $"{viewModel.TakeSummary}, {viewModel.SwapSummary}, {viewModel.LeaveSummary}, {viewModel.ReviewSummary}",
                "Loot Scan decision",
                false);
        }

        _shell.ShowLootScanResult(viewModel);
        Push();
    }

    private void OnCaptureSessionsChanged(object? sender, EventArgs eventArgs) => Push();

    private void Push()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        CaptureSessionSnapshot? session;
        V2CaptureReview? review;
        ScanIntent armedIntent;
        long intentRevision;
        string settingDevice;
        lock (_gate)
        {
            armedIntent = _armedIntent;
            intentRevision = _intentRevision;
            settingDevice = _settingDevice;
            review = _review;
            session = _currentSessionId is { } id
                ? _captureSessions.Snapshot.Sessions.FirstOrDefault(item => item.Request.SessionId == id)?.Snapshot
                : null;
        }

        try
        {
            _shell.UpdateCaptureState(new(
                armedIntent,
                new StateRevision(intentRevision),
                settingDevice,
                session,
                attention: null,
                review: review));
        }
        catch (Exception exception)
        {
            // A shape the shell's cross-field validation rejects (for example a review that has
            // not yet caught up with a brand-new arm) is a stale projection, not a crash.
            _logger.LogDebug(exception, "Skipped a capture-state update that did not validate yet.");
        }
    }

    public void Dispose()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        Volatile.Write(ref _disposed, true);
        _shell.CaptureArmRequested -= OnCaptureArmRequested;
        _shell.CaptureResolutionRequested -= OnCaptureResolutionRequested;
        _captureSessions.Changed -= OnCaptureSessionsChanged;
        _captureSessions.ReviewRequested -= OnReviewRequested;
        _lootScanHandoff.LootScanEvaluated -= OnLootScanEvaluated;
    }
}
