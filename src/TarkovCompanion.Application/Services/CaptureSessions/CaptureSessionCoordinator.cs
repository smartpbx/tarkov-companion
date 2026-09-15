using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Threading.Channels;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

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
        int historyLimit = 256)
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
    }

    public int QueueCapacity { get; }

    public long MaximumRetainedPixelBytes { get; }

    public int MaximumDecodeAttempts { get; }

    public TimeSpan DecodeRetryDelay { get; }

    public TimeSpan ReviewTimeout { get; }

    public TimeSpan IntentLifetime { get; }

    public TimeSpan DeduplicationLifetime { get; }

    public int HistoryLimit { get; }

    private static TimeSpan Positive(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero && value <= TimeSpan.FromHours(24)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);
}

/// <summary>Owns capture intake, review, and transient pixels; recognition stays behind a seam.</summary>
/// <remarks>
/// The queue has one reader because capture ordinals are evidence, not scheduling hints. A
/// producer waits when the bounded queue is full, and therefore receives backpressure rather
/// than a successful-looking drop. Heavy decode/analysis work is admitted through the shared
/// runtime supervisor one item at a time, while review waits hold no supervisor CPU slot.
/// </remarks>
public sealed class CaptureSessionCoordinator : ICaptureSessionService
{
    private readonly object _gate = new();
    private readonly ICaptureWorkScheduler _scheduler;
    private readonly ICaptureSessionPipeline _pipeline;
    private readonly WorkspaceOrigin _defaultOrigin;
    private readonly TimeProvider _timeProvider;
    private readonly CaptureSessionOptions _options;
    private readonly Channel<QueuedCapture> _queue;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<CaptureSessionId, MutableSession> _sessions = [];
    private readonly Dictionary<string, DeduplicationEntry> _deduplication = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingReview> _pendingReviews = new(StringComparer.Ordinal);
    private readonly List<CaptureTimingSnapshot> _timings = [];
    private readonly List<CaptureSessionNotice> _notices = [];
    private readonly Task _pump;
    private CaptureSessionId? _armedSessionId;
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

    public CaptureSessionCoordinator(
        ICaptureWorkScheduler scheduler,
        ICaptureSessionPipeline pipeline,
        WorkspaceOrigin defaultOrigin,
        TimeProvider? timeProvider = null,
        CaptureSessionOptions? options = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
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

            if (_sessions.ContainsKey(arm.Request.SessionId))
            {
                return new(true, arm.Request.SessionId, "already_known");
            }

            if (_armedSessionId is not null)
            {
                return new(false, arm.Request.SessionId, "intent_already_armed");
            }

            var expiresUtc = arm.Request.ExpiresUtc ?? now.Add(_options.IntentLifetime);
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
        _ = ExpireIntentAsync(request.SessionId, request.ExpiresUtc!.Value, _lifetime.Token);
        return new(true, request.SessionId, "armed");
    }

