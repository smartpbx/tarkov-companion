using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Platform.Windows.Watching;

namespace TarkovCompanion.UnitTests.CaptureSessions;

public sealed class StableScreenshotIntakeTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task WatcherWaitsForACompleteStableSharedRead()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    requiredStableProbes: 2)
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var next = enumerator.MoveNextAsync().AsTask();
            var path = Path.Combine(root, "chunked.png");
            await File.WriteAllBytesAsync(path, Png[..20], stopping.Token);
            await Task.Delay(60, stopping.Token);
            Assert.False(next.IsCompleted);

            await File.WriteAllBytesAsync(path, Png, stopping.Token);
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(path, enumerator.Current);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WatcherReportsTheSamePathAgainOnlyAfterItsContentChangesAndSettles()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var path = Path.Combine(root, "replace.png");
            await File.WriteAllBytesAsync(path, Png, stopping.Token);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    requiredStableProbes: 2)
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);

            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(path, enumerator.Current);
            var changed = Png.Concat(new byte[] { 0 }).ToArray();
            // A PNG with bytes after IEND is deliberately not considered complete. Replace it
            // with another envelope-sized file so the changed fingerprint must settle again.
            await File.WriteAllBytesAsync(path, changed, stopping.Token);
            await Task.Delay(40, stopping.Token);
            await File.WriteAllBytesAsync(path, Png, stopping.Token);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));

            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(path, enumerator.Current);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisappearingScreenshotRootEndsTheWatcherExplicitly()
    {
        var root = NewDirectory();
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var enumerator = new WindowsScreenshotWatcher(pollInterval: TimeSpan.FromMilliseconds(10))
            .WatchAsync(root, stopping.Token)
            .GetAsyncEnumerator(stopping.Token);
        var next = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(30, stopping.Token);
        Directory.Delete(root);

        await Assert.ThrowsAsync<ScreenshotSourceUnavailableException>(async () =>
        {
            await next.WaitAsync(TimeSpan.FromSeconds(2), stopping.Token);
        });
    }

    [Fact]
    public async Task LoaderRetriesIncompleteInputAndRequiresFullDecoderSuccess()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var path = Path.Combine(root, "decode.png");
            await File.WriteAllBytesAsync(path, Png[..24], stopping.Token);
            var loader = new SkiaScreenshotImageLoader(
                maximumAttempts: 8,
                retryDelay: TimeSpan.FromMilliseconds(15));
            var loading = loader.LoadAsync(path, stopping.Token);
            await Task.Delay(40, stopping.Token);
            await File.WriteAllBytesAsync(path, Png, stopping.Token);

            var image = await loading;
            Assert.NotNull(image);
            Assert.Equal(1, image.Width);
            Assert.Equal(1, image.Height);

            await File.WriteAllBytesAsync(path, Png[..24], stopping.Token);
            var rejected = await new SkiaScreenshotImageLoader(maximumAttempts: 1).LoadAsync(path, stopping.Token);
            Assert.Null(rejected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"capture-intake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
