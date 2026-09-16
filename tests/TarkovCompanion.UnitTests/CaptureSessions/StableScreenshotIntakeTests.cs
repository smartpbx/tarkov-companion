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
    public async Task NewerCompletedScreenshotDoesNotDropAnOlderSettlingFile()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var older = Path.Combine(root, "older.png");
            var newer = Path.Combine(root, "newer.png");
            var olderWritten = DateTime.UtcNow.AddSeconds(-2);
            await File.WriteAllBytesAsync(older, Png[..20], stopping.Token);
            File.SetLastWriteTimeUtc(older, olderWritten);
            await File.WriteAllBytesAsync(newer, Png, stopping.Token);
            File.SetLastWriteTimeUtc(newer, olderWritten.AddSeconds(1));
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    requiredStableProbes: 2)
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);

            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(newer, enumerator.Current);

            await File.WriteAllBytesAsync(older, Png, stopping.Token);
            File.SetLastWriteTimeUtc(older, olderWritten);
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(older, enumerator.Current);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectorySnapshotKeepsOnlyTheNewestBoundedPopulation()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var paths = new List<string>();
            var written = DateTime.UtcNow.AddSeconds(-30);
            for (var index = 0; index < 17; index++)
            {
                var path = Path.Combine(root, $"shot-{index:D2}.png");
                await File.WriteAllBytesAsync(path, Png, stopping.Token);
                File.SetLastWriteTimeUtc(path, written.AddSeconds(index));
                paths.Add(path);
            }

            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    requiredStableProbes: 2,
                    maximumTrackedFiles: 16)
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var delivered = new List<string>();
            for (var index = 0; index < 16; index++)
            {
                Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(
                    TimeSpan.FromSeconds(2),
                    stopping.Token));
                delivered.Add(enumerator.Current);
            }

            Assert.DoesNotContain(paths[0], delivered);
            Assert.Equal(paths.Skip(1), delivered);
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
    public async Task DisappearingScreenshotRootSignalsUnavailableAndARecreatedRootCanRecover()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var oldPath = Path.Combine(root, "old.png");
            await File.WriteAllBytesAsync(oldPath, Png, stopping.Token);
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddSeconds(-10));
            var oldWrittenUtc = File.GetLastWriteTimeUtc(oldPath);
            var watcher = new WindowsScreenshotWatcher(pollInterval: TimeSpan.FromMilliseconds(10));
            await using var enumerator = watcher.WatchAsync(root, stopping.Token).GetAsyncEnumerator(stopping.Token);
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));

            Directory.Delete(root, recursive: true);
            await Assert.ThrowsAsync<CaptureSourceUnavailableException>(async () =>
            {
                _ = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token);
            });

            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(oldPath, Png, stopping.Token);
            File.SetLastWriteTimeUtc(oldPath, oldWrittenUtc);
            var newPath = Path.Combine(root, "new.png");
            await File.WriteAllBytesAsync(newPath, Png, stopping.Token);
            File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow.AddSeconds(1));

            await using var recovered = watcher.WatchAsync(root, stopping.Token).GetAsyncEnumerator(stopping.Token);
            Assert.True(await recovered.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(newPath, recovered.Current);
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
            var handoff = new CountingHandoff();
            await using var coordinator = new CaptureSessionCoordinator(
                new InlineCaptureWorkScheduler(),
                pipeline,
                handoff,
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
            Assert.Equal(0, handoff.Calls);
            Assert.Equal(0, coordinator.Snapshot.PixelsInUse);
            var artifact = Assert.Single(Assert.Single(coordinator.Snapshot.Sessions).Artifacts);
            Assert.Equal(CaptureArtifactDisposition.NoChange, artifact.Disposition);
            Assert.Equal("decode_incomplete_or_unavailable", artifact.DiagnosticCode);
            Assert.Equal(4, artifact.DecodeAttempts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EncodedSizeLimitPreventsWatcherDelivery()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var path = Path.Combine(root, "oversize.png");
            await File.WriteAllBytesAsync(path, Png, stopping.Token);
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    maximumEncodedBytes: 32)
                .WatchAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var next = enumerator.MoveNextAsync().AsTask();

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
    public async Task FileSourceRetriesBoundedlyWhenAStableReadStillCannotDecode()
    {
        var loader = new NullLoader();
        await using var coordinator = new CaptureSessionCoordinator(
            new InlineCaptureWorkScheduler(),
            new CountingPipeline(),
            new AcceptingHandoff(),
            Origin,
            options: new(maximumDecodeAttempts: 4, decodeRetryDelay: TimeSpan.FromMilliseconds(1)));

        await coordinator.EnqueueAsync(
            new(
                CaptureDeliveryKind.WatchedFile,
                new ScreenshotFileCaptureSource("fixture.png", loader),
                CaptureContextMetadata.Empty,
                DateTimeOffset.UtcNow,
                CaptureCorrelationId.New()),
            CancellationToken.None);
        await UntilAsync(() => coordinator.Snapshot.Sessions.Any(item => item.IsTerminal));

        Assert.Equal(4, loader.Calls);
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

    private sealed class NullLoader : TarkovCompanion.Core.Abstractions.IScreenshotImageLoader
    {
        public int Calls { get; private set; }

        public Task<CapturedImage?> LoadAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<CapturedImage?>(null);
        }
    }

    private sealed class AcceptingHandoff : ICaptureResultHandoff
    {
        public ValueTask<CaptureHandoffResult> AcceptAsync(
            CaptureHandoffRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(CaptureHandoffResult.Accepted);
    }

    private sealed class CountingHandoff : ICaptureResultHandoff
    {
        public int Calls { get; private set; }

        public ValueTask<CaptureHandoffResult> AcceptAsync(
            CaptureHandoffRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(CaptureHandoffResult.Accepted);
        }
    }

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"capture-intake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }
}
