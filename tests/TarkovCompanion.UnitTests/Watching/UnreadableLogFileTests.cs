using System.Collections.Concurrent;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Platform.Windows.Watching;

namespace TarkovCompanion.UnitTests.Watching;

/// <summary>
/// #887: one log file that cannot be opened, or fails mid-read, no longer stops the others.
/// </summary>
/// <remarks>
/// The failure used to escape the watcher, and the observation service then cancelled the logs
/// and the screenshot watcher together and restarted them ten seconds later. These run the real
/// watcher over real files; only the open of one file is replaced so that it fails.
/// </remarks>
public sealed class UnreadableLogFileTests
{
    [Fact]
    public async Task AnAccessDeniedLogIsRetriedWhileTheOtherKeepsFlowing()
    {
        var failuresLeft = 2;
        var outcome = await WatchAsync(path =>
            Path.GetFileName(path).StartsWith("backend", StringComparison.Ordinal)
            && Interlocked.Decrement(ref failuresLeft) >= 0
                ? throw new UnauthorizedAccessException("delete pending")
                : null);

        Assert.Null(outcome.Fault);
        Assert.Equal([25.02, 31.5], outcome.Seconds.Order());
    }

    [Fact]
    public async Task AnIoErrorMidReadLosesNothingFromThatFile()
    {
        var failuresLeft = 1;
        var outcome = await WatchAsync(path =>
            Path.GetFileName(path).StartsWith("backend", StringComparison.Ordinal)
            && Interlocked.Decrement(ref failuresLeft) >= 0
                ? new FailingReads(OpenShared(path))
                : null);

        Assert.Null(outcome.Fault);
        // Exactly once each: the failed read did not advance the file's offset or double it.
        Assert.Equal([25.02, 31.5], outcome.Seconds.Order());
    }

    private static async Task<(Exception? Fault, double[] Seconds)> WatchAsync(Func<string, Stream?> open)
    {
        var root = Path.Combine(Path.GetTempPath(), $"tc-887-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "log_2026.09.25_10-00-00_1.1.5.0.47242");
        Directory.CreateDirectory(folder);
        var application = Path.Combine(folder, "application_000.log");
        var backend = Path.Combine(folder, "backend_000.log");
        File.WriteAllText(application, "");
        File.WriteAllText(backend, "");
        var observer = new LoadTimes();
        var watcher = new WindowsEftLogWatcher(new EftLogParser(), observer)
        {
            OpenLog = path => open(path) ?? OpenShared(path),
        };
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pump = Task.Run(
            async () =>
            {
                await foreach (var _ in watcher.WatchAsync(root, stopping.Token).ConfigureAwait(false))
                {
                }
            },
            CancellationToken.None);
        try
        {
            await Task.Delay(300, CancellationToken.None);
            File.AppendAllText(backend, Line(31.5));
            File.AppendAllText(application, Line(25.02));
            while (observer.Seconds.Count < 2 && !pump.IsCompleted && !stopping.IsCancellationRequested)
            {
                await Task.Delay(20, CancellationToken.None);
            }

            // One more poll, so a line read twice would have been.
            await Task.Delay(1200, CancellationToken.None);
            var fault = pump.IsFaulted ? pump.Exception!.GetBaseException() : null;
            await stopping.CancelAsync();
            try
            {
                await pump;
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException)
            {
            }

            return (fault, [.. observer.Seconds]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Line(double real) =>
        FormattableString.Invariant(
            $"2026-09-25 10:00:01.000|1.1.5.0.47242|Info|application|MatchingCompleted:18.36 real:{real} diff:6.66\n");

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);

    private sealed class LoadTimes : IEftLogObserver
    {
        public ConcurrentQueue<double> Seconds { get; } = new();

        public void Observe(GroupObservation observation)
        {
        }

        public void Observe(FleaSaleObservation sale)
        {
        }

        public void Observe(QuestStatusObservation quest)
        {
        }

        public void Observe(LoadTimeObservation loadTime) => Seconds.Enqueue(loadTime.RealSeconds);
    }

    /// <summary>A log whose length and seek work but whose read fails, as a lock-region violation does.</summary>
    private sealed class FailingReads(Stream inner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("The process cannot access the file because another process has locked a portion of it.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("The process cannot access the file because another process has locked a portion of it."));

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
