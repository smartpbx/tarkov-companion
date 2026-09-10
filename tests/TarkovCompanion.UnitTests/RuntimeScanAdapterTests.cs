using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using CoreScanUseCase = TarkovCompanion.Core.Abstractions.IScanUseCase;

namespace TarkovCompanion.UnitTests;

public sealed class RuntimeScanAdapterTests
{
    private static readonly DateTimeOffset ObservedUtc =
        new(2026, 9, 10, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreservesAmbiguityWithoutSelectingAnItem()
    {
        var candidates = new[]
        {
            new RecognitionCandidate("item-a", "Item A", new Confidence(0.94), "ocr"),
            new RecognitionCandidate("item-b", "Item B", new Confidence(0.90), "ocr"),
        };
        var adapter = new RecognitionScanAdapter(new StubRecognitionScanUseCase(
            Outcome(ScanCompletionStatus.Partial, candidates, "ambiguous_runner_up")));

        var result = await adapter.ScanAsync(CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.False(result.Succeeded);
        Assert.Null(result.CanonicalItemId);
        Assert.Null(result.ItemName);
        Assert.Equal(Confidence.Unknown, result.Confidence);
        Assert.Contains("no item was auto-selected", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsSelectedItemButWithholdsAdviceWithoutContextEvidence()
    {
        var selected = new RecognitionCandidate(
            "item-a",
            "Item A",
            new Confidence(0.96),
            "ocr");
        var adapter = new RecognitionScanAdapter(new StubRecognitionScanUseCase(
            Outcome(ScanCompletionStatus.Partial, [selected], "recommendation_context_unavailable")));

        var result = await adapter.ScanAsync(CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.True(result.Succeeded);
        Assert.Equal("item-a", result.CanonicalItemId);
        Assert.Equal("Item A", result.ItemName);
        Assert.Null(result.Recommendation);
        Assert.Equal(selected.Confidence, result.Confidence);
        Assert.Contains("withheld", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no pixels were persisted", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MapsUnavailableProviderWithoutClaimingAResult()
    {
        var adapter = new RecognitionScanAdapter(new StubRecognitionScanUseCase(
            Outcome(ScanCompletionStatus.Unavailable, [], "ocr_provider_unavailable")));

        var result = await adapter.ScanAsync(CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.False(result.Succeeded);
        Assert.Equal("local-ocr", result.Source);
        Assert.Contains("ocr_provider_unavailable", result.Detail, StringComparison.Ordinal);
    }

    private static ScanOutcome Outcome(
        ScanCompletionStatus status,
        IReadOnlyList<RecognitionCandidate> candidates,
        string diagnosticCode) => new(
        Guid.Parse("5290ecec-90a0-44f8-b41c-4d19d0d73e85"),
        status,
        ScanContext.SingleItem,
        ObservedUtc,
        new(ScanContext.SingleItem, candidates, ObservedUtc, diagnosticCode),
        null,
        null,
        null,
        null,
        [],
        diagnosticCode);

    private sealed class StubRecognitionScanUseCase(ScanOutcome outcome) : CoreScanUseCase
    {
        public Task<ScanOutcome> ScanAsync(ScanRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(request.Capture.AllowDesktopFallback);
            Assert.Equal("EscapeFromTarkov", request.Capture.WindowSelector);
            return Task.FromResult(outcome);
        }
    }
}
