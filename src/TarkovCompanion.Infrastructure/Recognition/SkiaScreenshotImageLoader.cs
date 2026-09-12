using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// Reads a screenshot the game wrote, so the player's own key can drive a scan.
/// </summary>
/// <remarks>
/// Decoding to the same pixel layout the screen capture produces means the recogniser cannot
/// tell the two apart, and nothing downstream needs to know which one it is looking at.
///
/// A file the game is still writing is a real possibility, since the watcher can see it within
/// a second of its creation. A partial read comes back as a decode failure rather than as
/// garbage, and the caller treats that as nothing to scan.
/// </remarks>
public sealed class SkiaScreenshotImageLoader : IScreenshotImageLoader
{
    /// <summary>
    /// The largest picture worth decoding, in pixels.
    /// </summary>
    /// <remarks>
    /// A 4K screenshot is about eight million pixels and thirty-three megabytes once decoded,
    /// which is fine. This guards against something absurd rather than against ordinary
    /// screenshots, and refusing is better than exhausting memory on a machine that is also
    /// running the game.
    /// </remarks>
    private const long MaximumPixels = 40_000_000;

    public async Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            return Decode(bytes, path);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or OutOfMemoryException)
        {
            return null;
        }
    }

    private static CapturedImage? Decode(byte[] bytes, string path)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaximumPixels)
        {
            return null;
        }

        var target = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(target);
        if (codec.GetPixels(target, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        var pixels = bitmap.Bytes;
        return new CapturedImage(
            pixels,
            target.Width,
            target.Height,
            target.RowBytes,
            PixelFormat.Bgra8888,
            File.GetLastWriteTimeUtc(path),
            "game screenshot");
    }
}
