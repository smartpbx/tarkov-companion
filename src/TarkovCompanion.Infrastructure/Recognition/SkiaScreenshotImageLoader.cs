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

    /// <summary>Wall-clock budget for reading and decoding one screenshot.</summary>
    /// <remarks>
    /// Started before the file is opened, so it covers the read, both buffers and the decode. A
    /// 4K PNG decodes in well under a second; this is a ceiling for a machine that is also running
    /// the game, not an estimate.
    /// </remarks>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
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
///
/// Nothing bounded the decode itself. The read, the native copy of the file, the pixel buffer
/// and the decode ran under no deadline and ignored cancellation, so the most expensive pixel
/// work before a scan was the one part a caller could not stop. One deadline now starts before
/// the file is opened. The decode runs in bounded chunks with a cancellation check between them
/// wherever the codec allows it, and always on its own thread, which owns the codec and the
/// pinned pixel buffer until native code has returned: a caller that gives up returns at once,
/// while the buffer native code is writing into stays alive until it stops. Decodes are
/// serialized, and an abandoned one keeps the gate until it settles, so a second screenshot
/// cannot allocate another frame beside a decode nobody is waiting for.
/// </remarks>
public sealed class SkiaScreenshotImageLoader : IScreenshotImageLoader
{
    private readonly ScreenshotImageLoaderOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _settledDecodes;

    public SkiaScreenshotImageLoader(ScreenshotImageLoaderOptions? options = null)
    {
        options ??= new ScreenshotImageLoaderOptions();
        if (options.MaximumEncodedBytes <= 0 ||
            options.MaximumEncodedBytes > Array.MaxLength ||
            options.MaximumPixels <= 0 ||
            options.Timeout <= TimeSpan.Zero ||
            options.Timeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Screenshot decode limits must be positive and bounded.");
        }

        _options = options;
    }

    /// <summary>
    /// Called on the decoding thread after each chunk of rows, with the rows decoded so far.
    /// </summary>
    /// <remarks>
    /// It runs exactly where the native decode runs, so a test can hold the decode there and prove
    /// what a caller's deadline does and does not release.
    /// </remarks>
    internal Action<int>? RowsDecoded { get; set; }

    /// <summary>Decodes that have finished and released their buffers, abandoned ones included.</summary>
    internal int SettledDecodes => Volatile.Read(ref _settledDecodes);

