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

public sealed record OcrLine(string Text, PixelRect Bounds, Confidence Confidence);

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, TimeSpan Duration, string Engine);

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
    public RecognitionCandidate? Selected => Candidates.FirstOrDefault(x => x.Confidence.Value >= 0.90);
}

public sealed record ContainerScanResult(
    IReadOnlyList<RecognitionCandidate> Items,
    long ApproximateValue,
    long IncludedOccupiedSlots,
    IReadOnlyList<string> DropFirstItemIds,
    Confidence Confidence);

public sealed record FleaListing(long PriceRoubles, int? Quantity, Confidence Confidence, PixelRect Bounds);
