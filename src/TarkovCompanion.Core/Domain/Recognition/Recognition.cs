using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Recognition;

public enum PixelFormat
{
    Bgra8888,
    Rgba8888,
    Gray8,
}

public sealed record CapturedImage(
    ReadOnlyMemory<byte> Pixels,
    int Width,
    int Height,
    int Stride,
    PixelFormat Format,
    DateTimeOffset CapturedUtc,
    string Source);

public enum ScanContext
{
    Unknown,
    SingleItem,
    Container,
    ExtractList,
    FleaListings,
}

public sealed record PixelRect(int X, int Y, int Width, int Height);

public sealed record OcrRequest(ScanContext Context, PixelRect? Region = null, string Language = "eng");

/// <summary>
/// Confidence is an engine-normalized quality score in the inclusive 0..1 range.
/// It is suitable for ranking output from the same configured provider; it is not
/// a calibrated probability that the text is correct.
/// </summary>
public sealed record OcrLine(string Text, PixelRect Bounds, Confidence Confidence);

public sealed record OcrResult(
    IReadOnlyList<OcrLine> Lines,
    TimeSpan Duration,
    string Engine,
    bool IsAvailable = true,
    string? DiagnosticCode = null);

public sealed record OcrEngineAvailability(bool IsAvailable, string Provider, string? Reason = null);

public enum RecognitionDecision
{
    NoMatch,
    Candidate,
    Ambiguous,
    AutoSelected,
}

public static class RecognitionThresholds
{
    public const double AutoSelect = 0.90;

    public const double Ambiguous = 0.70;

    public const double Candidate = 0.45;

    public const double MinimumRunnerUpLead = 0.08;

    public static RecognitionDecision Classify(Confidence confidence) => confidence.Value switch
    {
        >= AutoSelect => RecognitionDecision.AutoSelected,
        >= Ambiguous => RecognitionDecision.Ambiguous,
        >= Candidate => RecognitionDecision.Candidate,
        _ => RecognitionDecision.NoMatch,
    };
}

public sealed record CanonicalItemReference(
    string Id,
    string DisplayName,
    IReadOnlyList<string>? Aliases = null);

public sealed record RecognitionCandidate(
    string CanonicalId,
    string DisplayName,
    Confidence Confidence,
    string Evidence,
    PixelRect? Bounds = null,
    int? Quantity = null);

public sealed record RecognitionResult(
    ScanContext Context,
    IReadOnlyList<RecognitionCandidate> Candidates,
    DateTimeOffset ObservedUtc,
    string? DiagnosticCode = null)
{
    public RecognitionCandidate? Selected
    {
        get
        {
            var ranked = Candidates
                .Where(candidate => candidate.Confidence.Value >= RecognitionThresholds.Candidate)
                .OrderByDescending(candidate => candidate.Confidence.Value)
                .ThenBy(candidate => candidate.DisplayName, StringComparer.Ordinal)
                .ToArray();
            if (ranked.Length == 0 || ranked[0].Confidence.Value < RecognitionThresholds.AutoSelect)
            {
                return null;
            }

            if (ranked.Length > 1 &&
                ranked[0].Confidence.Value - ranked[1].Confidence.Value < RecognitionThresholds.MinimumRunnerUpLead)
            {
                return null;
            }

            return ranked[0];
        }
    }
}

public sealed record ContainerCellIssue(
    int Row,
    int Column,
    PixelRect Bounds,
    string Reason,
    IReadOnlyList<RecognitionCandidate> Candidates);

public sealed record ContainerScanResult(
    IReadOnlyList<RecognitionCandidate> Items,
    long ApproximateValue,
    long IncludedOccupiedSlots,
    IReadOnlyList<string> DropFirstItemIds,
    Confidence Confidence,
    IReadOnlyList<ContainerCellIssue> UnresolvedCells,
    IReadOnlyList<ContainerCellIssue> AmbiguousCells,
    bool IsPartial,
    string? DiagnosticCode = null);

public sealed record FleaListing(long PriceRoubles, int? Quantity, Confidence Confidence, PixelRect Bounds);

public sealed record FleaRecognitionResult(
    IReadOnlyList<FleaListing> Listings,
    DateTimeOffset ObservedUtc,
    Confidence Confidence,
    bool ProviderAvailable,
    string? DiagnosticCode = null);
