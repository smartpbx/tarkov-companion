using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.App.Services.V2.Capture;

/// <summary>How a picture the player chose reached the companion.</summary>
public enum ManualImageOrigin
{
    Paste = 1,
    Drop,
    Picker,
}

/// <param name="Accepted">Whether intake queued the picture.</param>
/// <param name="Message">One short line for the capture panel.</param>
public sealed record ManualImageOutcome(bool Accepted, string Message);

/// <summary>One file or in-memory picture in a manual batch.</summary>
public sealed record ManualImageInput(string Id, string Label, string? FilePath, CapturedImage? Image);

/// <summary>An intake update for one picture, before recognition owns the rest of its lifecycle.</summary>
public sealed record ManualImageBatchUpdate(
    string Id,
    string Status,
    bool IsTerminal,
    CaptureCorrelationId? CorrelationId = null);

public sealed record ManualImageBatchOutcome(int Accepted, int Failed, int Cancelled);

/// <summary>
/// Puts a picture the player pasted, dropped or picked onto the same intake the screenshot
/// watcher uses.
/// </summary>
/// <remarks>
/// The only way into a scan was the game writing a file, so nothing could be tried without the
/// game running. This reads a file the player chose, or the image on their clipboard, and
/// nothing else: it sends no input anywhere and touches nothing in the game. The pixels go
/// through <see cref="MemoryCaptureSource"/> and are released by the capture session like any
/// other frame; nothing is written to disk.
/// </remarks>
public sealed class ManualImageIntake(
    ICaptureSessionService sessions,
    IScreenshotImageLoader loader,
    ICaptureContextSource context,
    TimeProvider? timeProvider = null)
{
    public const int MaximumBatchImages = 32;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp"];

    private readonly ICaptureSessionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IScreenshotImageLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    private readonly ICaptureContextSource _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public static bool IsImageFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public async Task<ManualImageOutcome> SubmitFileAsync(string path, ManualImageOrigin origin, CancellationToken cancellationToken)
    {
        if (!IsImageFile(path))
        {
            return new(false, ShellText.CaptureNotAPictureFile);
        }

        if (!File.Exists(path))
        {
            return new(false, ShellText.CaptureFileGone);
        }

        var image = await _loader.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return image is null
            ? new(false, ShellText.CapturePictureUnreadable)
            : await SubmitAsync(image, origin, CaptureSourceKind.UserSelectedImage, cancellationToken).ConfigureAwait(false);
    }

    public Task<ManualImageOutcome> SubmitImageAsync(CapturedImage image, ManualImageOrigin origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        return SubmitAsync(
            image,
            origin,
            origin == ManualImageOrigin.Paste ? CaptureSourceKind.ClipboardImage : CaptureSourceKind.UserSelectedImage,
            cancellationToken);
    }

    /// <summary>
    /// Decodes and admits several player-selected pictures as one ordered capture session.
    /// </summary>
    /// <remarks>
    /// Each picture is decoded one ahead of its admission, so the last readable picture is known
    /// when it is admitted and can close the session; a missing last file would otherwise leave
    /// the earlier captures permanently open.
    ///
    /// #887: every picture used to be decoded up front and admitted in one synchronous burst, so
    /// the capture budget (96 MB of decoded pixels) was full before the pump released anything.
    /// At 1440p the seventh picture was refused, and that refusal cancelled the whole session,
    /// taking the six accepted pictures with it. Now intake holds at most two decoded pictures,
    /// waits for the budget to have room before admitting each, and a picture that still cannot
    /// be admitted fails alone. Cancellation clears every source intake has not handed on.
    /// </remarks>
    public async Task<ManualImageBatchOutcome> SubmitBatchAsync(
        IReadOnlyList<ManualImageInput> inputs,
        ManualImageOrigin origin,
        string batchId,
        CaptureSessionId sessionId,
        Action<ManualImageBatchUpdate> report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(report);
        if (inputs.Count == 0)
        {
            return new(0, 0, 0);
        }

        var terminal = new HashSet<string>(StringComparer.Ordinal);
        var admitted = new HashSet<string>(StringComparer.Ordinal);
        void Publish(ManualImageBatchUpdate update)
        {
            if (update.IsTerminal)
            {
                terminal.Add(update.Id);
            }

            report(update);
        }

        var accepted = 0;
        var failed = 0;
        var cancelled = 0;
        var nextInput = 0;
        PreparedImage? current = null;
        PreparedImage? next = null;

        // Reads inputs from nextInput on until one decodes, reporting each that does not.
        async Task<PreparedImage?> PrepareNextAsync()
        {
            while (nextInput < inputs.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = nextInput++;
                var input = inputs[index];
                if (index >= MaximumBatchImages)
                {
                    failed++;
                    Discard(input);
                    Publish(new(input.Id, ShellText.CaptureRowNotQueuedLimit(MaximumBatchImages), true));
                    continue;
                }

                Publish(new(input.Id, ShellText.CaptureRowReading, false));
                CapturedImage? image;
                if (input.Image is not null)
                {
                    image = input.Image;
                }
                else if (!IsImageFile(input.FilePath))
                {
                    failed++;
                    Publish(new(input.Id, ShellText.CaptureRowNotAPicture, true));
                    continue;
                }
                else if (!File.Exists(input.FilePath))
                {
                    failed++;
                    Publish(new(input.Id, ShellText.CaptureRowFileGone, true));
                    continue;
                }
                else
                {
                    try
                    {
                        image = await _loader.LoadAsync(input.FilePath, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        failed++;
                        Publish(new(input.Id, ShellText.CaptureRowUnreadable, true));
                        continue;
                    }
                }

                if (image is null)
                {
                    failed++;
                    Publish(new(input.Id, ShellText.CaptureRowUnreadable, true));
                    continue;
                }

                var budget = _sessions.Snapshot.PixelBudget;
                if (budget > 0 && image.Pixels.Length > budget)
                {
                    // Larger than the whole budget: no amount of waiting admits it.
                    failed++;
                    Discard(input with { Image = image });
                    Publish(new(input.Id, ShellText.CaptureRowNotQueued(PixelBudgetExceeded), true));
                    continue;
                }

                return new(
                    input,
                    image,
                    new MemoryCaptureSource(
                        image,
                        origin == ManualImageOrigin.Paste && input.Image is not null
                            ? CaptureSourceKind.ClipboardImage
                            : CaptureSourceKind.UserSelectedImage),
                    image.Pixels.Length);
            }

            return null;
        }

        int CancelEverythingLeft()
        {
            var count = 0;
            foreach (var item in new[] { current, next })
            {
                if (item is not null && !item.HandedOff && !terminal.Contains(item.Input.Id))
                {
                    item.Source.Dispose();
                    count++;
                    Publish(new(item.Input.Id, ShellText.CaptureRowCancelled, true));
                }
            }

            for (; nextInput < inputs.Count; nextInput++)
            {
                Discard(inputs[nextInput]);
                if (!terminal.Contains(inputs[nextInput].Id))
                {
                    count++;
                    Publish(new(inputs[nextInput].Id, ShellText.CaptureRowCancelled, true));
                }
            }

            current = null;
            next = null;
            return count;
        }

        try
        {
            var context = CaptureIntakeContext.For(_sessions, _context.Describe());
            current = await PrepareNextAsync().ConfigureAwait(false);
            while (current is not null)
            {
                next = await PrepareNextAsync().ConfigureAwait(false);
                var isLast = next is null;
                await WaitForRoomAsync(current.PixelBytes, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled += CancelEverythingLeft();
                    _sessions.Cancel(sessionId, "manual-batch-cancelled");
                    break;
                }

                var item = current;
                var correlationId = CaptureCorrelationId.New();
                // #937: the last picture is the one that closes the session, so a transient refusal
                // of it is retried once. A refused source is zeroed, so the retry needs a copy.
                var spare = isLast ? Copy(item.Image) : null;
                CaptureQueueReceipt receipt;
                try
                {
                    receipt = await EnqueueAsync(item.Source, correlationId).ConfigureAwait(false);
                    item.HandedOff = true;
                    if (spare is not null
                        && receipt.Disposition == CaptureQueueDisposition.Rejected
                        && IsTransient(receipt.Code))
                    {
                        await WaitForRoomAsync(item.PixelBytes, cancellationToken).ConfigureAwait(false);
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            var retry = new MemoryCaptureSource(spare, item.Source.SourceKind);
                            spare = null;
                            correlationId = CaptureCorrelationId.New();
                            receipt = await EnqueueAsync(retry, correlationId).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    if (spare is not null)
                    {
                        Discard(item.Input with { Image = spare });
                    }
                }

                ValueTask<CaptureQueueReceipt> EnqueueAsync(MemoryCaptureSource source, CaptureCorrelationId id) =>
                    _sessions.EnqueueAsync(
                        new(
                            CaptureDeliveryKind.Batch,
                            source,
                            context,
                            _timeProvider.GetUtcNow(),
                            id,
                            sessionId,
                            endSessionAfterReview: isLast,
                            batchId),
                        cancellationToken);
                if (receipt.Disposition == CaptureQueueDisposition.Accepted)
                {
                    accepted++;
                    admitted.Add(item.Input.Id);
                    Publish(new(item.Input.Id, ShellText.CaptureRowQueued, false, correlationId));
                }
                else if (receipt.Disposition == CaptureQueueDisposition.Cancelled)
                {
                    cancelled++;
                    Publish(new(item.Input.Id, ShellText.CaptureRowCancelled, true, correlationId));
                    current = null;
                    cancelled += CancelEverythingLeft();
                    _sessions.Cancel(sessionId, "manual-batch-cancelled");
                    break;
                }
                else if (!isLast && IsTransient(receipt.Code))
                {
                    // A room that filled between the wait and the admission (a game screenshot
                    // arriving at that instant): this picture fails, the session and the
                    // pictures already accepted carry on.
                    failed++;
                    Publish(new(item.Input.Id, ShellText.CaptureRowNotQueued(receipt.Code), true, correlationId));
                }
                else
                {
                    // The session itself refused (gone, context changed, or the picture meant to
                    // close it could not be admitted), so nothing after this can close it either.
                    failed++;
                    Publish(new(item.Input.Id, ShellText.CaptureRowNotQueued(receipt.Code), true, correlationId));
                    current = null;
                    cancelled += CancelEverythingLeft();
                    // #937: the last picture still refused after its retry fails alone. The pictures
                    // already accepted keep their reviews, and the session ends once those finish.
                    if (!(isLast && accepted > 0 && IsTransient(receipt.Code)
                        && _sessions.EndWhenIdle(sessionId, "manual-batch-last-not-admitted")))
                    {
                        _sessions.Cancel(sessionId, "manual-batch-admission-failed");
                    }

                    break;
                }

                current = next;
                next = null;
            }

            return new(accepted, failed, cancelled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled += CancelEverythingLeft();
            // A picture caught mid-read has said "Reading" and nothing since.
            foreach (var input in inputs.Where(input => !terminal.Contains(input.Id) && !admitted.Contains(input.Id)))
            {
                cancelled++;
                Publish(new(input.Id, ShellText.CaptureRowCancelled, true));
            }

            _sessions.Cancel(sessionId, "manual-batch-cancelled");
            return new(accepted, failed, cancelled);
        }
        catch
        {
            foreach (var item in new[] { current, next })
            {
                if (item is not null && !item.HandedOff)
                {
                    item.Source.Dispose();
                }
            }

            throw;
        }
    }

    /// <summary>How long a batch waits for the pixel budget before submitting regardless.</summary>
    /// <remarks>
    /// Longer than a review can hold a picture (two minutes), so the wait ends with room unless
    /// something else holds the budget; then the coordinator's refusal decides that one picture.
    /// </remarks>
    internal static TimeSpan BudgetWait { get; } = TimeSpan.FromMinutes(5);

    private const string PixelBudgetExceeded = "decoded_pixel_budget_exceeded";

    /// <summary>A copy of a picture's pixels in an array of its own, which is what a capture lease needs.</summary>
    private static CapturedImage Copy(CapturedImage image) => image with { Pixels = image.Pixels.ToArray() };

    private static bool IsTransient(string? code) =>
        code is PixelBudgetExceeded or "capture_queue_full";

    /// <summary>Waits until the coordinator has room for <paramref name="pixelBytes"/> and a queue slot.</summary>
    private async Task WaitForRoomAsync(long pixelBytes, CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + BudgetWait;
        while (!cancellationToken.IsCancellationRequested)
        {
            var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Wake(object? sender, EventArgs arguments) => woken.TrySetResult();
            _sessions.Changed += Wake;
            try
            {
                var snapshot = _sessions.Snapshot;
                var hasRoom = snapshot.PixelBudget <= 0
                    || pixelBytes <= snapshot.PixelBudget - snapshot.PixelsInUse;
                var hasSlot = snapshot.QueueCapacity <= 0 || snapshot.QueueDepth < snapshot.QueueCapacity;
                if ((hasRoom && hasSlot) || _timeProvider.GetUtcNow() >= deadline)
                {
                    return;
                }

                // Changed wakes it as soon as anything is released; the short delay is the
                // backstop for a release that raises no change.
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await Task.WhenAny(
                        woken.Task,
                        Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, poll.Token))
                    .ConfigureAwait(false);
                await poll.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                _sessions.Changed -= Wake;
            }
        }
    }

    /// <summary>Zeroes a picture the player handed over in memory that will never be admitted.</summary>
    private static void Discard(ManualImageInput input)
    {
        if (input.Image is { } image
            && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(image.Pixels, out var owned))
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(owned.AsSpan());
        }
    }

    private async Task<ManualImageOutcome> SubmitAsync(
        CapturedImage image,
        ManualImageOrigin origin,
        CaptureSourceKind sourceKind,
        CancellationToken cancellationToken)
    {
        var receipt = await _sessions.EnqueueAsync(
                new(
                    origin switch
                    {
                        ManualImageOrigin.Paste => CaptureDeliveryKind.Paste,
                        ManualImageOrigin.Drop => CaptureDeliveryKind.Drop,
                        _ => CaptureDeliveryKind.Picker,
                    },
                    new MemoryCaptureSource(image, sourceKind),
                    // The armed session's own context, or where the player is now.
                    CaptureIntakeContext.For(_sessions, _context.Describe()),
                    _timeProvider.GetUtcNow(),
                    CaptureCorrelationId.New()),
                cancellationToken)
            .ConfigureAwait(false);
        return receipt.Disposition switch
        {
            CaptureQueueDisposition.Accepted => new(true, ShellText.CaptureReadingPicture(image.Width, image.Height)),
            CaptureQueueDisposition.Duplicate => new(false, ShellText.CaptureAlreadyRead),
            _ => new(false, ShellText.CaptureNotTakenIn(receipt.Code)),
        };
    }

    private sealed class PreparedImage(ManualImageInput input, CapturedImage image, MemoryCaptureSource source, long pixelBytes)
    {
        public ManualImageInput Input { get; } = input;

        public CapturedImage Image { get; } = image;

        public MemoryCaptureSource Source { get; } = source;

        public long PixelBytes { get; } = pixelBytes;

        public bool HandedOff { get; set; }
    }
}
