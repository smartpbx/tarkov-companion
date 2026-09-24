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
    /// Files are decoded before admission so the last readable picture can close the session;
    /// otherwise a missing last file would leave the earlier captures permanently open. The
    /// batch is bounded, every prepared buffer is owned by <see cref="MemoryCaptureSource"/>,
    /// and cancellation clears every source that intake has not handed to the coordinator.
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

        var prepared = new List<PreparedImage>(Math.Min(inputs.Count, MaximumBatchImages));
        var terminal = new HashSet<string>(StringComparer.Ordinal);
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
        try
        {
            for (var index = 0; index < inputs.Count; index++)
            {
                var input = inputs[index];
                if (index >= MaximumBatchImages)
                {
                    failed++;
                    Publish(new(input.Id, ShellText.CaptureRowNotQueuedLimit(MaximumBatchImages), true));
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled += MarkCancelled(inputs, index, Publish);
                    break;
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

                prepared.Add(new(
                    input,
                    new MemoryCaptureSource(
                        image,
                        origin == ManualImageOrigin.Paste && input.Image is not null
                            ? CaptureSourceKind.ClipboardImage
                            : CaptureSourceKind.UserSelectedImage)));
            }

            var context = CaptureIntakeContext.For(_sessions, _context.Describe());
            for (var index = 0; index < prepared.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    for (var remaining = index; remaining < prepared.Count; remaining++)
                    {
                        prepared[remaining].Source.Dispose();
                        cancelled++;
                        Publish(new(prepared[remaining].Input.Id, ShellText.CaptureRowCancelled, true));
                    }

                    _sessions.Cancel(sessionId, "manual-batch-cancelled");
                    break;
                }

                var item = prepared[index];
                var correlationId = CaptureCorrelationId.New();
                var receipt = await _sessions.EnqueueAsync(
                        new(
                            CaptureDeliveryKind.Batch,
                            item.Source,
                            context,
                            _timeProvider.GetUtcNow(),
                            correlationId,
                            sessionId,
                            endSessionAfterReview: index == prepared.Count - 1,
                            batchId),
                        cancellationToken)
                    .ConfigureAwait(false);
                item.HandedOff = true;
                if (receipt.Disposition == CaptureQueueDisposition.Accepted)
                {
                    accepted++;
                    Publish(new(item.Input.Id, ShellText.CaptureRowQueued, false, correlationId));
                }
                else if (receipt.Disposition == CaptureQueueDisposition.Cancelled)
                {
                    cancelled++;
                    Publish(new(item.Input.Id, ShellText.CaptureRowCancelled, true, correlationId));
                    cancelled += CancelRemaining(prepared, index + 1, Publish);
                    _sessions.Cancel(sessionId, "manual-batch-cancelled");
                    break;
                }
                else
                {
                    failed++;
                    Publish(new(item.Input.Id, ShellText.CaptureRowNotQueued(receipt.Code), true, correlationId));
                    cancelled += CancelRemaining(prepared, index + 1, Publish);
                    _sessions.Cancel(sessionId, "manual-batch-admission-failed");
                    break;
                }
            }

            return new(accepted, failed, cancelled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var item in prepared.Where(item => !item.HandedOff))
            {
                item.Source.Dispose();
            }

            foreach (var input in inputs.Where(input => !terminal.Contains(input.Id)))
            {
                cancelled++;
                Publish(new(input.Id, ShellText.CaptureRowCancelled, true));
            }

            _sessions.Cancel(sessionId, "manual-batch-cancelled");
            return new(accepted, failed, cancelled);
        }
        catch
        {
            foreach (var item in prepared.Where(item => !item.HandedOff))
            {
                item.Source.Dispose();
            }

            throw;
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

    private static int MarkCancelled(
        IReadOnlyList<ManualImageInput> inputs,
        int first,
        Action<ManualImageBatchUpdate> report)
    {
        for (var index = first; index < inputs.Count; index++)
        {
            report(new(inputs[index].Id, ShellText.CaptureRowCancelled, true));
        }

        return inputs.Count - first;
    }

    private static int CancelRemaining(
        IReadOnlyList<PreparedImage> prepared,
        int first,
        Action<ManualImageBatchUpdate> report)
    {
        for (var index = first; index < prepared.Count; index++)
        {
            prepared[index].Source.Dispose();
            report(new(prepared[index].Input.Id, ShellText.CaptureRowCancelled, true));
        }

        return prepared.Count - first;
    }

    private sealed class PreparedImage(ManualImageInput input, MemoryCaptureSource source)
    {
        public ManualImageInput Input { get; } = input;

        public MemoryCaptureSource Source { get; } = source;

        public bool HandedOff { get; set; }
    }
}
