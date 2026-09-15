using System.Buffers;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests.Ocr;

/// <summary>
/// The Tesseract provider's gate, deadline and ceilings, proven through its page-reader seam so
/// they run on every host instead of only where the native library loads.
/// </summary>
public sealed class TesseractOcrBoundsTests
{
    // Mirrors the provider's cancellation interval, so a check that regressed to once per row
    // (or never, in the midpoint scan) reads the whole single-row frame and fails these tests.
    private const int CancellationCheckPixels = 1 << 16;

    [Fact]
    public async Task TimeoutKeepsTheProviderGateUntilNativeWorkReallyExits()
    {
        using var release = new ManualResetEventSlim();
        var reader = new FakePageReader(call =>
        {
            if (call == 0)
            {
                // Tesseract cannot be interrupted; this read ends only when the test says so.
                release.Wait();
            }

            return [new OcrLine("INSPECT", new(1, 1, 20, 8), new Confidence(0.9))];
        });
        using var engine = new TesseractOcrEngine(
            new TesseractOcrOptions { FrameTimeout = TimeSpan.FromMilliseconds(200) },
            reader);

        try
        {
            var abandoned = await engine.RecognizeDetailedAsync(Frame(64, 32), AsCaptured(), CancellationToken.None);
            var blocked = await engine.RecognizeDetailedAsync(Frame(64, 32), AsCaptured(), CancellationToken.None);

            Assert.Equal(OcrExecutionStatus.TimedOut, abandoned.Status);
            Assert.Equal("ocr_frame_timeout", abandoned.DiagnosticCode);
            Assert.False(abandoned.Result.IsAvailable);
            Assert.Equal(1, abandoned.PlannedTileCount);
            Assert.Equal(1, abandoned.AttemptedTileCount);
            Assert.Equal(0, abandoned.CompletedTileCount);
            Assert.Equal(OcrExecutionStatus.TimedOut, blocked.Status);
            Assert.Equal(0, blocked.AttemptedTileCount);
            Assert.Equal(1, reader.Calls);
        }
        finally
        {
            release.Set();
        }

        var resumed = await ReadWhenGateSettlesAsync(engine);

        Assert.Equal(OcrExecutionStatus.Complete, resumed.Status);
        Assert.Equal(1, resumed.CompletedTileCount);
        Assert.Equal(2, reader.Calls);
        Assert.Equal(1, reader.MaximumConcurrentReads);
    }

