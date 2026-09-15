using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
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
            var changed = Png.ToArray();
            changed[24] ^= 0x01;
            await File.WriteAllBytesAsync(path, changed, stopping.Token);
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
    public async Task DisappearingScreenshotRootWaitsForRecreationWithoutReplayingOldContent()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var oldPath = Path.Combine(root, "old.png");
            await File.WriteAllBytesAsync(oldPath, Png, stopping.Token);
            await using var enumerator = new WindowsScreenshotWatcher(pollInterval: TimeSpan.FromMilliseconds(10))
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));

            Directory.Delete(root, recursive: true);
            var next = enumerator.MoveNextAsync().AsTask();
            await Task.Delay(40, stopping.Token);
            Assert.False(next.IsCompleted);
            Directory.CreateDirectory(root);
            var newPath = Path.Combine(root, "new.png");
            await File.WriteAllBytesAsync(newPath, Png, stopping.Token);

            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(newPath, enumerator.Current);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AttributeOnlyChangesDoNotRedeliverAnOldScreenshot()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var path = Path.Combine(root, "attributes.png");
            await File.WriteAllBytesAsync(path, Png, stopping.Token);
            await using var enumerator = new WindowsScreenshotWatcher(pollInterval: TimeSpan.FromMilliseconds(10))
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));

            var next = enumerator.MoveNextAsync().AsTask();
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Archive);
            await Task.Delay(80, stopping.Token);
            Assert.False(next.IsCompleted);
            stopping.Cancel();
            Assert.False(await next);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnvelopeWithTrailingBytesStillYieldsForImmediateFilenameEvidence()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var path = Path.Combine(root, "trailing.png");
            await File.WriteAllBytesAsync(path, [.. Png, 1, 2, 3], stopping.Token);
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    requiredStableProbes: 2)
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);

            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(path, enumerator.Current);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TruncatedRealPngNeverReachesCaptureAnalysis()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var path = Path.Combine(root, "decode.png");
            await File.WriteAllBytesAsync(path, Png[..24], stopping.Token);
            var pipeline = new CountingPipeline();
            await using var coordinator = new CaptureSessionCoordinator(
                new InlineCaptureWorkScheduler(),
                pipeline,
                Origin,
                options: new(decodeRetryDelay: TimeSpan.FromMilliseconds(1)));
            var receipt = await coordinator.EnqueueAsync(
                new(
                    CaptureDeliveryKind.WatchedFile,
                    new ScreenshotFileCaptureSource(path, new SkiaScreenshotImageLoader()),
                    CaptureContextMetadata.Empty,
                    DateTimeOffset.UtcNow,
                    CaptureCorrelationId.New()),
                stopping.Token);
            Assert.Equal(CaptureQueueDisposition.Accepted, receipt.Disposition);

            await UntilAsync(() => coordinator.Snapshot.Sessions.Any(item => item.IsTerminal));
            Assert.Equal(0, pipeline.Calls);
            Assert.Equal(0, coordinator.Snapshot.PixelsInUse);
            Assert.Empty(Assert.Single(coordinator.Snapshot.Sessions).Artifacts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static readonly WorkspaceOrigin Origin = new(
        new(Guid.Parse("06cc9a72-7714-47bf-bb34-e51578710f36")),
        new(Guid.Parse("9dc8d96c-8c4f-4f65-bb20-ad63ec10bdf6")),
        WorkspaceOriginKind.DesktopApplication,
        "stable-capture-tests");

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var poll = 0; poll < 3000 && !condition(); poll++)
        {
            await Task.Delay(1);
        }

        Assert.True(condition());
    }

    private sealed class CountingPipeline : ICaptureSessionPipeline
    {
        public int Calls { get; private set; }

        public Task<CaptureAnalysis> AnalyzeAsync(
            CaptureAnalysisRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new CaptureAnalysis(
                "unexpected",
                RecognizedContext.Item,
                false,
                true,
                null,
                Confidence.Certain));
        }
    }

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"capture-intake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
