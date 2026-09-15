using SkiaSharp;
using System.Security.Cryptography;
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
public sealed class SkiaScreenshotImageLoader(
    TimeProvider? timeProvider = null,
    int maximumAttempts = 4,
    TimeSpan? retryDelay = null) : IScreenshotImageLoader
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
    private const long MaximumEncodedBytes = 256L * 1024 * 1024;
    private const FileAttributes CloudPlaceholderAttributes =
        FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly int _maximumAttempts = maximumAttempts is >= 1 and <= 16
        ? maximumAttempts
        : throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
    private readonly TimeSpan _retryDelay = retryDelay is { } delay
        ? delay >= TimeSpan.Zero && delay <= TimeSpan.FromSeconds(5)
            ? delay
            : throw new ArgumentOutOfRangeException(nameof(retryDelay))
        : TimeSpan.FromMilliseconds(100);

    public async Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
        {
            byte[]? bytes = null;
            try
            {
                var read = await ReadStableAsync(path, cancellationToken).ConfigureAwait(false);
                if (read is not null)
                {
                    bytes = read.Value.Bytes;
                    var decoded = Decode(bytes, read.Value.WrittenUtc);
                    if (decoded is not null)
                    {
                        return decoded;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or OutOfMemoryException)
            {
            }
            finally
            {
                if (bytes is not null)
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            if (attempt < _maximumAttempts && _retryDelay > TimeSpan.Zero)
            {
                await Task.Delay(_retryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static async Task<(byte[] Bytes, DateTimeOffset WrittenUtc)?> ReadStableAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var before = new FileInfo(path);
        before.Refresh();
        if (!before.Exists || before.Length is <= 0 or > MaximumEncodedBytes
            || (before.Attributes & CloudPlaceholderAttributes) != 0)
        {
            return null;
        }

        var expectedLength = before.Length;
        var expectedWriteUtc = before.LastWriteTimeUtc;
        if (expectedLength > int.MaxValue)
        {
            return null;
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)expectedLength);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expectedLength)
            {
                CryptographicOperations.ZeroMemory(bytes);
                return null;
            }

            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var after = new FileInfo(path);
            after.Refresh();
            if (!after.Exists || after.Length != expectedLength || after.LastWriteTimeUtc != expectedWriteUtc)
            {
                CryptographicOperations.ZeroMemory(bytes);
                return null;
            }

            return (bytes, new DateTimeOffset(expectedWriteUtc, TimeSpan.Zero));
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static CapturedImage? Decode(byte[] bytes, DateTimeOffset writtenUtc)
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
        // IncompleteInput used to be accepted and sent a truncated or black frame into OCR.
        // It is a retry signal: only a decoder-confirmed complete image crosses this boundary.
        if (codec.GetPixels(target, bitmap.GetPixels()) != SKCodecResult.Success)
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
            writtenUtc,
            "game screenshot");
    }
}
