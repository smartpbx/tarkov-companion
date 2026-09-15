using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Threading.Channels;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
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
        int historyLimit = 256,
        int sessionLimit = 256)
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
    private readonly HashSet<Task> _reviewTasks = [];
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

            if (_sessions.TryGetValue(arm.Request.SessionId, out var known))
            {
                return known.IsTerminal
                    ? new(false, arm.Request.SessionId, "terminal_session_already_known")
                    : new(true, arm.Request.SessionId, "already_known");
            }

            if (_armedSessionId is not null)
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

    public async ValueTask<CaptureQueueReceipt> EnqueueAsync(
        CaptureSubmission submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        QueuedCapture? queued = null;
        CaptureQueueReceipt? rejected = null;
        EventHandler? changed;
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_stopping || _disposed)
            {
                _rejected++;
                rejected = new(
                    -1,
                    CaptureQueueDisposition.Rejected,
                    submission.CorrelationId,
                    now,
                    "capture_service_stopping");
            }
            else if (submission.SubmittedUtc > now)
            {
                _rejected++;
                rejected = new(
                    -1,
                    CaptureQueueDisposition.Rejected,
                    submission.CorrelationId,
                    now,
                    "capture_submission_in_future");
            }
            else
            {
                var binding = ResolveSessionAtIntakeUnsafe(submission, now);
                if (binding is null)
                {
                    _rejected++;
                    AddNoticeUnsafe(
                        CaptureSessionNoticeKind.QueueRejected,
                        now,
                        submission.CorrelationId,
                        submission.SessionId,
                        null,
                        "unknown_or_terminal_session");
                    rejected = new(
                        -1,
                        CaptureQueueDisposition.Rejected,
                        submission.CorrelationId,
                        now,
                        "unknown_or_terminal_session");
                }
                else
                {
                    var intakeSequence = checked(_nextIntakeSequence++);
                    binding.Session.ActiveCaptureCount++;
                    queued = new(
                        intakeSequence,
                        submission,
                        now,
                        binding.Session.Request.SessionId,
                        binding.ClaimedArmedIntent,
                        binding.CreatedSession);
                    _queueDepth++;
                }
            }

            changed = _changed;
        }

        if (rejected is not null)
        {
            submission.Source.Dispose();
            Notify(changed);
            return rejected;
        }

        Notify(changed);
        try
        {
            await _queue.Writer.WriteAsync(queued!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            submission.Source.Dispose();
            var rearmed = false;
            lock (_gate)
            {
                _queueDepth--;
                _rejected++;
                rearmed = RollBackIntakeUnsafe(queued!, _timeProvider.GetUtcNow());
                changed = _changed;
            }
            if (rearmed)
            {
                _ = ExpireIntentAsync(queued!.BoundSessionId, _lifetime.Token);
            }

            Notify(changed);
            return new(
                queued!.IntakeSequence,
                CaptureQueueDisposition.Cancelled,
                submission.CorrelationId,
                _timeProvider.GetUtcNow(),
                "queue_wait_cancelled");
        }
        catch (ChannelClosedException)
        {
            submission.Source.Dispose();
            var rearmed = false;
            lock (_gate)
            {
                _queueDepth--;
                _rejected++;
                rearmed = RollBackIntakeUnsafe(queued!, _timeProvider.GetUtcNow());
                changed = _changed;
            }
            if (rearmed)
            {
                _ = ExpireIntentAsync(queued!.BoundSessionId, _lifetime.Token);
            }

            Notify(changed);
            return new(
                queued!.IntakeSequence,
                CaptureQueueDisposition.Rejected,
                submission.CorrelationId,
                _timeProvider.GetUtcNow(),
                "capture_service_stopping");
        }

        lock (_gate)
        {
            _accepted++;
            changed = _changed;
        }

        Notify(changed);
        return new(
            queued!.IntakeSequence,
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
        var trimmedOrigin = origin.Trim();
        if (trimmedOrigin.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(origin));
        }

        EventHandler? changed;
        CancellationTokenSource cancellation;
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

            var now = _timeProvider.GetUtcNow();
            session.CancellationRequested = true;
            foreach (var pending in _pendingReviews.Values.Where(item => item.SessionId == sessionId))
            {
                pending.Decision.TrySetResult(new(CaptureReviewAction.Cancel, trimmedOrigin, now));
            }

            if (session.ActiveCaptureCount == 0)
            {
                session.AppendSession(CaptureSessionStage.Cancelled, now, "session_cancelled");
                PruneSessionsUnsafe();
            }

            cancellation = session.Cancellation;
            changed = _changed;
        }

        cancellation.Cancel();
        Notify(changed);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource[] sessionCancellations;
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
                session.RequestTerminal(CaptureSessionStage.Cancelled);
            }

            foreach (var pending in _pendingReviews.Values)
            {
                pending.Decision.TrySetResult(new(
                    CaptureReviewAction.Cancel,
                    "shutdown",
                    now));
            }

            sessionCancellations = [.. _sessions.Values.Select(item => item.Cancellation)];
        }

        foreach (var cancellation in sessionCancellations)
        {
            cancellation.Cancel();
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
                queued.Submission.Source.Dispose();
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
            for (attempts = 1; attempts <= _options.MaximumDecodeAttempts; attempts++)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                FinalizeUnstartedCapture(queued, session, diagnostic ?? "decode_failed");
                return PreparedCapture.Rejected;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReservePixels(pixels))
            {
                pixels.Dispose();
                pixels = null;
                FinalizeUnstartedCapture(queued, session, "decoded_pixel_budget_exceeded");
                return PreparedCapture.Rejected;
            }

            reserved = true;
            var digest = ContentIdentity(pixels.Image);
            EventHandler? changed;
            lock (_gate)
            {
                var now = _timeProvider.GetUtcNow();
                TrimDeduplicationUnsafe(now);
                if (_deduplication.ContainsKey(digest))
                {
                    duplicate = true;
                    _duplicate++;
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
                    RejectUnsafe(queued, "stale_or_cancelled_session");
                    changed = _changed;
                }
                else
                {
                    var capturedUtc = pixels.Image.CapturedUtc == default
                        ? queued.Submission.SubmittedUtc
                        : pixels.Image.CapturedUtc.ToUniversalTime();
                    var artifactId = $"capture-{Guid.NewGuid():N}";
                    artifact = session.AddArtifact(
                        artifactId,
                        queued.Submission.DeliveryKind,
                        queued.Submission.CorrelationId,
                        queued.Submission.Source.SourceKind,
                        capturedUtc,
                        queued.Submission.SubmittedUtc,
                        queued.Submission.BatchId,
                        attempts,
                        digest);
                    for (var retry = 1; retry <= attempts; retry++)
                    {
                        session.AppendCapture(artifact, CaptureSessionStage.Settling, now, retry == 1 ? "source_settled" : "decode_retry");
                        session.AppendCapture(artifact, CaptureSessionStage.Decoding, now, $"decode_attempt_{retry}");
                    }

                    session.AppendCapture(artifact, CaptureSessionStage.DetectingContext, now, "detecting_context");
                    _deduplication.Add(digest, new(session.Request.SessionId, artifactId, now.Add(_options.DeduplicationLifetime)));
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
                FinalizeUnstartedCapture(queued, session, "capture_cancelled");
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
                FinalizeUnstartedCapture(queued, session, "capture_prepare_failed");
            }

            return PreparedCapture.Rejected;
        }
        finally
        {
            if (pixels is not null && reserved && !transferred)
            {
                ReleasePixels(pixels, session.Request.SessionId, artifact?.ArtifactId, "capture_cleanup");
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
                    decision = new(CaptureReviewAction.Cancel, "session-cancelled", _timeProvider.GetUtcNow());
                }

                if (decision.Action == CaptureReviewAction.Redecode)
                {
                    lock (_gate)
                    {
                        if (_pendingReviews.TryGetValue(artifact.ArtifactId, out var current)
                            && ReferenceEquals(current, pending))
                        {
                            _pendingReviews.Remove(artifact.ArtifactId);
                            artifact.Review = null;
                        }
                    }

                    if (await RedecodeAsync(queued, session, artifact, pixels, operation.Token).ConfigureAwait(false))
                    {
                        continue;
                    }

                    decision = new(
                        CaptureReviewAction.Cancel,
                        session.CancellationRequested ? "session-cancelled" : "redecode-failed",
                        _timeProvider.GetUtcNow());
                }

                CaptureAcceptedEventArgs? accepted = null;
                CaptureCorrection correction;
                var rearm = false;
                var cancelSession = false;
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
                    correction = new(
                        decision.Action,
                        session.Request.Intent,
                        artifact.Analysis?.DetectedContext,
                        correctedUtc,
                        decision.Origin);
                    artifact.Corrections.Add(correction);

                    switch (decision.Action)
                    {
                        case CaptureReviewAction.UseDetected:
                        case CaptureReviewAction.UseArmedIntent:
                            session.AppendCapture(artifact, CaptureSessionStage.Complete, correctedUtc, "review_accepted");
                            artifact.IsTerminal = true;
                            artifact.Disposition = CaptureQueueDisposition.Accepted;
                            if (queued.Submission.EndSessionAfterReview)
                            {
                                session.RequestTerminal(CaptureSessionStage.Complete);
                            }

                            if (session.PendingTerminalStage == CaptureSessionStage.Complete
                                && session.ActiveCaptureCount == 1
                                && session.Artifacts.All(item => item.IsTerminal))
                            {
                                session.AppendSession(CaptureSessionStage.Complete, correctedUtc, "session_complete");
                                PruneSessionsUnsafe();
                            }

                            var effectiveIntent = decision.Action == CaptureReviewAction.UseArmedIntent
                                ? session.Request.Intent
                                : IntentForDetectedContext(artifact.Analysis!.DetectedContext, session.Request.Intent);
                            accepted = new(
                                session.Request.SessionId,
                                artifact.ArtifactId,
                                artifact.Analysis!,
                                session.Context,
                                artifact.CorrelationId,
                                artifact.SourceKind,
                                artifact.CapturedUtc,
                                artifact.SubmittedUtc,
                                artifact.BatchId,
                                decision.Action,
                                effectiveIntent,
                                correction);
                            break;

                        case CaptureReviewAction.RetryCapture:
                            session.AppendCapture(artifact, CaptureSessionStage.Cancelled, correctedUtc, "retry_requested");
                            artifact.IsTerminal = true;
                            artifact.Disposition = CaptureQueueDisposition.Cancelled;
                            RemoveDeduplicationUnsafe(artifact);
                            rearm = RearmIntentUnsafe(session, correctedUtc, "retry_requested");
                            break;

                        default:
                            session.AppendCapture(artifact, CaptureSessionStage.Cancelled, correctedUtc, "review_cancelled");
                            artifact.IsTerminal = true;
                            artifact.Disposition = CaptureQueueDisposition.Cancelled;
                            RemoveDeduplicationUnsafe(artifact);
                            session.CancellationRequested = true;
                            cancelSession = true;
                            if (session.ActiveCaptureCount == 1)
                            {
                                session.AppendSession(CaptureSessionStage.Cancelled, correctedUtc, "session_cancelled");
                                PruneSessionsUnsafe();
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
                    changed = _changed;
                }

                if (cancelSession)
                {
                    session.Cancellation.Cancel();
                }

                if (rearm)
                {
                    _ = ExpireIntentAsync(session.Request.SessionId, _lifetime.Token);
                }

                if (accepted is not null)
                {
                    NotifyAccepted(accepted);
                }

                Notify(changed);
                return;
            }
        }
        finally
        {
            EventHandler? changed;
            lock (_gate)
            {
                _pendingReviews.Remove(artifact.ArtifactId);
                if (!pixels.IsDisposed)
                {
                    if (!artifact.IsTerminal)
                    {
                        FinalizeArtifactUnsafe(queued, session, artifact, CaptureSessionStage.Cancelled, "review_abandoned");
                    }

                    artifact.Review = null;
                    artifact.PixelsRetained = false;
                    ReleasePixelsUnsafe(pixels, session.Request.SessionId, artifact.ArtifactId, "review_cleanup");
                }

                CompleteActiveCaptureUnsafe(queued, session, _timeProvider.GetUtcNow());
                changed = _changed;
            }

            Notify(changed);
        }
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
                return new(CaptureReviewAction.Cancel, "review-expired", _timeProvider.GetUtcNow());
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
                existing.IntentClaimed = true;
                claimed = true;
            }

            return new(existing, claimed, false);
        }

        if (_armedSessionId is { } armed && _sessions.TryGetValue(armed, out var armedSession))
        {
            _armedSessionId = null;
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
        session.AppendSession(CaptureSessionStage.Cancelled, now, "intent_expired");
        AddNoticeUnsafe(CaptureSessionNoticeKind.IntentExpired, now, null, sessionId, null, "intent_expired");
        PruneSessionsUnsafe();
        return true;
    }

    private void FinalizeUnstartedCapture(QueuedCapture queued, MutableSession session, string code)
    {
        var rearmed = false;
        EventHandler? changed;
        lock (_gate)
        {
            RejectUnsafe(queued, code);
            if (queued.ClaimedArmedIntent && !session.CancellationRequested)
            {
                rearmed = RearmIntentUnsafe(session, _timeProvider.GetUtcNow(), code);
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

    private void FinalizeArtifactFailure(
        QueuedCapture queued,
        MutableSession session,
        MutableArtifact artifact,
        CapturePixelLease pixels,
        string code,
        bool cancelled)
    {
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
        artifact.Disposition = terminalStage == CaptureSessionStage.Complete
            ? CaptureQueueDisposition.Accepted
            : CaptureQueueDisposition.Cancelled;
        artifact.DiagnosticCode = code;
        if (terminalStage != CaptureSessionStage.Complete)
        {
            RemoveDeduplicationUnsafe(artifact);
        }

        if (terminalStage == CaptureSessionStage.Cancelled)
        {
            session.CancellationRequested = true;
            session.RequestTerminal(CaptureSessionStage.Cancelled);
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

        if (queued.CreatedSession && session.ActiveCaptureCount == 0 && session.Artifacts.Count == 0)
        {
            _sessions.Remove(session.Request.SessionId);
            session.Cancellation.Dispose();
            return false;
        }

        return queued.ClaimedArmedIntent && RearmIntentUnsafe(session, now, "queue_wait_cancelled");
    }

    private bool RestoreIntentAfterUnusableCaptureUnsafe(
        QueuedCapture queued,
        MutableSession session,
        DateTimeOffset now)
    {
        if (queued.ClaimedArmedIntent)
        {
            return RearmIntentUnsafe(session, now, "capture_not_consumed");
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
            || (_armedSessionId is { } armed && armed != session.Request.SessionId))
        {
            return false;
        }

        session.ReplaceExpiry(now.Add(_options.IntentLifetime));
        session.PendingTerminalStage = null;
        session.IntentClaimed = false;
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

    private bool EnsureSessionCapacityUnsafe()
    {
        foreach (var terminal in _sessions.Values
                     .Where(item => item.IsTerminal && item.ActiveCaptureCount == 0)
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
                     .Where(item => item.IsTerminal && item.ActiveCaptureCount == 0)
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
        if (_deduplication.TryGetValue(artifact.ContentDigest, out var entry)
            && entry.SessionId == artifact.SessionId
            && string.Equals(entry.ArtifactId, artifact.ArtifactId, StringComparison.Ordinal))
        {
            _deduplication.Remove(artifact.ContentDigest);
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
        DateTimeOffset EnqueuedUtc,
        CaptureSessionId BoundSessionId,
        bool ClaimedArmedIntent,
        bool CreatedSession);

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

        public CaptureSessionRequest Request { get; private set; } = request;

        public CaptureContextMetadata Context { get; } = context;

        public CaptureGuidance Guidance { get; } = guidance;

        public List<MutableArtifact> Artifacts { get; } = [];

        public bool IntentClaimed { get; set; }

        public bool CancellationRequested { get; set; }

        public int ActiveCaptureCount { get; set; }

        public CaptureSessionStage? PendingTerminalStage { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public DateTimeOffset LastChangedUtc { get; private set; } = request.RequestedUtc;

        public bool IsTerminal { get; private set; }

        public MutableArtifact AddArtifact(
            string artifactId,
            CaptureDeliveryKind deliveryKind,
            CaptureCorrelationId correlationId,
            CaptureSourceKind sourceKind,
            DateTimeOffset capturedUtc,
            DateTimeOffset submittedUtc,
            string? batchId,
            int decodeAttempts,
            string contentDigest)
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
                contentDigest);
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
        string contentDigest)
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

        public string ContentDigest { get; } = string.IsNullOrWhiteSpace(contentDigest)
            ? throw new ArgumentException("A private content identity is required.", nameof(contentDigest))
            : contentDigest;

        public Confidence Confidence { get; set; } = Confidence.Unknown;

        public CaptureAnalysis? Analysis { get; set; }

        public CaptureReviewRequest? Review { get; set; }

        public List<CaptureCorrection> Corrections { get; } = [];

        public CaptureQueueDisposition Disposition { get; set; } = CaptureQueueDisposition.Accepted;

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
            Disposition,
            PixelsRetained,
            DecodeAttempts,
            DecodeRevision,
            DiagnosticCode);
    }
}
