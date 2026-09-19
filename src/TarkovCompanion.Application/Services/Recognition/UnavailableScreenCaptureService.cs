using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Application.Services.Recognition;

/// <summary>
/// The screen-capture service now that nothing captures the screen.
/// </summary>
/// <remarks>
/// A GDI service used to fill this slot on Windows: it copied the game window's visible pixels on
/// a Scan click. Scans that matter read the picture the game itself saves, which is a better
/// record of what the player was looking at than a capture taken afterwards, so the capture stack
/// was retired (issue #316) rather than given a time and memory budget. It also removed the
/// window-handle reuse, region-overflow and desktop-fallback findings of the Windows platform
/// audit (WIN-001 to WIN-004) instead of mitigating them.
///
/// The slot stays because <see cref="ScanUseCase"/> and <see cref="CaptureSessions.VisiblePixelCaptureSource"/>
/// take one, and both already turn <see cref="PlatformNotSupportedException"/> into an honest
/// "capture unavailable" outcome, so a Scan click reports that instead of failing. Putting a
/// capturing service back is a decision to make deliberately, with bounds, not by default.
/// </remarks>
public sealed class UnavailableScreenCaptureService : IScreenCaptureService
{
    public const string Message =
        "Screen capture is turned off. Take a screenshot in the game and the companion reads that file.";

    public Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException(Message);
}
