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
    private readonly ShellCaptureContextSource? _contextSource;
    private readonly ManualImageIntake? _manualIntake;
    private readonly FleaCaptureHandoff? _fleaHandoff;
    private readonly CompositeCaptureResultHandoff? _captureRouting;
    private readonly ILootScanRecognitionProgressSource? _lootRecognitionProgress;
    private TarkovCompanion.App.ViewModels.V2.Intel.FleaScanViewModel? _fleaScan;
    private LootScanViewModel? _lootScan;
    private readonly Lock _gate = new();
    private long _intentRevision;
    private ScanIntent _armedIntent = ScanIntent.Auto;
    private string _settingDevice = V2NavigationContext.ThisDesktop;
    private CaptureSessionId? _currentSessionId;
    private V2CaptureReview? _review;
    private V2CaptureAttention? _attention;
    private bool _disposed;
    private CaptureCorrelationId? _latestLootRecognition;

    public V2ShellCaptureBridge(
        V2ShellViewModel shell,
        ICaptureSessionService captureSessions,
        LootScanCaptureHandoff lootScanHandoff,
        IntelCaptureHandoff intelHandoff,
        WorkspaceOrigin origin,
        ILogger<V2ShellCaptureBridge>? logger = null,
        ILootScanWorkspaceControls? lootScanControls = null,
        ShellCaptureContextSource? contextSource = null,
        ManualImageIntake? manualIntake = null,
        FleaCaptureHandoff? fleaHandoff = null,
        CompositeCaptureResultHandoff? captureRouting = null,
        ILootScanRecognitionProgressSource? lootRecognitionProgress = null)
    {
        _lootRecognitionProgress = lootRecognitionProgress;
        if (lootRecognitionProgress is not null)
        {
            lootRecognitionProgress.LootRecognitionStarted += OnLootRecognitionStarted;
            lootRecognitionProgress.LootItemMatched += OnLootItemMatched;
            lootRecognitionProgress.LootRecognitionStopped += OnLootRecognitionStopped;
        }

        _fleaHandoff = fleaHandoff;
        if (fleaHandoff is not null)
        {
            fleaHandoff.ListingsRead += OnFleaListingsRead;
        }

        // #572: a capture read as something other than a loot container is the shell's cue that
        // the player moved on from Loot - see V2ShellViewModel.ReportNonLootScreenshot.
        _captureRouting = captureRouting;
        if (captureRouting is not null)
        {
            captureRouting.NonLootIntentHandled += OnNonLootIntentHandled;
        }

        _manualIntake = manualIntake;
        _lootScanControls = lootScanControls;
        _contextSource = contextSource;
        contextSource?.Bind(shell?.Router ?? throw new ArgumentNullException(nameof(shell)));
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
        _lootScanHandoff.LootScanStarted += OnLootScanStarted;
        _intelHandoff.ItemIdentified += OnItemIdentified;
        _shell.ManualImageRequested += OnManualImageRequested;
        _shell.CaptureCandidateChosen += OnCaptureCandidateChosen;
        Push();
    }

    /// <summary>
    /// A picture the player pasted, dropped or picked, read under the intent selected in the panel.
    /// </summary>
    /// <remarks>
    /// Whatever is already armed and waiting takes the picture, exactly as it would take the
    /// game's next screenshot. With nothing waiting, the selected intent is armed first, so
    /// "Loot decision, then choose a picture" is one step and not two.
    /// </remarks>
    private async void OnManualImageRequested(object? sender, V2ManualImage image)
    {
        try
        {
            if (_manualIntake is null)
            {
                _shell.ReportManualImage("Pictures cannot be read here");
                return;
            }

            var waiting = _captureSessions.Snapshot.Sessions.Any(session =>
                !session.IsTerminal && !session.IntentClaimed && !session.CancellationRequested);
            if (!waiting)
            {
                long revision;
                lock (_gate)
                {
                    revision = _intentRevision;
                }

                try
                {
                    OnCaptureArmRequested(this, new(image.Intent, new StateRevision(revision), V2NavigationContext.ThisDesktop));
                }
                catch (InvalidOperationException)
                {
                    _shell.ReportManualImage("The last capture is still being read");
                    return;
                }
            }

            var origin = image.Origin switch
            {
                V2ManualImageOrigin.Paste => ManualImageOrigin.Paste,
                V2ManualImageOrigin.Drop => ManualImageOrigin.Drop,
                _ => ManualImageOrigin.Picker,
            };
            var outcome = image switch
            {
                { FilePath: { } path } => await _manualIntake.SubmitFileAsync(path, origin, CancellationToken.None).ConfigureAwait(false),
                { Pixels: { } pixels } => await _manualIntake.SubmitImageAsync(pixels, origin, CancellationToken.None).ConfigureAwait(false),
                _ => new ManualImageOutcome(false, "There was no picture to read"),
            };
            _shell.ReportManualImage(outcome.Message);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not take in a picture the player chose.");
            _shell.ReportManualImage("That picture could not be read");
        }
    }

    /// <summary>A photographed flea screen: its rows open in Intel &gt; Flea, and the review says how many paid.</summary>
    private void OnFleaListingsRead(object? sender, FleaScanResult scan)
    {
        var viewModel = new TarkovCompanion.App.ViewModels.V2.Intel.FleaScanViewModel(
            scan,
            offline: _shell.FleaPricesAreOffline,
            timeProvider: _shell.FleaClock);
        _fleaScan = viewModel;
        lock (_gate)
        {
            _attention = null;
            _review = new V2CaptureReview(
                scan.SessionId,
                scan.ArtifactId,
                0,
                ScanIntent.Flea,
                RecognizedContext.Flea,
                scan.ObservedUtc,
                $"{scan.ItemName ?? "Flea offers"} · {viewModel.SummaryLabel}",
                "Screenshot · flea rows",
                false);
        }

        _shell.ShowFleaScan(viewModel);
        Push();
    }

    /// <summary>The player says the capture was a different candidate: Intel opens on that one.</summary>
    private void OnCaptureCandidateChosen(object? sender, V2CaptureCandidate candidate)
    {
        lock (_gate)
        {
            if (_review is not { } review || review.Candidates.All(offered => offered.CanonicalId != candidate.CanonicalId))
            {
                return;
            }

            _review = new V2CaptureReview(
                review.SessionId,
                review.ArtifactId,
                review.CaptureOrdinal,
                review.AnalyzedAs,
                review.DetectedContext,
                review.CapturedUtc,
                $"{candidate.DisplayName} · chosen by you",
                review.Provenance,
                false)
            {
                Candidates = review.Candidates,
                ChosenCandidateId = candidate.CanonicalId,
            };
        }

        _shell.ShowScannedItem(candidate.CanonicalId);
        Push();
    }

    private void OnCaptureArmRequested(object? sender, V2CaptureArmRequest request)
    {
        if (!CaptureIntentSupport.IsSupported(request.Intent))
        {
            // The picker shows these disabled. Whatever else asks is told no rather than given
            // a session that ends in silence.
            throw new InvalidOperationException($"Nothing reads a {request.Intent} capture yet.");
        }

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

        // [f920 capture] "Review result" opens the result: the Loot page, or the item in Intel.
        // It used to be acknowledged and dropped, like "Correct result", which is gone: a wrong
        // item is put right from the candidate list the review now shows.
        if (request.Resolution == V2CaptureResolutionKind.Review)
        {
            string? itemId;
            ScanIntent? analyzedAs;
            lock (_gate)
            {
                itemId = _review?.ChosenCandidateId;
                analyzedAs = _review?.AnalyzedAs;
            }

            if (itemId is not null)
            {
                _shell.ShowScannedItem(itemId);
            }
            else if (analyzedAs == ScanIntent.Flea && _fleaScan is { } fleaScan)
            {
                _shell.ShowFleaScan(fleaScan);
            }
            else if (_lootScan is { } lootScan)
            {
                _shell.ShowLootScanResult(lootScan);
            }

            _shell.CloseCaptureAfterReview();
        }

        // KeepCurrentIntent / ArmSelectedIntent belong to a device race, which this bridge never
        // raises, and re-analysing a frame as a different intent is something #271's coordinator
        // has no action for.
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
        // One builder for the arm, the watcher and manual intake, so they cannot disagree.
        if (_contextSource is not null)
        {
            return _contextSource.Describe(requestingDevice);
        }

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
                    : "Screenshot",
                canCorrect: false)
            {
                Candidates =
                [
                    .. new[] { identification.Best }
                        .Concat(identification.Alternates)
                        .Take(6)
                        .Select(item => new V2CaptureCandidate(item.CanonicalId, item.DisplayName, item.Confidence.Value)),
                ],
                ChosenCandidateId = identification.Best.CanonicalId,
            };
        }

        _shell.ShowScannedItem(identification.Best.CanonicalId);
        Push();
    }

    /// <summary>#572: the Loot page must show something long before the full result is ready.</summary>
    private void OnLootScanStarted(object? sender, EventArgs eventArgs) => _shell.ShowLootScanStarting();

    private void OnLootRecognitionStarted(object? sender, LootScanRecognitionStarted started)
    {
        var viewModel = LootScanViewModel.CreateProgress(started, _lootScanControls);
        lock (_gate)
        {
            _latestLootRecognition = started.CorrelationId;
            _lootScan = viewModel;
        }

        _shell.ShowLootScanProgress(viewModel);
    }

    private void OnLootItemMatched(object? sender, LootScanItemMatched matched) =>
        _shell.UpdateLootScanProgress(matched.CorrelationId, viewModel => viewModel.AddPending(matched));

    private void OnLootRecognitionStopped(object? sender, LootScanRecognitionStopped stopped) =>
        _shell.UpdateLootScanProgress(stopped.CorrelationId, viewModel => viewModel.StopProgress());

    private void OnLootScanEvaluated(object? sender, LootScanResult result)
    {
        // The same frame decided again, after a pin or a change of raid phase, keeps the player
        // on the item they were looking at instead of jumping back to the top of the list.
        LootScanViewModel viewModel;
        lock (_gate)
        {
            if (_latestLootRecognition is { } latest && latest != result.CorrelationId)
            {
                return;
            }

            var previous = _lootScan;
            viewModel = previous is { IsProgressive: true } && previous.CorrelationId == result.CorrelationId
                ? previous
                : new LootScanViewModel(
                    result,
                    controls: _lootScanControls,
                    select: previous is not null &&
                            previous.Result.CaptureSessionId == result.CaptureSessionId &&
                            string.Equals(previous.Result.ArtifactId, result.ArtifactId, StringComparison.Ordinal)
                        ? previous.SelectedDecision?.SourceAnchor
                        : null);
            _lootScan = viewModel;
        }

        _shell.ApplyLootScanResult(viewModel, result, applied =>
        {
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
                    $"{applied.TakeSummary}, {applied.SwapSummary}, {applied.LeaveSummary}, {applied.ReviewSummary}",
                    "Loot Scan decision",
                    false);
            }

            LootScanShown?.Invoke(applied);
            Push();
        });
    }

    /// <summary>#572: a Loot Scan result was handed to the shell; the paired tablet shows it too.</summary>
    public event Action<LootScanViewModel>? LootScanShown;

    private void OnCaptureSessionsChanged(object? sender, EventArgs eventArgs) => Push();

    /// <summary>#572: told apart from a loot container, whatever the shell was showing because it
    /// opened Loot for itself mid-raid returns to the map now instead of waiting for the countdown.</summary>
    private void OnNonLootIntentHandled(object? sender, ScanIntent intent) => _shell.ReportNonLootScreenshot();

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
        _lootScanHandoff.LootScanStarted -= OnLootScanStarted;
        _intelHandoff.ItemIdentified -= OnItemIdentified;
        if (_fleaHandoff is not null)
        {
            _fleaHandoff.ListingsRead -= OnFleaListingsRead;
        }

        if (_captureRouting is not null)
        {
            _captureRouting.NonLootIntentHandled -= OnNonLootIntentHandled;
        }

        if (_lootRecognitionProgress is not null)
        {
            _lootRecognitionProgress.LootRecognitionStarted -= OnLootRecognitionStarted;
            _lootRecognitionProgress.LootItemMatched -= OnLootItemMatched;
            _lootRecognitionProgress.LootRecognitionStopped -= OnLootRecognitionStopped;
        }

        _shell.ManualImageRequested -= OnManualImageRequested;
        _shell.CaptureCandidateChosen -= OnCaptureCandidateChosen;
    }
}
