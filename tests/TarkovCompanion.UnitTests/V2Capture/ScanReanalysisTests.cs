using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// #287: the HEALTH tab told from a stash, the frame held for Read as…, and the retention chip.
/// </summary>
/// <remarks>
/// The HEALTH-tab lines are hand-transcribed from Clayton's real 3840x1080 frame of 2026-09-22
/// (incoming/2026-09-22-screens, "HEALTH (all full, baseline)"), text only: the picture is never
/// committed. Bounds are its pixel positions, rounded.
/// </remarks>
public sealed class ScanReanalysisTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-24T00:00:00Z");

    /// <summary>What Windows OCR would plausibly return for the real HEALTH tab.</summary>
    internal static IReadOnlyList<OcrLine> HealthTab { get; } =
    [
        Line("OVERALL", 1040, 18), Line("GEAR", 1215, 18), Line("HEALTH", 1405, 18),
        Line("CUSTOMIZATION", 1560, 18), Line("SKILLS", 1745, 18), Line("MAP", 1920, 18),
        Line("TASKS", 2100, 18), Line("ACHIEVEMENTS", 2275, 18), Line("BACK", 2720, 14),
        Line("HEAD", 1356, 196), Line("35/35", 1398, 218),
        Line("THORAX", 1228, 296), Line("85/85", 1270, 318),
        Line("RIGHT ARM", 1060, 376), Line("60/60", 1102, 398),
        Line("STOMACH", 1228, 376), Line("70/70", 1270, 398),
        Line("LEFT ARM", 1394, 376), Line("60/60", 1436, 398),
        Line("RIGHT LEG", 1106, 538), Line("65/65", 1148, 560),
        Line("LEFT LEG", 1346, 538), Line("65/65", 1388, 560),
        Line("440/440", 1240, 780),
        Line("POCKETS", 1620, 280), Line("SPECIAL SLOTS", 1910, 280), Line("BACKPACK", 1620, 452),
        Line("Day Pack", 1700, 470), Line("POUCH", 1620, 814), Line("STASH", 2255, 64),
        Line("300/300", 1845, 846), Line("QUICK USE", 1526, 942),
    ];

    /// <summary>The Gear tab: the same caption row and containers, slots instead of limbs.</summary>
    private static IReadOnlyList<OcrLine> GearTab { get; } =
    [
        Line("OVERALL", 1040, 18), Line("GEAR", 1215, 18), Line("HEALTH", 1405, 18), Line("MAP", 1920, 18),
        Line("HEADWEAR", 1100, 200), Line("EARPIECE", 1300, 200), Line("FACE COVER", 1100, 320),
        Line("BODY ARMOR", 1300, 320), Line("TACTICAL RIG", 1620, 93), Line("POCKETS", 1620, 399),
        Line("BACKPACK", 1620, 510), Line("STASH", 2255, 64), Line("30/30", 1845, 846),
        Line("60/60", 1900, 846), Line("300/300", 1950, 846),
    ];

    [Fact]
    public void TheRealHealthTabIsTheHealthTab()
    {
        var reading = HealthScreenClassifier.Classify(Ocr(HealthTab));

        Assert.True(reading.IsHealthTab);
        Assert.Equal(["HEAD", "THORAX", "STOMACH", "LEFT ARM", "RIGHT ARM", "LEFT LEG", "RIGHT LEG"], reading.Limbs);
        Assert.Equal(1.0, reading.Confidence.Value);
    }

    /// <summary>Why the classifier exists: the anchors alone call the HEALTH tab a container.</summary>
    [Fact]
    public void TheAnchorsAloneReadTheHealthTabAsAContainer()
    {
        var detection = new ScanContextDetector().Detect(Frame(), Ocr(HealthTab));

        Assert.Equal(ScanContext.Container, detection.Context);
    }

    [Fact]
    public void TheGearTabIsNotTheHealthTab()
    {
        Assert.False(HealthScreenClassifier.Classify(Ocr(GearTab)).IsHealthTab);
    }

    [Fact]
    public void AnEngineThatJoinsCaptionAndValueStillCounts()
    {
        var joined = HealthScreenClassifier.Classify(Ocr(
        [
            Line("HEAD 35/35", 0, 0), Line("THORAX 85/85", 0, 40), Line("STOMACH 70/70", 0, 80),
            Line("LEFT ARM 60 / 60", 0, 120),
        ]));

        Assert.True(joined.IsHealthTab);
        Assert.Equal(4, joined.LimbValues);
    }

    [Fact]
    public void ThreeLimbsAreNotEnough()
    {
        Assert.False(HealthScreenClassifier.Classify(Ocr(
            [Line("HEAD", 0, 0), Line("THORAX", 0, 40), Line("STOMACH", 0, 80), Line("35/35", 0, 20), Line("85/85", 0, 60), Line("70/70", 0, 100)]))
            .IsHealthTab);
    }

    /// <summary>
    /// Armed as Loot, the HEALTH tab used to be measured as a loot grid. It is now placed as the
    /// character screen before any grid is measured, and the Loot page's progress is stopped.
    /// </summary>
    [Fact]
    public async Task AnArmedLootCaptureOfTheHealthTabIsPlacedAsTheCharacterScreen()
    {
        var pipeline = new CaptureRecognitionPipeline(
            new OcrCoordinator(new ScriptedOcr(HealthTab), new ScanContextDetector()),
            new GridPixelReconstructionBuilder(new NoIcons(), new LootScanFactFixtures.Catalog(), new ScriptedOcr([])),
            new ManualTimeProvider(Now));
        var stopped = false;
        pipeline.LootRecognitionStopped += (_, _) => stopped = true;

        var analysis = await pipeline.AnalyzeAsync(Request(ScanIntent.Loot), CancellationToken.None);

        Assert.Equal(RecognizedContext.HealthAndCharacter, analysis.DetectedContext);
        Assert.False(analysis.IsAmbiguous);
        Assert.Null(analysis.Grid);
        Assert.Equal(CaptureRecognitionPipeline.HealthTabDiagnostic, analysis.DiagnosticCode);
        Assert.True(stopped);
    }

    [Fact]
    public async Task TheFrameIsHeldAsACopyAndWipedWhenItExpires()
    {
        var clock = new ManualTimeProvider(Now);
        var inner = new Recording();
        using var memory = new ScanFrameMemory(inner, clock);
        var request = Request(ScanIntent.Loot);

        await memory.AnalyzeAsync(request, CancellationToken.None);
        // What the coordinator does to its own lease the moment analysis returns.
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(request.Image.Pixels, out var leased));
        leased.AsSpan().Clear();

        var copy = memory.TakeCopy(request.ArtifactId);
        Assert.NotNull(copy);
        Assert.Equal(-1, copy.Pixels.Span.IndexOfAnyExcept((byte)7));
        Assert.Equal(ScanIntent.Loot, memory.Describe(request.ArtifactId)?.ReadAs);
        Assert.Null(memory.TakeCopy("another-capture"));

        clock.Advance(ScanFrameMemory.HoldFor);
        Assert.Null(memory.Describe(request.ArtifactId));
        Assert.Null(memory.TakeCopy(request.ArtifactId));
    }

    /// <summary>#887: the frame is released on time even when nobody asks for it again.</summary>
    [Fact]
    public async Task AnUnreadFrameIsReleasedWhenItsHoldRunsOut()
    {
        var clock = new ManualTimeProvider(Now);
        using var memory = new ScanFrameMemory(new Recording(), clock);

        await memory.AnalyzeAsync(Request(ScanIntent.Loot), CancellationToken.None);
        Assert.True(memory.HoldsFrame);

        clock.Advance(ScanFrameMemory.HoldFor - TimeSpan.FromSeconds(1));
        Assert.True(memory.HoldsFrame);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(memory.HoldsFrame);
        Assert.Equal(0, clock.ScheduledTimerCount);
    }

    /// <summary>A newer frame restarts the hold; the replaced frame's timer must not cut it short.</summary>
    [Fact]
    public async Task ANewerFrameIsHeldForItsOwnFullTerm()
    {
        var clock = new ManualTimeProvider(Now);
        using var memory = new ScanFrameMemory(new Recording(), clock);

        await memory.AnalyzeAsync(Request(ScanIntent.Loot), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        await memory.AnalyzeAsync(Request(ScanIntent.Stash), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.True(memory.HoldsFrame);
        Assert.Equal(1, clock.ScheduledTimerCount);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(memory.HoldsFrame);
    }

    [Fact]
    public void AReleasedFrameOffersNoReadAsButSaysWhy()
    {
        var source = new ScanSourceViewModel(CaptureSourceKind.ClipboardImage, null, ScanIntent.Loot, frameHeld: false, reread: null);

        Assert.False(source.CanReadAs);
        Assert.Equal("Image released · capture it again", source.ReadAsUnavailable);
        Assert.Equal(ScanRetention.NotKept, source.RetentionLabel);
    }

    [Fact]
    public void AHeldFrameOffersEveryOtherIntent()
    {
        var source = new ScanSourceViewModel(
            CaptureSourceKind.GameWrittenScreenshot,
            null,
            ScanIntent.Loot,
            frameHeld: true,
            reread: _ => Task.FromResult(new ScanReadAsOutcome(true, "ok")));

        Assert.Equal([ScanIntent.Stash, ScanIntent.Flea, ScanIntent.QuestItems], source.ReadAsOptions.Select(option => option.Intent));
    }

    [Theory]
    [InlineData(CaptureSourceKind.ClipboardImage, false, "Image not kept")]
    [InlineData(CaptureSourceKind.GameWrittenScreenshot, false, "Image not kept · game file stays")]
    [InlineData(CaptureSourceKind.GameWrittenScreenshot, true, "Image not kept · game file tidied after 24 h")]
    public void TheRetentionChipSaysWhereThePixelsAre(CaptureSourceKind source, bool tidy, string expected)
    {
        var label = ScanRetention.Describe(source, new ScreenshotRetentionSettings(tidy, 24), heldInMemory: true);

        Assert.Equal(expected, label.Label);
        Assert.Contains("No copy is saved.", label.Detail, StringComparison.Ordinal);
    }

    internal static OcrLine Line(string text, int x, int y) => new(text, new(x, y, Math.Max(8, text.Length * 9), 14), null);

    private static OcrResult Ocr(IReadOnlyList<OcrLine> lines) => new(lines, TimeSpan.Zero, "windows-media-ocr-lines");

    private static CapturedImage Frame() =>
        new(Enumerable.Repeat((byte)7, 3840 * 1080).ToArray(), 3840, 1080, 3840, PixelFormat.Gray8, Now, "fixture://health-tab");

    private static CaptureAnalysisRequest Request(ScanIntent intent) => new(
        new CaptureSessionId(Guid.Parse("30000000-0000-0000-0000-000000000287")),
        "capture-health",
        0,
        intent,
        CaptureContextMetadata.Empty,
        Frame(),
        CaptureCorrelationId.New(),
        0);

    internal sealed class ScriptedOcr(IReadOnlyList<OcrLine> lines) : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(CapturedImage image, OcrRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Region is null ? Ocr(lines) : Ocr([]));
    }

    private sealed class Recording : ICaptureSessionPipeline
    {
        public Task<CaptureAnalysis> AnalyzeAsync(CaptureAnalysisRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CaptureAnalysis(new string('a', 64), RecognizedContext.Loot, false, true, null, new(0.9)));
    }

    internal sealed class NoIcons : IIconEvidenceCache
    {
        public Task<IconContentEvidenceAsset> StoreAsync(IconContentWriteRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IconContentEvidenceAsset?> GetAsync(IconEvidenceKey key, CancellationToken cancellationToken) =>
            Task.FromResult<IconContentEvidenceAsset?>(null);

        public Task<IReadOnlyList<IconContentEvidence>> ListEvidenceAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IconContentEvidence>>([]);
    }
}
