using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions;
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

    /// <summary>A future filesystem stamp must not become an ordering gate.</summary>
    [Fact]
    public async Task FutureDatedHandledFileDoesNotBlockNewFilesWithCorrectMtimes()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var captured = DateTimeOffset.UtcNow;
            var future = Path.Combine(root, ScreenshotName(captured, 0));
            await File.WriteAllBytesAsync(future, Png, stopping.Token);
            File.SetLastWriteTimeUtc(future, captured.UtcDateTime.AddHours(4));
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10))
                .WatchSettledAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);

            Assert.True(await NextAsync(enumerator, stopping.Token));
            Assert.Equal(future, enumerator.Current);

            var first = Path.Combine(root, ScreenshotName(captured, 1));
            await File.WriteAllBytesAsync(first, Png, stopping.Token);
            File.SetLastWriteTimeUtc(first, captured.UtcDateTime);
            Assert.True(await NextAsync(enumerator, stopping.Token));
            Assert.Equal(first, enumerator.Current);

            var second = Path.Combine(root, ScreenshotName(captured, 2));
            await File.WriteAllBytesAsync(second, Png, stopping.Token);
            File.SetLastWriteTimeUtc(second, captured.UtcDateTime.AddSeconds(1));
            Assert.True(await NextAsync(enumerator, stopping.Token));
            Assert.Equal(second, enumerator.Current);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The singleton's handled identities survive a source restart without ordering new files.</summary>
    [Fact]
    public async Task RestartWithExistingFutureDatedStateStillProcessesANewFile()
    {
        var root = NewDirectory();
        try
        {
            var captured = DateTimeOffset.UtcNow;
            var future = Path.Combine(root, ScreenshotName(captured, 0));
            await File.WriteAllBytesAsync(future, Png);
            File.SetLastWriteTimeUtc(future, captured.UtcDateTime.AddHours(4));
            var watcher = new WindowsScreenshotWatcher(pollInterval: TimeSpan.FromMilliseconds(10));

            using (var firstRun = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await using var first = watcher
                    .WatchSettledAsync(root, firstRun.Token)
                    .GetAsyncEnumerator(firstRun.Token);
                Assert.True(await NextAsync(first, firstRun.Token));
                Assert.Equal(future, first.Current);
            }

            var current = Path.Combine(root, ScreenshotName(captured, 1));
            await File.WriteAllBytesAsync(current, Png);
            File.SetLastWriteTimeUtc(current, captured.UtcDateTime);
            using var secondRun = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var second = watcher
                .WatchSettledAsync(root, secondRun.Token)
                .GetAsyncEnumerator(secondRun.Token);
            Assert.True(await NextAsync(second, secondRun.Token));
            Assert.Equal(current, second.Current);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A poll-sized burst is identity work, not a newest-file contest.</summary>
    [Fact]
    public async Task BurstOfTwelveFilesIsDeliveredExactlyOnce()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10))
                .WatchSettledAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var firstDelivery = enumerator.MoveNextAsync().AsTask();
            var captured = DateTimeOffset.UtcNow;
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < 12; index++)
            {
                var path = Path.Combine(root, ScreenshotName(captured, index));
                await File.WriteAllBytesAsync(path, Png, stopping.Token);
                expected.Add(path);
            }

            var delivered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assert.True(await firstDelivery.WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            delivered.Add(enumerator.Current);
            while (delivered.Count < expected.Count)
            {
                Assert.True(await NextAsync(enumerator, stopping.Token));
                Assert.True(delivered.Add(enumerator.Current), "a screenshot was delivered more than once");
            }

            Assert.True(expected.SetEquals(delivered));
            var extra = enumerator.MoveNextAsync().AsTask();
            await Task.Delay(80, stopping.Token);
            Assert.False(extra.IsCompleted);
            stopping.Cancel();
            Assert.False(await extra);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Old files with misleading future mtimes stay history when a large folder is opened.</summary>
    [Fact]
    public async Task StartupUsesFilenameTimeToIgnoreOldHistory()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 24; index++)
            {
                var old = Path.Combine(root, ScreenshotName(now.AddDays(-1), index));
                await File.WriteAllBytesAsync(old, Png, stopping.Token);
                File.SetLastWriteTimeUtc(old, now.UtcDateTime.AddHours(4).AddSeconds(index));
            }

            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    maximumTrackedFiles: 16)
                .WatchSettledAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var next = enumerator.MoveNextAsync().AsTask();
            var current = Path.Combine(root, ScreenshotName(now, 99));
            await File.WriteAllBytesAsync(current, Png, stopping.Token);
            File.SetLastWriteTimeUtc(current, now.UtcDateTime);

            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(current, enumerator.Current);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartupLogsTheFolderAndEachSkippedIdentityOnlyOnce()
    {
        var root = NewDirectory();
        try
        {
            var old = Path.Combine(root, ScreenshotName(DateTimeOffset.UtcNow.AddDays(-1), 0));
            await File.WriteAllBytesAsync(old, Png);
            var logger = new RecordingLogger<WindowsScreenshotWatcher>();
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    logger: logger)
                .WatchSettledAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var next = enumerator.MoveNextAsync().AsTask();

            await UntilAsync(() => logger.Messages.Any(message =>
                message.Contains("Skipped startup screenshot", StringComparison.Ordinal)));
            await Task.Delay(80, stopping.Token);

            Assert.Contains(logger.Messages, message => message.Contains(root, StringComparison.Ordinal));
            Assert.Single(logger.Messages, message =>
                message.Contains("Skipped startup screenshot", StringComparison.Ordinal));
            stopping.Cancel();
            Assert.False(await next);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// [#893] One information line for the whole folder; each file only at Debug. Fails on main,
    /// which wrote one information line per old screenshot (70% of the owner's startup log).
    /// </summary>
    [Fact]
    public async Task StartupSummarisesSkippedScreenshotsInOneInformationLine()
    {
        var root = NewDirectory();
        try
        {
            var now = DateTimeOffset.UtcNow;
            for (var index = 0; index < 5; index++)
            {
                await File.WriteAllBytesAsync(Path.Combine(root, ScreenshotName(now.AddDays(-1).AddMinutes(index), index)), Png);
            }

            var logger = new LevelLogger<WindowsScreenshotWatcher>();
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10),
                    logger: logger)
                .WatchSettledAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            var next = enumerator.MoveNextAsync().AsTask();

            await UntilAsync(() => logger.Entries.Any(entry => entry.Message.StartsWith("Skipped 5 ", StringComparison.Ordinal)));
            await Task.Delay(80, stopping.Token);

            var information = logger.Entries.Where(entry => entry.Level >= LogLevel.Information).Select(entry => entry.Message).ToArray();
            Assert.Single(information, message => message.Contains("startup screenshot", StringComparison.Ordinal));
            Assert.Equal(5, logger.Entries.Count(entry =>
                entry.Level == LogLevel.Debug && entry.Message.StartsWith("Skipped startup screenshot", StringComparison.Ordinal)));
            stopping.Cancel();
            Assert.False(await next);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LevelLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

        [Fact]
    public async Task EitherOccurrenceOfARepeatedDstMinuteIsRecentAtStartup()
    {
        using var zone = LocalTime.UseZone(EasternLike());
        var occurrences = new[]
        {
            new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero),
        };

        foreach (var now in occurrences)
        {
            var root = NewDirectory();
            try
            {
                var path = Path.Combine(root, ScreenshotName(now, 0));
                await File.WriteAllBytesAsync(path, Png);
                File.SetLastWriteTimeUtc(path, now.UtcDateTime.AddDays(-1));
                using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var enumerator = new WindowsScreenshotWatcher(
                        pollInterval: TimeSpan.FromMilliseconds(10),
                        timeProvider: new FixedTimeProvider(now))
                    .WatchSettledAsync(root, stopping.Token)
                    .GetAsyncEnumerator(stopping.Token);

                Assert.True(await NextAsync(enumerator, stopping.Token));
                Assert.Equal(path, enumerator.Current);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

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
                .WatchSettledAsync(root, stopping.Token)
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
                .WatchSettledAsync(root, stopping.Token)
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
                .WatchSettledAsync(root, stopping.Token)
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
                .WatchSettledAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);

            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), stopping.Token));
            Assert.Equal(path, enumerator.Current);
            var changed = new byte[Png.Length + 1];
            Png.CopyTo(changed, 0);
            changed[^1] = 1;
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
    public async Task HandledNameAndSizeAreNotRedeliveredWhenOnlyMtimeChanges()
    {
        var root = NewDirectory();
        try
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var path = Path.Combine(root, "mtime-only.png");
            await File.WriteAllBytesAsync(path, Png, stopping.Token);
            await using var enumerator = new WindowsScreenshotWatcher(
                    pollInterval: TimeSpan.FromMilliseconds(10))
                .WatchSettledAsync(root, stopping.Token)
                .GetAsyncEnumerator(stopping.Token);
            Assert.True(await NextAsync(enumerator, stopping.Token));

            var next = enumerator.MoveNextAsync().AsTask();
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(4));
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
            await using var enumerator = watcher.WatchSettledAsync(root, stopping.Token).GetAsyncEnumerator(stopping.Token);
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

            await using var recovered = watcher.WatchSettledAsync(root, stopping.Token).GetAsyncEnumerator(stopping.Token);
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
                .WatchSettledAsync(root, stopping.Token)
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
                .WatchSettledAsync(root, stopping.Token)
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
                .WatchSettledAsync(root, stopping.Token)
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

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(state, exception));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string NewDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"capture-intake-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<bool> NextAsync(
        IAsyncEnumerator<string> enumerator,
        CancellationToken cancellationToken) =>
        await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);

    private static string ScreenshotName(DateTimeOffset captured, int suffix)
    {
        var local = TimeZoneInfo.ConvertTime(captured, LocalTime.Zone);
        return $"{local:yyyy-MM-dd[HH-mm]}_shot-{suffix:D2}.png";
    }

    private static TimeZoneInfo EasternLike()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(
                new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone(
            "Screenshot watcher Eastern-like",
            TimeSpan.FromHours(-5),
            "Eastern-like",
            "Standard",
            "Daylight",
            [rule]);
    }
}
