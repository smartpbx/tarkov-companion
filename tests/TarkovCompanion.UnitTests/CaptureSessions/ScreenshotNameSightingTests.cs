using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Platform.Windows.Watching;

namespace TarkovCompanion.UnitTests.CaptureSessions;

/// <summary>
/// The watcher's first signal: the name, on sight, with nothing waited for.
/// </summary>
/// <remarks>
/// The player's coordinates are in the filename and are complete the moment the directory entry
/// exists. The pixels are not, and the two stable probes that prove they are took two to three
/// seconds — which the position spent waiting for a number that was already on disk.
/// </remarks>
public sealed class ScreenshotNameSightingTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public async Task ANameIsReportedOnSightWhileThePixelsAreStillArriving()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    requiredStableProbes: 2)
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var next = enumerator.MoveNextAsync().AsTask();
            var path = Path.Combine(root, "half-written.png");
            await File.WriteAllBytesAsync(path, Png[..20], stopping.Token);

            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(new(path, ScreenshotSightingKind.NameSeen), enumerator.Current);

            // And nothing else, because the file is still half a picture.
            var settled = enumerator.MoveNextAsync().AsTask();
            await Task.Delay(80, stopping.Token);
            Assert.False(settled.IsCompleted);

            await File.WriteAllBytesAsync(path, Png, stopping.Token);
            Assert.True(await settled.WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(new(path, ScreenshotSightingKind.Settled), enumerator.Current);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A file that keeps growing is announced once, not once per probe.
    /// </summary>
    /// <remarks>
    /// Each write restarts the stability count, because the pixels really have changed. The name
    /// has not, and re-announcing it would apply the same position over and over.
    /// </remarks>
    [Fact]
    public async Task AGrowingFileIsAnnouncedByNameExactlyOnce()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var path = Path.Combine(root, "growing.png");
            await File.WriteAllBytesAsync(path, Png[..8], stopping.Token);
            var sightings = new List<ScreenshotSighting>();
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    requiredStableProbes: 2)
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);

            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            sightings.Add(enumerator.Current);
            for (var written = 16; written < Png.Length; written += 16)
            {
                await File.WriteAllBytesAsync(path, Png[..Math.Min(written, Png.Length)], stopping.Token);
                await Task.Delay(40, stopping.Token);
            }

            await File.WriteAllBytesAsync(path, Png, stopping.Token);
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), stopping.Token));
            sightings.Add(enumerator.Current);

            Assert.Equal(
                [new(path, ScreenshotSightingKind.NameSeen), new(path, ScreenshotSightingKind.Settled)],
                sightings);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Yesterday's screenshot is not announced at all, by name or otherwise.
    /// </summary>
    /// <remarks>
    /// The startup grace is what stops an old file announcing a raid that is over. Reporting the
    /// name earlier must not reach around it, which is exactly the kind of thing a faster path
    /// gets wrong.
    /// </remarks>
    [Fact]
    public async Task AScreenshotFromBeforeTheGraceIsNeverAnnounced()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var old = Path.Combine(root, "yesterday.png");
            await File.WriteAllBytesAsync(old, Png, stopping.Token);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-1));
            await using var enumerator = new WindowsScreenshotWatcher(pollInterval: TimeSpan.FromMilliseconds(10))
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var next = enumerator.MoveNextAsync().AsTask();

            await Task.Delay(120, stopping.Token);
            Assert.False(next.IsCompleted);

            var fresh = Path.Combine(root, "now.png");
            await File.WriteAllBytesAsync(fresh, Png, stopping.Token);
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(new(fresh, ScreenshotSightingKind.NameSeen), enumerator.Current);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tarkov-name-sighting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
