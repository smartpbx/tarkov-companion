using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.UnitTests.Ocr;

/// <summary>
/// A probe report records a provider's availability only once native work its passes abandoned
/// has settled, and says so when it could not wait that long.
/// </summary>
/// <remarks>
/// The Tesseract provider runs through its page-reader seam, so the late failure is the provider's
/// own retirement path rather than a fixture that flips a flag.
/// </remarks>
public sealed class OcrProbeSettleTests
{
    [Fact]
    public async Task ALateFailureOfTheLastPassesAbandonedReadIsInTheReportRatherThanAStaleAvailable()
    {
        using var release = new ManualResetEventSlim();
        var reader = new StalledReader(release, failLate: true);
        using var tesseract = new TesseractOcrEngine(
            new TesseractOcrOptions { FrameTimeout = TimeSpan.FromMilliseconds(200) },
            reader);
        // Released only once the report is certainly waiting to record availability, so the
        // failure lands after the last pass and before the report, which is the lie being tested.
        tesseract.SettleWaiting = release.Set;

        OcrProbeReport report;
        try
        {
            report = await OcrProbe.ProbeCellsAsync(
                Frame(640, 120),
                new PixelRect(0, 0, 640, 120),
                Cells(1),
                [("tesseract", tesseract)],
                new ScanContextDetector(),
                new OcrProbeLimits(),
                TextWriter.Null,
                CancellationToken.None);
        }
        finally
        {
            release.Set();
        }

        var engine = Assert.Single(report.Engines);
        var pass = Assert.Single(engine.Passes);
        Assert.Equal("timed-out", pass.Status);
        Assert.Equal(1, engine.AttemptedPassCount);
        Assert.False(engine.IsAvailable);
        Assert.Contains("InvalidOperationException", engine.UnavailableReason, StringComparison.Ordinal);
        Assert.Equal("ocr_provider_failed", engine.DiagnosticCode);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(1, reader.Disposals);
    }

    [Fact]
    public async Task NativeWorkThatHasNotSettledWithinTheBoundIsReportedAsUnsettledNotAsKnownAvailable()
    {
        using var release = new ManualResetEventSlim();
        var reader = new StalledReader(release, failLate: false);
        using var tesseract = new TesseractOcrEngine(
            new TesseractOcrOptions { FrameTimeout = TimeSpan.FromMilliseconds(200) },
            reader);

        OcrProbeReport report;
        try
        {
            report = await OcrProbe.ProbeCellsAsync(
                Frame(640, 120),
                new PixelRect(0, 0, 640, 120),
                Cells(1),
                [("tesseract", tesseract)],
                new ScanContextDetector(),
                new OcrProbeLimits { SettleTimeout = TimeSpan.FromMilliseconds(50) },
                TextWriter.Null,
                CancellationToken.None);
        }
        finally
        {
            release.Set();
        }

        var engine = Assert.Single(report.Engines);
        Assert.Equal("timed-out", Assert.Single(engine.Passes).Status);
        Assert.Equal(OcrProbe.SettleTimeoutDiagnostic, engine.DiagnosticCode);
        Assert.Equal("ocr_provider_settle_timeout", engine.DiagnosticCode);
    }

    [Fact]
    public async Task APassQueuedBehindAnAbandonedReadThatFailsLateNeverStartsAReadAndTheReportSaysUnavailable()
    {
        using var release = new ManualResetEventSlim();
        var reader = new StalledReader(release, failLate: true);
        using var tesseract = new TesseractOcrEngine(
            new TesseractOcrOptions { FrameTimeout = TimeSpan.FromSeconds(2) },
            reader);
        // The first preparation's pass times out with its read still stalled. Its description is
        // written before the next pass is asked for, so releasing the read there makes it fail
        // while the provider still has five passes planned.
        using var log = new PassSignallingWriter("as captured:", release.Set);

        OcrProbeReport report;
        try
        {
            report = await OcrProbe.ProbeFrameAsync(
                Frame(640, 120),
                new PixelRect(0, 0, 640, 120),
                [("tesseract", tesseract)],
                new ScanContextDetector(),
                new OcrProbeLimits(),
                3,
                log,
                CancellationToken.None);
        }
        finally
        {
            release.Set();
        }

        var engine = Assert.Single(report.Engines);
        // The failure is seen either between the passes or by the next pass on taking the gate.
        // Which one depends on the scheduler; both stop the provider, and neither starts a read
        // on the failed engine or leaves the report saying available.
        Assert.InRange(engine.AttemptedPassCount, 1, 2);
        Assert.Contains(engine.DiagnosticCode, new[] { "ocr_provider_failed", "ocr_provider_unavailable" });
        Assert.False(engine.IsAvailable);
        Assert.Equal(6, engine.PlannedPassCount);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(1, reader.Disposals);
        Assert.Equal(0, reader.DisposalsDuringRead);
        Assert.Null(report.DiagnosticCode);
    }

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
        "fixture://ocr-probe-settle");

    /// <summary>
    /// Stalls its first read until released, then fails it as a native provider does or lets it
    /// finish; every later read answers at once.
    /// </summary>
    private sealed class StalledReader(ManualResetEventSlim release, bool failLate) : ITesseractPageReader
    {
        private int _calls;
        private int _running;
        private int _disposals;
        private int _disposalsDuringRead;

        public int Calls => Volatile.Read(ref _calls);

        public int Disposals => Volatile.Read(ref _disposals);

        public int DisposalsDuringRead => Volatile.Read(ref _disposalsDuringRead);

        public IReadOnlyList<OcrLine> Read(byte[] portableGraymap, PixelRect region, int scale, int maximumLines)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            Interlocked.Increment(ref _running);
            try
            {
                if (call == 0)
                {
                    release.Wait();
                    if (failLate)
                    {
                        throw new InvalidOperationException("synthetic late native failure");
                    }
                }

                return [new OcrLine("Wires", region, new Confidence(0.9))];
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _running) > 0)
            {
                Interlocked.Increment(ref _disposalsDuringRead);
            }

            Interlocked.Increment(ref _disposals);
        }
    }

    /// <summary>Runs an action the first time a line starting with a pass's name is written.</summary>
    private sealed class PassSignallingWriter(string prefix, Action onPass) : StringWriter
    {
        private int _signalled;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith(prefix, StringComparison.Ordinal) == true &&
                Interlocked.Exchange(ref _signalled, 1) == 0)
            {
                onPass();
            }
        }
    }
}