    public async ValueTask<CaptureQueueReceipt> EnqueueAsync(
        CaptureSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        long intakeSequence;
        lock (_gate)
        {
            if (_stopping || _disposed)
            {
                Interlocked.Increment(ref _rejected);
                submission.Source.Dispose();
                return new(
                    -1,
                    CaptureQueueDisposition.Rejected,
                    submission.CorrelationId,
                    _timeProvider.GetUtcNow(),
                    "capture_service_stopping");
            }

            if (submission.SubmittedUtc > _timeProvider.GetUtcNow())
            {
                _rejected++;
                submission.Source.Dispose();
                return new(
                    -1,
                    CaptureQueueDisposition.Rejected,
                    submission.CorrelationId,
                    _timeProvider.GetUtcNow(),
                    "capture_submission_in_future");
            }

            intakeSequence = checked(_nextIntakeSequence++);
            _queueDepth++;
        }

        var queued = new QueuedCapture(intakeSequence, submission, _timeProvider.GetUtcNow());
        try
        {
            await _queue.Writer.WriteAsync(queued, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            submission.Source.Dispose();
            lock (_gate)
            {
                _queueDepth--;
                _rejected++;
            }
            return new(
                intakeSequence,
                CaptureQueueDisposition.Cancelled,
                submission.CorrelationId,
                _timeProvider.GetUtcNow(),
                "queue_wait_cancelled");
        }
        catch (ChannelClosedException)
        {
            submission.Source.Dispose();
            lock (_gate)
            {
                _queueDepth--;
                _rejected++;
            }
            return new(
                intakeSequence,
                CaptureQueueDisposition.Rejected,
                submission.CorrelationId,
                _timeProvider.GetUtcNow(),
                "capture_service_stopping");
        }

        EventHandler? changed;
        lock (_gate)
        {
            _accepted++;
            changed = _changed;
        }

        Notify(changed);
        return new(
            intakeSequence,
            CaptureQueueDisposition.Accepted,
            submission.CorrelationId,
            _timeProvider.GetUtcNow(),
            "queued");
    }

    public bool TryReview(
        CaptureSessionId sessionId,
        string artifactId,
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
                || pending.SessionId != sessionId)
            {
                return false;
            }

            return pending.Decision.TrySetResult(new(action, trimmedOrigin, _timeProvider.GetUtcNow()));
        }
    }

    public bool Cancel(CaptureSessionId sessionId, string origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
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

            foreach (var pending in _pendingReviews.Values.Where(item => item.SessionId == sessionId))
            {
                pending.Decision.TrySetResult(new(CaptureReviewAction.Cancel, origin.Trim(), _timeProvider.GetUtcNow()));
            }

            if (!_pendingReviews.Values.Any(item => item.SessionId == sessionId))
            {
                session.AppendSession(CaptureSessionStage.Cancelled, _timeProvider.GetUtcNow(), "session_cancelled");
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
            foreach (var pending in _pendingReviews.Values)
            {
                pending.Decision.TrySetResult(new(
                    CaptureReviewAction.Cancel,
                    "shutdown",
                    _timeProvider.GetUtcNow()));
            }
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        while (_queue.Reader.TryRead(out var queued))
        {
            queued.Submission.Source.Dispose();
        }

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

            try
            {
                await ProcessQueuedAsync(queued, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                queued.Submission.Source.Dispose();
            }
        }
    }

    private async Task ProcessQueuedAsync(QueuedCapture queued, CancellationToken cancellationToken)
    {
        var prepared = PreparedCapture.Rejected;
        var work = await _scheduler.RunAsync(
                new(
                    queued.Submission.CorrelationId,
                    $"capture:{queued.IntakeSequence}",
                    CaptureWorkPriority.Intake),
                async token =>
                {
                    prepared = await PrepareAsync(queued, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!work.Accepted || !work.Succeeded)
        {
            Reject(queued, work.DiagnosticCode ?? "capture_work_rejected");
            return;
        }

        if (prepared.IsDuplicate || prepared.Pixels is null || prepared.Session is null || prepared.Artifact is null)
        {
            return;
        }

        await ReviewLoopAsync(queued, prepared, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PreparedCapture> PrepareAsync(QueuedCapture queued, CancellationToken cancellationToken)
    {
        var decodeStarted = _timeProvider.GetUtcNow();
        CapturePixelLease? pixels = null;
        var attempts = 0;
        string? diagnostic = null;
        for (attempts = 1; attempts <= _options.MaximumDecodeAttempts; attempts++)
        {
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
            Reject(queued, diagnostic ?? "decode_failed");
            return PreparedCapture.Rejected;
        }

        if (!TryReservePixels(pixels))
        {
            pixels.Dispose();
            Reject(queued, "decoded_pixel_budget_exceeded");
            return PreparedCapture.Rejected;
        }

        var digest = ContentIdentity(pixels.Image);
        MutableSession session;
        MutableArtifact artifact;
        EventHandler? changed;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            TrimDeduplicationUnsafe(now);
            if (_deduplication.ContainsKey(digest))
            {
                _duplicate++;
                AddNoticeUnsafe(
                    CaptureSessionNoticeKind.Duplicate,
                    now,
                    queued.Submission.CorrelationId,
                    queued.Submission.SessionId,
                    null,
                    "duplicate_content");
                ReleasePixelsUnsafe(pixels, queued.Submission.SessionId, null, "duplicate_content");
                changed = _changed;
                Notify(changed);
                return PreparedCapture.Duplicate;
            }

            var resolvedSession = ResolveSessionUnsafe(queued.Submission, now);
            if (resolvedSession is null)
            {
                ReleasePixelsUnsafe(pixels, queued.Submission.SessionId, null, "unknown_session");
                RejectUnsafe(queued, "unknown_session");
                changed = _changed;
                Notify(changed);
                return PreparedCapture.Rejected;
            }

            session = resolvedSession;

            if (session.IsTerminal)
            {
                ReleasePixelsUnsafe(pixels, session.Request.SessionId, null, "stale_session");
                RejectUnsafe(queued, "stale_session");
                changed = _changed;
                Notify(changed);
                return PreparedCapture.Rejected;
            }

            var artifactId = $"capture-{Guid.NewGuid():N}";
            artifact = session.AddArtifact(
                artifactId,
                queued.Submission.DeliveryKind,
                queued.Submission.CorrelationId,
                attempts);
            for (var retry = 1; retry <= attempts; retry++)
            {
                session.AppendCapture(artifact, CaptureSessionStage.Settling, now, retry == 1 ? "source_settled" : "decode_retry");
                session.AppendCapture(artifact, CaptureSessionStage.Decoding, now, $"decode_attempt_{retry}");
            }

            session.AppendCapture(artifact, CaptureSessionStage.DetectingContext, now, "detecting_context");
            _deduplication.Add(digest, new(artifactId, now.Add(_options.DeduplicationLifetime)));
            changed = _changed;
        }

        Notify(changed);
        var analysisStarted = _timeProvider.GetUtcNow();
        CaptureAnalysis analysis;
        try
        {
            analysis = await _pipeline.AnalyzeAsync(
                    new(
                        session.Request.SessionId,
                        artifact.ArtifactId,
                        artifact.CaptureOrdinal,
                        session.Request.Intent,
                        session.Context,
                        pixels.Image,
                        queued.Submission.CorrelationId,
                        0),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                session.AppendCapture(artifact, CaptureSessionStage.Failed, _timeProvider.GetUtcNow(), "analysis_failed");
                artifact.DiagnosticCode = "analysis_failed";
                if (queued.Submission.EndSessionAfterReview)
                {
                    session.AppendSession(CaptureSessionStage.Failed, _timeProvider.GetUtcNow(), "analysis_failed");
                }

                artifact.PixelsRetained = false;
                ReleasePixelsUnsafe(pixels, session.Request.SessionId, artifact.ArtifactId, "analysis_failed");
            }

            throw;
        }

        CaptureReviewRequest review;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            artifact.Analysis = analysis;
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
            var pending = new PendingReview(session.Request.SessionId, artifact.ArtifactId, pixels);
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
        }

        NotifyReviewRequested(review);
        NotifyChanged();
        return new(session, artifact, pixels, false);
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
        while (true)
        {
            PendingReview pending;
            lock (_gate)
            {
                pending = _pendingReviews[artifact.ArtifactId];
            }

            ReviewDecision decision;
            try
            {
                decision = await pending.Decision.Task
                    .WaitAsync(_options.ReviewTimeout, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                decision = new(CaptureReviewAction.Cancel, "review-expired", _timeProvider.GetUtcNow());
            }

            if (decision.Action == CaptureReviewAction.Redecode)
            {
                if (!await RedecodeAsync(queued, session, artifact, pixels, cancellationToken).ConfigureAwait(false))
                {
                    decision = new(CaptureReviewAction.Cancel, "redecode-failed", _timeProvider.GetUtcNow());
                }
                else
                {
                    continue;
                }
            }

            CaptureAcceptedEventArgs? accepted = null;
            lock (_gate)
            {
                _pendingReviews.Remove(artifact.ArtifactId);
                var correctedUtc = decision.ChangedUtc < reviewStarted ? reviewStarted : decision.ChangedUtc;
                artifact.Corrections.Add(new(
                    decision.Action,
                    session.Request.Intent,
                    artifact.Analysis?.DetectedContext,
                    correctedUtc,
                    decision.Origin));

                switch (decision.Action)
                {
                    case CaptureReviewAction.UseDetected:
                    case CaptureReviewAction.UseArmedIntent:
                        session.AppendCapture(artifact, CaptureSessionStage.Complete, correctedUtc, "review_accepted");
                        artifact.Disposition = CaptureQueueDisposition.Accepted;
                        if (queued.Submission.EndSessionAfterReview)
                        {
                            session.AppendSession(CaptureSessionStage.Complete, correctedUtc, "session_complete");
                        }

                        accepted = new(
                            session.Request.SessionId,
                            artifact.ArtifactId,
                            artifact.Analysis!,
                            session.Context,
                            artifact.CorrelationId);
                        break;

                    case CaptureReviewAction.RetryCapture:
                        session.AppendCapture(artifact, CaptureSessionStage.Cancelled, correctedUtc, "retry_requested");
                        session.AppendSession(CaptureSessionStage.AwaitingCapture, correctedUtc, "retry_requested");
                        artifact.Disposition = CaptureQueueDisposition.Cancelled;
                        break;

                    default:
                        session.AppendCapture(artifact, CaptureSessionStage.Cancelled, correctedUtc, "review_cancelled");
                        artifact.Disposition = CaptureQueueDisposition.Cancelled;
                        if (queued.Submission.EndSessionAfterReview)
                        {
                            session.AppendSession(CaptureSessionStage.Cancelled, correctedUtc, "session_cancelled");
                        }

                        break;
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
                ReleasePixelsUnsafe(pixels, session.Request.SessionId, artifact.ArtifactId, "consumer_finished");
            }

            if (accepted is not null)
            {
                NotifyAccepted(accepted);
            }

            NotifyChanged();
            return;
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
            artifact.DecodeRevision = revision;
            artifact.Analysis = analysis;
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
            _pendingReviews[artifact.ArtifactId] = new(session.Request.SessionId, artifact.ArtifactId, pixels);
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

    private MutableSession? ResolveSessionUnsafe(CaptureSubmission submission, DateTimeOffset now)
    {
        ExpireArmedUnsafe(now);
        if (submission.SessionId is { } requested)
        {
            if (!_sessions.TryGetValue(requested, out var existing))
            {
                return null;
            }

            if (_armedSessionId == requested)
            {
                _armedSessionId = null;
                existing.IntentClaimed = true;
            }

            return existing;
        }

        if (_armedSessionId is { } armed && _sessions.TryGetValue(armed, out var armedSession))
        {
            _armedSessionId = null;
            armedSession.IntentClaimed = true;
            return armedSession;
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
        return session;
    }

    private async Task ExpireIntentAsync(
        CaptureSessionId sessionId,
        DateTimeOffset expiresUtc,
        CancellationToken cancellationToken)
    {
        var delay = expiresUtc - _timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        EventHandler? changed;
        lock (_gate)
        {
            if (_armedSessionId != sessionId)
            {
                return;
            }

            ExpireArmedUnsafe(_timeProvider.GetUtcNow());
            changed = _changed;
        }

        Notify(changed);
    }

    private void ExpireArmedUnsafe(DateTimeOffset now)
    {
        if (_armedSessionId is not { } sessionId
            || !_sessions.TryGetValue(sessionId, out var session)
            || session.Request.ExpiresUtc is not { } expiresUtc
            || expiresUtc > now)
        {
            return;
        }

        _armedSessionId = null;
        session.AppendSession(CaptureSessionStage.Cancelled, now, "intent_expired");
        AddNoticeUnsafe(CaptureSessionNoticeKind.IntentExpired, now, null, sessionId, null, "intent_expired");
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

        _pixelsInUse -= pixels.ByteLength;
        pixels.Dispose();
        AddNoticeUnsafe(
            CaptureSessionNoticeKind.PixelsReleased,
            _timeProvider.GetUtcNow(),
            null,
            sessionId,
            artifactId,
            code);
    }

    private void Reject(QueuedCapture queued, string code)
    {
        EventHandler? changed;
        lock (_gate)
        {
            RejectUnsafe(queued, code);
            changed = _changed;
        }

        Notify(changed);
    }

    private void RejectUnsafe(QueuedCapture queued, string code)
    {
        _rejected++;
        AddNoticeUnsafe(
            CaptureSessionNoticeKind.QueueRejected,
            _timeProvider.GetUtcNow(),
            queued.Submission.CorrelationId,
            queued.Submission.SessionId,
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
            [.. _notices]);
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

    private sealed record QueuedCapture(
        long IntakeSequence,
        CaptureSubmission Submission,
        DateTimeOffset EnqueuedUtc);

    private sealed record DeduplicationEntry(string ArtifactId, DateTimeOffset ExpiresUtc);

    private sealed record ReviewDecision(
        CaptureReviewAction Action,
        string Origin,
        DateTimeOffset ChangedUtc);

    private sealed class PendingReview(
        CaptureSessionId sessionId,
        string artifactId,
        CapturePixelLease pixels)
    {
        public CaptureSessionId SessionId { get; } = sessionId;

        public string ArtifactId { get; } = artifactId;

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

        public CaptureSessionRequest Request { get; } = request;

        public CaptureContextMetadata Context { get; } = context;

        public CaptureGuidance Guidance { get; } = guidance;

        public List<MutableArtifact> Artifacts { get; } = [];

        public bool IntentClaimed { get; set; }

        public bool IsTerminal { get; private set; }

        public MutableArtifact AddArtifact(
            string artifactId,
            CaptureDeliveryKind deliveryKind,
            CaptureCorrelationId correlationId,
            int decodeAttempts)
        {
            var artifact = new MutableArtifact(
                artifactId,
                Artifacts.Count,
                deliveryKind,
                correlationId,
                Context,
                decodeAttempts);
            Artifacts.Add(artifact);
            return artifact;
        }

        public void AppendSession(CaptureSessionStage stage, DateTimeOffset changedUtc, string detail)
        {
            if (stage is CaptureSessionStage.Complete or CaptureSessionStage.Cancelled or CaptureSessionStage.Failed)
            {
                IsTerminal = true;
            }

            Append(new(Request.SessionId, _progress.Count, stage, changedUtc, detail: detail));
        }

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
                IsTerminal);
        }

        private void Append(CaptureStageProgress progress)
        {
            _progress.Add(progress);
            // Validate at every transition so no invalid history can be published even briefly.
            _ = Freeze();
        }
    }

    private sealed class MutableArtifact(
        string artifactId,
        int captureOrdinal,
        CaptureDeliveryKind deliveryKind,
        CaptureCorrelationId correlationId,
        CaptureContextMetadata context,
        int decodeAttempts)
    {
        public string ArtifactId { get; } = artifactId;

        public int CaptureOrdinal { get; } = captureOrdinal;

        public CaptureDeliveryKind DeliveryKind { get; } = deliveryKind;

        public CaptureCorrelationId CorrelationId { get; } = correlationId;

        public CaptureContextMetadata Context { get; } = context;

        public CaptureAnalysis? Analysis { get; set; }

        public CaptureReviewRequest? Review { get; set; }

        public List<CaptureCorrection> Corrections { get; } = [];

        public CaptureQueueDisposition Disposition { get; set; } = CaptureQueueDisposition.Accepted;

        public int DecodeAttempts { get; } = decodeAttempts;

        public int DecodeRevision { get; set; }

        public bool PixelsRetained { get; set; } = true;

        public string? DiagnosticCode { get; set; }

        public CaptureArtifactSnapshot Freeze() => new(
            ArtifactId,
            CaptureOrdinal,
            DeliveryKind,
            CorrelationId,
            Context,
            Analysis,
            Review,
            [.. Corrections],
            Disposition,
            PixelsRetained,
            DecodeAttempts,
            DecodeRevision,
            DiagnosticCode);
    }
}
