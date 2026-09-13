using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// When no anchor matches, the log has to say what was wanted and what was read.
/// </summary>
/// <remarks>
/// A score of zero says nothing matched. It does not say what the gap is, and without that the
/// only way forward is to guess at anchor terms and wait for somebody to play the game again.
/// The anchors were derived from a simulator and score zero on real screenshots while eighty
/// lines of perfectly good text sit on screen; this is what turns that into one measurement.
/// </remarks>
public sealed class ContextMissDiagnosticTests
{
    [Fact]
    public void AMissReportsBothSidesOfTheComparison()
    {
        var detection = Detect("Health", "Energy", "Hydration", "Armor Class 2");

        Assert.Equal(ScanContext.Unknown, detection.Context);
        Assert.Contains("wanted=[", detection.Evidence, StringComparison.Ordinal);
        Assert.Contains("read=[", detection.Evidence, StringComparison.Ordinal);
        Assert.Contains("hydration", detection.Evidence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDumpIsBoundedBecauseSomebodyHasToPasteIt()
    {
        // A real screenshot produces two hundred lines. This goes into a log file a person
        // copies into a chat window, so it cannot grow with the picture.
        var many = Enumerable.Range(0, 400).Select(i => $"a line of interface text number {i}").ToArray();

        var detection = Detect(many);

        Assert.True(detection.Evidence.Length < 1600, $"evidence was {detection.Evidence.Length} characters");
    }

    [Fact]
    public void ShortNoiseIsLeftOut()
    {
        // Text recognition finds stray characters in artwork. Anchors match captions, so the
        // captions are what is worth printing.
        var detection = Detect("x", "7", "|", "Grenade case");

        Assert.Contains("grenade case", detection.Evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("| |", detection.Evidence, StringComparison.Ordinal);
    }

    private static ContextDetection Detect(params string[] lines)
    {
        var ocr = new OcrResult(
            // Stacked one pixel apart so several hundred still fit the frame. Detection drops
            // a line whose bounds fall outside it, which is correct and which made an earlier
            // version of this fixture silently discard everything below the first line.
            lines.Select((text, index) => new OcrLine(text, new PixelRect(0, index, 300, 1), new Confidence(0.9))).ToArray(),
            TimeSpan.Zero,
            "test-engine",
            true,
            null);
        return new ScanContextDetector().Detect(Image(), ocr);
    }

    /// <summary>
    /// Big enough to contain the lines the tests place on it.
    /// </summary>
    /// <remarks>
    /// Detection drops any line whose bounds fall outside the frame, which is correct and
    /// which an eight-pixel test image made invisible: every line below the first was
    /// discarded before the diagnostic saw it, and the test looked like the diagnostic was
    /// broken.
    /// </remarks>
    private const int Width = 400;

    private const int Height = 500;

    private static CapturedImage Image() => new(
        new byte[Width * 4 * Height],
        Width: Width,
        Height: Height,
        Stride: Width * 4,
        Format: PixelFormat.Bgra8888,
        CapturedUtc: DateTimeOffset.UnixEpoch,
        Source: "test");
}
