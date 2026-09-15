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

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Noise, so the compressed image data is long enough that half of it is really incomplete.</summary>
    private static byte[] EncodeNoisyPng(int width, int height)
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
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