    public async Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.Timeout);
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An earlier decode, possibly one its caller abandoned, still holds the gate.
            deadline.Dispose();
            return null;
        }
        catch
        {
            deadline.Dispose();
            throw;
        }

        var settledHere = true;
        try
        {
            var bytes = await ReadBoundedAsync(path, deadline.Token).ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            var writtenUtc = File.GetLastWriteTimeUtc(path);
            var token = deadline.Token;
            var decode = Task.Run(() => Decode(bytes, writtenUtc, token), CancellationToken.None);
            try
            {
                return await decode.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (decode.IsCompleted)
                {
                    Discard(decode);
                }
                else
                {
                    settledHere = false;
                    _ = SettleAsync(decode, deadline);
                }

                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The deadline rather than the caller: nothing to scan, said the same way a file that
            // would not decode says it.
            return null;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            if (settledHere)
            {
                deadline.Dispose();
                _gate.Release();
            }
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

        cancellationToken.ThrowIfCancellationRequested();
        var bytes = new byte[length];
        // Exactly the length that was checked. A file that shrank throws EndOfStreamException, an
        // IOException, and one that grew is decoded from its checked prefix and fails as
        // incomplete input.
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    /// <summary>Decodes on the calling thread, which owns every buffer until this returns.</summary>
    private CapturedImage? Decode(byte[] bytes, DateTime writtenUtc, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Over the managed bytes rather than a native copy of them, and able to hand the codec
            // its input a chunk at a time.
            var input = new MeteredInput(bytes);
            using var codec = SKCodec.Create(input);
            if (codec is null)
            {
                return null;
            }

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > _options.MaximumPixels)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var target = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            // Decoded straight into the buffer the frame keeps, rather than into a native bitmap
            // that was then copied, so a large frame is held once instead of twice.
            var pixels = new byte[checked((long)target.RowBytes * target.Height)];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var complete = false;
            try
            {
                complete = DecodeInto(codec, input, target, handle.AddrOfPinnedObject(), cancellationToken);
            }
            finally
            {
                // Unpinned on the thread that was inside native code, and only after it returned.
                handle.Free();
                if (!complete)
                {
                    // IncompleteInput included: a partly written file is not a picture of the screen.
                    Array.Clear(pixels);
                }
            }

            return complete
                ? new CapturedImage(
                    pixels,
                    target.Width,
                    target.Height,
                    target.RowBytes,
                    PixelFormat.Bgra8888,
                    writtenUtc,
                    "game screenshot")
                : null;
        }
        finally
        {
            Interlocked.Increment(ref _settledDecodes);
        }
    }

    /// <summary>
    /// Decodes in bounded chunks with a cancellation check between them, where the codec allows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured against this build of Skia (SkiaSharp 3.119) rather than assumed: JPEG offers
    /// top-down scanlines and no incremental decode, PNG offers incremental decode and no
    /// scanlines, and WebP offers neither. So JPEG decodes at most one cancellation interval of pixels per call. PNG decodes
    /// incrementally while the input is handed over <see cref="MeteredInput.Chunk"/> bytes at a
    /// time, so each call inflates at most one chunk of the file; the pixels that come out of a
    /// chunk are bounded by the format's own compression ratio rather than by a row count.
    /// </para>
    /// <para>
    /// Anything else decodes in the one native call it offers. The deadline still bounds the
    /// caller's wait, and this thread still owns the buffer until that call returns.
    /// </para>
    /// <para>
    /// Incomplete input is refused on every path: a scanline chunk that decoded fewer rows than
    /// asked for, an incremental decode still wanting input once all of it was handed over, and a
    /// whole-picture call reporting anything but success.
    /// </para>
    /// </remarks>
    private bool DecodeInto(
        SKCodec codec,
        MeteredInput input,
        SKImageInfo target,
        IntPtr destination,
        CancellationToken cancellationToken)
    {
        if (codec.StartScanlineDecode(target) == SKCodecResult.Success &&
            codec.ScanlineOrder == SKCodecScanlineOrder.TopDown)
        {
            var rowsPerChunk = Math.Max(1, PixelCancellationCheck.Interval / target.Width);
            var decoded = 0;
            while (decoded < target.Height)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(rowsPerChunk, target.Height - decoded);
                var rows = codec.GetScanlines(
                    destination + checked((nint)decoded * target.RowBytes),
                    count,
                    target.RowBytes);
                if (rows != count)
                {
                    return false;
                }

                decoded += count;
                RowsDecoded?.Invoke(decoded);
            }

            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (codec.StartIncrementalDecode(target, destination, target.RowBytes) == SKCodecResult.Success)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var consumed = input.Position;
                input.Release();
                var result = codec.IncrementalDecode(out var rows);
                if (result == SKCodecResult.Success)
                {
                    RowsDecoded?.Invoke(target.Height);
                    return true;
                }

                // Still wanting input with all of it read is a truncated file. Wanting input
                // without reading any of the chunk offered is a decoder that will not progress.
                if (result != SKCodecResult.IncompleteInput || input.IsExhausted || input.Position == consumed)
                {
                    return false;
                }

                RowsDecoded?.Invoke(rows);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        input.ReleaseAll();
        var whole = codec.GetPixels(target, destination);
        RowsDecoded?.Invoke(target.Height);
        return whole == SKCodecResult.Success;
    }

    private async Task SettleAsync(Task<CapturedImage?> decode, CancellationTokenSource deadline)
    {
        try
        {
            Clear(await decode.ConfigureAwait(false));
        }
        catch
        {
            // The caller that abandoned this decode already returned. Awaiting here observes a
            // late failure so it cannot surface as an unobserved exception.
        }
        finally
        {
            deadline.Dispose();
            _gate.Release();
        }
    }

    private static void Discard(Task<CapturedImage?> decode)
    {
        if (decode.IsCompletedSuccessfully)
        {
            Clear(decode.Result);
        }
        else
        {
            _ = decode.Exception;
        }
    }

    private static void Clear(CapturedImage? image)
    {
        if (image is not null && MemoryMarshal.TryGetArray(image.Pixels, out var segment) && segment.Array is not null)
        {
            Array.Clear(segment.Array, segment.Offset, segment.Count);
        }
    }

    /// <summary>
    /// The encoded file as a stream whose readable end the decoder moves.
    /// </summary>
    /// <remarks>
    /// Everything is readable while the codec reads the header. An incremental decode then sees the
    /// file end one chunk past what it has consumed; Skia answers that with incomplete input, and
    /// continues from the same place once more is released. Measured to reproduce the one-call
    /// decode byte for byte for PNG, JPEG and WebP. Seekable, because asking a codec whether it
    /// offers scanlines leaves it needing a rewind before any other kind of decode, and a
    /// forward-only stream turned every PNG into a decode failure.
    /// </remarks>
    private sealed class MeteredInput(byte[] bytes) : Stream
    {
        public const int Chunk = 1 << 16;

        private long _position;
        private long _readable = bytes.Length;

        /// <summary>All of the file was released and the codec has read it.</summary>
        public bool IsExhausted => _readable == bytes.Length && _position == bytes.Length;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, bytes.Length);
        }

        /// <summary>Makes one more chunk past what has been consumed readable.</summary>
        public void Release() => _readable = Math.Min(bytes.Length, _position + Chunk);

        public void ReleaseAll() => _readable = bytes.Length;

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, _readable - _position);
            if (count <= 0)
            {
                return 0;
            }

            bytes.AsSpan((int)_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => bytes.Length + offset,
        };

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
