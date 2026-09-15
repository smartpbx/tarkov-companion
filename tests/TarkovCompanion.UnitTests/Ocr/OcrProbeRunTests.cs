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
        bool emptyRead = false) : IOcrEngine
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
            return emptyRead
                ? new OcrResult([], TimeSpan.Zero, name, true, "ocr_no_text")
                : new OcrResult([new OcrLine("Wires", region, null)], TimeSpan.Zero, name);
        }
    }
}
