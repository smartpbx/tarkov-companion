using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.RecognitionTests.Ocr;

/// <summary>
/// One budget per captured frame, started by the scan and spent by every stage that reads the
/// frame: recognition, the dispatch's passes, its supplemental passes and its lookups.
/// </summary>
/// <remarks>
/// Measured on a manual clock. Every provider pass and lookup advances it by a set cost, and the
/// frame's timer fires inside the stage that crosses the budget, so each case knows exactly which
/// stage ran out and what had finished before it. The recognizer's coordinator and the container
/// recognizer each used to start a fresh thirty seconds, and the extract and flea passes and every
/// lookup ran on the caller's token, so one capture could spend a minute and then some.
/// </remarks>
public sealed class ScanFrameBudgetTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(30, "Complete|")]
    [InlineData(28, "Partial|ocr_pipeline_timeout")]
    public async Task TheBudgetIsTheWholeFrameNotEachStage(int budgetSeconds, string expected)
    {
        // Twenty-nine seconds in five stages, none longer than ten. Each fits a thirty-second
        // budget of its own many times over; only their sum decides.
        await using var harness = new ScanHarness(ScanScene.SingleItem, TimeSpan.FromSeconds(budgetSeconds));
        Cost(harness, ("RecognitionFrame", 10), ("RecognitionContext", 10), ("item", 3), ("price", 3), ("context", 3));

        var outcome = await harness.ScanAsync();

        Assert.Equal(expected, $"{outcome.Status}|{outcome.DiagnosticCode}");
        AssertOneBudget(harness, TimeSpan.FromSeconds(budgetSeconds));
    }

    [Fact]
    public async Task SingleItemLookupsSpendFromTheFramesBudgetAndKeepTheRecognition()
    {
        await using var harness = new ScanHarness(ScanScene.SingleItem, Budget);
        Cost(harness, ("RecognitionFrame", 10), ("RecognitionContext", 10), ("item", 5), ("price", 10), ("context", 1));

        var outcome = await harness.ScanAsync();

        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Equal("ocr_pipeline_timeout", outcome.DiagnosticCode);
        // Recognition finished inside the budget and is kept; the price lookup crossed it.
        Assert.Equal("item-1", outcome.Recognition.Selected?.CanonicalId);
        Assert.Null(outcome.EconomicValue);
        Assert.Null(outcome.Recommendation);
        Assert.Equal(["RecognitionFrame", "RecognitionContext", "item", "price"], harness.Stages.Select(stage => stage.Name));
        Assert.Equal(0, harness.RecommendationContext.Calls);
        // Recorded on the caller's token after the budget ran out, with the reason it stopped.
        Assert.Equal("ocr_pipeline_timeout", Assert.Single(harness.Events.Saved).DiagnosticCode);
        Assert.Same(outcome, harness.Publisher.Current);
        AssertOneBudget(harness, Budget);
    }

    [Fact]
    public async Task ExtractListSupplementalPanelPassIsCutByTheFrameAndTheFramesMatchesAreKept()
    {
        await using var harness = new ScanHarness(ScanScene.ExtractList, Budget);
        Cost(harness, ("RecognitionFrame", 5), ("RecognitionContext", 5), ("map", 5), ("ExtractFrame", 10), ("ExtractPanel", 10));

        var outcome = await harness.ScanAsync();

        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Equal("ocr_pipeline_timeout", outcome.DiagnosticCode);
        var extracts = Assert.IsType<ExtractRecognitionResult>(outcome.Extracts);
        Assert.True(extracts.ProviderAvailable);
        Assert.Equal("ocr_pipeline_timeout", extracts.DiagnosticCode);
        Assert.Contains(extracts.Extracts, extract => extract.ExtractId == "road");
        Assert.Equal(
            [ScanPass.RecognitionFrame, ScanPass.RecognitionContext, ScanPass.ExtractFrame, ScanPass.ExtractPanel],
            harness.Engine.Passes);
        // What the frame matched before the budget ran out still reaches the raid.
        Assert.Equal(1, harness.Recorder.ExtractsRecorded);
        Assert.Equal(["road"], harness.RaidState.Current.ActiveExtracts.Select(extract => extract.ExtractId));
        AssertOneBudget(harness, Budget);
    }

    [Fact]
    public async Task AnExtractFramePassCutByTheBudgetRecordsNoExitsInsteadOfClearingThem()
    {
        await using var harness = new ScanHarness(ScanScene.ExtractList, Budget);
        Cost(harness, ("RecognitionFrame", 10), ("RecognitionContext", 10), ("map", 5), ("ExtractFrame", 10));

        var outcome = await harness.ScanAsync();

        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Equal("ocr_pipeline_timeout", outcome.DiagnosticCode);
        Assert.False(Assert.IsType<ExtractRecognitionResult>(outcome.Extracts).ProviderAvailable);
        Assert.DoesNotContain(ScanPass.ExtractPanel, harness.Engine.Passes);
        Assert.Equal(0, harness.Recorder.ExtractsRecorded);
        AssertOneBudget(harness, Budget);
    }

    [Fact]
    public async Task FleaPassCutByTheFrameIsAPartialScanNotAMissingProvider()
    {
        await using var harness = new ScanHarness(ScanScene.FleaListings, Budget);
        Cost(harness, ("RecognitionFrame", 10), ("RecognitionContext", 10), ("Flea", 15));

        var outcome = await harness.ScanAsync();

        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Equal("ocr_pipeline_timeout", outcome.DiagnosticCode);
        var flea = Assert.IsType<FleaRecognitionResult>(outcome.Flea);
        Assert.Empty(flea.Listings);
        Assert.Equal("ocr_pipeline_timeout", flea.DiagnosticCode);
        Assert.Equal([ScanPass.RecognitionFrame, ScanPass.RecognitionContext, ScanPass.Flea], harness.Engine.Passes);
        AssertOneBudget(harness, Budget);
    }

    [Fact]
    public async Task ContainerPriceLookupsSpendFromTheFrameAndTheLastFinishedAnalysisIsKept()
    {
        await using var harness = new ScanHarness(ScanScene.Container, Budget);
        // Twenty seconds of passes, then four per price: the third lookup crosses the budget.
        Cost(harness, ("RecognitionFrame", 5), ("RecognitionContext", 5), ("ContainerGrid", 10), ("price", 4));

        var outcome = await harness.ScanAsync();

        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Equal("ocr_pipeline_timeout", outcome.DiagnosticCode);
        var container = Assert.IsType<ContainerScanResult>(outcome.Container);
        Assert.True(container.IsPartial);
        Assert.Equal("ocr_pipeline_timeout", container.DiagnosticCode);
        // The whole-grid analysis finished before the lookups and is what the scan keeps.
        Assert.Equal(3, container.Items.Count);
        Assert.Equal(3, harness.Stages.Count(stage => stage.Name == "price"));
        Assert.Equal(TimeSpan.FromSeconds(32), harness.Clock.Elapsed);
        AssertOneBudget(harness, Budget);
    }

    [Fact]
    public async Task ARecognitionPassCutByTheFrameEndsTheScanBeforeItsDispatchStarts()
    {
        await using var harness = new ScanHarness(ScanScene.Container, Budget);
        Cost(harness, ("RecognitionFrame", 20), ("RecognitionContext", 15));

        var outcome = await harness.ScanAsync();

        Assert.Equal(ScanContext.Container, outcome.Context);
        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Equal("ocr_pipeline_timeout", outcome.DiagnosticCode);
        Assert.Null(outcome.Container);
        Assert.Equal([ScanPass.RecognitionFrame, ScanPass.RecognitionContext], harness.Engine.Passes);
        Assert.Equal("ocr_pipeline_timeout", Assert.Single(harness.Events.Saved).DiagnosticCode);
        AssertOneBudget(harness, Budget);
    }

    [Fact]
    public async Task TheCallerCancellingDuringALookupStillThrowsAndRecordsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        await using var harness = new ScanHarness(ScanScene.SingleItem, Budget);
        harness.OnStage = name =>
        {
            if (name == "price")
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.ScanAsync(cancellation.Token));

        Assert.Empty(harness.Events.Saved);
        Assert.Null(harness.Publisher.Current);
    }

    private static void Cost(ScanHarness harness, params (string Stage, int Seconds)[] costs)
    {
        foreach (var (stage, seconds) in costs)
        {
            harness.Costs[stage] = TimeSpan.FromSeconds(seconds);
        }
    }

    /// <summary>
    /// Every pass and lookup was handed the one token, and the scan's frame is the only budget
    /// that was started.
    /// </summary>
    /// <remarks>
    /// A component that started a budget of its own would hand the provider or repository a token
    /// linked to it, not the frame's, so a single distinct token is the proof that nothing nested
    /// one. The clock sees the frame's timer and nothing else.
    /// </remarks>
    private static void AssertOneBudget(ScanHarness harness, TimeSpan budget)
    {
        Assert.NotEmpty(harness.Stages);
        var token = Assert.Single(harness.Stages.Select(stage => stage.Token).Distinct());
        Assert.True(token.CanBeCanceled);
        Assert.Equal([budget], harness.Clock.TimersCreated);
    }
}
