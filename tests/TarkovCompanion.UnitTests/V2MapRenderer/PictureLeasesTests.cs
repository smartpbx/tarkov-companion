using SkiaSharp;
using TarkovCompanion.App.ViewModels.V2.Raid;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// A picture is freed when its owner has retired it and nobody is reading it, whichever comes second.
/// </summary>
public sealed class PictureLeasesTests
{
    [Fact]
    public void A_picture_nobody_is_reading_is_released_the_moment_it_is_retired()
    {
        var released = new List<object>();
        var leases = new PictureLeases<object>(released.Add);
        var picture = new object();
        leases.Track(picture);

        leases.Retire(picture);

        Assert.Same(picture, Assert.Single(released));
        Assert.Equal(0, leases.Count);
    }

    [Fact]
    public void A_picture_being_read_is_released_when_the_last_reader_has_finished_and_only_once()
    {
        var released = new List<object>();
        var leases = new PictureLeases<object>(released.Add);
        var picture = new object();
        leases.Track(picture);
        var first = leases.TryRead(picture);
        var second = leases.TryRead(picture);
        Assert.NotNull(first);
        Assert.NotNull(second);

        leases.Retire(picture);
        leases.Retire(picture);
        Assert.Empty(released);

        first.Dispose();
        first.Dispose();
        Assert.Empty(released);

        second.Dispose();
        Assert.Same(picture, Assert.Single(released));
        Assert.Equal(0, leases.Count);
    }

    [Fact]
    public void A_retired_picture_and_one_never_tracked_cannot_be_read()
    {
        var leases = new PictureLeases<object>(_ => { });
        var retired = new object();
        leases.Track(retired);
        using var reading = leases.TryRead(retired);
        leases.Retire(retired);

        Assert.Null(leases.TryRead(retired));
        Assert.Null(leases.TryRead(new object()));
    }

    [Fact]
    public void A_reader_that_finishes_before_the_picture_is_retired_releases_nothing()
    {
        var released = new List<object>();
        var leases = new PictureLeases<object>(released.Add);
        var picture = new object();
        leases.Track(picture);

        leases.TryRead(picture)!.Dispose();

        Assert.Empty(released);
        Assert.NotNull(leases.TryRead(picture));
    }

    /// <remarks>
    /// The fault itself, with Skia's own encoder and no interface: a worker PNG-encodes a picture
    /// while its owner replaces it. Freed under the encoder, this does not fail an assertion; it
    /// takes the test host down with an access violation, which is what it did to the application.
    /// </remarks>
    [Fact]
    public async Task A_picture_replaced_while_a_worker_is_encoding_it_is_freed_after_the_encode()
    {
        for (var round = 0; round < 6; round++)
        {
            var picture = Noise(2048, 2048, round);
            var freed = 0;
            var leases = new PictureLeases<SKBitmap>(bitmap =>
            {
                Interlocked.Increment(ref freed);
                bitmap.Dispose();
            });
            leases.Track(picture);
            using var encoding = new ManualResetEventSlim();
            var encoded = Task.Run(() =>
            {
                using var lease = leases.TryRead(picture);
                Assert.NotNull(lease);
                encoding.Set();
                using var data = picture.Encode(SKEncodedImageFormat.Png, 100);
                Assert.Equal(0, Volatile.Read(ref freed));
                return data.Size;
            });

            Assert.True(encoding.Wait(TimeSpan.FromSeconds(30)));
            leases.Retire(picture);

            Assert.True(await encoded.WaitAsync(TimeSpan.FromSeconds(60)) > 0);
            Assert.Equal(1, Volatile.Read(ref freed));
            Assert.Equal(0, leases.Count);
        }
    }

    private static SKBitmap Noise(int width, int height, int seed)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var random = new Random(seed);
        random.NextBytes(bitmap.GetPixelSpan());
        return bitmap;
    }
}
