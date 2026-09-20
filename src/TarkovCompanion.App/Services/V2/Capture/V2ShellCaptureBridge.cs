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
/// [V2 rough package 60 — Intel scan] #287. This used to resolve every review automatically,
/// preferring the detected context and falling back to the armed intent, so a screenshot always
/// reached handoff without a dialog — and the player never saw that the recognizer had disagreed
/// with them, never saw the alternatives, and had nothing to correct or retry. The shell already
/// rendered all of that; nothing ever filled it in.
///
/// Now the bridge pauses on exactly the cases where the answer is in doubt and the player is the
/// one who can settle it: the recognizer read a different screen than the one that was armed
/// (<see cref="V2CaptureAttentionKind.IntentMismatch"/>, offering Skip / Analyze as armed /
/// Analyze as detected), or it could not place the screen at all
/// (<see cref="V2CaptureAttentionKind.UnknownContext"/>, offering Skip / Analyze as the selected
/// intent). Agreement still resolves silently, because interrupting somebody to confirm what they
/// already asked for is the reason the auto-resolve was written in the first place.
///
/// The capture context was also empty — every field null — so nothing downstream could tell which
/// workspace, profile, map, plan or selection a frame was taken from. It is filled from the
/// router's own navigation context, which the shell keeps current for exactly this purpose.
///
/// Only one capture session is tracked at a time, matching the shell's single global Capture
/// affordance.
/// </remarks>
public sealed class V2ShellCaptureBridge : IDisposable
{
    private readonly V2ShellViewModel _shell;
    private readonly ICaptureSessionService _captureSessions;
    private readonly LootScanCaptureHandoff _lootScanHandoff;
    private readonly IntelCaptureHandoff _intelHandoff;
    private readonly WorkspaceOrigin _origin;
    private readonly ILogger<V2ShellCaptureBridge> _logger;
    private readonly ILootScanWorkspaceControls? _lootScanControls;
    private LootScanViewModel? _lootScan;
    private readonly Lock _gate = new();
    private long _intentRevision;
    private ScanIntent _armedIntent = ScanIntent.Auto;
    private string _settingDevice = V2NavigationContext.ThisDesktop;
    private CaptureSessionId? _currentSessionId;
    private V2CaptureReview? _review;
    private V2CaptureAttention? _attention;
    private bool _disposed;

    public V2ShellCaptureBridge(
        V2ShellViewModel shell,
        ICaptureSessionService captureSessions,
        LootScanCaptureHandoff lootScanHandoff,
        IntelCaptureHandoff intelHandoff,
        WorkspaceOrigin origin,
        ILogger<V2ShellCaptureBridge>? logger = null,
        ILootScanWorkspaceControls? lootScanControls = null)
    {
        _lootScanControls = lootScanControls;
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _captureSessions = captureSessions ?? throw new ArgumentNullException(nameof(captureSessions));
        _lootScanHandoff = lootScanHandoff ?? throw new ArgumentNullException(nameof(lootScanHandoff));
        _intelHandoff = intelHandoff ?? throw new ArgumentNullException(nameof(intelHandoff));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _logger = logger ?? NullLogger<V2ShellCaptureBridge>.Instance;

        _shell.CaptureArmRequested += OnCaptureArmRequested;
        _shell.CaptureResolutionRequested += OnCaptureResolutionRequested;
        _captureSessions.Changed += OnCaptureSessionsChanged;
        _captureSessions.ReviewRequested += OnReviewRequested;
        _lootScanHandoff.LootScanEvaluated += OnLootScanEvaluated;
        _intelHandoff.ItemIdentified += OnItemIdentified;
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
            var context = ContextFrom(request.RequestingDevice);
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
            _attention = null;
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

        // Review/Correct/KeepCurrentIntent/ArmSelectedIntent: the result is already visible on
        // its destination route, and re-analysing a frame as a *different* intent is something
        // #271's coordinator has no action for — its five are Use detected, Use armed, Redecode,
        // Retry and Cancel. Acknowledged rather than rejected, since nothing failed.
    }

