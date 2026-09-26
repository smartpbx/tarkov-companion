using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.V2Capture;

public sealed class ManualImageIntakeBatchTests
{
    private static readonly CaptureSessionId SessionId = new(Guid.Parse("a1000000-0000-0000-0000-000000000271"));

    private static readonly CaptureContextMetadata Context = new(
        "intel",
        "profile-a",
        "customs",
        "plan-a",
        "item-a",
        "scan-a",
        "this-desktop");

    [Theory]
    [InlineData(ManualImageOrigin.Paste, CaptureSourceKind.ClipboardImage)]
    [InlineData(ManualImageOrigin.Drop, CaptureSourceKind.UserSelectedImage)]
    [InlineData(ManualImageOrigin.Picker, CaptureSourceKind.UserSelectedImage)]
    public async Task ThreeImagesEnterOneOrderedBatchAndTheLastClosesIt(
        ManualImageOrigin origin,
        CaptureSourceKind expectedSource)
    {
        await using var sessions = new RecordingSessions();
        var intake = new ManualImageIntake(sessions, new NoFiles(), new FixedContextSource());
        var updates = new List<ManualImageBatchUpdate>();

        var outcome = await intake.SubmitBatchAsync(
            Inputs(3),
            origin,
            "batch-three",
            SessionId,
            updates.Add,
            CancellationToken.None);

        Assert.Equal(new ManualImageBatchOutcome(3, 0, 0), outcome);
        Assert.Equal(3, sessions.Submissions.Count);
        Assert.All(sessions.Submissions, submission =>
        {
            Assert.Equal(CaptureDeliveryKind.Batch, submission.DeliveryKind);
            Assert.Equal("batch-three", submission.BatchId);
            Assert.Equal(SessionId, submission.SessionId);
            Assert.Equal(Context, submission.Context);
            Assert.Equal(expectedSource, submission.SourceKind);
            Assert.IsType<MemoryCaptureSource>(submission.Source);
        });
        Assert.False(sessions.Submissions[0].EndSessionAfterReview);
        Assert.False(sessions.Submissions[1].EndSessionAfterReview);
        Assert.True(sessions.Submissions[2].EndSessionAfterReview);
        Assert.Equal(["Queued", "Queued", "Queued"],
            updates.Where(update => update.CorrelationId is not null).Select(update => update.Status));
    }

    [Fact]
    public async Task CancellationAfterFirstAdmissionStopsAndClearsTheRest()
    {
        using var cancellation = new CancellationTokenSource();
        await using var sessions = new RecordingSessions(() => cancellation.Cancel());
        var intake = new ManualImageIntake(sessions, new NoFiles(), new FixedContextSource());
        var pixels = new[]
        {
            Enumerable.Repeat((byte)1, 16).ToArray(),
            Enumerable.Repeat((byte)2, 16).ToArray(),
            Enumerable.Repeat((byte)3, 16).ToArray(),
        };
        var updates = new List<ManualImageBatchUpdate>();

        var outcome = await intake.SubmitBatchAsync(
            Inputs(pixels),
            ManualImageOrigin.Drop,
            "batch-cancel",
            SessionId,
            updates.Add,
            cancellation.Token);

        Assert.Equal(new ManualImageBatchOutcome(1, 0, 2), outcome);
        Assert.Single(sessions.Submissions);
        Assert.All(pixels[0], value => Assert.Equal(1, value));
        Assert.All(pixels[1], value => Assert.Equal(0, value));
        Assert.All(pixels[2], value => Assert.Equal(0, value));
        Assert.Equal(2, updates.Count(update => update is { Status: "Cancelled", IsTerminal: true }));
    }

    [Fact]
    public async Task AdmissionFailureCancelsTheSessionAndDoesNotSubmitLaterImages()
    {
        await using var sessions = new RecordingSessions(
            disposition: call => call == 2 ? CaptureQueueDisposition.Rejected : CaptureQueueDisposition.Accepted);
        var intake = new ManualImageIntake(sessions, new NoFiles(), new FixedContextSource());
        var pixels = new[]
        {
            Enumerable.Repeat((byte)1, 16).ToArray(),
            Enumerable.Repeat((byte)2, 16).ToArray(),
            Enumerable.Repeat((byte)3, 16).ToArray(),
        };

        var outcome = await intake.SubmitBatchAsync(
            Inputs(pixels),
            ManualImageOrigin.Picker,
            "batch-rejected",
            SessionId,
            _ => { },
            CancellationToken.None);

        Assert.Equal(new ManualImageBatchOutcome(1, 1, 1), outcome);
        Assert.Equal(2, sessions.Submissions.Count);
        Assert.Equal(1, sessions.CancelCalls);
        Assert.All(pixels[2], value => Assert.Equal(0, value));
    }