    [Fact]
    public async Task CallerCancellationThrowsButTheAbandonedNativeReadIsObservedBeforeTheGateOpens()
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var reader = new FakePageReader(call =>
        {
            if (call != 0)
            {
                return [];
            }

            cancellation.Cancel();
            release.Wait();
            throw new FormatException("synthetic late native failure");
        });
        using var engine = new TesseractOcrEngine(
            new TesseractOcrOptions { FrameTimeout = TimeSpan.FromMilliseconds(200) },
            reader);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                engine.RecognizeDetailedAsync(Frame(64, 32), AsCaptured(), cancellation.Token));
            var blocked = await engine.RecognizeDetailedAsync(Frame(64, 32), AsCaptured(), CancellationToken.None);

            Assert.Equal(OcrExecutionStatus.TimedOut, blocked.Status);
            Assert.Equal(0, blocked.AttemptedTileCount);
        }
        finally
        {
            release.Set();
        }

        var resumed = await ReadWhenGateSettlesAsync(engine);

        Assert.Equal(OcrExecutionStatus.Empty, resumed.Status);
        Assert.True(engine.Availability.IsAvailable, engine.Availability.Reason);
        Assert.Equal(2, reader.Calls);
        Assert.Equal(1, reader.MaximumConcurrentReads);
    }

    [Theory]
    [InlineData(15L, 1_000_000L, 1_000_000L, "ocr_input_limit_exceeded")]
    [InlineData(1_000_000L, 15L, 1_000_000L, "ocr_prepared_pixel_limit_exceeded")]
    [InlineData(1_000_000L, 1_000_000L, 100L, "ocr_memory_limit_exceeded")]
    public async Task PixelAndMemoryCeilingsRejectBeforeAnyNativeWork(
        long maximumSourcePixels,
        long maximumPreparedPixels,
        long maximumEstimatedPeakBytes,
        string expectedDiagnostic)
    {
        var reader = new FakePageReader(_ => []);
        using var engine = new TesseractOcrEngine(
            new TesseractOcrOptions
            {
                MaximumSourcePixels = maximumSourcePixels,
                MaximumPreparedPixels = maximumPreparedPixels,
                MaximumEstimatedPeakBytes = maximumEstimatedPeakBytes,
            },
            reader);

        // Sixteen source and prepared pixels; 16 + (16 * 2) + 64 = 112 estimated peak bytes.
        var execution = await engine.RecognizeDetailedAsync(Frame(4, 4), AsCaptured(), CancellationToken.None);

        Assert.Equal(OcrExecutionStatus.Rejected, execution.Status);
        Assert.Equal(expectedDiagnostic, execution.DiagnosticCode);
        Assert.False(execution.Result.IsAvailable);
        Assert.Equal(112, execution.EstimatedPeakBytes);
        Assert.Equal(0, execution.AttemptedTileCount);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task EmptyNativeReadIsAvailableEmptyAndDistinctFromComplete()
    {
        var reader = new FakePageReader(_ => [new OcrLine("   ", new(0, 0, 4, 4), new Confidence(0.2))]);
        using var engine = new TesseractOcrEngine(new TesseractOcrOptions(), reader);

        var execution = await engine.RecognizeDetailedAsync(Frame(64, 32), AsCaptured(), CancellationToken.None);

        Assert.Equal(OcrExecutionStatus.Empty, execution.Status);
        Assert.Equal("ocr_no_text", execution.DiagnosticCode);
        Assert.True(execution.Result.IsAvailable);
        Assert.Empty(execution.Result.Lines);
        Assert.Equal(1, execution.CompletedTileCount);
    }

    [Fact]
    public async Task ProviderLinesAndTextAreBoundedAndReportedAsPartial()
    {
        var reader = new FakePageReader(_ => Enumerable.Range(0, 10)
            .Select(index => new OcrLine(
                index == 0 ? new string('B', 50) : $"LINE {index}",
                new PixelRect(0, index * 3, 20, 2),
                new Confidence(0.8)))
            .ToArray());
        using var engine = new TesseractOcrEngine(
            new TesseractOcrOptions { MaximumLines = 3, MaximumLineTextLength = 8 },
            reader);

        var execution = await engine.RecognizeDetailedAsync(Frame(64, 32), AsCaptured(), CancellationToken.None);

        Assert.Equal(OcrExecutionStatus.Partial, execution.Status);
        Assert.Equal("ocr_line_limit_exceeded", execution.DiagnosticCode);
        Assert.True(execution.Result.IsAvailable);
        Assert.Equal(3, execution.ProviderLineCount);
        Assert.Equal(1, execution.TruncatedLineCount);
        Assert.Equal(3, execution.Result.Lines.Count);
        Assert.All(execution.Result.Lines, line => Assert.InRange(line.Text.Length, 1, 8));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationChecksCancellationInBoundedPixelChunksIncludingTheMidpointScan(bool brightTextOnly)
    {
        // One row, 300,000 pixels wide. A per-row check would read all of it before noticing;
        // the midpoint scan samples every pixel of a region this thin.
        const int width = 300_000;
        using var cancellation = new CancellationTokenSource();
        var pixels = new CancellingPixels(new byte[width], cancellation, cancelAfterReads: 10);
        var image = new CapturedImage(
            pixels.Memory,
            width,
            1,
            width,
            PixelFormat.Gray8,
            new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
            "fixture://cancellable-pixels");
        var reader = new FakePageReader(_ => []);
        using var engine = new TesseractOcrEngine(new TesseractOcrOptions(), reader);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RecognizeDetailedAsync(
            image,
            new OcrRequest(ScanContext.Unknown) { Preparation = new OcrPreparation(1, brightTextOnly) },
            cancellation.Token));

        // At most one check interval past the cancelling read, and far short of the 300,000
        // reads either scan would need to reach the end of the row.
        Assert.InRange(pixels.Reads, 10, CancellationCheckPixels * 2);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public void DeduplicationOfStackedDistinctLinesStaysWithinItsBudgetAndKeepsEvidence()
    {
        // Every pair overlaps and no pair matches, which a pairwise scan answers with about four
        // and a half million comparisons.
        var stacked = Enumerable.Range(0, 3_000)
            .Select(index => new OcrLine($"Q{index:D4}X", new PixelRect(10, 10, 40, 12), null))
            .ToArray();

        var merge = OcrLineDeduplicator.Merge(stacked);

        Assert.False(merge.IsExhaustive);
        Assert.Equal(3_000, merge.Lines.Count);
    }

    [Fact]
    public void OverlappingPassesOverALargeCaptionGridMergeExhaustively()
    {
        var first = new List<OcrLine>();
        var second = new List<OcrLine>();
        for (var row = 0; row < 40; row++)
        {
            for (var column = 0; column < 50; column++)
            {
                var text = $"Item {row}:{column}";
                first.Add(new OcrLine(text, new PixelRect(column * 70, row * 70, 60, 14), null));
                second.Add(new OcrLine(text, new PixelRect((column * 70) + 2, (row * 70) + 1, 60, 14), null));
            }
        }

        var merge = OcrLineDeduplicator.Merge(first, second);

        Assert.True(merge.IsExhaustive);
        Assert.Equal(2_000, merge.Lines.Count);
    }

    private static async Task<TesseractOcrExecution> ReadWhenGateSettlesAsync(TesseractOcrEngine engine)
    {
        // The gate is released on a continuation after the native read returns, so the first
        // retry can still find it held.
        TesseractOcrExecution execution;
        var attempts = 0;
        do
        {
            execution = await engine.RecognizeDetailedAsync(Frame(64, 32), AsCaptured(), CancellationToken.None);
            attempts++;
        }
        while (execution is { Status: OcrExecutionStatus.TimedOut, AttemptedTileCount: 0 } && attempts < 25);

        return execution;
    }

    private static OcrRequest AsCaptured() => new(ScanContext.Unknown)
    {
        Preparation = OcrPreparation.AsCaptured,
    };

    private static CapturedImage Frame(int width, int height) => new(
        new byte[checked(width * height)],
        width,
        height,
        width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero),
        "fixture://tesseract-bounds");

    private sealed class FakePageReader(Func<int, IReadOnlyList<OcrLine>> read) : ITesseractPageReader
    {
        private int _calls;
        private int _running;
        private int _maximumRunning;

        public int Calls => Volatile.Read(ref _calls);

        public int MaximumConcurrentReads => Volatile.Read(ref _maximumRunning);

        public IReadOnlyList<OcrLine> Read(byte[] portableGraymap, PixelRect region, int scale, int maximumLines)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            var running = Interlocked.Increment(ref _running);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumRunning);
            }
            while (running > observed &&
                   Interlocked.CompareExchange(ref _maximumRunning, running, observed) != observed);

            try
            {
                return read(call);
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Pixels that cancel a token after a set number of reads, to stop preparation mid-row.</summary>
    private sealed class CancellingPixels(
        byte[] pixels,
        CancellationTokenSource cancellation,
        int cancelAfterReads) : MemoryManager<byte>
    {
        public int Reads { get; private set; }

        public override Span<byte> GetSpan()
        {
            Reads++;
            if (Reads == cancelAfterReads)
            {
                cancellation.Cancel();
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