    /// <summary>
    /// The workspace facts a capture is taken in, frozen at intake.
    /// </summary>
    /// <remarks>
    /// Every one of these was null. The router keeps the same facts current for its own address
    /// bar and continuity, so reading them here costs nothing and means a frame that reaches a
    /// handoff, a report or a paired device carries where it came from.
    /// </remarks>
    private CaptureContextMetadata ContextFrom(string requestingDevice)
    {
        var context = _shell.Router.Context;
        return new(
            activeWorkspace: context.WorkspaceId ?? _shell.Router.Current.Location.Route.Value,
            // The profile's stable id, never its display name: a capture context that named
            // "Clayton" instead of a guid would put a player's handle into every report.
            activeProfile: context.ProfileId,
            activeMap: context.MapId,
            activePlan: context.PlanId,
            selectedEntity: context.SelectedEntity ?? _shell.Router.Current.SelectedEntity,
            priorScan: context.PriorScan,
            initiatingDevice: requestingDevice);
    }

    /// <summary>
    /// Decides whether a finished decode needs the player, or can be resolved where it stands.
    /// </summary>
    /// <remarks>
    /// The two cases that need asking are the two the player can actually answer: the recognizer
    /// read a different screen than the one that was armed, and the recognizer could not place the
    /// screen at all. Anything else — agreement, or a detected context with no disagreement flag —
    /// is resolved without interrupting, which is what the previous pass did for everything.
    /// </remarks>
    private void OnReviewRequested(object? sender, CaptureReviewRequestedEventArgs eventArgs)
    {
        var review = eventArgs.Review;
        var kind = review switch
        {
            { HasIntentDisagreement: true, DetectedContext: not null } => V2CaptureAttentionKind.IntentMismatch,
            { DetectedContext: null } => V2CaptureAttentionKind.UnknownContext,
            _ => (V2CaptureAttentionKind?)null,
        };

        if (kind is null)
        {
            _captureSessions.TryReview(
                review.SessionId,
                review.ArtifactId,
                review.DecodeRevision,
                CaptureReviewAction.UseDetected,
                "system-agreed");
            return;
        }

        lock (_gate)
        {
            _attention = new V2CaptureAttention(
                kind.Value,
                review.SessionId,
                review.ArtifactId,
                review.DecodeRevision,
                review.RequestedIntent,
                new StateRevision(_intentRevision),
                _settingDevice,
                kind == V2CaptureAttentionKind.IntentMismatch ? review.DetectedContext : null);
        }

        Push();
    }

    /// <summary>Shows what one capture was read as, and opens that item's Intel page.</summary>
    /// <remarks>
    /// The review names the alternates as well as the answer. A recognizer that was 62% sure and
    /// had two runners-up is a different thing from one that was certain, and a player looking at
    /// the wrong item needs to see the second guess to know that Retry is worth pressing.
    /// </remarks>
    private void OnItemIdentified(object? sender, CaptureItemIdentification identification)
    {
        var alternates = identification.Alternates.Count == 0
            ? string.Empty
            : $" · also {string.Join(", ", identification.Alternates.Take(2).Select(item => item.DisplayName))}";
        lock (_gate)
        {
            _attention = null;
            _review = new V2CaptureReview(
                identification.SessionId,
                identification.ArtifactId,
                0,
                identification.EffectiveIntent,
                RecognizedContext.Item,
                identification.ObservedUtc,
                $"{identification.Best.DisplayName} · {identification.Best.Confidence.Value:P0} sure{alternates}",
                identification.DiagnosticCode is { } code
                    ? $"Screenshot · {code}"
                    : "Screenshot");
        }

        _shell.ShowScannedItem(identification.Best.CanonicalId);
        Push();
    }

    private void OnLootScanEvaluated(object? sender, LootScanResult result)
    {
        // The same frame decided again, after a pin or a change of raid phase, keeps the player
        // on the item they were looking at instead of jumping back to the top of the list.
        var previous = _lootScan;
        var viewModel = new LootScanViewModel(
            result,
            controls: _lootScanControls,
            select: previous is not null &&
                    previous.Result.CaptureSessionId == result.CaptureSessionId &&
                    string.Equals(previous.Result.ArtifactId, result.ArtifactId, StringComparison.Ordinal)
                ? previous.SelectedDecision?.SourceAnchor
                : null);
        _lootScan = viewModel;
        lock (_gate)
        {
            // The frame that reached handoff is the only capture this session's single-review
            // lifecycle produces, so ordinal 0 names it without inventing a count.
            _attention = null;
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
        V2CaptureAttention? attention;
        ScanIntent armedIntent;
        long intentRevision;
        string settingDevice;
        lock (_gate)
        {
            armedIntent = _armedIntent;
            intentRevision = _intentRevision;
            settingDevice = _settingDevice;
            review = _review;
            attention = _attention;
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
                attention,
                review));
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
        _intelHandoff.ItemIdentified -= OnItemIdentified;
    }
}
