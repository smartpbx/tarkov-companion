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
            return new(false, "That file is not a picture");
        }

        if (!File.Exists(path))
        {
            return new(false, "That file is no longer there");
        }

        var image = await _loader.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return image is null
            ? new(false, "That picture could not be read")
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
            CaptureQueueDisposition.Accepted => new(true, $"Reading a {image.Width} × {image.Height} picture"),
            CaptureQueueDisposition.Duplicate => new(false, "That picture was already read"),
            _ => new(false, $"The picture was not taken in ({receipt.Code})"),
        };
    }
}
