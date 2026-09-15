using System.Buffers;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.UnitTests.Ocr;

/// <summary>
/// The cell probe reads a bounded number of captions under one run deadline, reports what it
/// planned, attempted and completed, and still throws when the person running it cancels.
/// </summary>
public sealed class OcrProbeRunTests
{
    [Fact]
    public async Task CellProbeReadsOnlyTheCellCeilingWithEveryProvider()
    {
        var first = new CaptionEngine("first-fixture");
        var second = new CaptionEngine("second-fixture");

        var report = await OcrProbe.ProbeCellsAsync(
            Frame(640, 120),
            new PixelRect(0, 0, 640, 120),
            Cells(10),
            [("first", first), ("second", second)],
            new ScanContextDetector(),
            new OcrProbeLimits { MaximumCells = 3 },
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(OcrProbe.CellLimitDiagnostic, report.DiagnosticCode);
        Assert.Equal(10, report.DetectedCellCount);
        Assert.Equal(3, report.Cells.Count);
        Assert.Equal(3, first.Calls);
        Assert.Equal(3, second.Calls);
        Assert.All(report.Engines, engine =>
        {
            Assert.Equal(3, engine.PlannedPassCount);
            Assert.Equal(3, engine.AttemptedPassCount);
            Assert.Equal(3, engine.CompletedPassCount);
            Assert.All(engine.Passes, pass => Assert.Equal("complete", pass.Status));
        });
    }

    [Fact]
    public async Task CellProbeStopsAtItsRunDeadlineAndKeepsFinishedPasses()
    {
        var slow = new CaptionEngine("slow-fixture", blockFromCall: 1);
        var never = new CaptionEngine("never-fixture");

        var report = await OcrProbe.ProbeCellsAsync(
            Frame(640, 120),
            new PixelRect(0, 0, 640, 120),
            Cells(4),
            [("slow", slow), ("never", never)],
            new ScanContextDetector(),
            new OcrProbeLimits { RunTimeout = TimeSpan.FromMilliseconds(300) },
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(OcrProbe.DeadlineDiagnostic, report.DiagnosticCode);
        var slowReport = report.Engines[0];
        Assert.Single(slowReport.Passes);
        Assert.Equal(4, slowReport.PlannedPassCount);
        Assert.Equal(2, slowReport.AttemptedPassCount);
        Assert.Equal(1, slowReport.CompletedPassCount);
        var neverReport = report.Engines[1];
        Assert.Equal(4, neverReport.PlannedPassCount);
        Assert.Equal(0, neverReport.AttemptedPassCount);
        Assert.Equal(0, never.Calls);
    }

    [Fact]
    public async Task CellProbeRethrowsTheCallersCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var engine = new CaptionEngine("cancelling-fixture", blockFromCall: 0, onCall: cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OcrProbe.ProbeCellsAsync(
            Frame(640, 120),
            new PixelRect(0, 0, 640, 120),
            Cells(2),
            [("cancelling", engine)],
            new ScanContextDetector(),
            new OcrProbeLimits(),
            TextWriter.Null,
            cancellation.Token));
    }

    [Fact]
    public async Task FrameProbeReportsAnEmptyReadAsEmptyAndCompleted()
    {
        var engine = new CaptionEngine("empty-fixture", emptyRead: true);

        var report = await OcrProbe.ProbeFrameAsync(
            Frame(640, 120),
            new PixelRect(0, 0, 640, 120),
            [("empty", engine)],
            new ScanContextDetector(),
            new OcrProbeLimits(),
            3,
            TextWriter.Null,
            CancellationToken.None);

        var engineReport = Assert.Single(report.Engines);
        Assert.Null(report.DiagnosticCode);
        Assert.Equal(6, engineReport.PlannedPassCount);
        Assert.Equal(6, engineReport.CompletedPassCount);
        Assert.All(engineReport.Passes, pass =>
        {
            Assert.Equal("empty", pass.Status);
            Assert.Equal(0, pass.LineCount);
        });
    }

    [Fact]
    public async Task AProviderThatFailsStopsItsOwnPassesAndReportsItsAvailabilityAfterward()
    {
        var failing = new FailingEngine();
        var healthy = new CaptionEngine("healthy-fixture");

        var report = await OcrProbe.ProbeFrameAsync(
            Frame(640, 120),
            new PixelRect(0, 0, 640, 120),
            [("failing", failing), ("healthy", healthy)],
            new ScanContextDetector(),
            new OcrProbeLimits(),
            3,
            TextWriter.Null,
            CancellationToken.None);

        var failed = report.Engines[0];
        Assert.Equal(1, failing.Calls);
        Assert.False(failed.IsAvailable);
        Assert.Equal("synthetic native failure", failed.UnavailableReason);
        Assert.Equal("ocr_provider_failed", failed.DiagnosticCode);
        Assert.Equal(6, failed.PlannedPassCount);
        Assert.Equal(1, failed.AttemptedPassCount);
        Assert.Equal(0, failed.CompletedPassCount);
        Assert.Single(failed.Passes);
        // One provider failing is that provider's result, not the run's.
        Assert.Null(report.DiagnosticCode);
        var other = report.Engines[1];
        Assert.True(other.IsAvailable);
        Assert.Null(other.DiagnosticCode);
        Assert.Equal(6, other.CompletedPassCount);
    }

    [Fact]
    public async Task AFailedPassStopsAProviderEvenWhenItStillReportsItselfAvailable()
    {
        var engine = new CaptionEngine("failed-pass-fixture", failedRead: true);

        var report = await OcrProbe.ProbeCellsAsync(
            Frame(640, 120),
            new PixelRect(0, 0, 640, 120),
            Cells(5),
            [("failed", engine)],
            new ScanContextDetector(),
            new OcrProbeLimits(),
            TextWriter.Null,
            CancellationToken.None);

        var engineReport = Assert.Single(report.Engines);
        Assert.Equal(1, engine.Calls);
        Assert.Equal("ocr_provider_failed", engineReport.DiagnosticCode);
        Assert.Equal(5, engineReport.PlannedPassCount);
        Assert.Equal(1, engineReport.AttemptedPassCount);
    }

    [Fact]
    public async Task CellDiscoveryWithAPreCancelledTokenReadsNoPixels()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var pixels = new ObservedPixels(new byte[640 * 120]);
        var engine = new CaptionEngine("unused-fixture");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OcrProbe.ProbeCellsAsync(
            Frame(pixels, 640, 120),
            new PixelRect(0, 0, 640, 120),
            [("unused", engine)],
            new ScanContextDetector(),
            new OcrProbeLimits(),
            TextWriter.Null,
            cancellation.Token));

