using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests.Ocr;

public sealed class CommonOcrFoundationTests
{
    [Theory]
    [InlineData(1920, 1080, 20, "SingleItem", "INSPECT")]
    [InlineData(2560, 1440, 33, "Container", "STASH")]
    [InlineData(3840, 2160, 60, "ExtractList", "EXTRACTS")]
    [InlineData(3840, 1080, 20, "FleaListings", "FLEA MARKET")]
    public void FullFrameContextDetectionCoversCommonAndUltrawideShapes(
        int width,
        int height,
        int lineHeight,
        string expectedContext,
        string anchor)
    {
        var image = Frame(width, height);
        // The line deliberately moves from the left edge to the far right as the aspect ratio
        // changes. Full-frame context evidence is not gated by a layout crop.
        var x = width > height * 2 ? width - 500 : width / 3;
        var result = new OcrResult(
            [new OcrLine(anchor, new(x, height / 4, 300, lineHeight), new Confidence(0.92))],
            TimeSpan.FromMilliseconds(4),
            "fixture");

        var detection = new ScanContextDetector().Detect(image, result);

        Assert.Equal(Enum.Parse<ScanContext>(expectedContext), detection.Context);
        Assert.InRange(detection.EstimatedUiScale, 0.9, 1.6);
        Assert.Contains($"resolution={width}x{height}", detection.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void SupplementalSignalsFindMovedCharacterAndVersionTextWithoutInventingConfidence()
    {
        var result = new OcrResult(
        [
            new OcrLine("GEAR", new(3400, 70, 100, 30), null),
            new OcrLine("HEALTH", new(80, 900, 150, 30), null),
            new OcrLine("1.1.5.0.47242 | PvP", new(1850, 1030, 420, 24), null),
        ], TimeSpan.Zero, "unscored-fixture");

        var signals = new SupplementalOcrSignalDetector().Detect(result);

        Assert.True(signals.HealthAndCharacter.IsPresent);
        Assert.Null(signals.HealthAndCharacter.Confidence);
        Assert.Equal(new PixelRect(80, 70, 3420, 860), signals.HealthAndCharacter.Bounds);
        Assert.True(signals.VersionStrip.IsPresent);
        Assert.Null(signals.VersionStrip.Confidence);
        Assert.Equal(new PixelRect(1850, 1030, 420, 24), signals.VersionStrip.Bounds);
    }

    [Fact]
    public void TheCharacterTabRowIsFoundWhenTheEngineReturnsItAsOneLine()
    {
        // Two real Gear screens 14 seconds apart read present and absent: the engine returns the
        // tab row as one line or several, and the whole line used to have to equal "health".
        var result = new OcrResult(
        [
            new OcrLine("OVERALL GEAR HEALTH SKILLS MAP TASKS ACHIEVEMENTS", new(1000, 10, 900, 20), null),
        ], TimeSpan.Zero, "unscored-fixture");

        Assert.True(new SupplementalOcrSignalDetector().Detect(result).HealthAndCharacter.IsPresent);
    }

    [Fact]
    public void HealthInAnItemDescriptionIsNotTheCharacterScreen()
    {
        var result = new OcrResult(
        [
            new OcrLine("Restores health over time and lets you get back to the map sooner", new(900, 400, 700, 20), null),
        ], TimeSpan.Zero, "unscored-fixture");

        Assert.False(new SupplementalOcrSignalDetector().Detect(result).HealthAndCharacter.IsPresent);
    }

    [Fact]
    public async Task CoordinatorDeterministicallyDeduplicatesOverlappingPasses()
    {
        var image = Frame(1920, 1080);
        var engine = new SequencedEngine(
            new OcrResult(
            [
                new OcrLine("INSPECT", new(400, 80, 150, 24), null),
                new OcrLine("WEIGHT", new(400, 700, 150, 24), null),
                new OcrLine("Graphics", new(600, 280, 160, 30), null),
            ], TimeSpan.FromMilliseconds(5), "fixture"),
            new OcrResult(
                [new OcrLine("Graphics Card", new(600, 280, 250, 30), null)],
                TimeSpan.FromMilliseconds(2),
                "fixture"));

        var result = await new OcrCoordinator(engine, new ScanContextDetector())
            .RecognizeAsync(image, CancellationToken.None);

        Assert.True(result.UsedFullFrameSupplement);
        Assert.Single(result.Candidates.Lines, line => line.Text == "Graphics Card");
        Assert.DoesNotContain(result.Candidates.Lines, line => line.Text == "Graphics");
        Assert.Equal(
            ["INSPECT", "Graphics Card", "WEIGHT"],
            result.Candidates.Lines.Select(line => line.Text));
    }

    [Fact]
    public async Task CoordinatorKeepsFullFrameEvidenceWhenContextualPassFails()
    {
        var image = Frame(1920, 1080);
        var engine = new SequencedEngine(
            new OcrResult(
            [
                new OcrLine("INSPECT", new(400, 80, 150, 24), new Confidence(0.9)),
                new OcrLine("WEIGHT", new(400, 700, 150, 24), new Confidence(0.9)),
            ], TimeSpan.FromMilliseconds(5), "fixture"),
            new OcrResult([], TimeSpan.FromMilliseconds(3), "fixture", false, "ocr_tile_failed"));

        var result = await new OcrCoordinator(engine, new ScanContextDetector())
            .RecognizeAsync(image, CancellationToken.None);

        Assert.True(result.IsPartial);
        Assert.Equal("ocr_tile_failed", result.DiagnosticCode);
        Assert.True(result.Candidates.IsAvailable);
        Assert.Equal(2, result.Candidates.Lines.Count);
    }

    [Fact]
    public async Task CoordinatorPropagatesCallerCancellation()
    {
        var engine = new CancellingEngine();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OcrCoordinator(engine, new ScanContextDetector())
                .RecognizeAsync(Frame(320, 180), cancellation.Token));
    }

    private static CapturedImage Frame(int width, int height) => new(
        new byte[checked(width * height)],
        width,
        height,
        width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero),
        "fixture://common-ocr");

    private sealed class SequencedEngine(params OcrResult[] results) : IOcrEngine
    {
        private int _index;

        public Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results[Math.Min(_index++, results.Length - 1)]);
        }
    }

    private sealed class CancellingEngine : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken) => Task.FromCanceled<OcrResult>(cancellationToken);
    }
}
