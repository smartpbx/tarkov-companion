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

/// <summary>
/// How a picture is prepared before the engine is allowed to read it.
/// </summary>
/// <remarks>
/// A screenshot of a game is not a scan of a document. On a 3840x1080 display the interface
/// text is around sixteen pixels tall in a frame of four million pixels, and the engine wants
/// closer to thirty; the rest of the frame is scenery, which it reads as words. The result was
/// lines of genuine noise with the occasional real string leaking through: "Bp B B", "pee tl",
/// "dhe,", and then "LOOT THIS".
///
/// Neither remedy is free, and which one matters is a question about real screenshots rather
/// than a thing to reason out, so both are options rather than assumptions.
/// </remarks>
/// <param name="Scale">
/// How many times larger to make the picture before reading it. One leaves it alone.
/// </param>
/// <param name="BrightTextOnly">
/// Whether to keep only what is brighter than the picture's own midpoint, which is how the
/// game draws every panel: light text on a dark ground. It costs dark-on-light text, of which
/// the interface has none, and removes most of what the scenery contributes.
/// </param>
public sealed record OcrPreparation(int Scale = 1, bool BrightTextOnly = false)
{
    /// <summary>The picture as it was taken.</summary>
    public static OcrPreparation AsCaptured { get; } = new();

    /// <summary>Scale bounded so that a large frame cannot be enlarged into an unreadable one.</summary>
    public int SafeScale => Math.Clamp(Scale, 1, 4);

    public bool IsAsCaptured => SafeScale == 1 && !BrightTextOnly;
}

public sealed record OcrRequest(ScanContext Context, PixelRect? Region = null, string Language = "eng")
{
    /// <summary>How to prepare the picture, which defaults to not preparing it at all.</summary>
    public OcrPreparation Preparation { get; init; } = OcrPreparation.AsCaptured;
}

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

public sealed record RecognitionCapabilityStatus(
    string Capability,
    bool IsAvailable,
    string Provider,
    string Detail);

public sealed record RecognitionSelfTestResult(
    DateTimeOffset CheckedUtc,
    bool IsReady,
    IReadOnlyList<RecognitionCapabilityStatus> Capabilities);

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
    /// <summary>
    /// Why the recogniser reached this answer, in enough detail to act on.
    /// </summary>
    /// <remarks>
    /// The diagnostic code says what happened; this says why. "context_unknown" on its own is
    /// true of a screenshot of a wall, a screenshot the text engine could not read at all, and
    /// a screenshot where two contexts scored equally, and those three call for entirely
    /// different work. The detector already establishes the difference and used to discard it.
    ///
    /// An init property rather than another positional parameter, because this record is
    /// constructed in several places that have nothing to add here.
    /// </remarks>
    public string? Detail { get; init; }

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
