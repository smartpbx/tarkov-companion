using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// #712 1-1: every screenshot goes to every detector, the one that is sure gets it, and a tie or a
/// weak reading is "not sure" rather than a guess. Nothing here needs an intent armed.
/// </summary>
public sealed class ScreenDetectorRunnerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-26T12:00:00Z");
    private static readonly ScreenDetectorRunner Runner = new();

    [Theory]
    [InlineData(ScanContext.Container, true, true, false, ScreenKind.Loot)]
    [InlineData(ScanContext.Container, false, false, true, ScreenKind.Stash)]
    [InlineData(ScanContext.SingleItem, false, false, false, ScreenKind.Item)]
    [InlineData(ScanContext.FleaListings, false, false, false, ScreenKind.Flea)]
    [InlineData(ScanContext.QuestTasks, false, false, false, ScreenKind.Tasks)]
    [InlineData(ScanContext.ExtractList, true, false, false, ScreenKind.ExtractList)]
    public void EachScreenGoesToItsOwnDetector(ScanContext anchor, bool inRaid, bool lootLattice, bool stashLattice, ScreenKind expected)
    {
        var routing = Runner.Decide(new(Scores((anchor, 0.8)), inRaid, LootLatticeMeasured: lootLattice, StashLatticeMeasured: stashLattice));

        Assert.Equal(ScreenRoutingOutcome.Routed, routing.Outcome);
        Assert.Equal(expected, routing.Kind);
        Assert.Contains("detector", routing.Because, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHealthTabIsHealthThoughItsAnchorsSayContainer()
    {
        var routing = Runner.Decide(new(Scores((ScanContext.Container, 1.0)), InRaid: false, HealthTab: 0.9, StashLatticeMeasured: true));

        Assert.Equal(ScreenRoutingOutcome.Routed, routing.Outcome);
        Assert.Equal(ScreenKind.Health, routing.Kind);
    }

    [Fact]
    public void TwoConfidentDetectorsThatDisagreeAreUnsure()
    {
        var routing = Runner.Decide(new(Scores((ScanContext.FleaListings, 0.95), (ScanContext.SingleItem, 0.65)), InRaid: false));

        Assert.True(routing.IsUnsure);
        Assert.Null(routing.Kind);
        Assert.Contains("both claimed", routing.Because, StringComparison.Ordinal);
    }

    [Fact]
    public void ALeadUnderTheRunnerUpMarginIsUnsure()
    {
        var routing = Runner.Decide(new(Scores((ScanContext.FleaListings, 0.60), (ScanContext.SingleItem, 0.52)), InRaid: false));

        Assert.True(routing.IsUnsure);
    }

    [Fact]
    public void AWeakReadingIsUnsureAndKeepsItsGuesses()
    {
        var routing = Runner.Decide(new(Scores((ScanContext.FleaListings, 0.40), (ScanContext.SingleItem, 0.20)), InRaid: false));

        Assert.True(routing.IsUnsure);
        Assert.Equal([ScreenKind.Flea, ScreenKind.Item], routing.Guesses.Select(guess => guess.Kind));
    }

    /// <summary>
    /// #893's lift, now the loot detector's: a container placed by its words, in raid, with its
    /// lattice measured, is acted on at the Ambiguous floor. A lattice alone places nothing.
    /// </summary>
    [Theory]
    [InlineData(0.60, true, true, ScreenRoutingOutcome.Routed, RecognitionThresholds.Ambiguous)]
    [InlineData(0.60, true, false, ScreenRoutingOutcome.Routed, 0.60)]
    [InlineData(0.30, true, true, ScreenRoutingOutcome.Unsure, 0.30)]
    [InlineData(0.60, false, true, ScreenRoutingOutcome.Unsure, 0.0)]
    public void ALootLatticeLiftsOnlyAContainerItsWordsPlaced(
        double containerScore,
        bool inRaid,
        bool lattice,
        ScreenRoutingOutcome expected,
        double confidence)
    {
        var routing = Runner.Decide(new(Scores((ScanContext.Container, containerScore)), inRaid, LootLatticeMeasured: lattice));

        Assert.Equal(expected, routing.Outcome);
        Assert.Equal(confidence, routing.Confidence.Value, 3);
    }

    [Fact]
    public void AStashNeedsItsLatticeAsWellAsItsWords()
    {
        var routing = Runner.Decide(new(Scores((ScanContext.Container, 0.9)), InRaid: false));

        Assert.True(routing.IsUnsure);
    }

    [Fact]
    public void APositionScreenshotIsTheWorldViewNotUnrecognised()
    {
        var routing = Runner.Decide(new(Scores(), InRaid: true, NameCarriesPosition: true));

        Assert.Equal(ScreenRoutingOutcome.Background, routing.Outcome);
        Assert.Equal(ScreenKind.Position, routing.Kind);
    }

    /// <summary>The game names every in-raid screenshot with a position, the loot ones too.</summary>
    [Fact]
    public void APositionInTheNameNeverTiesWithAScreen()
    {
        var routing = Runner.Decide(new(Scores((ScanContext.Container, 0.8)), InRaid: true, LootLatticeMeasured: true, NameCarriesPosition: true));

        Assert.Equal(ScreenRoutingOutcome.Routed, routing.Outcome);
        Assert.Equal(ScreenKind.Loot, routing.Kind);
    }

    [Theory]
    [InlineData(ScreenKind.Trader)]
    [InlineData(ScreenKind.Hideout)]
    [InlineData(ScreenKind.GearTab)]
    [InlineData(ScreenKind.PostRaidSummary)]
    [InlineData(ScreenKind.Messenger)]
    public void AScreenNothingReadsYetNeverClaimsAFrame(ScreenKind kind)
    {
        var detector = Assert.Single(ScreenDetectors.All, candidate => candidate.Kind == kind);
        var everything = Scores(Enum.GetValues<ScanContext>().Select(context => (context, 1.0)).ToArray());

        Assert.Equal(0, detector.Score(new(everything, InRaid: true, HealthTab: 1, LootLatticeMeasured: true, StashLatticeMeasured: true, FleaRows: 9, NameCarriesPosition: true)).Confidence.Value);
    }

    [Fact]
    public void EveryScreenKindHasADetector()
    {
        Assert.Equal(Enum.GetValues<ScreenKind>().Order(), ScreenDetectors.All.Select(detector => detector.Kind).Order());
    }

    /// <summary>The flea words a real market page shows, and nothing armed.</summary>
    [Fact]
    public async Task AnUnarmedFleaFrameIsPlacedAndAnnounced()
    {
        var log = new ScreenRoutingLog();
        var pipeline = Pipeline([ScanReanalysisTests.Line("Flea Market", 200, 20), ScanReanalysisTests.Line("Filter by item", 200, 120)], log);

        var analysis = await pipeline.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(RecognizedContext.Flea, analysis.DetectedContext);
        Assert.False(analysis.IsAmbiguous);
        Assert.Equal(ScreenKind.Flea, analysis.Routing?.Kind);
        Assert.Equal(ScreenKind.Flea, Assert.Single(log.Recent).Routing.Kind);
    }

    [Fact]
    public async Task AnUnarmedHealthTabIsPlacedByTheDetectors()
    {
        var pipeline = Pipeline(ScanReanalysisTests.HealthTab, new ScreenRoutingLog());

        var analysis = await pipeline.AnalyzeAsync(Request(), CancellationToken.None);

        Assert.Equal(RecognizedContext.HealthAndCharacter, analysis.DetectedContext);
        Assert.Equal(ScreenKind.Health, analysis.Routing?.Kind);
        Assert.Null(analysis.Grid);
    }

    [Fact]
    public async Task AnUnreadFrameGoesToTheTrayAndAPositionShotDoesNot()
    {
        var log = new ScreenRoutingLog();
        using var tray = new UnrecognisedScreenTray(log);
        var pipeline = Pipeline([], log);

        var unread = await pipeline.AnalyzeAsync(Request(), CancellationToken.None);
        var position = await pipeline.AnalyzeAsync(Request() with { ArtifactId = "shot-2", NameCarriesPosition = true }, CancellationToken.None);

        Assert.True(unread.IsAmbiguous);
        Assert.Null(position.DetectedContext);
        Assert.False(position.IsAmbiguous);
        Assert.Equal("shot-1", Assert.Single(tray.Items).ArtifactId);
    }

    /// <summary>"Read as…" is the player saying what it was; the detectors are not asked.</summary>
    [Fact]
    public async Task ReadAsOverridesTheDetectors()
    {
        var log = new ScreenRoutingLog();
        var pipeline = Pipeline([ScanReanalysisTests.Line("Flea Market", 200, 20), ScanReanalysisTests.Line("Filter by item", 200, 120)], log);

        var analysis = await pipeline.AnalyzeAsync(Request() with { RequestedIntent = ScanIntent.Stash }, CancellationToken.None);

        Assert.Equal(ScreenRoutingOutcome.Chosen, analysis.Routing?.Outcome);
        Assert.Equal(ScreenKind.Stash, analysis.Routing?.Kind);
    }

    [Fact]
    public void TheSituationSaysWhichDetectorPlacedTheLastScreenshot()
    {
        var folder = new SituationFolder();
        var routing = Runner.Decide(new(Scores((ScanContext.FleaListings, 0.75)), InRaid: false));
        folder.Observe(new ScreenRoutingRecord(CaptureCorrelationId.New(), Session, "shot-1", Now, routing));

        var situation = folder.Fold(Now.AddSeconds(5), 1);

        Assert.NotNull(situation.LastScan);
        Assert.Equal(ScanContext.FleaListings, situation.LastScan.Kind);
        Assert.Contains("flea detector, 75%", situation.LastScan.Because, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsureScreenshotLeavesTheSituationAlone()
    {
        var folder = new SituationFolder();
        var routing = Runner.Decide(new(Scores((ScanContext.FleaListings, 0.3)), InRaid: false));
        folder.Observe(new ScreenRoutingRecord(CaptureCorrelationId.New(), Session, "shot-1", Now, routing));

        Assert.Null(folder.Fold(Now.AddSeconds(5), 1).LastScan);
    }

    /// <summary>The detector names the handler; an intent does only where no detector placed the frame.</summary>
    [Theory]
    [InlineData(ScreenRoutingOutcome.Routed, ScreenKind.Stash, ScanIntent.Loot, ScreenKind.Stash)]
    [InlineData(ScreenRoutingOutcome.Chosen, ScreenKind.Stash, ScanIntent.Flea, ScreenKind.Flea)]
    [InlineData(ScreenRoutingOutcome.Routed, ScreenKind.ExtractList, ScanIntent.ExtractsAndMap, ScreenKind.ExtractList)]
    public void AHandedOnFrameReachesItsScreensHandler(ScreenRoutingOutcome outcome, ScreenKind kind, ScanIntent intent, ScreenKind expected)
    {
        Assert.Equal(expected, CompositeKind(outcome, kind, intent));
    }

    private static readonly CaptureSessionId Session = new(Guid.Parse("30000000-0000-0000-0000-000000000712"));

    private static ScreenKind CompositeKind(ScreenRoutingOutcome outcome, ScreenKind routed, ScanIntent intent)
    {
        var analysis = new CaptureAnalysis(new string('a', 64), RecognizedContext.Stash, false, true, null, new(0.8))
        {
            Routing = new(outcome, routed, new(0.8), null, [], "fixture"),
        };
        var request = new CaptureHandoffRequest(
            Session,
            "shot-1",
            analysis,
            CaptureContextMetadata.Empty,
            CaptureCorrelationId.New(),
            CaptureSourceKind.GameWrittenScreenshot,
            Now,
            Now,
            null,
            CaptureDeliveryKind.WatchedFile,
            new(
                TarkovCompanion.Core.Domain.Evidence.EvidenceSourceClass.GameWrittenScreenshot,
                "fixture://capture",
                Now,
                TarkovCompanion.Core.Domain.Evidence.EvidenceConfidence.Unscored,
                new TarkovCompanion.Core.Domain.Evidence.ProducerIdentity("fixture", "1")),
            0,
            CaptureReviewAction.UseDetected,
            intent,
            new CaptureCorrection(CaptureReviewAction.UseDetected, intent, RecognizedContext.Stash, 0, Now, "fixture"));
        return CompositeCaptureResultHandoff.ScreenFor(request);
    }

    private static IReadOnlyDictionary<ScanContext, double> Scores(params (ScanContext Context, double Score)[] scores) =>
        scores.ToDictionary(score => score.Context, score => score.Score);

    private static CaptureRecognitionPipeline Pipeline(IReadOnlyList<OcrLine> lines, ScreenRoutingLog log) =>
        new(
            new OcrCoordinator(new ScanReanalysisTests.ScriptedOcr(lines), new ScanContextDetector()),
            new GridPixelReconstructionBuilder(new ScanReanalysisTests.NoIcons(), new LootScanFactFixtures.Catalog(), new ScanReanalysisTests.ScriptedOcr([])),
            new ManualTimeProvider(Now),
            routingLog: log);

    private static CaptureAnalysisRequest Request() => new(
        Session,
        "shot-1",
        0,
        ScanIntent.Auto,
        CaptureContextMetadata.Empty,
        new(Enumerable.Repeat((byte)7, 1920 * 1080).ToArray(), 1920, 1080, 1920, PixelFormat.Gray8, Now, "fixture://screen"),
        CaptureCorrelationId.New(),
        0);
}
