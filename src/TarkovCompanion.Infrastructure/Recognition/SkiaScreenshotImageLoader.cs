using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>Hard ceilings for decoding one screenshot from disk.</summary>
public sealed record ScreenshotImageLoaderOptions
{
    /// <summary>
    /// The largest encoded file read, checked before any buffer for it exists.
    /// </summary>
    /// <remarks>
    /// A 3840x2160 PNG of busy game scenery is tens of megabytes; this leaves room for an
    /// ultrawide frame while refusing a file that is not a screenshot at all.
    /// </remarks>
    public long MaximumEncodedBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>The largest picture worth decoding, in pixels.</summary>
    /// <remarks>
    /// A 4K screenshot is about eight million pixels and thirty-three megabytes once decoded,
    /// which is fine. This guards against something absurd rather than against ordinary
    /// screenshots, and refusing is better than exhausting memory on a machine that is also
    /// running the game.
    /// </remarks>
    public long MaximumPixels { get; init; } = 40_000_000;
}

/// <summary>
/// Reads a screenshot the game wrote, so the player's own key can drive a scan.
/// </summary>
/// <remarks>
/// Decoding to the same pixel layout the screen capture produces means the recogniser cannot
/// tell the two apart, and nothing downstream needs to know which one it is looking at.
///
/// A file the game is still writing is a real possibility, since the watcher can see it within
/// a second of its creation. A partial read comes back as a decode failure rather than as
/// garbage, and the caller treats that as nothing to scan. That used to be only half true:
/// Skia's incomplete-input result was accepted, so a truncated PNG became a frame whose missing
/// rows were blank pixels that OCR then read as a real, emptier screen. Only a complete decode is
/// returned now.
///
/// The file used to be read whole before anything looked at its size, so a large enough file
/// was allocated in full and then refused. Its length is checked first, and the read never
/// takes more than that length even if the file grows while it is being read.
/// </remarks>
public sealed class SkiaScreenshotImageLoader : IScreenshotImageLoader
{
    private readonly ScreenshotImageLoaderOptions _options;

    public SkiaScreenshotImageLoader(ScreenshotImageLoaderOptions? options = null)
    {
        options ??= new ScreenshotImageLoaderOptions();
        if (options.MaximumEncodedBytes <= 0 ||
            options.MaximumEncodedBytes > Array.MaxLength ||
            options.MaximumPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Screenshot decode limits must be positive and bounded.");
        }

        _options = options;
    }

    public async Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var bytes = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Decode(bytes, path);
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or OutOfMemoryException)
        {
            return null;
        }
    }

    private async Task<byte[]?> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        if (length <= 0 || length > _options.MaximumEncodedBytes)
        {
            return null;
        }

        var bytes = new byte[length];
        // Exactly the length that was checked. A file that shrank throws EndOfStreamException, an
        // IOException, and one that grew is decoded from its checked prefix and fails as
        // incomplete input.
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private CapturedImage? Decode(byte[] bytes, string path)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > _options.MaximumPixels)
        {
            return null;
        }

        var target = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        // Decoded straight into the buffer the frame keeps, rather than into a native bitmap
        // that was then copied, so a large frame is held once instead of twice.
        var pixels = new byte[checked((long)target.RowBytes * target.Height)];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        SKCodecResult decoded;
        try
        {
            decoded = codec.GetPixels(target, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        if (decoded != SKCodecResult.Success)
        {
            // IncompleteInput included: a partly written file is not a picture of the screen.
            Array.Clear(pixels);
            return null;
        }

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
