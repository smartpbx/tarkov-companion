using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Threading.Channels;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Application.Services.CaptureSessions;

public sealed record CaptureSessionOptions
{
    public CaptureSessionOptions(
        int queueCapacity = 32,
        long maximumRetainedPixelBytes = 96 * 1024 * 1024,
        int maximumDecodeAttempts = 4,
        TimeSpan? decodeRetryDelay = null,
        TimeSpan? reviewTimeout = null,
        TimeSpan? intentLifetime = null,
        TimeSpan? deduplicationLifetime = null,
        int historyLimit = 256,
        int sessionLimit = 256,
        int maximumCapturesPerSession = 64,
        int maximumRedecodeRevisions = 8,
        TimeSpan? handoffTimeout = null)
    {
        QueueCapacity = queueCapacity is >= 1 and <= 4096
            ? queueCapacity
            : throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        MaximumRetainedPixelBytes = maximumRetainedPixelBytes is >= 1 and <= 1024L * 1024 * 1024
            ? maximumRetainedPixelBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumRetainedPixelBytes));
        MaximumDecodeAttempts = maximumDecodeAttempts is >= 1 and <= 16
            ? maximumDecodeAttempts
            : throw new ArgumentOutOfRangeException(nameof(maximumDecodeAttempts));
        DecodeRetryDelay = Positive(decodeRetryDelay ?? TimeSpan.FromMilliseconds(150), nameof(decodeRetryDelay));
        ReviewTimeout = Positive(reviewTimeout ?? TimeSpan.FromMinutes(2), nameof(reviewTimeout));
        IntentLifetime = Positive(intentLifetime ?? TimeSpan.FromSeconds(15), nameof(intentLifetime));
        DeduplicationLifetime = Positive(deduplicationLifetime ?? TimeSpan.FromMinutes(10), nameof(deduplicationLifetime));
        HistoryLimit = historyLimit is >= 16 and <= 100_000
            ? historyLimit
            : throw new ArgumentOutOfRangeException(nameof(historyLimit));
        SessionLimit = sessionLimit is >= 16 and <= 10_000
            ? sessionLimit
            : throw new ArgumentOutOfRangeException(nameof(sessionLimit));
        MaximumCapturesPerSession = maximumCapturesPerSession is >= 1 and <= 4096
            ? maximumCapturesPerSession
            : throw new ArgumentOutOfRangeException(nameof(maximumCapturesPerSession));
        MaximumRedecodeRevisions = maximumRedecodeRevisions is >= 0 and <= 64
            ? maximumRedecodeRevisions
            : throw new ArgumentOutOfRangeException(nameof(maximumRedecodeRevisions));
        HandoffTimeout = Positive(handoffTimeout ?? TimeSpan.FromSeconds(15), nameof(handoffTimeout));
    }

    public int QueueCapacity { get; }

    public long MaximumRetainedPixelBytes { get; }

    public int MaximumDecodeAttempts { get; }

    public TimeSpan DecodeRetryDelay { get; }

    public TimeSpan ReviewTimeout { get; }

    public TimeSpan IntentLifetime { get; }

    public TimeSpan DeduplicationLifetime { get; }

    public int HistoryLimit { get; }

    public int SessionLimit { get; }

    public int MaximumCapturesPerSession { get; }

    public int MaximumRedecodeRevisions { get; }

    public TimeSpan HandoffTimeout { get; }

    private static TimeSpan Positive(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero && value <= TimeSpan.FromHours(24)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);
}

/// <summary>Owns capture intake, review, and transient pixels; recognition stays behind a seam.</summary>
/// <remarks>
/// The queue has one reader because capture ordinals are evidence, not scheduling hints. Intake
/// is serialized under the state lock and a full queue returns an explicit rejection; no hidden
/// population of waiting writers can retain sources beyond the advertised capacity or reorder
/// around one another. Heavy decode/analysis work is admitted through the shared runtime
/// supervisor one item at a time, while review waits hold no supervisor CPU slot.
/// </remarks>
public sealed class CaptureSessionCoordinator : ICaptureSessionService
{
    private static readonly ProducerIdentity CaptureProducer = new(
        "Tarkov Companion contextual capture",
        V2ContractVersion.Current.ToString());

    private readonly object _gate = new();
    private readonly ICaptureWorkScheduler _scheduler;
    private readonly ICaptureSessionPipeline _pipeline;
    private readonly ICaptureResultHandoff _handoff;
    private readonly WorkspaceOrigin _defaultOrigin;
    private readonly TimeProvider _timeProvider;
    private readonly CaptureSessionOptions _options;
    private readonly Channel<QueuedCapture> _queue;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<CaptureSessionId, MutableSession> _sessions = [];
    private readonly Dictionary<string, DeduplicationEntry> _deduplication = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingReview> _pendingReviews = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _reviewTasks = [];
    private readonly List<CaptureTimingSnapshot> _timings = [];
    private readonly List<CaptureSessionNotice> _notices = [];
    private readonly Task _pump;
    private CaptureSessionId? _armedSessionId;
    // A claimed intent keeps the one global slot until content is proven usable or the intent is
    // restored. Without this fence a faster second arm could strand the first session forever.
    private CaptureSessionId? _claimedIntentSessionId;
    private long _nextIntakeSequence;
    private long _nextNoticeSequence;
    private long _accepted;
    private long _duplicate;
    private long _rejected;
    private long _pixelsInUse;
    private int _queueDepth;
    private bool _stopping;
    private bool _disposed;
    private EventHandler? _changed;

    // [#893] Optional: every existing composition and test predates it.
    private readonly Microsoft.Extensions.Logging.ILogger<CaptureSessionCoordinator>? _logger;

    // #712 0-12: every capture's milestones, whatever it turns out to be. Optional like the logger.
    private readonly ICaptureStageTimeline? _stageTimeline;

    public CaptureSessionCoordinator(
        ICaptureWorkScheduler scheduler,
        ICaptureSessionPipeline pipeline,
        ICaptureResultHandoff handoff,
        WorkspaceOrigin defaultOrigin,
        TimeProvider? timeProvider = null,
        CaptureSessionOptions? options = null,
        Microsoft.Extensions.Logging.ILogger<CaptureSessionCoordinator>? logger = null,
        ICaptureStageTimeline? stageTimeline = null)
    {
        _logger = logger;
        _stageTimeline = stageTimeline;
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        _defaultOrigin = defaultOrigin ?? throw new ArgumentNullException(nameof(defaultOrigin));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options = options ?? new();
        _queue = Channel.CreateBounded<QueuedCapture>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _pump = Task.Run(() => PumpAsync(_lifetime.Token), CancellationToken.None);
    }

    public event EventHandler? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    public event EventHandler<CaptureReviewRequestedEventArgs>? ReviewRequested;

    public event EventHandler<CaptureAcceptedEventArgs>? Accepted;

