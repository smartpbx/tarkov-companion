using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.RecognitionTests;

public sealed class RecognitionConfidenceTests
{
    [Fact]
    public void HighConfidenceCandidateIsSelected()
    {
        var result = new RecognitionResult(
            ScanContext.SingleItem,
            [new RecognitionCandidate("graphics-card", "Graphics Card", new Confidence(0.94), "fixture OCR")],
            DateTimeOffset.UtcNow);

        Assert.Equal("graphics-card", result.Selected?.CanonicalId);
    }

    [Fact]
    public void AmbiguousCandidateIsNotSilentlySelected()
    {
        var result = new RecognitionResult(
            ScanContext.SingleItem,
            [new RecognitionCandidate("candidate", "Candidate", new Confidence(0.69), "noisy OCR")],
            DateTimeOffset.UtcNow);

        Assert.Null(result.Selected);
    }

    [Fact]
    public void SelectionUsesHighestConfidenceAndRejectsHighConfidenceNearTieRegardlessOfOrder()
    {
        var lower = new RecognitionCandidate("lower", "Lower", new Confidence(0.85), "fixture");
        var highest = new RecognitionCandidate("highest", "Highest", new Confidence(0.97), "fixture");
        var clear = new RecognitionResult(ScanContext.SingleItem, [lower, highest], DateTimeOffset.UtcNow);
        var nearTie = new RecognitionResult(
            ScanContext.SingleItem,
            [highest, new("runner", "Runner", new Confidence(0.94), "fixture")],
            DateTimeOffset.UtcNow);

        Assert.Equal("highest", clear.Selected?.CanonicalId);
        Assert.Null(nearTie.Selected);
    }
}