        Assert.Equal(0, pixels.Reads);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task TheRunDeadlineCoversStashGridDiscoveryAndStopsItPartway()
    {
        // Discovery reads 640 x 120 pixels on its first axis. The deadline's clock passes it at one
        // of the first reads; only a deadline that started before discovery can stop the rest.
        //
        // On a manual clock, so the deadline fires inside that read. A 400 ms stall against a
        // 100 ms system timer measured how soon a busy pool ran the timer, not the deadline.
        var clock = new ManualClock();
        var pixels = new ObservedPixels(new byte[640 * 120], atRead: 10, () => clock.Advance(TimeSpan.FromMilliseconds(400)));
        var engine = new CaptionEngine("unreached-fixture");

        var report = await OcrProbe.ProbeCellsAsync(
            Frame(pixels, 640, 120),
            new PixelRect(0, 0, 640, 120),
            [("unreached", engine)],
            new ScanContextDetector(),
            new OcrProbeLimits { RunTimeout = TimeSpan.FromMilliseconds(100), TimeProvider = clock },
            TextWriter.Null,
            CancellationToken.None);

        Assert.Equal(OcrProbe.DeadlineDiagnostic, report.DiagnosticCode);
        Assert.Empty(report.Cells);
        Assert.Null(report.DetectedCellCount);
        Assert.InRange(pixels.Reads, 10, (1 << 16) + 10);
        Assert.Equal(0, engine.Calls);
        var engineReport = Assert.Single(report.Engines);
        Assert.Equal(0, engineReport.PlannedPassCount);
        Assert.Equal(0, engineReport.AttemptedPassCount);
    }

    private static CapturedImage Frame(ObservedPixels pixels, int width, int height) => new(
        pixels.Memory,
        width,
        height,
        width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        "fixture://ocr-probe-discovery");

    private static StashCell[] Cells(int count) => Enumerable.Range(0, count)
        .Select(index => new StashCell(
            new PixelRect(index * 60, 0, 60, 60),
            new PixelRect(index * 60, 40, 60, 20)))
        .ToArray();

    private static CapturedImage Frame(int width, int height) => new(
        new byte[checked(width * height)],
        width,
        height,
        width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        "fixture://ocr-probe-run");

    private sealed class CaptionEngine(
        string name,
        int blockFromCall = int.MaxValue,
        Action? onCall = null,
        bool emptyRead = false,
        bool failedRead = false) : IOcrEngine
    {
        private int _calls;

        public int Calls => _calls;

        public async Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            var call = _calls++;
            onCall?.Invoke();
            if (call >= blockFromCall)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var region = request.Region ?? new PixelRect(0, 0, image.Width, image.Height);
            if (failedRead)
            {
                return new OcrResult([], TimeSpan.Zero, name, false, "ocr_provider_failed");
            }

            return emptyRead
                ? new OcrResult([], TimeSpan.Zero, name, true, "ocr_no_text")
                : new OcrResult([new OcrLine("Wires", region, null)], TimeSpan.Zero, name);
        }
    }

    /// <summary>Fails its first read and marks itself unavailable, as the Tesseract provider does.</summary>
    private sealed class FailingEngine : IOcrEngine, IOcrEngineStatus
    {
        public int Calls { get; private set; }

        public OcrEngineAvailability Availability { get; private set; } = new(true, "failing-fixture");

        public Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            Availability = new(false, "failing-fixture", "synthetic native failure");
            return Task.FromResult(new OcrResult([], TimeSpan.Zero, "failing-fixture", false, "ocr_provider_failed"));
        }
    }

    /// <summary>A clock that moves only when told to and fires the timers it created as it passes them.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero) + _elapsed;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _elapsed += by;
                due = [.. _timers.Where(timer => timer.DueAt <= _elapsed)];
                foreach (var timer in due)
                {
                    _timers.Remove(timer);
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan DueAt { get; private set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        DueAt = clock._elapsed + dueTime;
                        clock._timers.Add(this);
                    }
                }

                return true;
            }

            public void Dispose()
            {
                lock (clock._gate)
                {
                    clock._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Pixels that count their reads and run an action at one chosen read.</summary>
    private sealed class ObservedPixels(byte[] pixels, int atRead = 0, Action? action = null) : MemoryManager<byte>
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        // The base property takes the span to learn the length, which would count as a pixel read.
        public override Memory<byte> Memory => CreateMemory(pixels.Length);

        public override Span<byte> GetSpan()
        {
            if (Interlocked.Increment(ref _reads) == atRead)
            {
                action?.Invoke();
            }

            return pixels;
        }

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