    public CaptureSessionServiceSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return SnapshotUnsafe();
            }
        }
    }

    public CaptureArmReceipt Arm(CaptureArmRequest arm)
    {
        ArgumentNullException.ThrowIfNull(arm);
        EventHandler? changed;
        CaptureSessionRequest request;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            ExpireArmedUnsafe(now);
            if (arm.Request.RequestedUtc > now)
            {
                return new(false, arm.Request.SessionId, "intent_request_in_future");
            }

            if (_sessions.TryGetValue(arm.Request.SessionId, out var known))
            {
                return known.IsTerminal
                    ? new(false, arm.Request.SessionId, "terminal_session_already_known")
                    : new(true, arm.Request.SessionId, "already_known");
            }

            if (_armedSessionId is not null || _claimedIntentSessionId is not null)
            {
                return new(false, arm.Request.SessionId, "intent_already_armed");
            }

            if (!EnsureSessionCapacityUnsafe())
            {
                return new(false, arm.Request.SessionId, "capture_session_capacity_reached");
            }

            // A remote or malformed caller cannot hold the one armed slot for days. The
            // configured lifetime is a ceiling as well as the default.
            var maximumExpiry = now.Add(_options.IntentLifetime);
            var expiresUtc = arm.Request.ExpiresUtc is { } requestedExpiry && requestedExpiry < maximumExpiry
                ? requestedExpiry
                : maximumExpiry;
            if (expiresUtc <= now)
            {
                return new(false, arm.Request.SessionId, "intent_already_expired");
            }

            request = new(
                arm.Request.SessionId,
                arm.Request.Intent,
                arm.Request.Origin,
                arm.Request.RequestedUtc,
                arm.Request.ProfileId,
                arm.Request.MapId,
                expiresUtc);
            var session = new MutableSession(request, arm.Context, arm.Guidance);
            session.AppendSession(CaptureSessionStage.Armed, now, "intent_armed");
            session.AppendSession(CaptureSessionStage.AwaitingCapture, now, arm.Guidance.Code);
            _sessions.Add(request.SessionId, session);
            _armedSessionId = request.SessionId;
            AddNoticeUnsafe(CaptureSessionNoticeKind.Progress, now, null, request.SessionId, null, "intent_armed");
            changed = _changed;
        }

        Notify(changed);
        _ = ExpireIntentAsync(request.SessionId, _lifetime.Token);
        return new(true, request.SessionId, "armed");
    }

    public ValueTask<CaptureQueueReceipt> EnqueueAsync(
        CaptureSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        // A watched file began its timeline when its name was seen; this is a no-op for it. A
        // picture handed over by hand (a paste, a drop) starts here.
        _stageTimeline?.Begin(submission.CorrelationId, submission.SubmittedUtc);
        CaptureQueueReceipt receipt;
        var disposeSource = false;
        QueuedCapture? rejectedQueued = null;
        CaptureSessionId? rearmedSessionId = null;
        var retainedPixelBytes = submission.Source is MemoryCaptureSource retainedSource
            ? retainedSource.RetainedPixelBytes
            : 0;
        EventHandler? changed;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (cancellationToken.IsCancellationRequested)
            {
                _rejected++;
                disposeSource = true;
                receipt = new(
                    -1,
                    CaptureQueueDisposition.Cancelled,
                    submission.CorrelationId,
                    now,
                    "capture_admission_cancelled");
            }
            else if (_stopping || _disposed)
            {
                _rejected++;
                disposeSource = true;
                receipt = new(
                    -1,
                    CaptureQueueDisposition.Rejected,
                    submission.CorrelationId,
                    now,
                    "capture_service_stopping");
            }
            else if (submission.SubmittedUtc > now)
            {
                _rejected++;
                disposeSource = true;
                receipt = new(
                    -1,
                    CaptureQueueDisposition.Rejected,
                    submission.CorrelationId,
                    now,
                    "capture_submission_in_future");
            }
            else if (_queueDepth >= _options.QueueCapacity)
            {
                _rejected++;
                disposeSource = true;
                AddNoticeUnsafe(
                    CaptureSessionNoticeKind.QueueRejected,
                    now,
                    submission.CorrelationId,
                    submission.SessionId,
                    null,
                    "capture_queue_full");
                receipt = new(
                    -1,
                    CaptureQueueDisposition.Rejected,
                    submission.CorrelationId,
                    now,
                    "capture_queue_full");
            }
            else if (retainedPixelBytes > _options.MaximumRetainedPixelBytes - _pixelsInUse)
            {
                _rejected++;
                disposeSource = true;
                AddNoticeUnsafe(
                    CaptureSessionNoticeKind.QueueRejected,
                    now,
                    submission.CorrelationId,
                    submission.SessionId,
                    null,
                    "decoded_pixel_budget_exceeded");
                receipt = new(
                    -1,
                    CaptureQueueDisposition.Rejected,
                    submission.CorrelationId,
                    now,
                    "decoded_pixel_budget_exceeded");
            }
            else
            {
                var binding = ResolveSessionAtIntakeUnsafe(submission, now);
                if (binding is null)
                {
                    _rejected++;
                    disposeSource = true;
                    AddNoticeUnsafe(
                        CaptureSessionNoticeKind.QueueRejected,
                        now,
                        submission.CorrelationId,
                        submission.SessionId,
                        null,
                        "unknown_or_terminal_session");
                    receipt = new(
                        -1,
                        CaptureQueueDisposition.Rejected,
                        submission.CorrelationId,
                        now,
                        "unknown_or_terminal_session");
                }
                else if (!binding.Session.CanAcceptContext(submission.Context))
                {
                    RollBackBindingUnsafe(binding);
                    _rejected++;
                    disposeSource = true;
                    AddNoticeUnsafe(
                        CaptureSessionNoticeKind.QueueRejected,
                        now,
                        submission.CorrelationId,
                        binding.Session.Request.SessionId,
                        null,
                        "capture_context_changed_since_arm");
                    receipt = new(
                        -1,
                        CaptureQueueDisposition.Rejected,
                        submission.CorrelationId,
                        now,
                        "capture_context_changed_since_arm");
                }
                else if (binding.Session.AdmissionCount >= _options.MaximumCapturesPerSession)
                {
                    RollBackBindingUnsafe(binding);
                    _rejected++;
                    disposeSource = true;
                    AddNoticeUnsafe(
                        CaptureSessionNoticeKind.QueueRejected,
                        now,
                        submission.CorrelationId,
                        binding.Session.Request.SessionId,
                        null,
                        "capture_session_artifact_limit");
                    receipt = new(
                        -1,
                        CaptureQueueDisposition.Rejected,
                        submission.CorrelationId,
                        now,
                        "capture_session_artifact_limit");
                }
                else
                {
                    var intakeSequence = _nextIntakeSequence;
                    binding.Session.ActiveCaptureCount++;
                    binding.Session.AdmissionCount++;
                    _pixelsInUse += retainedPixelBytes;
                    var queued = new QueuedCapture(
                        intakeSequence,
                        submission,
                        now,
                        binding.Session.Request.SessionId,
                        binding.ClaimedArmedIntent,
                        binding.CreatedSession,
                        retainedPixelBytes);
                    if (!_queue.Writer.TryWrite(queued))
                    {
                        _rejected++;
                        disposeSource = true;
                        rejectedQueued = queued;
                        if (RollBackIntakeUnsafe(queued, now))
                        {
                            rearmedSessionId = queued.BoundSessionId;
                        }
                        receipt = new(
                            intakeSequence,
                            CaptureQueueDisposition.Rejected,
                            submission.CorrelationId,
                            now,
                            "capture_service_stopping");
                    }
                    else
                    {
                        binding.Session.BindContext(submission.Context);
                        _nextIntakeSequence = checked(_nextIntakeSequence + 1);
                        _queueDepth++;
                        _accepted++;
                        receipt = new(
                            intakeSequence,
                            CaptureQueueDisposition.Accepted,
                            submission.CorrelationId,
                            now,
                            "queued");
                    }
                }
            }

            changed = _changed;
        }

        if (disposeSource)
        {
            DisposeSourceSafely(
                submission.Source,
                submission.CorrelationId,
                rejectedQueued?.BoundSessionId ?? submission.SessionId);
            if (rejectedQueued is not null)
            {
                ReleaseQueuedPixelReservation(rejectedQueued, "capture_admission_rolled_back");
            }
        }

        if (rearmedSessionId is { } sessionId)
        {
            _ = ExpireIntentAsync(sessionId, _lifetime.Token);
        }

        Notify(changed);
        return ValueTask.FromResult(receipt);
    }

    public bool TryReview(
        CaptureSessionId sessionId,
        string artifactId,
        int decodeRevision,
        CaptureReviewAction action,
        string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        var trimmedOrigin = origin.Trim();
        if (trimmedOrigin.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        lock (_gate)
        {
            if (!_pendingReviews.TryGetValue(artifactId, out var pending)
                || pending.SessionId != sessionId
                || pending.DecodeRevision != decodeRevision)
            {
                return false;
            }

            return pending.Decision.TrySetResult(new(
                action,
                decodeRevision,
                trimmedOrigin,
                _timeProvider.GetUtcNow()));
        }
    }

    public bool Cancel(CaptureSessionId sessionId, string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        var trimmedOrigin = origin.Trim();
        if (trimmedOrigin.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        EventHandler? changed;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.IsTerminal)
            {
                return false;
            }

            if (_armedSessionId == sessionId)
            {
                _armedSessionId = null;
            }

            if (_claimedIntentSessionId == sessionId)
            {
                _claimedIntentSessionId = null;
            }

            var now = _timeProvider.GetUtcNow();
            session.CancellationRequested = true;
            StartCancellationUnsafe(session);
            foreach (var pending in _pendingReviews.Values.Where(item => item.SessionId == sessionId))
            {
                pending.Decision.TrySetResult(new(
                    CaptureReviewAction.Cancel,
                    pending.DecodeRevision,
                    trimmedOrigin,
                    now));
            }

            if (session.ActiveCaptureCount == 0)
            {
                session.AppendSession(CaptureSessionStage.Cancelled, now, "session_cancelled");
                PruneSessionsUnsafe();
            }

            changed = _changed;
        }

        Notify(changed);
        return true;
    }

    /// <remarks>
    /// #937: a manual batch whose last picture could not be admitted has nothing left to close its
    /// session, and cancelling it threw away the reviews of every picture already accepted.
    /// </remarks>
    public bool EndWhenIdle(CaptureSessionId sessionId, string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        EventHandler? changed;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.IsTerminal || session.CancellationRequested)
            {
                return false;
            }

            if (_armedSessionId == sessionId)
            {
                _armedSessionId = null;
            }

            if (_claimedIntentSessionId == sessionId)
            {
                _claimedIntentSessionId = null;
            }

            session.RequestTerminal(CaptureSessionStage.Complete);
            if (session.ActiveCaptureCount == 0 && session.PendingTerminalStage is { } stage)
            {
                session.AppendSession(
                    stage,
                    _timeProvider.GetUtcNow(),
                    stage switch
                    {
                        CaptureSessionStage.Complete => "session_complete",
                        CaptureSessionStage.Failed => "session_failed",
                        _ => "session_cancelled",
                    });
                PruneSessionsUnsafe();
            }

            changed = _changed;
        }

        Notify(changed);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping = true;
            _queue.Writer.TryComplete();
            var now = _timeProvider.GetUtcNow();
            foreach (var session in _sessions.Values.Where(item => !item.IsTerminal))
            {
                session.CancellationRequested = true;
                StartCancellationUnsafe(session);
                if (session.ActiveCaptureCount == 0)
                {
                    session.AppendSession(CaptureSessionStage.Cancelled, now, "shutdown");
                }
                else
                {
                    session.RequestTerminal(CaptureSessionStage.Cancelled);
                }
            }

            _armedSessionId = null;
            _claimedIntentSessionId = null;

            foreach (var pending in _pendingReviews.Values)
            {
                pending.Decision.TrySetResult(new(
                    CaptureReviewAction.Cancel,
                    pending.DecodeRevision,
                    "shutdown",
                    now));
            }
        }

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // Linked hostile callbacks cannot prevent the remaining owned cleanup.
        }
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        while (_queue.Reader.TryRead(out var queued))
        {
            DisposeSourceSafely(
                queued.Submission.Source,
                queued.Submission.CorrelationId,
                queued.BoundSessionId);
            ReleaseQueuedPixelReservation(queued, "shutdown");
            lock (_gate)
            {
                _queueDepth--;
                if (_sessions.TryGetValue(queued.BoundSessionId, out var session))
                {
                    CompleteActiveCaptureUnsafe(queued, session, _timeProvider.GetUtcNow());
                }
            }
        }

        Task[] reviews;
        lock (_gate)
        {
            reviews = [.. _reviewTasks];
        }

        try
        {
            await Task.WhenAll(reviews).ConfigureAwait(false);
        }
        catch
        {
            // Review loops contain their own cleanup; a fault is observed here so disposal can
            // still perform the final lease audit below.
        }

        Task[] cancellationDeliveries;
        lock (_gate)
        {
            cancellationDeliveries = [.. _sessions.Values.Select(item => item.CancellationDelivery)];
        }

        await Task.WhenAll(cancellationDeliveries).ConfigureAwait(false);

        lock (_gate)
        {
            foreach (var review in _pendingReviews.Values)
            {
                if (_sessions.TryGetValue(review.SessionId, out var session))
                {
                    var artifact = session.Artifacts.FirstOrDefault(item => item.ArtifactId == review.ArtifactId);
                    if (artifact is not null)
                    {
                        artifact.PixelsRetained = false;
                    }
                }

                ReleasePixelsUnsafe(review.Pixels, review.SessionId, review.ArtifactId, "shutdown");
            }

            _pendingReviews.Clear();
            foreach (var session in _sessions.Values)
            {
                session.Cancellation.Dispose();
            }
        }

        _lifetime.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        await foreach (var queued in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (_gate)
            {
                _queueDepth--;
            }

            var reviewOwnsCompletion = false;
            try
            {
                reviewOwnsCompletion = await ProcessQueuedAsync(queued, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                DisposeSourceSafely(
                    queued.Submission.Source,
                    queued.Submission.CorrelationId,
                    queued.BoundSessionId);
                ReleaseQueuedPixelReservation(queued, "capture_source_released");
                if (!reviewOwnsCompletion)
                {
                    CompleteActiveCapture(queued);
                }
            }
        }
    }

    private async Task<bool> ProcessQueuedAsync(QueuedCapture queued, CancellationToken cancellationToken)
    {
        MutableSession session;
        lock (_gate)
        {
            session = _sessions[queued.BoundSessionId];
        }

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.Cancellation.Token);
        var prepared = PreparedCapture.Rejected;
        var work = await _scheduler.RunAsync(
                new(
                    queued.Submission.CorrelationId,
                    $"capture:{queued.IntakeSequence}",
                    CaptureWorkPriority.Intake),
                async token =>
                {
                    prepared = await PrepareAsync(queued, session, token).ConfigureAwait(false);
                },
                operation.Token)
            .ConfigureAwait(false);
        if (!work.Accepted || !work.Succeeded)
        {
            FinalizeUnstartedCapture(
                queued,
                session,
                session.CancellationRequested ? "capture_cancelled" : work.DiagnosticCode ?? "capture_work_rejected");
            return false;
        }

        if (prepared.IsDuplicate || prepared.Pixels is null || prepared.Session is null || prepared.Artifact is null)
        {
            return false;
        }

        var review = ReviewLoopAsync(queued, prepared, cancellationToken);
        lock (_gate)
        {
            _reviewTasks.Add(review);
        }

        _ = review.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (_gate)
                {
                    _reviewTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    private async Task<PreparedCapture> PrepareAsync(
        QueuedCapture queued,
        MutableSession session,
        CancellationToken cancellationToken)
    {
        var decodeStarted = _timeProvider.GetUtcNow();
        _stageTimeline?.Reached(queued.Submission.CorrelationId, CaptureTimelineKinds.Dequeued);
        CapturePixelLease? pixels = null;
        MutableArtifact? artifact = null;
        var reserved = false;
        var transferred = false;
        var duplicate = false;
        var rearmedIntent = false;
        var attempts = 0;
        string? diagnostic = null;
        try
        {
            while (attempts < _options.MaximumDecodeAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;
                var result = await queued.Submission.Source.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (result.Pixels is not null)
                {
                    pixels = result.Pixels;
                    break;
                }

                diagnostic = result.DiagnosticCode ?? "decode_failed";
                if (!result.Retryable || attempts == _options.MaximumDecodeAttempts)
                {
                    break;
                }

                await Task.Delay(_options.DecodeRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            if (pixels is null)
            {
                FinalizeUnstartedCapture(queued, session, diagnostic ?? "decode_failed", attempts);
                return PreparedCapture.Rejected;
            }

            // The file's bytes and its decode are one call of the source (the loader reads and
            // decodes together), so "decoded" covers both.
            _stageTimeline?.Reached(queued.Submission.CorrelationId, CaptureTimelineKinds.Decoded);

            cancellationToken.ThrowIfCancellationRequested();
            var admissionReservation = queued.TakeReservedPixelBytes();
            if (admissionReservation != 0 && admissionReservation != pixels.ByteLength)
            {
                pixels.Dispose();
                pixels = null;
                ReleasePixelBytes(
                    admissionReservation,
                    session.Request.SessionId,
                    null,
                    "decoded_pixel_reservation_mismatch");
                FinalizeUnstartedCapture(queued, session, "decoded_pixel_reservation_mismatch", attempts);
                return PreparedCapture.Rejected;
            }

            if (admissionReservation == 0 && !TryReservePixels(pixels))
            {
                pixels.Dispose();
                pixels = null;
                FinalizeUnstartedCapture(queued, session, "decoded_pixel_budget_exceeded", attempts);
                return PreparedCapture.Rejected;
            }

            reserved = true;
            var digest = ContentIdentity(pixels.Image);
            EventHandler? changed;
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                TrimDeduplicationUnsafe(now);
                // #287: "Read as…" re-reads a frame the player already captured, on purpose, so
                // the same pixels arriving again are the request rather than a double shutter.
                if (_deduplication.ContainsKey(digest) && queued.Submission.ReanalysisOf is null)
                {
                    duplicate = true;
                    _duplicate++;
                    ReleaseAdmissionUnsafe(session);
                    rearmedIntent = RestoreIntentAfterUnusableCaptureUnsafe(queued, session, now);
                    AddNoticeUnsafe(
                        CaptureSessionNoticeKind.Duplicate,
                        now,
                        queued.Submission.CorrelationId,
                        session.Request.SessionId,
                        null,
                        "duplicate_content");
                    ReleasePixelsUnsafe(pixels, session.Request.SessionId, null, "duplicate_content");
                    reserved = false;
                    pixels = null;
                    changed = _changed;
                }
                else if (session.IsTerminal || session.CancellationRequested)
                {
                    ReleasePixelsUnsafe(pixels, session.Request.SessionId, null, "stale_or_cancelled_session");
                    reserved = false;
                    pixels = null;
                    RecordNoChangeArtifactUnsafe(
                        queued,
                        session,
                        "stale_or_cancelled_session",
                        attempts,
                        CaptureSessionStage.Cancelled,
                        now);
                    RejectUnsafe(queued, "stale_or_cancelled_session");
                    changed = _changed;
                }
                else
                {
                    ConsumeClaimedIntentUnsafe(queued, session);
                    var capturedUtc = pixels.Image.CapturedUtc == default
                        ? queued.Submission.SubmittedUtc
                        : pixels.Image.CapturedUtc.ToUniversalTime();
                    var artifactId = $"capture-{Guid.NewGuid():N}";
                    artifact = session.AddArtifact(
                        artifactId,
                        queued.Submission.DeliveryKind,
                        queued.Submission.CorrelationId,
                        queued.Submission.SourceKind,
                        capturedUtc,
                        queued.Submission.SubmittedUtc,
                        queued.Submission.BatchId,
                        attempts,
                        digest,
                        CreateProvenance(
                            queued.Submission.SourceKind,
                            queued.Submission.DeliveryKind,
                            capturedUtc));
                    for (var retry = 1; retry <= attempts; retry++)
                    {
                        session.AppendCapture(artifact, CaptureSessionStage.Settling, now, retry == 1 ? "source_settled" : "decode_retry");
                        session.AppendCapture(artifact, CaptureSessionStage.Decoding, now, $"decode_attempt_{retry}");
                    }

                    session.AppendCapture(artifact, CaptureSessionStage.DetectingContext, now, "detecting_context");
                    _deduplication[digest] = new(session.Request.SessionId, artifactId, now.Add(_options.DeduplicationLifetime));
                    changed = _changed;
                }
            }

            Notify(changed);
            if (rearmedIntent)
            {
                _ = ExpireIntentAsync(session.Request.SessionId, _lifetime.Token);
            }

            if (pixels is null)
            {
                _stageTimeline?.Complete(queued.Submission.CorrelationId, _timeProvider.GetUtcNow(), CaptureTimelineKinds.Unread);
                return duplicate ? PreparedCapture.Duplicate : PreparedCapture.Rejected;
            }

            var analysisStarted = _timeProvider.GetUtcNow();
            CaptureAnalysis analysis;
            try
            {
                analysis = await _pipeline.AnalyzeAsync(
                        new(
                            session.Request.SessionId,
                            artifact!.ArtifactId,
                            artifact.CaptureOrdinal,
                            session.Request.Intent,
                            session.Context,
                            pixels.Image,
                            queued.Submission.CorrelationId,
                            0),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                FinalizeArtifactFailure(queued, session, artifact!, pixels, "analysis_cancelled", cancelled: true);
                reserved = false;
                pixels = null;
                return PreparedCapture.Rejected;
            }
            catch
            {
                FinalizeArtifactFailure(queued, session, artifact!, pixels, "analysis_failed", cancelled: false);
                reserved = false;
                pixels = null;
                return PreparedCapture.Rejected;
            }

            _stageTimeline?.Reached(queued.Submission.CorrelationId, CaptureTimelineKinds.Recognised);
            CaptureReviewRequest review;
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                if (session.CancellationRequested || session.IsTerminal)
                {
                    FinalizeArtifactUnsafe(queued, session, artifact!, CaptureSessionStage.Cancelled, "analysis_cancelled");
                    artifact!.PixelsRetained = false;
                    ReleasePixelsUnsafe(pixels, session.Request.SessionId, artifact.ArtifactId, "analysis_cancelled");
                    reserved = false;
                    pixels = null;
                    changed = _changed;
                    review = null!;
                }
                else
                {
                    artifact!.Analysis = analysis;
                    artifact.Confidence = analysis.Confidence;
                    session.AppendCapture(artifact, CaptureSessionStage.DetectingRegions, now, "context_detected");
                    session.AppendCapture(artifact, CaptureSessionStage.Matching, now, "matching_complete");
                    session.AppendCapture(artifact, CaptureSessionStage.EnrichingProfile, now, "profile_context_frozen");
                    session.AppendCapture(artifact, CaptureSessionStage.Recommending, now, "analysis_ready");
                    session.AppendCapture(artifact, CaptureSessionStage.AwaitingReview, now, "review_required");
                    var disagreement = RequiresReview(session.Request.Intent, analysis);
                    review = new(
                        session.Request.SessionId,
                        artifact.ArtifactId,
                        artifact.CaptureOrdinal,
                        session.Request.Intent,
                        analysis.DetectedContext,
                        disagreement,
                        artifact.DecodeRevision,
                        now.Add(_options.ReviewTimeout),
                        queued.Submission.CorrelationId);
                    artifact.Review = review;
                    var pending = new PendingReview(
                        session.Request.SessionId,
                        artifact.ArtifactId,
                        artifact.DecodeRevision,
                        pixels);
                    _pendingReviews.Add(artifact.ArtifactId, pending);
                    AddNoticeUnsafe(
                        CaptureSessionNoticeKind.ReviewRequired,
                        now,
                        queued.Submission.CorrelationId,
                        session.Request.SessionId,
                        artifact.ArtifactId,
                        disagreement ? "intent_context_disagreement" : "decision_pause");
                    AddTimingUnsafe(new(
                        queued.IntakeSequence,
                        queued.Submission.CorrelationId,
                        session.Request.SessionId,
                        artifact.ArtifactId,
                        ElapsedMilliseconds(queued.EnqueuedUtc, decodeStarted),
                        ElapsedMilliseconds(decodeStarted, analysisStarted),
                        ElapsedMilliseconds(analysisStarted, now),
                        0));
                    transferred = true;
                    changed = _changed;
                }
            }

            Notify(changed);
            if (!transferred)
            {
                return PreparedCapture.Rejected;
            }

            NotifyReviewRequested(review);
            return new(session, artifact, pixels, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (artifact is not null && pixels is not null && reserved)
            {
                FinalizeArtifactFailure(queued, session, artifact, pixels, "capture_cancelled", cancelled: true);
                reserved = false;
                pixels = null;
            }
            else
            {
                FinalizeUnstartedCapture(queued, session, "capture_cancelled", attempts);
            }

            return PreparedCapture.Rejected;
        }
        catch
        {
            if (artifact is not null && pixels is not null && reserved)
            {
                FinalizeArtifactFailure(queued, session, artifact, pixels, "capture_prepare_failed", cancelled: false);
                reserved = false;
                pixels = null;
            }
            else
            {
                FinalizeUnstartedCapture(queued, session, "capture_prepare_failed", attempts);
            }

            return PreparedCapture.Rejected;
        }
        finally
        {
            if (pixels is not null && !transferred)
            {
                if (reserved)
                {
                    ReleasePixels(pixels, session.Request.SessionId, artifact?.ArtifactId, "capture_cleanup");
                }
                else
                {
                    pixels.Dispose();
                }
            }
        }
    }

    private async Task ReviewLoopAsync(
        QueuedCapture queued,
        PreparedCapture prepared,
        CancellationToken cancellationToken)
    {
        var session = prepared.Session!;
        var artifact = prepared.Artifact!;
        var pixels = prepared.Pixels!;
        var reviewStarted = _timeProvider.GetUtcNow();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            session.Cancellation.Token);
        try
        {
            while (true)
            {
                PendingReview pending;
                DateTimeOffset expiresUtc;
                lock (_gate)
                {
                    if (!_pendingReviews.TryGetValue(artifact.ArtifactId, out pending!))
                    {
                        return;
                    }

                    expiresUtc = artifact.Review!.ExpiresUtc;
                }

                ReviewDecision decision;
                try
                {
                    decision = await WaitForReviewDecisionAsync(pending, expiresUtc, operation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested)
                {
                    decision = new(
                        CaptureReviewAction.Cancel,
                        pending.DecodeRevision,
                        "session-cancelled",
                        _timeProvider.GetUtcNow());
                }

                if (decision.Action == CaptureReviewAction.Redecode)
                {
                    EventHandler? redecodeChanged;
                    var mayRedecode = false;
                    lock (_gate)
                    {
                        if (_pendingReviews.TryGetValue(artifact.ArtifactId, out var current)
                            && ReferenceEquals(current, pending))
                        {
                            _pendingReviews.Remove(artifact.ArtifactId);
                            artifact.Review = null;
                        }

                        var correctedUtc = session.NormalizeTimestamp(decision.ChangedUtc < reviewStarted
                            ? reviewStarted
                            : decision.ChangedUtc);
                        artifact.Corrections.Add(new(
                            CaptureReviewAction.Redecode,
                            session.Request.Intent,
                            artifact.Analysis?.DetectedContext,
                            decision.DecodeRevision,
                            correctedUtc,
                            decision.Origin));
                        mayRedecode = artifact.DecodeRevision < _options.MaximumRedecodeRevisions
                            && !session.CancellationRequested
                            && !session.IsTerminal;
                        AddNoticeUnsafe(
                            CaptureSessionNoticeKind.ReviewResolved,
                            correctedUtc,
                            artifact.CorrelationId,
                            session.Request.SessionId,
                            artifact.ArtifactId,
                            mayRedecode ? "redecode_requested" : "redecode_limit_reached");
                        redecodeChanged = _changed;
                    }

                    Notify(redecodeChanged);
                    if (mayRedecode
                        && await RedecodeAsync(queued, session, artifact, pixels, operation.Token).ConfigureAwait(false))
                    {
                        reviewStarted = _timeProvider.GetUtcNow();
                        continue;
                    }

                    decision = new(
                        CaptureReviewAction.Cancel,
                        artifact.DecodeRevision,
                        session.CancellationRequested ? "session-cancelled" : "redecode-failed",
                        _timeProvider.GetUtcNow(),
                        session.CancellationRequested
                            ? null
                            : mayRedecode ? "redecode_failed" : "redecode_limit_reached");
                }

                CaptureHandoffRequest? handoffRequest = null;
                var rearm = false;
                EventHandler? changed;
                lock (_gate)
                {
                    if (_pendingReviews.TryGetValue(artifact.ArtifactId, out var current)
                        && ReferenceEquals(current, pending))
                    {
                        _pendingReviews.Remove(artifact.ArtifactId);
                    }

                    var correctedUtc = session.NormalizeTimestamp(decision.ChangedUtc < reviewStarted
                        ? reviewStarted
                        : decision.ChangedUtc);
                    var correction = new CaptureCorrection(
                        decision.Action,
                        session.Request.Intent,
                        artifact.Analysis?.DetectedContext,
                        decision.DecodeRevision,
                        correctedUtc,
                        decision.Origin);
                    artifact.Corrections.Add(correction);

                    var noChangeCode = decision.NoChangeCode ?? NoChangeCode(decision.Action, artifact.Analysis);
                    if (noChangeCode is null
                        && decision.Action is (CaptureReviewAction.UseDetected or CaptureReviewAction.UseArmedIntent)
                        && !session.CancellationRequested
                        && !session.IsTerminal)
                    {
                        var effectiveIntent = decision.Action == CaptureReviewAction.UseArmedIntent
                            ? session.Request.Intent
                            : IntentForDetectedContext(artifact.Analysis!.DetectedContext, session.Request.Intent);
                        handoffRequest = new(
                            session.Request.SessionId,
                            artifact.ArtifactId,
                            artifact.Analysis!,
                            session.Context,
                            artifact.CorrelationId,
                            artifact.SourceKind,
                            artifact.CapturedUtc,
                            artifact.SubmittedUtc,
                            artifact.BatchId,
                            artifact.DeliveryKind,
                            artifact.Provenance,
                            artifact.DecodeRevision,
                            decision.Action,
                            effectiveIntent,
                            correction);
                        artifact.HandoffDisposition = CaptureHandoffDisposition.Pending;
                        artifact.DiagnosticCode = "capture_handoff_pending";
                    }
                    else if (noChangeCode is not null)
                    {
                        // [#893] Every one of these ended silently, so a real in-raid Container
                        // frame that produced no loot scan could not say which gate held it.
                        if (_logger is not null)
                        {
                            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(
                                _logger,
                                "Capture {CorrelationId} ended with no change: {Code} ({Action}, context {Context}, confidence {Confidence:0.00}).",
                                artifact.CorrelationId,
                                noChangeCode,
                                decision.Action,
                                artifact.Analysis?.DetectedContext?.ToString() ?? "none",
                                artifact.Analysis?.Confidence.Value ?? 0);
                        }

                        session.AppendCapture(artifact, CaptureSessionStage.Cancelled, correctedUtc, noChangeCode);
                        artifact.IsTerminal = true;
                        artifact.Disposition = CaptureArtifactDisposition.NoChange;
                        artifact.DiagnosticCode = noChangeCode;
                        RemoveDeduplicationUnsafe(artifact);
                        if (queued.Submission.EndSessionAfterReview)
                        {
                            session.RequestTerminal(CaptureSessionStage.Cancelled);
                        }

                        AddNoticeUnsafe(
                            CaptureSessionNoticeKind.NoChange,
                            correctedUtc,
                            artifact.CorrelationId,
                            session.Request.SessionId,
                            artifact.ArtifactId,
                            noChangeCode);
                    }
                    else
                    {
                        switch (decision.Action)
                        {
                            case CaptureReviewAction.RetryCapture:
                                session.AppendCapture(artifact, CaptureSessionStage.Cancelled, correctedUtc, "retry_requested");
                                artifact.IsTerminal = true;
                                artifact.Disposition = CaptureArtifactDisposition.RetryRequested;
                                artifact.DiagnosticCode = "retry_requested";
                                RemoveDeduplicationUnsafe(artifact);
                                rearm = RearmIntentUnsafe(session, correctedUtc, "retry_requested");
                                if (!rearm)
                                {
                                    artifact.DiagnosticCode = "retry_rearm_conflict";
                                    session.RequestTerminal(CaptureSessionStage.Failed);
                                    AddNoticeUnsafe(
                                        CaptureSessionNoticeKind.NoChange,
                                        correctedUtc,
                                        artifact.CorrelationId,
                                        session.Request.SessionId,
                                        artifact.ArtifactId,
                                        "retry_rearm_conflict");
                                }

                                break;

                            default:
                                session.AppendCapture(artifact, CaptureSessionStage.Cancelled, correctedUtc, "review_cancelled");
                                artifact.IsTerminal = true;
                                artifact.Disposition = CaptureArtifactDisposition.NoChange;
                                artifact.DiagnosticCode = "review_cancelled";
                                RemoveDeduplicationUnsafe(artifact);
                                session.CancellationRequested = true;
                                session.RequestTerminal(CaptureSessionStage.Cancelled);
                                StartCancellationUnsafe(session);
                                AddNoticeUnsafe(
                                    CaptureSessionNoticeKind.NoChange,
                                    correctedUtc,
                                    artifact.CorrelationId,
                                    session.Request.SessionId,
                                    artifact.ArtifactId,
                                    "review_cancelled");
                                break;
                        }
                    }

                    artifact.Review = null;
                    artifact.PixelsRetained = false;
                    AddNoticeUnsafe(
                        CaptureSessionNoticeKind.ReviewResolved,
                        correctedUtc,
                        artifact.CorrelationId,
                        session.Request.SessionId,
                        artifact.ArtifactId,
                        decision.Action.ToString());
                    UpdateReviewTimingUnsafe(queued.IntakeSequence, reviewStarted, correctedUtc);
                    ReleasePixelsUnsafe(
                        pixels,
                        session.Request.SessionId,
                        artifact.ArtifactId,
                        handoffRequest is null ? "review_finished" : "handoff_pixels_released");
                    changed = _changed;
                }

                Notify(changed);

                if (rearm)
                {
                    _ = ExpireIntentAsync(session.Request.SessionId, _lifetime.Token);
                }

                if (handoffRequest is null)
                {
                    _stageTimeline?.Complete(artifact.CorrelationId, _timeProvider.GetUtcNow(), CaptureTimelineKinds.Unread);
                }
                else
                {
                    // Before the handoff, so a Loot scan's own Complete inside it is already a Loot one.
                    _stageTimeline?.Classify(artifact.CorrelationId, CaptureTimelineKinds.For(handoffRequest.EffectiveIntent));
                }

                if (handoffRequest is not null)
                {
                    var handoff = await AcceptHandoffAsync(handoffRequest, operation.Token).ConfigureAwait(false);
                    CaptureAcceptedEventArgs? accepted = null;
                    lock (_gate)
                    {
                        var completedUtc = session.NormalizeTimestamp(_timeProvider.GetUtcNow());
                        artifact.HandoffDisposition = handoff.Disposition;
                        artifact.DiagnosticCode = handoff.Code;
                        if (handoff.Disposition == CaptureHandoffDisposition.DurablyAccepted)
                        {
                            session.AppendCapture(artifact, CaptureSessionStage.Complete, completedUtc, handoff.Code);
                            artifact.IsTerminal = true;
                            artifact.Disposition = CaptureArtifactDisposition.Accepted;
                            if (queued.Submission.EndSessionAfterReview)
                            {
                                session.RequestTerminal(CaptureSessionStage.Complete);
                            }

                            accepted = new(handoffRequest);
                        }
                        else
                        {
                            session.AppendCapture(artifact, CaptureSessionStage.Failed, completedUtc, handoff.Code);
                            artifact.IsTerminal = true;
                            artifact.Disposition = CaptureArtifactDisposition.NoChange;
                            if (handoff.Disposition == CaptureHandoffDisposition.Rejected)
                            {
                                RemoveDeduplicationUnsafe(artifact);
                            }

                            if (queued.Submission.EndSessionAfterReview)
                            {
                                session.RequestTerminal(CaptureSessionStage.Failed);
                            }

                            AddNoticeUnsafe(
                                CaptureSessionNoticeKind.NoChange,
                                completedUtc,
                                artifact.CorrelationId,
                                session.Request.SessionId,
                                artifact.ArtifactId,
                                handoff.Code);
                        }

                        AddNoticeUnsafe(
                            CaptureSessionNoticeKind.HandoffResolved,
                            completedUtc,
                            artifact.CorrelationId,
                            session.Request.SessionId,
                            artifact.ArtifactId,
                            handoff.Code);
                        changed = _changed;
                    }

                    if (accepted is not null)
                    {
                        NotifyAccepted(accepted);
                    }

                    Notify(changed);
                    _stageTimeline?.Reached(artifact.CorrelationId, CaptureTimelineKinds.Shown);
                    _stageTimeline?.Complete(
                        artifact.CorrelationId,
                        _timeProvider.GetUtcNow(),
                        handoff.Disposition == CaptureHandoffDisposition.DurablyAccepted ? null : CaptureTimelineKinds.Unread);
                }

                return;
            }
        }
        finally
        {
            EventHandler? changed;
            lock (_gate)
            {
                _pendingReviews.Remove(artifact.ArtifactId);
                if (!artifact.IsTerminal)
                {
                    FinalizeArtifactUnsafe(queued, session, artifact, CaptureSessionStage.Cancelled, "review_abandoned");
                }

                if (!pixels.IsDisposed)
                {
                    artifact.Review = null;
                    artifact.PixelsRetained = false;
                    ReleasePixelsUnsafe(pixels, session.Request.SessionId, artifact.ArtifactId, "review_cleanup");
                }

                CompleteActiveCaptureUnsafe(queued, session, _timeProvider.GetUtcNow());
                changed = _changed;
            }

            Notify(changed);
            // A review abandoned or cancelled on the way: still one line, as Unread.
            _stageTimeline?.Complete(artifact.CorrelationId, _timeProvider.GetUtcNow(), CaptureTimelineKinds.Unread);
        }
    }

    private async ValueTask<CaptureHandoffResult> AcceptHandoffAsync(
        CaptureHandoffRequest request,
        CancellationToken cancellationToken)
    {
        var timeout = new CancellationTokenSource(_options.HandoffTimeout, _timeProvider);
        var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        Task<CaptureHandoffResult>? acknowledgement = null;
        try
        {
            // Invoke the dependency on a worker so a hostile synchronous prefix cannot run
            // before the acknowledgement deadline is armed.
            acknowledgement = Task.Run(
                async () => await _handoff.AcceptAsync(request, operation.Token).ConfigureAwait(false),
                CancellationToken.None);
            var result = await acknowledgement.WaitAsync(operation.Token).ConfigureAwait(false);
            return result ?? new(
                CaptureHandoffDisposition.AcknowledgementUnknown,
                "capture_handoff_acknowledgement_unknown");
        }
        catch (OperationCanceledException)
        {
            // Cancellation can race a durable accept. Without an acknowledgement the truthful
            // state is unknown, never rejected and never safe to publish as accepted.
            return new(
                CaptureHandoffDisposition.AcknowledgementUnknown,
                "capture_handoff_acknowledgement_unknown");
        }
        catch
        {
            return new(
                CaptureHandoffDisposition.AcknowledgementUnknown,
                "capture_handoff_acknowledgement_unknown");
        }
        finally
        {
            if (acknowledgement is { IsCompleted: false })
            {
                _ = ObserveLateHandoffAsync(acknowledgement, operation, timeout);
            }
            else
            {
                operation.Dispose();
                timeout.Dispose();
            }
        }
    }

    private static async Task ObserveLateHandoffAsync(
        Task<CaptureHandoffResult> acknowledgement,
        CancellationTokenSource operation,
        CancellationTokenSource timeout)
    {
        try
        {
            _ = await acknowledgement.ConfigureAwait(false);
        }
        catch
        {
            // The visible result is already acknowledgement-unknown. This observer retains
            // token ownership until the dependency actually returns and consumes a late fault.
        }
        finally
        {
            operation.Dispose();
            timeout.Dispose();
        }
    }

    private static string? NoChangeCode(CaptureReviewAction action, CaptureAnalysis? analysis)
    {
        if (action is not (CaptureReviewAction.UseDetected or CaptureReviewAction.UseArmedIntent))
        {
            return null;
        }

        if (analysis is null || !analysis.IsAvailable)
        {
            return "capture_result_unavailable_no_change";
        }

        if (analysis.IsAmbiguous)
        {
            return "capture_result_ambiguous_no_change";
        }

        if (RecognitionThresholds.Classify(analysis.Confidence) == RecognitionDecision.NoMatch)
        {
            return "capture_result_below_threshold_no_change";
        }

        return analysis.DetectedContext is null
            ? "capture_context_unknown_no_change"
            : null;
    }

    private async Task<ReviewDecision> WaitForReviewDecisionAsync(
        PendingReview pending,
        DateTimeOffset expiresUtc,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = expiresUtc - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return new(
                    CaptureReviewAction.Cancel,
                    pending.DecodeRevision,
                    "review-expired",
                    _timeProvider.GetUtcNow(),
                    "review_expired_no_change");
            }

            try
            {
                return await pending.Decision.Task
                    .WaitAsync(remaining, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // TimeProvider timers may wake on a coarse boundary. Re-check the provider and
                // arm the remaining interval instead of silently losing the expiry.
            }
        }
    }

    private async Task<bool> RedecodeAsync(
        QueuedCapture queued,
        MutableSession session,
        MutableArtifact artifact,
        CapturePixelLease pixels,
        CancellationToken cancellationToken)
    {
        var revision = checked(artifact.DecodeRevision + 1);
        CaptureAnalysis? analysis = null;
        var result = await _scheduler.RunAsync(
                new(
                    artifact.CorrelationId,
                    $"redecode:{queued.IntakeSequence}",
                    CaptureWorkPriority.ReviewBlocking),
                async token =>
                {
                    analysis = await _pipeline.AnalyzeAsync(
                            new(
                                session.Request.SessionId,
                                artifact.ArtifactId,
                                artifact.CaptureOrdinal,
                                session.Request.Intent,
                                session.Context,
                                pixels.Image,
                                artifact.CorrelationId,
                                revision),
                            token)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Accepted || !result.Succeeded || analysis is null)
        {
            return false;
        }
        CaptureReviewRequest review;
        lock (_gate)
        {
            if (session.CancellationRequested || session.IsTerminal || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            artifact.DecodeRevision = revision;
            artifact.Analysis = analysis;
            artifact.Confidence = analysis.Confidence;
            var now = _timeProvider.GetUtcNow();
            review = new(
                session.Request.SessionId,
                artifact.ArtifactId,
                artifact.CaptureOrdinal,
                session.Request.Intent,
                analysis.DetectedContext,
                RequiresReview(session.Request.Intent, analysis),
                revision,
                now.Add(_options.ReviewTimeout),
                artifact.CorrelationId);
            artifact.Review = review;
            _pendingReviews[artifact.ArtifactId] = new(
                session.Request.SessionId,
                artifact.ArtifactId,
                revision,
                pixels);
            AddNoticeUnsafe(
                CaptureSessionNoticeKind.ReviewRequired,
                now,
                artifact.CorrelationId,
                session.Request.SessionId,
                artifact.ArtifactId,
                "redecode_review_required");
        }

        NotifyReviewRequested(review);
        NotifyChanged();
        return true;
    }

    private IntakeBinding? ResolveSessionAtIntakeUnsafe(CaptureSubmission submission, DateTimeOffset now)
    {
        ExpireArmedUnsafe(now);
        if (submission.SessionId is { } requested)
        {
            if (!_sessions.TryGetValue(requested, out var existing)
                || existing.IsTerminal
                || existing.CancellationRequested)
            {
                return null;
            }

            var claimed = false;
            if (_armedSessionId == requested)
            {
                _armedSessionId = null;
                _claimedIntentSessionId = requested;
                existing.IntentClaimed = true;
                claimed = true;
            }

            return new(existing, claimed, false);
        }

        if (_armedSessionId is { } armed && _sessions.TryGetValue(armed, out var armedSession))
        {
            _armedSessionId = null;
            _claimedIntentSessionId = armed;
            armedSession.IntentClaimed = true;
            return new(armedSession, true, false);
        }

        if (!EnsureSessionCapacityUnsafe())
        {
            return null;
        }

        var request = new CaptureSessionRequest(
            new(Guid.NewGuid()),
            ScanIntent.Auto,
            _defaultOrigin,
            submission.SubmittedUtc,
            submission.Context.ActiveProfile,
            submission.Context.ActiveMap);
        var session = new MutableSession(
            request,
            submission.Context,
            new("automatic_context", "Detect the visible screen and review the result."))
        {
            IntentClaimed = true,
        };
        session.AppendSession(CaptureSessionStage.Armed, now, "automatic_session");
        session.AppendSession(CaptureSessionStage.AwaitingCapture, now, "capture_received");
        _sessions.Add(request.SessionId, session);
        return new(session, false, true);
    }

    private async Task ExpireIntentAsync(
        CaptureSessionId sessionId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset expiresUtc;
            lock (_gate)
            {
                if (_armedSessionId != sessionId
                    || !_sessions.TryGetValue(sessionId, out var session)
                    || session.Request.ExpiresUtc is not { } currentExpiry)
                {
                    return;
                }

                expiresUtc = currentExpiry;
            }

            var delay = expiresUtc - _timeProvider.GetUtcNow();
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            EventHandler? changed;
            var expired = false;
            lock (_gate)
            {
                if (_armedSessionId != sessionId)
                {
                    return;
                }

                expired = ExpireArmedUnsafe(_timeProvider.GetUtcNow());
                changed = _changed;
            }

            if (expired)
            {
                Notify(changed);
                return;
            }

            // A coarse timer can wake a fraction early. Looping recalculates and re-arms the
            // remaining duration against the injected provider.
        }
    }

    private bool ExpireArmedUnsafe(DateTimeOffset now)
    {
        if (_armedSessionId is not { } sessionId
            || !_sessions.TryGetValue(sessionId, out var session)
            || session.Request.ExpiresUtc is not { } expiresUtc
            || expiresUtc > now)
        {
            return false;
        }

        _armedSessionId = null;
        if (_claimedIntentSessionId == sessionId)
        {
            _claimedIntentSessionId = null;
        }
        session.AppendSession(CaptureSessionStage.Cancelled, now, "intent_expired");
        AddNoticeUnsafe(CaptureSessionNoticeKind.IntentExpired, now, null, sessionId, null, "intent_expired");
        PruneSessionsUnsafe();
        return true;
    }

    private void FinalizeUnstartedCapture(
        QueuedCapture queued,
        MutableSession session,
        string code,
        int decodeAttempts = 0)
    {
        _stageTimeline?.Complete(queued.Submission.CorrelationId, _timeProvider.GetUtcNow(), CaptureTimelineKinds.Unread);
        var rearmed = false;
        EventHandler? changed;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            RecordNoChangeArtifactUnsafe(
                queued,
                session,
                code,
                decodeAttempts,
                session.CancellationRequested
                    ? CaptureSessionStage.Cancelled
                    : CaptureSessionStage.Failed,
                now);
            RejectUnsafe(queued, code);
            if (queued.ClaimedArmedIntent && !session.CancellationRequested)
            {
                rearmed = RearmIntentUnsafe(session, now, code);
                if (!rearmed)
                {
                    session.RequestTerminal(CaptureSessionStage.Failed);
                    AddNoticeUnsafe(
                        CaptureSessionNoticeKind.NoChange,
                        now,
                        queued.Submission.CorrelationId,
                        session.Request.SessionId,
                        null,
                        "intent_restore_failed");
                }
            }
            else if (session.CancellationRequested)
            {
                session.RequestTerminal(CaptureSessionStage.Cancelled);
            }
            else if (queued.CreatedSession || queued.Submission.EndSessionAfterReview)
            {
                session.RequestTerminal(CaptureSessionStage.Failed);
            }

            changed = _changed;
        }

        if (rearmed)
        {
            _ = ExpireIntentAsync(session.Request.SessionId, _lifetime.Token);
        }

        Notify(changed);
    }

    private void RecordNoChangeArtifactUnsafe(
        QueuedCapture queued,
        MutableSession session,
        string code,
        int decodeAttempts,
        CaptureSessionStage terminalStage,
        DateTimeOffset changedUtc)
    {
        var artifact = session.AddArtifact(
            $"capture-{Guid.NewGuid():N}",
            queued.Submission.DeliveryKind,
            queued.Submission.CorrelationId,
            queued.Submission.SourceKind,
            queued.Submission.SubmittedUtc,
            queued.Submission.SubmittedUtc,
            queued.Submission.BatchId,
            Math.Max(0, decodeAttempts),
            contentDigest: null,
            CreateProvenance(
                queued.Submission.SourceKind,
                queued.Submission.DeliveryKind,
                queued.Submission.SubmittedUtc));
        for (var attempt = 1; attempt <= decodeAttempts; attempt++)
        {
            session.AppendCapture(artifact, CaptureSessionStage.Settling, changedUtc, attempt == 1
                ? "source_settling"
                : "decode_retry");
            session.AppendCapture(artifact, CaptureSessionStage.Decoding, changedUtc, $"decode_attempt_{attempt}");
        }

        session.AppendCapture(artifact, terminalStage, changedUtc, code);
        artifact.IsTerminal = true;
        artifact.PixelsRetained = false;
        artifact.Disposition = CaptureArtifactDisposition.NoChange;
        artifact.DiagnosticCode = code;
        AddNoticeUnsafe(
            CaptureSessionNoticeKind.NoChange,
            changedUtc,
            artifact.CorrelationId,
            session.Request.SessionId,
            artifact.ArtifactId,
            code);
    }

    private void FinalizeArtifactFailure(
        QueuedCapture queued,
        MutableSession session,
        MutableArtifact artifact,
        CapturePixelLease pixels,
        string code,
        bool cancelled)
    {
        _stageTimeline?.Complete(queued.Submission.CorrelationId, _timeProvider.GetUtcNow(), CaptureTimelineKinds.Unread);
        EventHandler? changed;
        lock (_gate)
        {
            FinalizeArtifactUnsafe(
                queued,
                session,
                artifact,
                cancelled ? CaptureSessionStage.Cancelled : CaptureSessionStage.Failed,
                code);
            artifact.PixelsRetained = false;
            ReleasePixelsUnsafe(pixels, session.Request.SessionId, artifact.ArtifactId, code);
            changed = _changed;
        }

        Notify(changed);
    }

    private void FinalizeArtifactUnsafe(
        QueuedCapture queued,
        MutableSession session,
        MutableArtifact artifact,
        CaptureSessionStage terminalStage,
        string code)
    {
        if (artifact.IsTerminal)
        {
            return;
        }

        session.AppendCapture(artifact, terminalStage, _timeProvider.GetUtcNow(), code);
        artifact.IsTerminal = true;
        artifact.Disposition = terminalStage switch
        {
            CaptureSessionStage.Complete => CaptureArtifactDisposition.Accepted,
            _ => CaptureArtifactDisposition.NoChange,
        };
        artifact.DiagnosticCode = code;
        if (terminalStage != CaptureSessionStage.Complete)
        {
            RemoveDeduplicationUnsafe(artifact);
            AddNoticeUnsafe(
                CaptureSessionNoticeKind.NoChange,
                session.LastChangedUtc,
                artifact.CorrelationId,
                session.Request.SessionId,
                artifact.ArtifactId,
                code);
        }

        if (terminalStage == CaptureSessionStage.Cancelled)
        {
            session.CancellationRequested = true;
            session.RequestTerminal(CaptureSessionStage.Cancelled);
            StartCancellationUnsafe(session);
        }
        else if (terminalStage == CaptureSessionStage.Failed && queued.Submission.EndSessionAfterReview)
        {
            session.RequestTerminal(CaptureSessionStage.Failed);
        }
    }

    private void CompleteActiveCapture(QueuedCapture queued)
    {
        EventHandler? changed;
        lock (_gate)
        {
            if (_sessions.TryGetValue(queued.BoundSessionId, out var session))
            {
                CompleteActiveCaptureUnsafe(queued, session, _timeProvider.GetUtcNow());
            }

            changed = _changed;
        }

        Notify(changed);
    }

    private void CompleteActiveCaptureUnsafe(
        QueuedCapture queued,
        MutableSession session,
        DateTimeOffset changedUtc)
    {
        if (session.ActiveCaptureCount <= 0)
        {
            return;
        }

        session.ActiveCaptureCount--;
        if (session.ActiveCaptureCount != 0 || session.IsTerminal)
        {
            return;
        }

        var terminal = session.CancellationRequested
            ? CaptureSessionStage.Cancelled
            : session.PendingTerminalStage;
        if (terminal is { } stage)
        {
            var detail = stage switch
            {
                CaptureSessionStage.Complete => "session_complete",
                CaptureSessionStage.Failed => "session_failed",
                _ => "session_cancelled",
            };
            session.AppendSession(stage, changedUtc, detail);
            PruneSessionsUnsafe();
        }
    }

    private bool RollBackIntakeUnsafe(QueuedCapture queued, DateTimeOffset now)
    {
        if (!_sessions.TryGetValue(queued.BoundSessionId, out var session))
        {
            return false;
        }

        if (session.ActiveCaptureCount > 0)
        {
            session.ActiveCaptureCount--;
        }

        if (session.AdmissionCount > 0)
        {
            session.AdmissionCount--;
        }

        if (session.CancellationRequested)
        {
            if (session.ActiveCaptureCount == 0 && !session.IsTerminal)
            {
                session.AppendSession(CaptureSessionStage.Cancelled, now, "session_cancelled");
                PruneSessionsUnsafe();
            }

            return false;
        }

        if (queued.CreatedSession && session.ActiveCaptureCount == 0 && session.Artifacts.Count == 0)
        {
            _sessions.Remove(session.Request.SessionId);
            session.Cancellation.Dispose();
            return false;
        }

        return queued.ClaimedArmedIntent && RearmIntentUnsafe(session, now, "queue_wait_cancelled");
    }

    private void RollBackBindingUnsafe(IntakeBinding binding)
    {
        if (binding.CreatedSession)
        {
            _sessions.Remove(binding.Session.Request.SessionId);
            binding.Session.Cancellation.Dispose();
            return;
        }

        if (!binding.ClaimedArmedIntent)
        {
            return;
        }

        binding.Session.IntentClaimed = false;
        if (_claimedIntentSessionId == binding.Session.Request.SessionId)
        {
            _claimedIntentSessionId = null;
        }

        _armedSessionId = binding.Session.Request.SessionId;
    }

    private bool RestoreIntentAfterUnusableCaptureUnsafe(
        QueuedCapture queued,
        MutableSession session,
        DateTimeOffset now)
    {
        if (queued.ClaimedArmedIntent)
        {
            var rearmed = RearmIntentUnsafe(session, now, "capture_not_consumed");
            if (!rearmed && !session.CancellationRequested)
            {
                session.RequestTerminal(CaptureSessionStage.Failed);
                AddNoticeUnsafe(
                    CaptureSessionNoticeKind.NoChange,
                    now,
                    queued.Submission.CorrelationId,
                    session.Request.SessionId,
                    null,
                    "intent_restore_failed");
            }

            return rearmed;
        }

        if (queued.CreatedSession && !session.IsTerminal)
        {
            session.RequestTerminal(CaptureSessionStage.Cancelled);
        }

        return false;
    }

    private bool RearmIntentUnsafe(MutableSession session, DateTimeOffset now, string detail)
    {
        if (session.IsTerminal
            || session.CancellationRequested
            || _stopping
            || (_armedSessionId is { } armed && armed != session.Request.SessionId)
            || (_claimedIntentSessionId is { } claimed && claimed != session.Request.SessionId))
        {
            return false;
        }

        session.ReplaceExpiry(now.Add(_options.IntentLifetime));
        session.PendingTerminalStage = null;
        session.IntentClaimed = false;
        if (_claimedIntentSessionId == session.Request.SessionId)
        {
            _claimedIntentSessionId = null;
        }

        _armedSessionId = session.Request.SessionId;
        session.AppendSession(CaptureSessionStage.AwaitingCapture, now, detail);
        AddNoticeUnsafe(
            CaptureSessionNoticeKind.Progress,
            now,
            null,
            session.Request.SessionId,
            null,
            "intent_rearmed");
        return true;
    }

    private void ConsumeClaimedIntentUnsafe(QueuedCapture queued, MutableSession session)
    {
        if (queued.ClaimedArmedIntent && _claimedIntentSessionId == session.Request.SessionId)
        {
            _claimedIntentSessionId = null;
        }
    }

    private static void ReleaseAdmissionUnsafe(MutableSession session)
    {
        if (session.AdmissionCount > 0)
        {
            session.AdmissionCount--;
        }
    }

    private bool EnsureSessionCapacityUnsafe()
    {
        foreach (var terminal in _sessions.Values
                     .Where(item => item.IsTerminal
                         && item.ActiveCaptureCount == 0
                         && item.CancellationDelivery.IsCompleted)
                     .OrderBy(item => item.LastChangedUtc)
                     .ThenBy(item => item.Request.SessionId.Value)
                     .ToArray())
        {
            if (_sessions.Count < _options.SessionLimit)
            {
                break;
            }

            _sessions.Remove(terminal.Request.SessionId);
            terminal.Cancellation.Dispose();
        }

        return _sessions.Count < _options.SessionLimit;
    }

    private void PruneSessionsUnsafe()
    {
        if (_sessions.Count <= _options.SessionLimit)
        {
            return;
        }

        foreach (var terminal in _sessions.Values
                     .Where(item => item.IsTerminal
                         && item.ActiveCaptureCount == 0
                         && item.CancellationDelivery.IsCompleted)
                     .OrderBy(item => item.LastChangedUtc)
                     .ThenBy(item => item.Request.SessionId.Value)
                     .Take(_sessions.Count - _options.SessionLimit)
                     .ToArray())
        {
            _sessions.Remove(terminal.Request.SessionId);
            terminal.Cancellation.Dispose();
        }
    }

    private void RemoveDeduplicationUnsafe(MutableArtifact artifact)
    {
        if (artifact.ContentDigest is { } digest
            && _deduplication.TryGetValue(digest, out var entry)
            && entry.SessionId == artifact.SessionId
            && string.Equals(entry.ArtifactId, artifact.ArtifactId, StringComparison.Ordinal))
        {
            _deduplication.Remove(digest);
        }
    }

    private static ScanIntent IntentForDetectedContext(RecognizedContext? context, ScanIntent fallback) =>
        context switch
        {
            RecognizedContext.Item or RecognizedContext.Grid or RecognizedContext.Loot => ScanIntent.Loot,
            RecognizedContext.Stash => ScanIntent.Stash,
            RecognizedContext.Ammo => ScanIntent.Ammo,
            RecognizedContext.Keys => ScanIntent.Keys,
            RecognizedContext.QuestItems => ScanIntent.QuestItems,
            RecognizedContext.ExtractsAndMap => ScanIntent.ExtractsAndMap,
            RecognizedContext.HealthAndCharacter => ScanIntent.HealthAndCharacter,
            RecognizedContext.Flea => ScanIntent.Flea,
            _ => fallback,
        };

    private static EvidenceProvenance CreateProvenance(
        CaptureSourceKind sourceKind,
        CaptureDeliveryKind deliveryKind,
        DateTimeOffset capturedUtc)
    {
        var sourceClass = deliveryKind == CaptureDeliveryKind.PairedDevice
            ? EvidenceSourceClass.PairedDeviceAction
            : sourceKind == CaptureSourceKind.GameWrittenScreenshot
                ? EvidenceSourceClass.GameWrittenScreenshot
                : EvidenceSourceClass.ExternalVisiblePixels;
        return new(
            sourceClass,
            $"capture-session:{sourceKind}:{deliveryKind}",
            capturedUtc,
            EvidenceConfidence.Certain,
            CaptureProducer);
    }

    private void ReleasePixels(
        CapturePixelLease pixels,
        CaptureSessionId? sessionId,
        string? artifactId,
        string code)
    {
        EventHandler? changed;
        lock (_gate)
        {
            ReleasePixelsUnsafe(pixels, sessionId, artifactId, code);
            changed = _changed;
        }

        Notify(changed);
    }

    private void ReleaseQueuedPixelReservation(QueuedCapture queued, string code)
    {
        var reservedPixelBytes = queued.TakeReservedPixelBytes();
        if (reservedPixelBytes == 0)
        {
            return;
        }

        ReleasePixelBytes(
            reservedPixelBytes,
            queued.BoundSessionId,
            artifactId: null,
            code: code);
    }

    private void ReleasePixelBytes(
        long byteLength,
        CaptureSessionId? sessionId,
        string? artifactId,
        string code)
    {
        EventHandler? changed;
        lock (_gate)
        {
            ReleasePixelBytesUnsafe(byteLength, sessionId, artifactId, code);
            changed = _changed;
        }

        Notify(changed);
    }

    private bool TryReservePixels(CapturePixelLease pixels)
    {
        lock (_gate)
        {
            if (pixels.ByteLength > _options.MaximumRetainedPixelBytes - _pixelsInUse)
            {
                return false;
            }

            _pixelsInUse += pixels.ByteLength;
            return true;
        }
    }

    private void ReleasePixelsUnsafe(
        CapturePixelLease pixels,
        CaptureSessionId? sessionId,
        string? artifactId,
        string code)
    {
        if (pixels.IsDisposed)
        {
            return;
        }

        var byteLength = pixels.ByteLength;
        pixels.Dispose();
        ReleasePixelBytesUnsafe(byteLength, sessionId, artifactId, code);
    }

    private void ReleasePixelBytesUnsafe(
        long byteLength,
        CaptureSessionId? sessionId,
        string? artifactId,
        string code)
    {
        _pixelsInUse -= byteLength;
        AddNoticeUnsafe(
            CaptureSessionNoticeKind.PixelsReleased,
            _timeProvider.GetUtcNow(),
            null,
            sessionId,
            artifactId,
            code);
    }

    private void DisposeSourceSafely(
        ICaptureContentSource source,
        CaptureCorrelationId correlationId,
        CaptureSessionId? sessionId)
    {
        try
        {
            source.Dispose();
        }
        catch
        {
            EventHandler? changed;
            lock (_gate)
            {
                AddNoticeUnsafe(
                    CaptureSessionNoticeKind.Progress,
                    _timeProvider.GetUtcNow(),
                    correlationId,
                    sessionId,
                    null,
                    "capture_source_dispose_failed");
                changed = _changed;
            }

            Notify(changed);
        }
    }

    private static void StartCancellationUnsafe(MutableSession session)
    {
        if (session.Cancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            session.CancellationDelivery = ObserveCancellationDeliveryAsync(
                session.Cancellation.CancelAsync());
        }
        catch (Exception)
        {
            session.CancellationDelivery = Task.CompletedTask;
        }
    }

    private static async Task ObserveCancellationDeliveryAsync(Task delivery)
    {
        try
        {
            await delivery.ConfigureAwait(false);
        }
        catch
        {
            // Cancellation callback failures are observed but cannot escape a synchronous
            // command boundary or prevent later pixel/session cleanup.
        }
    }

    private void RejectUnsafe(QueuedCapture queued, string code)
    {
        _rejected++;
        AddNoticeUnsafe(
            CaptureSessionNoticeKind.QueueRejected,
            _timeProvider.GetUtcNow(),
            queued.Submission.CorrelationId,
            queued.BoundSessionId,
            null,
            code);
    }

    private static bool RequiresReview(ScanIntent intent, CaptureAnalysis analysis)
    {
        if (!analysis.IsAvailable || analysis.IsAmbiguous || analysis.DetectedContext is null)
        {
            return true;
        }

        if (intent == ScanIntent.Auto)
        {
            return false;
        }

        return intent switch
        {
            ScanIntent.Loot => analysis.DetectedContext is not (RecognizedContext.Loot or RecognizedContext.Item or RecognizedContext.Grid),
            ScanIntent.Stash => analysis.DetectedContext != RecognizedContext.Stash,
            ScanIntent.Ammo => analysis.DetectedContext != RecognizedContext.Ammo,
            ScanIntent.Keys => analysis.DetectedContext != RecognizedContext.Keys,
            ScanIntent.QuestItems => analysis.DetectedContext != RecognizedContext.QuestItems,
            ScanIntent.ExtractsAndMap => analysis.DetectedContext != RecognizedContext.ExtractsAndMap,
            ScanIntent.HealthAndCharacter => analysis.DetectedContext != RecognizedContext.HealthAndCharacter,
            ScanIntent.Flea => analysis.DetectedContext != RecognizedContext.Flea,
            _ => true,
        };
    }

    private static string ContentIdentity(TarkovCompanion.Core.Domain.Recognition.CapturedImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> metadata = stackalloc byte[16];
        BitConverter.TryWriteBytes(metadata, image.Width);
        BitConverter.TryWriteBytes(metadata[4..], image.Height);
        BitConverter.TryWriteBytes(metadata[8..], image.Stride);
        BitConverter.TryWriteBytes(metadata[12..], (int)image.Format);
        hash.AppendData(metadata);
        hash.AppendData(image.Pixels.Span);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private void TrimDeduplicationUnsafe(DateTimeOffset now)
    {
        foreach (var digest in _deduplication.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _deduplication.Remove(digest);
        }

        if (_deduplication.Count <= _options.HistoryLimit)
        {
            return;
        }

        foreach (var digest in _deduplication
                     .OrderBy(pair => pair.Value.ExpiresUtc)
                     .Take(_deduplication.Count - _options.HistoryLimit)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _deduplication.Remove(digest);
        }
    }

    private CaptureSessionServiceSnapshot SnapshotUnsafe()
    {
        var sessions = _sessions.Values
            .OrderBy(item => item.Request.RequestedUtc)
            .Select(item => item.Freeze())
            .ToImmutableArray();
        return new(
            _stopping,
            _queueDepth,
            _options.QueueCapacity,
            _accepted,
            _duplicate,
            _rejected,
            _pixelsInUse,
            sessions,
            [.. _timings],
            [.. _notices],
            _options.MaximumRetainedPixelBytes);
    }

    private void AddTimingUnsafe(CaptureTimingSnapshot timing)
    {
        _timings.Add(timing);
        TrimHistory(_timings);
    }

    private void UpdateReviewTimingUnsafe(long intakeSequence, DateTimeOffset startedUtc, DateTimeOffset completedUtc)
    {
        var index = _timings.FindLastIndex(item => item.IntakeSequence == intakeSequence);
        if (index >= 0)
        {
            _timings[index] = _timings[index] with
            {
                ReviewMilliseconds = ElapsedMilliseconds(startedUtc, completedUtc),
            };
        }
    }

    private void AddNoticeUnsafe(
        CaptureSessionNoticeKind kind,
        DateTimeOffset changedUtc,
        CaptureCorrelationId? correlationId,
        CaptureSessionId? sessionId,
        string? artifactId,
        string code)
    {
        _notices.Add(new(
            checked(_nextNoticeSequence++),
            kind,
            changedUtc.ToUniversalTime(),
            correlationId,
            sessionId,
            artifactId,
            code));
        TrimHistory(_notices);
    }

    private void TrimHistory<T>(List<T> history)
    {
        if (history.Count > _options.HistoryLimit)
        {
            history.RemoveRange(0, history.Count - _options.HistoryLimit);
        }
    }

    private void NotifyChanged()
    {
        EventHandler? changed;
        lock (_gate)
        {
            changed = _changed;
        }

        Notify(changed);
    }

    private void Notify(EventHandler? changed)
    {
        try
        {
            changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A presentation subscriber cannot stop intake or strand a pixel lease.
        }
    }

    private void NotifyReviewRequested(CaptureReviewRequest review)
    {
        try
        {
            ReviewRequested?.Invoke(this, new(review));
        }
        catch
        {
            // Review remains visible in Snapshot even when a presentation subscriber fails.
        }
    }

    private void NotifyAccepted(CaptureAcceptedEventArgs accepted)
    {
        try
        {
            Accepted?.Invoke(this, accepted);
        }
        catch
        {
            // A downstream subscriber cannot strand pixels after the reviewed decision.
        }
    }

    private static long ElapsedMilliseconds(DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, (long)(end - start).TotalMilliseconds);

    private sealed class QueuedCapture(
        long intakeSequence,
        CaptureSubmission submission,
        DateTimeOffset enqueuedUtc,
        CaptureSessionId boundSessionId,
        bool claimedArmedIntent,
        bool createdSession,
        long reservedPixelBytes)
    {
        // The reservation moves atomically from the queued source to its returned lease. Leaving
        // it on the queue until source disposal keeps accounting truthful through zeroization.
        private long _reservedPixelBytes = reservedPixelBytes;

        public long IntakeSequence { get; } = intakeSequence;

        public CaptureSubmission Submission { get; } = submission;

        public DateTimeOffset EnqueuedUtc { get; } = enqueuedUtc;

        public CaptureSessionId BoundSessionId { get; } = boundSessionId;

        public bool ClaimedArmedIntent { get; } = claimedArmedIntent;

        public bool CreatedSession { get; } = createdSession;

        public long TakeReservedPixelBytes() => Interlocked.Exchange(ref _reservedPixelBytes, 0);
    }

    private sealed record IntakeBinding(
        MutableSession Session,
        bool ClaimedArmedIntent,
        bool CreatedSession);

    private sealed record DeduplicationEntry(
        CaptureSessionId SessionId,
        string ArtifactId,
        DateTimeOffset ExpiresUtc);

    private sealed record ReviewDecision(
        CaptureReviewAction Action,
        int DecodeRevision,
        string Origin,
        DateTimeOffset ChangedUtc,
        string? NoChangeCode = null);

    private sealed class PendingReview(
        CaptureSessionId sessionId,
        string artifactId,
        int decodeRevision,
        CapturePixelLease pixels)
    {
        public CaptureSessionId SessionId { get; } = sessionId;

        public string ArtifactId { get; } = artifactId;

        public int DecodeRevision { get; } = decodeRevision;

        public CapturePixelLease Pixels { get; } = pixels;

        public TaskCompletionSource<ReviewDecision> Decision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PreparedCapture(
        MutableSession? Session,
        MutableArtifact? Artifact,
        CapturePixelLease? Pixels,
        bool IsDuplicate)
    {
        public static PreparedCapture Rejected { get; } = new(null, null, null, false);

        public static PreparedCapture Duplicate { get; } = new(null, null, null, true);
    }

    private sealed class MutableSession(
        CaptureSessionRequest request,
        CaptureContextMetadata context,
        CaptureGuidance guidance)
    {
        private readonly List<CaptureStageProgress> _progress = [];

        public CaptureSessionRequest Request { get; private set; } = request;

        public CaptureContextMetadata Context { get; private set; } = context;

        public CaptureGuidance Guidance { get; } = guidance;

        public List<MutableArtifact> Artifacts { get; } = [];

        public bool IntentClaimed { get; set; }

        public bool CancellationRequested { get; set; }

        public int ActiveCaptureCount { get; set; }

        public int AdmissionCount { get; set; }

        public bool HasBoundContext { get; private set; }

        public CaptureSessionStage? PendingTerminalStage { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public Task CancellationDelivery { get; set; } = Task.CompletedTask;

        public DateTimeOffset LastChangedUtc { get; private set; } = request.RequestedUtc;

        public bool IsTerminal { get; private set; }

        public bool CanAcceptContext(CaptureContextMetadata candidate) => Equals(Context, candidate);

        public void BindContext(CaptureContextMetadata candidate)
        {
            if (!CanAcceptContext(candidate))
            {
                throw new InvalidOperationException("Capture intake context changed after validation.");
            }

            if (!HasBoundContext)
            {
                // Keep the exact intake object, not the earlier guidance-time equivalent.
                Context = candidate;
                HasBoundContext = true;
            }
        }

        public MutableArtifact AddArtifact(
            string artifactId,
            CaptureDeliveryKind deliveryKind,
            CaptureCorrelationId correlationId,
            CaptureSourceKind sourceKind,
            DateTimeOffset capturedUtc,
            DateTimeOffset submittedUtc,
            string? batchId,
            int decodeAttempts,
            string? contentDigest,
            EvidenceProvenance provenance)
        {
            var artifact = new MutableArtifact(
                Request.SessionId,
                artifactId,
                Artifacts.Count,
                deliveryKind,
                correlationId,
                Context,
                sourceKind,
                capturedUtc,
                submittedUtc,
                batchId,
                decodeAttempts,
                contentDigest,
                provenance);
            Artifacts.Add(artifact);
            return artifact;
        }

        public void AppendSession(CaptureSessionStage stage, DateTimeOffset changedUtc, string detail) =>
            Append(new(Request.SessionId, _progress.Count, stage, changedUtc, detail: detail));

        public void AppendCapture(
            MutableArtifact artifact,
            CaptureSessionStage stage,
            DateTimeOffset changedUtc,
            string detail) =>
            Append(new(
                Request.SessionId,
                _progress.Count,
                stage,
                changedUtc,
                artifact.ArtifactId,
                artifact.CaptureOrdinal,
                detail: detail));

        public CaptureSessionState Freeze()
        {
            var completeness = IsTerminal
                ? _progress[^1].Stage switch
                {
                    CaptureSessionStage.Complete => ResultCompleteness.Complete,
                    CaptureSessionStage.Failed => ResultCompleteness.Unavailable,
                    _ => ResultCompleteness.Partial,
                }
                : Artifacts.Count == 0
                    ? ResultCompleteness.Unknown
                    : ResultCompleteness.Partial;
            var status = new ResultStatus(
                completeness,
                IsTerminal && completeness == ResultCompleteness.Unavailable
                    ? FreshnessState.Unknown
                    : FreshnessState.Current);
            return new(
                Request,
                Context,
                Guidance,
                new(Request, [.. _progress], status),
                [.. Artifacts.Select(item => item.Freeze())],
                IntentClaimed,
                CancellationRequested,
                IsTerminal);
        }

        public DateTimeOffset NormalizeTimestamp(DateTimeOffset changedUtc)
        {
            var utc = changedUtc == default ? LastChangedUtc : changedUtc.ToUniversalTime();
            return utc < LastChangedUtc ? LastChangedUtc : utc;
        }

        public void ReplaceExpiry(DateTimeOffset expiresUtc)
        {
            Request = new(
                Request.SessionId,
                Request.Intent,
                Request.Origin,
                Request.RequestedUtc,
                Request.ProfileId,
                Request.MapId,
                expiresUtc);
        }

        public void RequestTerminal(CaptureSessionStage stage)
        {
            if (stage is not (CaptureSessionStage.Complete or CaptureSessionStage.Cancelled or CaptureSessionStage.Failed))
            {
                throw new ArgumentOutOfRangeException(nameof(stage));
            }

            if (PendingTerminalStage is null || TerminalRank(stage) > TerminalRank(PendingTerminalStage.Value))
            {
                PendingTerminalStage = stage;
            }
        }

        private void Append(CaptureStageProgress progress)
        {
            var changedUtc = NormalizeTimestamp(progress.ChangedUtc);
            var candidate = new CaptureStageProgress(
                progress.SessionId,
                progress.Sequence,
                progress.Stage,
                changedUtc,
                progress.ArtifactId,
                progress.CaptureOrdinal,
                progress.Percent,
                progress.Detail);
            var sessionTerminal = candidate.ArtifactId is null
                && candidate.Stage is CaptureSessionStage.Complete or CaptureSessionStage.Cancelled or CaptureSessionStage.Failed;
            var completeness = sessionTerminal
                ? candidate.Stage switch
                {
                    CaptureSessionStage.Complete => ResultCompleteness.Complete,
                    CaptureSessionStage.Failed => ResultCompleteness.Unavailable,
                    _ => ResultCompleteness.Partial,
                }
                : Artifacts.Count == 0
                    ? ResultCompleteness.Unknown
                    : ResultCompleteness.Partial;
            var status = new ResultStatus(
                completeness,
                sessionTerminal && completeness == ResultCompleteness.Unavailable
                    ? FreshnessState.Unknown
                    : FreshnessState.Current);
            var proposed = _progress.Append(candidate).ToArray();

            // Validate the proposed immutable history before committing it. Cancellation used
            // to add first and validate second, leaving one bad entry that made every later
            // Snapshot throw and stranded the pixel lease.
            _ = new CaptureSessionSnapshot(Request, proposed, status);
            _progress.Add(candidate);
            LastChangedUtc = changedUtc;
            if (sessionTerminal)
            {
                IsTerminal = true;
                PendingTerminalStage = null;
            }
        }

        private static int TerminalRank(CaptureSessionStage stage) => stage switch
        {
            CaptureSessionStage.Complete => 1,
            CaptureSessionStage.Failed => 2,
            CaptureSessionStage.Cancelled => 3,
            _ => 0,
        };
    }

    private sealed class MutableArtifact(
        CaptureSessionId sessionId,
        string artifactId,
        int captureOrdinal,
        CaptureDeliveryKind deliveryKind,
        CaptureCorrelationId correlationId,
        CaptureContextMetadata context,
        CaptureSourceKind sourceKind,
        DateTimeOffset capturedUtc,
        DateTimeOffset submittedUtc,
        string? batchId,
        int decodeAttempts,
        string? contentDigest,
        EvidenceProvenance provenance)
    {
        public CaptureSessionId SessionId { get; } = sessionId;

        public string ArtifactId { get; } = artifactId;

        public int CaptureOrdinal { get; } = captureOrdinal;

        public CaptureDeliveryKind DeliveryKind { get; } = deliveryKind;

        public CaptureCorrelationId CorrelationId { get; } = correlationId;

        public CaptureContextMetadata Context { get; } = context;

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

        public string? ContentDigest { get; } = string.IsNullOrWhiteSpace(contentDigest)
            ? null
            : contentDigest;

        public EvidenceProvenance Provenance { get; } = provenance ?? throw new ArgumentNullException(nameof(provenance));

        public Confidence Confidence { get; set; } = Confidence.Unknown;

        public CaptureAnalysis? Analysis { get; set; }

        public CaptureReviewRequest? Review { get; set; }

        public List<CaptureCorrection> Corrections { get; } = [];

        public CaptureArtifactDisposition Disposition { get; set; } = CaptureArtifactDisposition.Pending;

        public CaptureHandoffDisposition HandoffDisposition { get; set; } = CaptureHandoffDisposition.NotAttempted;

        public int DecodeAttempts { get; } = decodeAttempts;

        public int DecodeRevision { get; set; }

        public bool PixelsRetained { get; set; } = true;

        public string? DiagnosticCode { get; set; }

        public bool IsTerminal { get; set; }

        public CaptureArtifactSnapshot Freeze() => new(
            ArtifactId,
            CaptureOrdinal,
            DeliveryKind,
            CorrelationId,
            Context,
            SourceKind,
            CapturedUtc,
            SubmittedUtc,
            BatchId,
            Confidence,
            Analysis,
            Review,
            [.. Corrections],
            PixelsRetained,
            DecodeAttempts,
            DecodeRevision,
            DiagnosticCode,
            Provenance,
            Disposition,
            HandoffDisposition);
    }
}
