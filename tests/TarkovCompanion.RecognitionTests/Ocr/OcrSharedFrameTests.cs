using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests.Ocr;

/// <summary>
/// [#453] Each game screenshot was read by the always-on scan and by the V2 capture session, both
/// through the one coordinator, so every screenshot paid for its OCR twice while the game ran.
/// </summary>
public sealed class OcrSharedFrameTests
{
    [Fact]
    public async Task A_host_that_shares_frames_reads_the_same_pixels_once()
    {
        var engine = new CountingEngine();
        var coordinator = new OcrCoordinator(engine, new ScanContextDetector()) { SharesIdenticalFrames = true };

        var first = await coordinator.RecognizeAsync(Frame(0), CancellationToken.None);
        var calls = engine.Calls;
        var second = await coordinator.RecognizeAsync(Frame(0), CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(calls, engine.Calls);

        await coordinator.RecognizeAsync(Frame(1), CancellationToken.None);
        Assert.True(engine.Calls > calls);
    }

    [Fact]
    public async Task By_default_every_call_reads_again()
    {
        var engine = new CountingEngine();
        var coordinator = new OcrCoordinator(engine, new ScanContextDetector());

        await coordinator.RecognizeAsync(Frame(0), CancellationToken.None);
        var calls = engine.Calls;
        await coordinator.RecognizeAsync(Frame(0), CancellationToken.None);

        Assert.Equal(calls * 2, engine.Calls);
    }

    [Fact]
    public async Task A_reading_the_engine_could_not_make_is_not_handed_to_the_next_caller()
    {
        var engine = new CountingEngine { Available = false };
        var coordinator = new OcrCoordinator(engine, new ScanContextDetector()) { SharesIdenticalFrames = true };

        await coordinator.RecognizeAsync(Frame(0), CancellationToken.None);
        var calls = engine.Calls;
        await coordinator.RecognizeAsync(Frame(0), CancellationToken.None);

        Assert.Equal(calls * 2, engine.Calls);
    }

    private static CapturedImage Frame(byte fill) => new(
        Enumerable.Repeat(fill, 64 * 32).ToArray(),
        64,
        32,
        64,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero),
        "fixture://shared-frame");

    private sealed class CountingEngine : IOcrEngine
    {
        public int Calls { get; private set; }

        public bool Available { get; init; } = true;

        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new OcrResult([], TimeSpan.FromMilliseconds(1), "counting", Available));
        }
    }
}