    /// <summary>
    /// #937: the last picture refused for want of room (a game screenshot took the slot) is tried
    /// again, and the session is not cancelled with the pictures already accepted in it.
    /// </summary>
    [Fact]
    public async Task ALastPictureRefusedForWantOfRoomIsTriedAgain()
    {
        await using var sessions = new RecordingSessions(
            disposition: call => call == 3 ? CaptureQueueDisposition.Rejected : CaptureQueueDisposition.Accepted,
            code: _ => "capture_queue_full");
        var intake = new ManualImageIntake(sessions, new NoFiles(), new FixedContextSource());

        var outcome = await intake.SubmitBatchAsync(Inputs(3), ManualImageOrigin.Drop, "batch-retry", SessionId, _ => { }, CancellationToken.None);

        Assert.Equal(new ManualImageBatchOutcome(3, 0, 0), outcome);
        Assert.Equal(4, sessions.Submissions.Count);
        Assert.True(sessions.Submissions[3].EndSessionAfterReview);
        var retried = await sessions.Submissions[3].Source.ReadAsync(CancellationToken.None);
        Assert.All(retried.Pixels!.Image.Pixels.ToArray(), value => Assert.Equal(3, value));
        retried.Pixels.Dispose();
        Assert.Equal(0, sessions.CancelCalls);
    }

    /// <summary>#937: refused again, the last picture fails alone and the session ends when the accepted ones are reviewed.</summary>
    [Fact]
    public async Task ALastPictureRefusedTwiceFailsAloneAndKeepsTheAcceptedOnes()
    {
        await using var sessions = new RecordingSessions(
            disposition: call => call >= 3 ? CaptureQueueDisposition.Rejected : CaptureQueueDisposition.Accepted,
            code: _ => "decoded_pixel_budget_exceeded");
        var intake = new ManualImageIntake(sessions, new NoFiles(), new FixedContextSource());

        var outcome = await intake.SubmitBatchAsync(Inputs(3), ManualImageOrigin.Drop, "batch-refused", SessionId, _ => { }, CancellationToken.None);

        Assert.Equal(new ManualImageBatchOutcome(2, 1, 0), outcome);
        Assert.Equal(4, sessions.Submissions.Count);
        Assert.Equal(0, sessions.CancelCalls);
        Assert.Equal(1, sessions.EndWhenIdleCalls);
    }

    private static IReadOnlyList<ManualImageInput> Inputs(int count) =>
        Inputs(Enumerable.Range(1, count).Select(index =>
            Enumerable.Repeat(checked((byte)index), 16).ToArray()).ToArray());

    private static IReadOnlyList<ManualImageInput> Inputs(IReadOnlyList<byte[]> pixels) =>
        [.. pixels.Select((buffer, index) => new ManualImageInput(
            $"item-{index + 1}",
            $"image-{index + 1}.png",
            null,
            new CapturedImage(
                buffer,
                2,
                2,
                8,
                PixelFormat.Bgra8888,
                DateTimeOffset.Parse("2026-09-23T00:00:00Z").AddSeconds(index),
                "batch-fixture")))];

    private sealed class FixedContextSource : ICaptureContextSource
    {
        public CaptureContextMetadata Describe(string? initiatingDevice = null) => Context;
    }

    private sealed class NoFiles : TarkovCompanion.Core.Abstractions.IScreenshotImageLoader
    {
        public Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The fixture supplies pixels directly.");
    }

    private sealed class RecordingSessions(
        Action? afterAdmission = null,
        Func<int, CaptureQueueDisposition>? disposition = null,
        Func<int, string>? code = null) : ICaptureSessionService
    {
        public int EndWhenIdleCalls { get; private set; }

        public bool EndWhenIdle(CaptureSessionId sessionId, string origin)
        {
            EndWhenIdleCalls++;
            return true;
        }

        public List<CaptureSubmission> Submissions { get; } = [];

        public int CancelCalls { get; private set; }

        public event EventHandler? Changed { add { } remove { } }

        public event EventHandler<CaptureReviewRequestedEventArgs>? ReviewRequested { add { } remove { } }

        public event EventHandler<CaptureAcceptedEventArgs>? Accepted { add { } remove { } }

        public CaptureSessionServiceSnapshot Snapshot => CaptureSessionServiceSnapshot.Empty;

        public CaptureArmReceipt Arm(CaptureArmRequest request) =>
            new(false, request.Request.SessionId, "not_used");

        public ValueTask<CaptureQueueReceipt> EnqueueAsync(
            CaptureSubmission submission,
            CancellationToken cancellationToken)
        {
            Submissions.Add(submission);
            afterAdmission?.Invoke();
            var result = disposition?.Invoke(Submissions.Count) ?? CaptureQueueDisposition.Accepted;
            if (result != CaptureQueueDisposition.Accepted)
            {
                submission.Source.Dispose();
            }

            return ValueTask.FromResult(new CaptureQueueReceipt(
                Submissions.Count - 1,
                result,
                submission.CorrelationId,
                submission.SubmittedUtc,
                result == CaptureQueueDisposition.Accepted ? "queued" : code?.Invoke(Submissions.Count) ?? "queue_full"));
        }

        public bool TryReview(
            CaptureSessionId sessionId,
            string artifactId,
            int decodeRevision,
            CaptureReviewAction action,
            string origin) => false;

        public bool Cancel(CaptureSessionId sessionId, string origin)
        {
            CancelCalls++;
            return true;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var submission in Submissions)
            {
                submission.Source.Dispose();
            }

            return ValueTask.CompletedTask;
        }
    }
}
