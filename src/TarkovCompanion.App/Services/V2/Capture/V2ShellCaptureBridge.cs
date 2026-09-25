using System.Globalization;
using TarkovCompanion.App.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Application.Services.Workspaces;
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
    private readonly LootScanHistoryViewModel? _lootHistory;
    private readonly ILootScanWorkspaceControls? _lootScanControls;
    private readonly ShellCaptureContextSource? _contextSource;
    private readonly ManualImageIntake? _manualIntake;
    private readonly FleaCaptureHandoff? _fleaHandoff;
    private readonly CompositeCaptureResultHandoff? _captureRouting;
    private readonly ILootScanRecognitionProgressSource? _lootRecognitionProgress;
    private readonly IItemIntelService? _itemIntel;
    private readonly IWikiLinkOpener? _wikiOpener;
    private readonly RelayMarksBridge? _relayBridge;
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
    private CancellationTokenSource? _manualBatchCancellation;
    private string? _manualBatchId;
    private CaptureSessionId? _manualBatchSessionId;
    private readonly CaptureReanalysis? _reanalysis;
    private readonly UnsupportedScreenHandoff? _unsupported;
    private readonly IScreenshotRetentionStore? _tidyStore;

    // [#902 P8] The Loot Scan's verdict chip, kept across re-decisions, new scans and restarts.
    private readonly PageState _lootPage;
    private ScanSourceViewModel? _reviewSource;

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
        ILootScanRecognitionProgressSource? lootRecognitionProgress = null,
        IItemIntelService? itemIntel = null,
        IWikiLinkOpener? wikiOpener = null,
        RelayMarksBridge? relayBridge = null,
        LootScanHistoryViewModel? lootHistory = null,
        // #287: Read as…, the "not supported yet" screens, and the retention chip's tidy setting.
        CaptureReanalysis? reanalysis = null,
        UnsupportedScreenHandoff? unsupported = null,
        IScreenshotRetentionStore? tidyStore = null,
        IWorkspaceLayoutStore? layout = null)
    {
        _lootPage = new(layout, WorkspaceLayoutKeys.PageLoot);
        _reanalysis = reanalysis;
        _unsupported = unsupported;
        _tidyStore = tidyStore;
        if (unsupported is not null)
        {
            unsupported.ScreenRead += OnUnsupportedScreenRead;
        }

        _itemIntel = itemIntel;
        _wikiOpener = wikiOpener;
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
        _relayBridge = relayBridge;
        _lootHistory = lootHistory;
        if (lootHistory is not null)
        {
            _shell.LootHistory = lootHistory;
        }

        _shell.CaptureArmRequested += OnCaptureArmRequested;
        if (_relayBridge is not null)
        {
            _relayBridge.DesktopCaptureIntentRequested += OnDesktopCaptureIntentRequested;
        }
        _shell.CaptureResolutionRequested += OnCaptureResolutionRequested;
        _captureSessions.Changed += OnCaptureSessionsChanged;
        _captureSessions.ReviewRequested += OnReviewRequested;
        _lootScanHandoff.LootScanEvaluated += OnLootScanEvaluated;
        _lootScanHandoff.LootScanStarted += OnLootScanStarted;
        _intelHandoff.ItemIdentified += OnItemIdentified;
        _shell.ManualImageRequested += OnManualImageRequested;
        _shell.ManualImageBatchRequested += OnManualImageBatchRequested;
        _shell.ManualImageBatchCancelRequested += OnManualImageBatchCancelRequested;
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
                _shell.ReportManualImage(ShellText.CaptureCannotReadHere);
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
                    _shell.ReportManualImage(ShellText.CaptureStillReading);
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
                _ => new ManualImageOutcome(false, ShellText.CaptureNoPicture),
            };
            _shell.ReportManualImage(outcome.Message);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not take in a picture the player chose.");
            _shell.ReportManualImage(ShellText.CapturePictureUnreadable);
        }
    }

    /// <summary>Queues a bounded set under one armed intent while keeping every row visible.</summary>
    private async void OnManualImageBatchRequested(object? sender, V2ManualImageBatch batch)
    {
        CancellationTokenSource? cancellation = null;
        try
        {
            if (_manualIntake is null)
            {
                foreach (var item in batch.Items)
                {
                    _shell.ReportManualImageBatchItem(batch.BatchId, item.Id, ShellText.CaptureRowUnavailable, true);
                }

                _shell.ReportManualImageBatch(ShellText.CaptureCannotReadHere);
                return;
            }

            var waiting = _captureSessions.Snapshot.Sessions.LastOrDefault(session =>
                !session.IsTerminal && !session.IntentClaimed && !session.CancellationRequested);
            if (waiting is null)
            {
                long revision;
                lock (_gate)
                {
                    revision = _intentRevision;
                }

                OnCaptureArmRequested(
                    this,
                    new(batch.Intent, new StateRevision(revision), V2NavigationContext.ThisDesktop));
                waiting = _captureSessions.Snapshot.Sessions.LastOrDefault(session =>
                    !session.IsTerminal && !session.IntentClaimed && !session.CancellationRequested);
            }

            if (waiting is null)
            {
                throw new InvalidOperationException("The batch could not claim an armed capture session.");
            }

            cancellation = new CancellationTokenSource();
            lock (_gate)
            {
                _manualBatchCancellation?.Cancel();
                _manualBatchCancellation?.Dispose();
                _manualBatchCancellation = cancellation;
                _manualBatchId = batch.BatchId;
                _manualBatchSessionId = waiting.Request.SessionId;
            }

            var outcome = await _manualIntake.SubmitBatchAsync(
                    [.. batch.Items.Select(item => new ManualImageInput(
                        item.Id,
                        item.Label,
                        item.FilePath,
                        item.Pixels))],
                    batch.Origin switch
                    {
                        V2ManualImageOrigin.Paste => ManualImageOrigin.Paste,
                        V2ManualImageOrigin.Drop => ManualImageOrigin.Drop,
                        _ => ManualImageOrigin.Picker,
                    },
                    batch.BatchId,
                    waiting.Request.SessionId,
                    update => _shell.ReportManualImageBatchItem(
                        batch.BatchId,
                        update.Id,
                        update.Status,
                        update.IsTerminal,
                        update.CorrelationId),
                    cancellation.Token)
                .ConfigureAwait(false);
            _shell.ReportManualImageBatch(
                ShellText.CaptureBatchOutcome(outcome.Accepted, outcome.Failed, outcome.Cancelled));
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
        {
            _shell.ReportManualImageBatch(ShellText.CaptureBatchCancelled);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not take in a batch of pictures the player chose.");
            foreach (var item in batch.Items)
            {
                var row = _shell.CaptureBatchItems.FirstOrDefault(existing => existing.Id == item.Id);
                if (row is not null && !row.IsTerminal)
                {
                    _shell.ReportManualImageBatchItem(batch.BatchId, item.Id, ShellText.CaptureRowUnreadable, true);
                }
            }

            _shell.ReportManualImageBatch(ShellText.CaptureBatchUnreadable);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_manualBatchCancellation, cancellation))
                {
                    _manualBatchCancellation = null;
                }
            }

            cancellation?.Dispose();
        }
    }

    private void OnManualImageBatchCancelRequested(object? sender, string batchId)
    {
        CaptureSessionId? sessionId;
        lock (_gate)
        {
            if (!string.Equals(_manualBatchId, batchId, StringComparison.Ordinal))
            {
                return;
            }

            _manualBatchCancellation?.Cancel();
            sessionId = _manualBatchSessionId;
        }

        if (sessionId is { } id)
        {
            _captureSessions.Cancel(id, V2NavigationContext.ThisDesktop);
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
                V2ShellText.Format("V2.Shell.Capture.Evidence", CultureInfo.CurrentCulture, scan.ItemName ?? ShellText.CaptureReviewFleaOffers, viewModel.SummaryLabel),
                ShellText.CaptureReviewScreenshotFleaRows,
                false);
            _reviewSource = BuildSource(scan.ArtifactId, ScanIntent.Flea, null);
        }

        _shell.ShowFleaScan(viewModel);
        FleaScanShown?.Invoke(viewModel);
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

            _reanalysis?.Corrections.Record(new(
                ScanCorrectionKind.Candidate,
                review.ArtifactId,
                review.ChosenCandidateId ?? "none",
                candidate.CanonicalId,
                TimeProvider.System.GetUtcNow()));
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

            var sessionId = request.RequestedSessionId ?? new CaptureSessionId(Guid.NewGuid());
            var now = TimeProvider.System.GetUtcNow();
            var context = request.RequestedContext ?? ContextFrom(request.RequestingDevice);
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
                new("hold_screen", ShellText.CaptureHoldScreen)));
            if (!receipt.Accepted)
            {
                throw new InvalidOperationException($"Capture could not be armed: {receipt.Code}.");
            }

            _armedIntent = request.Intent;
            _intentRevision++;
            _settingDevice = request.RequestingDevice;
            _currentSessionId = sessionId;
            _review = null;
            _reviewSource = null;
            _attention = null;
        }

        Push();
    }

    /// <summary>Translates an applied paired command into the shell's ordinary Arm action.</summary>
    internal void OnDesktopCaptureIntentRequested(DesktopCaptureIntentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = request.Command;
        var wireContext = command.Context;
        var requestingDevice = string.IsNullOrWhiteSpace(request.DeviceName)
            ? $"paired:{request.DeviceId.Value:D}"
            : request.DeviceName;
        var context = new CaptureContextMetadata(
            activeWorkspace: _shell.Router.Context.WorkspaceId ?? _shell.Router.Current.Location.Route.Value,
            activeProfile: wireContext.ProfileId,
            activeMap: wireContext.MapId,
            activePlan: wireContext.PlanIds.FirstOrDefault(),
            selectedEntity: wireContext.ObjectiveIds.FirstOrDefault(),
            priorScan: wireContext.PreviousResultId,
            initiatingDevice: requestingDevice);
        _shell.ArmCaptureFromPairedDevice(
            command.Intent,
            requestingDevice,
            command.CaptureSessionId,
            context);
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
            else if (analyzedAs is not (ScanIntent.HealthAndCharacter or ScanIntent.ExtractsAndMap) && _lootScan is { } lootScan)
            {
                // A screen nothing reads has no page; an older loot result is not its answer.
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
        // #287: a screen nothing reads yet is answered "not supported yet" straight away. Asking
        // whether to analyse the HEALTH tab as the armed Loot would only lead to wrong advice.
        if (review.DetectedContext is RecognizedContext.HealthAndCharacter or RecognizedContext.ExtractsAndMap
            && _unsupported is not null)
        {
            _captureSessions.TryReview(
                review.SessionId,
                review.ArtifactId,
                review.DecodeRevision,
                CaptureReviewAction.UseDetected,
                "system-unsupported-screen");
            return;
        }

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

        // A screenshot the game wrote (the watched folder) never asks "what is this?": the player
        // presses the game's own key in a raid and cannot answer a prompt on a second screen, and
        // with a Loot or Stash capture armed every position screenshot asked (2026-09-24, "this is
        // unusable"). Unreadable ones are dropped quietly; a disagreement follows what was seen.
        if (IsWatchedFile(review.SessionId, review.ArtifactId))
        {
            _captureSessions.TryReview(
                review.SessionId,
                review.ArtifactId,
                review.DecodeRevision,
                kind == V2CaptureAttentionKind.UnknownContext ? CaptureReviewAction.Cancel : CaptureReviewAction.UseDetected,
                "system-watched-file");
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
            : ShellText.CaptureReviewAlso(string.Join(", ", identification.Alternates.Take(2).Select(item => item.DisplayName)));
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
                ShellText.CaptureReviewIdentified(identification.Best.DisplayName, identification.Best.Confidence.Value.ToString("P0", CultureInfo.CurrentCulture)) + alternates,
                identification.DiagnosticCode is { } code
                    ? ShellText.CaptureReviewScreenshotCode(code)
                    : ShellText.CaptureReviewScreenshot,
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
            _reviewSource = BuildSource(identification.ArtifactId, identification.EffectiveIntent, null);
        }

        _shell.ShowScannedItem(identification.Best.CanonicalId);
        Push();
    }

    /// <summary>#572: the Loot page must show something long before the full result is ready.</summary>
    private void OnLootScanStarted(object? sender, EventArgs eventArgs) => _shell.ShowLootScanStarting();

    private void OnLootRecognitionStarted(object? sender, LootScanRecognitionStarted started)
    {
        var viewModel = LootScanViewModel.CreateProgress(started, _lootScanControls, openWiki: WikiAction());
        viewModel.History = _lootHistory;
        viewModel.Source = BuildSource(started.ArtifactId, ScanIntent.Loot, started.CorrelationId);
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
                    openWiki: WikiAction(),
                    select: previous is not null &&
                            previous.Result.CaptureSessionId == result.CaptureSessionId &&
                            string.Equals(previous.Result.ArtifactId, result.ArtifactId, StringComparison.Ordinal)
                        ? previous.SelectedDecision?.SourceAnchor
                        : null,
                    filter: previous?.Filter ?? RememberedLootVerdict());
            if (!ReferenceEquals(viewModel, previous))
            {
                viewModel.PropertyChanged += RememberLootVerdict;
            }

            _lootScan = viewModel;
        }

        viewModel.History = _lootHistory;
        viewModel.Source ??= BuildSource(result.ArtifactId, ScanIntent.Loot, result.CorrelationId);
        _shell.ApplyLootScanResult(viewModel, result, applied =>
        {
            _ = RecordLootScanAsync(applied);
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
                    ShellText.CaptureReviewLootScan,
                    false);
            }

            LootScanShown?.Invoke(applied);
            Push();
        });
    }

    private TarkovCompanion.Core.Domain.Loot.LootScanVerdict? RememberedLootVerdict() =>
        _lootPage.Get("verdict") is { } stored &&
        Enum.TryParse<TarkovCompanion.Core.Domain.Loot.LootScanVerdict>(stored, out var verdict) &&
        Enum.IsDefined(verdict)
            ? verdict
            : null;

    private void RememberLootVerdict(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (sender is LootScanViewModel scan && eventArgs.PropertyName == nameof(LootScanViewModel.Filter))
        {
            _lootPage.Set("verdict", scan.Filter?.ToString());
        }
    }

    /// <summary>#274: every completed scan the page shows is saved with its ruleset version.</summary>
    private async Task RecordLootScanAsync(LootScanViewModel applied)
    {
        if (_lootHistory is null)
        {
            return;
        }

        try
        {
            await _lootHistory.RecordAsync(applied).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not save a loot scan.");
        }
    }

    /// <summary>#572: a Loot Scan result was handed to the shell; the paired tablet shows it too.</summary>
    public event Action<LootScanViewModel>? LootScanShown;

    /// <summary>#290: a flea screen was read and handed to Intel &gt; Flea; the paired tablet shows it too.</summary>
    public event Action<TarkovCompanion.App.ViewModels.V2.Intel.FleaScanViewModel>? FleaScanShown;

    private Func<string, Task>? WikiAction() =>
        _itemIntel is not null && _wikiOpener is not null ? OpenWikiAsync : null;

    private async Task OpenWikiAsync(string itemId)
    {
        try
        {
            var intel = await _itemIntel!.GetAsync(itemId, CancellationToken.None);
            _wikiOpener!.TryOpen(intel.WikiUri);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not open the catalog wiki link for loot item {ItemId}.", itemId);
        }
    }

    private void OnCaptureSessionsChanged(object? sender, EventArgs eventArgs)
    {
        RefreshManualBatchStatuses();
        ShowStashWhenRead();
        Push();
    }

    private void RefreshManualBatchStatuses()
    {
        string? batchId;
        CaptureSessionId? sessionId;
        lock (_gate)
        {
            batchId = _manualBatchId;
            sessionId = _manualBatchSessionId;
        }

        if (batchId is null || sessionId is null)
        {
            return;
        }

        var session = _captureSessions.Snapshot.Sessions.FirstOrDefault(item => item.Request.SessionId == sessionId);
        if (session is null)
        {
            return;
        }

        foreach (var row in _shell.CaptureBatchItems.Where(row => !row.IsTerminal))
        {
            var artifact = row.CorrelationId is { } correlation
                ? session.Artifacts.FirstOrDefault(item => item.CorrelationId == correlation)
                : null;
            if (artifact is not null)
            {
                var (status, terminal) = artifact.Disposition switch
                {
                    CaptureArtifactDisposition.Accepted => (ShellText.CaptureRowDone, true),
                    CaptureArtifactDisposition.NoChange when session.CancellationRequested => (ShellText.CaptureRowCancelled, true),
                    CaptureArtifactDisposition.NoChange => (ShellText.CaptureRowNoChange, true),
                    CaptureArtifactDisposition.RetryRequested => (ShellText.CaptureRowRetryRequested, true),
                    _ when artifact.Review is not null => (ShellText.CaptureRowNeedsReview, false),
                    _ => (ShellText.CaptureRowReading, false),
                };
                _shell.ReportManualImageBatchItem(batchId, row.Id, status, terminal, row.CorrelationId);
            }
            else if (session.IsTerminal)
            {
                _shell.ReportManualImageBatchItem(
                    batchId,
                    row.Id,
                    session.CancellationRequested ? ShellText.CaptureRowCancelled : ShellText.CaptureRowNotCompleted,
                    true,
                    row.CorrelationId);
            }
        }

        if (!_shell.HasActiveManualImageBatch)
        {
            lock (_gate)
            {
                if (string.Equals(_manualBatchId, batchId, StringComparison.Ordinal))
                {
                    _manualBatchId = null;
                    _manualBatchSessionId = null;
                }
            }
        }
    }

    /// <summary>#572: told apart from a loot container, whatever the shell was showing because it
    /// opened Loot for itself mid-raid returns to the map now instead of waiting for the countdown.</summary>
    private void OnNonLootIntentHandled(object? sender, ScanIntent intent) => _shell.ReportNonLootScreenshot();

    /// <summary>
    /// #287: a screen the companion recognised and cannot read yet. The capture panel opens on it,
    /// says so, says what to do instead, and still offers Read as… for a wrong guess.
    /// </summary>
    private void OnUnsupportedScreenRead(object? sender, UnsupportedScreen screen)
    {
        lock (_gate)
        {
            _attention = null;
            _review = new V2CaptureReview(
                screen.SessionId,
                screen.ArtifactId,
                0,
                screen.Intent,
                screen.Intent == ScanIntent.HealthAndCharacter
                    ? RecognizedContext.HealthAndCharacter
                    : RecognizedContext.ExtractsAndMap,
                screen.ObservedUtc,
                screen.Title,
                screen.Instead,
                canCorrect: false);
            _reviewSource = BuildSource(screen.ArtifactId, screen.Intent, screen.CorrelationId);
        }

        _shell.OpenCaptureForReview();
        Push();
    }

    private bool IsWatchedFile(CaptureSessionId sessionId, string artifactId) =>
        _captureSessions.Snapshot.Sessions
            .Where(session => session.Request.SessionId == sessionId)
            .SelectMany(session => session.Artifacts)
            .Any(item => string.Equals(item.ArtifactId, artifactId, StringComparison.Ordinal)
                && item.DeliveryKind == CaptureDeliveryKind.WatchedFile);

    /// <summary>The retention chip and Read as… for one captured frame.</summary>
    private ScanSourceViewModel BuildSource(string artifactId, ScanIntent readAs, CaptureCorrelationId? correlation)
    {
        var artifact = _captureSessions.Snapshot.Sessions
            .SelectMany(session => session.Artifacts)
            .LastOrDefault(item => string.Equals(item.ArtifactId, artifactId, StringComparison.Ordinal));
        var held = _reanalysis?.CanReadAgain(artifactId) == true;
        var correction = correlation is { } rereading ? _reanalysis?.Corrections.ForRereading(rereading) : null;
        var source = new ScanSourceViewModel(
            artifact?.SourceKind ?? CaptureSourceKind.UserSelectedImage,
            _lastTidy,
            readAs,
            held,
            held ? intent => ReadAsAsync(artifactId, intent) : null,
            correction is null ? null : ShellText.ReadAsByYou(IntentLabel(correction.To), IntentLabel(correction.From)));
        if (_tidyStore is not null)
        {
            _ = ApplyTidyAsync(source);
        }

        return source;
    }

    private static string IntentLabel(string intent) =>
        Enum.TryParse<ScanIntent>(intent, out var parsed) ? ScanReadAs.Label(parsed) : intent;

    private ScreenshotRetentionSettings? _lastTidy;

    private CaptureCorrelationId? _stashAfter;

    private void ShowStashWhenRead()
    {
        CaptureCorrelationId wanted;
        lock (_gate)
        {
            if (_stashAfter is not { } pending)
            {
                return;
            }

            wanted = pending;
        }

        var artifact = _captureSessions.Snapshot.Sessions
            .SelectMany(session => session.Artifacts)
            .LastOrDefault(item => item.CorrelationId == wanted);
        if (artifact is null || artifact.Disposition == CaptureArtifactDisposition.Pending)
        {
            return;
        }

        lock (_gate)
        {
            if (_stashAfter != wanted)
            {
                return;
            }

            _stashAfter = null;
        }

        _shell.ShowStashScan();
    }

    private async Task ApplyTidyAsync(ScanSourceViewModel source)
    {
        try
        {
            var tidy = await _tidyStore!.GetAsync(CancellationToken.None).ConfigureAwait(false);
            _lastTidy = tidy;
            source.ApplyTidy(tidy);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not read the screenshot tidy setting for a retention chip.");
        }
    }

    /// <summary>#287 "Read as…": the same frame, armed and submitted again under <paramref name="intent"/>.</summary>
    private async Task<ScanReadAsOutcome> ReadAsAsync(string artifactId, ScanIntent intent)
    {
        if (_reanalysis is null)
        {
            return new(false, ShellText.ReadAsCannotHere);
        }

        try
        {
            var outcome = await _reanalysis.ReadAsAsync(
                    artifactId,
                    intent,
                    (wanted, context) =>
                    {
                        long revision;
                        lock (_gate)
                        {
                            revision = _intentRevision;
                        }

                        var sessionId = new CaptureSessionId(Guid.NewGuid());
                        OnCaptureArmRequested(this, new(
                            wanted,
                            new StateRevision(revision),
                            V2NavigationContext.ThisDesktop,
                            sessionId,
                            context));
                        return sessionId;
                    },
                    CancellationToken.None)
                .ConfigureAwait(true);
            if (outcome is { Started: true, Rereading: { } rereading } && intent == ScanIntent.Stash)
            {
                // The Stash page loads when it is opened, so it is opened once the snapshot is in.
                lock (_gate)
                {
                    _stashAfter = rereading;
                }

                ShowStashWhenRead();
            }

            return outcome;
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogInformation(exception, "Read as {Intent} could not arm.", intent);
            return new(false, ShellText.CaptureStillReading);
        }
    }

    private void Push()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        CaptureSessionSnapshot? session;
        V2CaptureReview? review;
        V2CaptureAttention? attention;
        ScanSourceViewModel? reviewSource;
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
            reviewSource = _review is null ? null : _reviewSource;
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
            _shell.ShowCaptureReviewSource(reviewSource);
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
        if (_relayBridge is not null)
        {
            _relayBridge.DesktopCaptureIntentRequested -= OnDesktopCaptureIntentRequested;
        }
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
        _shell.ManualImageBatchRequested -= OnManualImageBatchRequested;
        _shell.ManualImageBatchCancelRequested -= OnManualImageBatchCancelRequested;
        _shell.CaptureCandidateChosen -= OnCaptureCandidateChosen;
        if (_unsupported is not null)
        {
            _unsupported.ScreenRead -= OnUnsupportedScreenRead;
        }

        lock (_gate)
        {
            _manualBatchCancellation?.Cancel();
            _manualBatchCancellation?.Dispose();
            _manualBatchCancellation = null;
        }
    }
}
