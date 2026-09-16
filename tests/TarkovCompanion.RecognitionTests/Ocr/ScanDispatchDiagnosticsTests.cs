using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.RecognitionTests.Ocr;

/// <summary>
/// A degraded provider pass, anywhere in a scan, keeps its diagnostic all the way to the scan
/// that is published, persisted and returned, for every context a scan dispatches to.
/// </summary>
/// <remarks>
/// Each case runs the real recognizer, coordinator, extract, container and flea recognizers and
/// <c>ScanUseCase</c> over one screen, and degrades exactly one pass. The dispatches used to
/// decide a scan's status from their own reading alone, and the extract and flea recognizers
/// dropped the code of any reading that was still available, so a partial provider could publish
/// Complete through every context but a single item.
/// </remarks>
public sealed class ScanDispatchDiagnosticsTests
{
    public static TheoryData<ScanScene> Scenes => new()
    {
        ScanScene.SingleItem,
        ScanScene.ExtractList,
        ScanScene.Container,
        ScanScene.FleaListings,
    };

    public static TheoryData<ScanScene, ScanPass, bool> DegradedPasses
    {
        get
        {
            (ScanScene Scene, ScanPass Pass)[] passes =
            [
                (ScanScene.SingleItem, ScanPass.RecognitionFrame),
                (ScanScene.SingleItem, ScanPass.RecognitionContext),
                (ScanScene.ExtractList, ScanPass.RecognitionFrame),
                (ScanScene.ExtractList, ScanPass.RecognitionContext),
                (ScanScene.ExtractList, ScanPass.ExtractFrame),
                (ScanScene.ExtractList, ScanPass.ExtractPanel),
                (ScanScene.Container, ScanPass.RecognitionFrame),
                (ScanScene.Container, ScanPass.RecognitionContext),
                (ScanScene.Container, ScanPass.ContainerGrid),
                (ScanScene.Container, ScanPass.ContainerCell),
                (ScanScene.FleaListings, ScanPass.RecognitionFrame),
                (ScanScene.FleaListings, ScanPass.RecognitionContext),
                (ScanScene.FleaListings, ScanPass.Flea),
            ];
            var data = new TheoryData<ScanScene, ScanPass, bool>();
            foreach (var (scene, pass) in passes)
            {
                // Still available with its lines and a code, and not available at all.
                data.Add(scene, pass, true);
                data.Add(scene, pass, false);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public async Task ACompleteProviderStillPublishesACompleteScanForEveryDispatch(ScanScene scene)
    {
        // The other half of every case below: without it, a scan that could never be Complete
        // would pass them all.
        await using var harness = new ScanHarness(scene);

        var outcome = await harness.ScanAsync();

        Assert.Equal($"{ContextOf(scene)}|Complete|", $"{outcome.Context}|{outcome.Status}|{outcome.DiagnosticCode}");
        Assert.Null(Assert.Single(harness.Events.Saved).DiagnosticCode);
        Assert.DoesNotContain(outcome.Evidence, evidence => evidence.Code == "diagnostic");
    }

    [Theory]
    [MemberData(nameof(DegradedPasses))]
    public async Task ADegradedPassCanNeverLeaveTheScanComplete(ScanScene scene, ScanPass pass, bool stillAvailable)
    {
        await using var harness = new ScanHarness(scene);
        // A cell is read on its own only when the whole-grid pass left it unresolved.
        harness.Engine.HideLastCellFromGridPass = pass == ScanPass.ContainerCell;
        var code = stillAvailable ? "ocr_partial_tiles" : "ocr_frame_timeout";
        harness.Engine.Substitutes[pass] = clean => stillAvailable
            ? clean with { DiagnosticCode = code }
            : new OcrResult([], clean.Duration, clean.Engine, false, code);

        var outcome = await harness.ScanAsync();

        Assert.Contains(pass, harness.Engine.Passes);
        Assert.NotEqual(ScanCompletionStatus.Complete, outcome.Status);
        var diagnostic = Assert.IsType<string>(outcome.DiagnosticCode);
        Assert.Contains(code, diagnostic.Split(';', StringSplitOptions.TrimEntries));
        Assert.Equal(diagnostic, Assert.Single(harness.Events.Saved).DiagnosticCode);
        Assert.Contains(outcome.Evidence, evidence => evidence.Code == "diagnostic" && evidence.Detail == diagnostic);
        Assert.Same(outcome, harness.Publisher.Current);
    }

    [Fact]
    public async Task ADegradedItemReadKeepsItsCodeWhenThePriceLookupThenFindsNothing()
    {
        await using var harness = new ScanHarness(ScanScene.SingleItem);
        harness.Engine.Substitutes[ScanPass.RecognitionContext] = clean => clean with { DiagnosticCode = "ocr_partial_tiles" };
        harness.Items.HasPrice = false;

        var outcome = await harness.ScanAsync();

        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        // Both: what the reading lost, then what the lookup could not find. The lookup's code
        // used to replace the reading's.
        Assert.Equal("ocr_partial_tiles; canonical_item_or_price_unavailable", outcome.DiagnosticCode);
        Assert.Equal("item-1", outcome.Recognition.Selected?.CanonicalId);
        Assert.Null(outcome.EconomicValue);
        Assert.Null(outcome.Recommendation);
        Assert.Equal(outcome.DiagnosticCode, Assert.Single(harness.Events.Saved).DiagnosticCode);
    }

    [Fact]
    public async Task ADegradedItemReadKeepsItsCodeWhenTheRecommendationIsWithheld()
    {
        await using var harness = new ScanHarness(ScanScene.SingleItem);
        harness.Engine.Substitutes[ScanPass.RecognitionFrame] = clean => clean with { DiagnosticCode = "ocr_line_text_truncated" };
        harness.RecommendationContext.HasContext = false;

        var outcome = await harness.ScanAsync();

        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Equal("ocr_line_text_truncated; recommendation_context_unavailable", outcome.DiagnosticCode);
        Assert.Equal(200_000L, outcome.EconomicValue);
        Assert.Null(outcome.Recommendation);
    }

    [Fact]
    public async Task ADegradedItemReadThatSelectedNothingReportsTheReadingNotTheSelection()
    {
        await using var harness = new ScanHarness(ScanScene.SingleItem);
        // The item's name comes back as some other word in both passes, so nothing is selected.
        Func<OcrResult, OcrResult> fragment = clean => clean with
        {
            Lines = [.. clean.Lines.Select(line => line.Text == "Graphics Card" ? line with { Text = "Wrench" } : line)],
        };
        harness.Engine.Substitutes[ScanPass.RecognitionFrame] = fragment;
        harness.Engine.Substitutes[ScanPass.RecognitionContext] = clean => fragment(clean) with { DiagnosticCode = "ocr_partial_tiles" };

        var outcome = await harness.ScanAsync();

        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Null(outcome.Recognition.Selected);
        Assert.Equal("ocr_partial_tiles", outcome.DiagnosticCode);
        Assert.DoesNotContain(harness.Stages, stage => stage.Name is "item" or "price");
    }

    [Fact]
    public async Task ADegradedExtractPanelIsPartialEvenThoughEveryRowThatArrivedMatched()
    {
        await using var harness = new ScanHarness(ScanScene.ExtractList);
        harness.Engine.Substitutes[ScanPass.ExtractPanel] = clean => new OcrResult([], clean.Duration, clean.Engine, false, "ocr_provider_failed");

        var outcome = await harness.ScanAsync();

        var extracts = Assert.IsType<ExtractRecognitionResult>(outcome.Extracts);
        Assert.Empty(extracts.UnmatchedLines);
        Assert.Empty(extracts.AmbiguousLines);
        Assert.Contains(extracts.Extracts, extract => extract.ExtractId == "road");
        Assert.Equal("ocr_provider_failed", extracts.DiagnosticCode);
        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Equal("ocr_provider_failed", outcome.DiagnosticCode);
        // The exits the frame did read still reach the raid, as any partial reading's do.
        Assert.Equal(["road"], harness.RaidState.Current.ActiveExtracts.Select(extract => extract.ExtractId));
    }

    [Fact]
    public async Task APartialFleaReadWithRowsIsPartialNotComplete()
    {
        await using var harness = new ScanHarness(ScanScene.FleaListings);
        harness.Engine.Substitutes[ScanPass.Flea] = clean => clean with { DiagnosticCode = "ocr_line_limit_exceeded" };

        var outcome = await harness.ScanAsync();

        var flea = Assert.IsType<FleaRecognitionResult>(outcome.Flea);
        Assert.Equal(2, flea.Listings.Count);
        Assert.Equal("ocr_line_limit_exceeded", flea.DiagnosticCode);
        Assert.Equal(ScanCompletionStatus.Partial, outcome.Status);
        Assert.Equal("ocr_line_limit_exceeded", outcome.DiagnosticCode);
    }

    private static ScanContext ContextOf(ScanScene scene) => scene switch
    {
        ScanScene.SingleItem => ScanContext.SingleItem,
        ScanScene.ExtractList => ScanContext.ExtractList,
        ScanScene.Container => ScanContext.Container,
        ScanScene.FleaListings => ScanContext.FleaListings,
        _ => throw new ArgumentOutOfRangeException(nameof(scene)),
    };
}
