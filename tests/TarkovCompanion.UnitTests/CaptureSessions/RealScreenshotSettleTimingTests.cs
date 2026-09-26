using System.Diagnostics;
using System.Globalization;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Platform.Windows.Watching;
using Xunit.Abstractions;

namespace TarkovCompanion.UnitTests.CaptureSessions;

/// <summary>
/// #712 0-12: how long a real screenshot takes from being written to being settled and decoded,
/// with the settle probe as it was (the next ordinary poll) and as it is (a tenth of a second).
/// </summary>
/// <remarks>
/// Reports only, and skips unless <c>TARKOV_SETTLE_FRAMES</c> names a folder of real screenshots,
/// which are the player's own and are never committed. Each frame is written into a watched folder
/// in 256 KB pieces, as an encoder writes, so the watcher also meets it half written. Classifying
/// and recognising are not timed here: they need Windows OCR, and this change does not touch them.
/// </remarks>
public sealed class RealScreenshotSettleTimingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReportsSettleAndDecodeOnRealFrames()
    {
        var folder = Environment.GetEnvironmentVariable("TARKOV_SETTLE_FRAMES");
        if (folder is null || !Directory.Exists(folder))
        {
            output.WriteLine("[settle-timing] skipped: TARKOV_SETTLE_FRAMES is not a folder.");
            return;
        }

        var count = int.TryParse(Environment.GetEnvironmentVariable("TARKOV_SETTLE_COUNT"), out var wanted) ? wanted : 12;
        var frames = Directory.EnumerateFiles(folder, "*.png").Order(StringComparer.Ordinal).ToArray();
        // Half raid frames (a position in the name), half menu frames (stash, flea, trader, TASKS).
        var raid = frames.Where(path => Path.GetFileName(path).Count(character => character == ',') >= 2).Take(count / 2);
        var menu = frames.Where(path => Path.GetFileName(path).Count(character => character == ',') < 2).Take(count - (count / 2));
        var chosen = raid.Concat(menu).ToArray();
        output.WriteLine($"[settle-timing] {chosen.Length} frames, {chosen.Sum(path => new FileInfo(path).Length) / 1024 / 1024} MB");

        foreach (var (pace, poll) in new[] { ("idle", TimeSpan.FromSeconds(1)), ("attentive", TimeSpan.FromMilliseconds(250)) })
        {
            foreach (var (label, probe) in new[] { ("before", poll), ("after", (TimeSpan?)null) })
            {
                var settled = new List<double>();
                var named = new List<double>();
                foreach (var frame in chosen)
                {
                    var (seen, done) = await MeasureAsync(frame, poll, probe);
                    named.Add(seen);
                    settled.Add(done);
                }

                output.WriteLine(
                    $"[settle-timing] {pace} {label}: name seen p50 {P(named, 0.5)} p95 {P(named, 0.95)} ms; " +
                    $"name seen to settled p50 {P(settled, 0.5)} p95 {P(settled, 0.95)} ms");
            }
        }

        var loader = new SkiaScreenshotImageLoader();
        var decodes = new List<double>();
        foreach (var frame in chosen)
        {
            var stopwatch = Stopwatch.StartNew();
            var image = await loader.LoadAsync(frame, CancellationToken.None);
            decodes.Add(stopwatch.Elapsed.TotalMilliseconds);
            Assert.NotNull(image);
        }

        output.WriteLine($"[settle-timing] read+decode p50 {P(decodes, 0.5)} p95 {P(decodes, 0.95)} ms");
    }

    private static async Task<(double NameSeen, double Settled)> MeasureAsync(string frame, TimeSpan poll, TimeSpan? probe)
    {
        var root = Path.Combine(Path.GetTempPath(), "tc-settle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var watcher = new WindowsScreenshotWatcher(pollInterval: poll, settleProbeInterval: probe);
            await using var sightings = watcher.WatchAsync(root, stopping.Token).GetAsyncEnumerator(stopping.Token);
            var first = sightings.MoveNextAsync().AsTask();
            await Task.Delay(poll + TimeSpan.FromMilliseconds(50), stopping.Token);
            // A random phase against the poll, as a player's key press has.
            await Task.Delay(Random.Shared.Next((int)poll.TotalMilliseconds), stopping.Token);
            var bytes = await File.ReadAllBytesAsync(frame, stopping.Token);
            var written = Stopwatch.StartNew();
            await using (var stream = new FileStream(Path.Combine(root, Path.GetFileName(frame)), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                for (var offset = 0; offset < bytes.Length; offset += 256 * 1024)
                {
                    await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(256 * 1024, bytes.Length - offset)), stopping.Token);
                    await stream.FlushAsync(stopping.Token);
                }
            }

            Assert.True(await first);
            Assert.Equal(ScreenshotSightingKind.NameSeen, sightings.Current.Kind);
            var seen = written.Elapsed.TotalMilliseconds;
            Assert.True(await sightings.MoveNextAsync());
            Assert.Equal(ScreenshotSightingKind.Settled, sightings.Current.Kind);
            return (seen, written.Elapsed.TotalMilliseconds - seen);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string P(List<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        var value = sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];
        return value.ToString("0", CultureInfo.InvariantCulture);
    }
}
