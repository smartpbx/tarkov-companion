using SkiaSharp;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.UnitTests.Ocr;

/// <summary>
/// A screenshot on disk is bounded before it is read, and only a complete decode becomes a frame.
/// </summary>
/// <remarks>
/// These live beside the probe tests rather than with the recognition suite because this project
/// carries the Linux Skia native assets; the decoder has to actually run to prove anything.
/// </remarks>
public sealed class ScreenshotImageLoaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "tarkov-loader-" + Guid.NewGuid().ToString("N"));

    public ScreenshotImageLoaderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ACompletePngDecodesToBgraPixels()
    {
        var path = Write("complete.png", EncodeNoisyPng(96, 64));

        var image = await new SkiaScreenshotImageLoader().LoadAsync(path, CancellationToken.None);

        Assert.NotNull(image);
        Assert.Equal(96, image.Width);
        Assert.Equal(64, image.Height);
        Assert.Equal(PixelFormat.Bgra8888, image.Format);
        Assert.Equal(96 * 4 * 64, image.Pixels.Length);
    }

    [Fact]
    public async Task ATruncatedPngIsRejectedRatherThanDecodedWithBlankRows()
    {
        var encoded = EncodeNoisyPng(256, 256);
        var path = Write("truncated.png", encoded[..(encoded.Length / 2)]);

        var image = await new SkiaScreenshotImageLoader().LoadAsync(path, CancellationToken.None);

        Assert.Null(image);
    }

    [Fact]
    public async Task AnOversizedFileIsRefusedBeforeAnyBufferForItExists()
    {
        var path = Path.Combine(_directory, "oversized.png");
        await using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            stream.SetLength(4L * 1024 * 1024);
        }

        var loader = new SkiaScreenshotImageLoader(new ScreenshotImageLoaderOptions
        {
            MaximumEncodedBytes = 1024 * 1024,
        });

        // Measured on this thread before the first await: reading the file whole would allocate
        // its four megabytes here.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var pending = loader.LoadAsync(path, CancellationToken.None);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(await pending);
        Assert.True(allocated < 512 * 1024, $"allocated {allocated} bytes before refusing the file");
    }

    [Fact]
    public async Task TheEncodedCeilingIsInclusive()
    {
        var encoded = EncodeNoisyPng(96, 64);
        var path = Write("exact.png", encoded);

        var below = new SkiaScreenshotImageLoader(new ScreenshotImageLoaderOptions { MaximumEncodedBytes = encoded.Length - 1 });
        var exact = new SkiaScreenshotImageLoader(new ScreenshotImageLoaderOptions { MaximumEncodedBytes = encoded.Length });

        Assert.Null(await below.LoadAsync(path, CancellationToken.None));
        Assert.NotNull(await exact.LoadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task APictureWithTooManyPixelsIsRefusedBeforeDecoding()
    {
        var path = Write("dimensions.png", EncodeNoisyPng(96, 64));
        var loader = new SkiaScreenshotImageLoader(new ScreenshotImageLoaderOptions
        {
            MaximumPixels = (96 * 64) - 1,
        });

        Assert.Null(await loader.LoadAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task APreCancelledLoadThrowsBeforeTheFileIsEvenOpened()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // A missing file opened first would come back as null, not as cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SkiaScreenshotImageLoader()
            .LoadAsync(Path.Combine(_directory, "missing.png"), cancellation.Token));
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    public async Task TheChunkedDecodeMatchesSkiasOneCallDecodeExactly(SKEncodedImageFormat format)
    {
        // Several 64-KiB input chunks of PNG, and several 65,536-pixel scanline chunks of JPEG.
        var encoded = EncodeNoise(256, 1_024, format);
        var path = Write("chunked." + format.ToString().ToLowerInvariant(), encoded);
        var chunks = 0;
        var loader = new SkiaScreenshotImageLoader { RowsDecoded = _ => chunks++ };

        var image = await loader.LoadAsync(path, CancellationToken.None);

        Assert.NotNull(image);
        Assert.True(chunks > 1, $"decoded in {chunks} chunk(s)");
        using var codec = SKCodec.Create(SKData.CreateCopy(encoded));
        using var reference = new SKBitmap(new SKImageInfo(256, 1_024, SKColorType.Bgra8888, SKAlphaType.Premul));
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(reference.Info, reference.GetPixels()));
        Assert.True(reference.GetPixelSpan().SequenceEqual(image.Pixels.Span), "chunked pixels differ from the one-call decode");
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png)]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    public async Task CancellationDuringTheDecodeStopsItAtTheNextChunk(SKEncodedImageFormat format)
    {
        var path = Write("tall." + format.ToString().ToLowerInvariant(), EncodeNoise(64, 8_192, format));
        using var cancellation = new CancellationTokenSource();
        var loader = new SkiaScreenshotImageLoader();
        var reported = new List<int>();
        loader.RowsDecoded = rows =>
        {
            lock (reported)
            {
                reported.Add(rows);
            }

            cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loader.LoadAsync(path, cancellation.Token));

        Assert.True(SpinWait.SpinUntil(() => loader.SettledDecodes == 1, TimeSpan.FromSeconds(10)));
        lock (reported)
        {
            // One chunk ran; the check before the next one saw the cancellation.
            var rows = Assert.Single(reported);
            Assert.InRange(rows, 1, 8_191);
        }
    }

    [Fact]
    public async Task ADeadlineMidDecodeReturnsAtOnceWhileTheDecodeKeepsItsBuffersAndTheGateUntilItSettles()
    {
        var tall = Write("stalled.png", EncodeNoisyPng(64, 4_096));
        var small = Write("small.png", EncodeNoisyPng(96, 64));
        using var stalled = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var loader = new SkiaScreenshotImageLoader(new ScreenshotImageLoaderOptions
        {
            Timeout = TimeSpan.FromMilliseconds(500),
        });
        var chunks = 0;
        loader.RowsDecoded = _ =>
        {
            // Where native decoding runs. Held here, the decode cannot be interrupted, exactly
            // like a codec call that ignores the deadline.
            if (Interlocked.Increment(ref chunks) == 1)
            {
                stalled.Set();
                release.Wait();
            }
        };

        try
        {
            var abandoned = await loader.LoadAsync(tall, CancellationToken.None);

            Assert.Null(abandoned);
            Assert.True(stalled.IsSet);
            // Returned to the caller, but the codec, its data and the pinned pixels are still held.
            Assert.Equal(0, loader.SettledDecodes);

            // A second screenshot waits for the gate instead of allocating a frame beside it.
            var blocked = await loader.LoadAsync(small, CancellationToken.None);

            Assert.Null(blocked);
            Assert.Equal(1, Volatile.Read(ref chunks));
            Assert.Equal(0, loader.SettledDecodes);
        }
        finally
        {
            release.Set();
        }

        Assert.True(SpinWait.SpinUntil(() => loader.SettledDecodes == 1, TimeSpan.FromSeconds(10)));
        loader.RowsDecoded = null;
        CapturedImage? resumed = null;
        for (var attempt = 0; attempt < 20 && resumed is null; attempt++)
        {
            // The gate opens on the settle continuation, just after the decode's own count.
            resumed = await loader.LoadAsync(small, CancellationToken.None);
        }

        Assert.NotNull(resumed);
        Assert.Equal(96, resumed.Width);
        // The abandoned decode observed the deadline at its next chunk rather than running on.
        Assert.Equal(1, Volatile.Read(ref chunks));
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] EncodeNoisyPng(int width, int height) => EncodeNoise(width, height, SKEncodedImageFormat.Png);

    /// <summary>Noise, so the compressed image data is long enough that half of it is really incomplete.</summary>
    private static byte[] EncodeNoise(int width, int height, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var random = new Random(299);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, format == SKEncodedImageFormat.Jpeg ? 90 : 100);
        return data.ToArray();
    }
}
