using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.Raids;

/// <summary>
/// [#893] Each in-raid screenshot is decoded once for the capture session and the always-on scan.
/// </summary>
public sealed class SharedScreenshotDecodeTests
{
    private const string ShotPath = "C:/shots/frame.png";

    [Fact]
    public async Task TwoReadersShareOneDecode()
    {
        var inner = new CountingLoader();
        var shared = new SharedScreenshotDecode(inner, ShotPath, expectedReaders: 2);

        var first = await shared.LoadAsync(ShotPath, CancellationToken.None);
        var second = await shared.LoadAsync(ShotPath, CancellationToken.None);

        Assert.Equal(1, inner.Loads);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first.Pixels.Span.SequenceEqual(second.Pixels.Span));
    }

    /// <summary>
    /// The capture session zeroes its pixels when it is done; the other reader's must survive.
    /// </summary>
    [Fact]
    public async Task EachReaderOwnsItsOwnBuffer()
    {
        var shared = new SharedScreenshotDecode(new CountingLoader(), ShotPath, expectedReaders: 2);

        var first = await shared.LoadAsync(ShotPath, CancellationToken.None);
        var second = await shared.LoadAsync(ShotPath, CancellationToken.None);
        System.Runtime.InteropServices.MemoryMarshal.AsMemory(first!.Pixels).Span.Clear();

        Assert.Contains(second!.Pixels.ToArray(), value => value != 0);
    }

    [Fact]
    public async Task AFailedDecodeIsTriedAgainByTheNextReader()
    {
        var inner = new CountingLoader { FailFirst = true };
        var shared = new SharedScreenshotDecode(inner, ShotPath, expectedReaders: 2);

        Assert.Null(await shared.LoadAsync(ShotPath, CancellationToken.None));
        Assert.NotNull(await shared.LoadAsync(ShotPath, CancellationToken.None));
        Assert.Equal(2, inner.Loads);
    }

    [Fact]
    public async Task AReaderBeyondTheExpectedOnesDecodesForItself()
    {
        var inner = new CountingLoader();
        var shared = new SharedScreenshotDecode(inner, ShotPath, expectedReaders: 1);
        _ = await shared.LoadAsync(ShotPath, CancellationToken.None);
        _ = await shared.LoadAsync(ShotPath, CancellationToken.None);

        Assert.Equal(2, inner.Loads);
    }

    [Fact]
    public async Task AReleasedReaderLetsTheOtherTakeTheDecodedBuffer()
    {
        var inner = new CountingLoader();
        var shared = new SharedScreenshotDecode(inner, ShotPath, expectedReaders: 2);
        shared.Release();

        var image = await shared.LoadAsync(ShotPath, CancellationToken.None);

        Assert.Same(inner.Last, image);
    }

    private sealed class CountingLoader : IScreenshotImageLoader
    {
        public int Loads { get; private set; }

        public bool FailFirst { get; init; }

        public CapturedImage? Last { get; private set; }

        public Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken)
        {
            Loads++;
            if (FailFirst && Loads == 1)
            {
                return Task.FromResult<CapturedImage?>(null);
            }

            var pixels = new byte[4 * 2 * 2];
            for (var index = 0; index < pixels.Length; index++)
            {
                pixels[index] = (byte)(index + 1);
            }

            Last = new CapturedImage(pixels, 2, 2, 8, PixelFormat.Bgra8888, DateTimeOffset.UnixEpoch, path);
            return Task.FromResult<CapturedImage?>(Last);
        }
    }
}
