using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>The bounded outcome of one portable OCR request.</summary>
public enum OcrExecutionStatus
{
    Complete,

    /// <summary>The provider ran, nothing degraded the read, and it found no text.</summary>
    Empty,
    Partial,
    TimedOut,
    Rejected,
    Unavailable,
    Failed,
}

/// <summary>
/// A machine-readable account of a Tesseract OCR request. It intentionally carries no source
/// path or pixels and does not claim that a provider quality score is calibrated accuracy.
/// </summary>
/// <remarks>
/// Tesseract reads a region as one tile. Planned is one for any non-empty region; attempted is
/// one only once native recognition actually started; completed is one only once it returned.
/// A rejection, a timeout while waiting for the provider, or a failed preparation therefore
/// reports zero attempted work instead of a tile that never ran.
/// </remarks>
public sealed record TesseractOcrExecution(
    OcrResult Result,
    PixelRect SourceRegion,
    int SourceWidth,
    int SourceHeight,
    int Scale,
    int PreparedWidth,
    int PreparedHeight,
    int PlannedTileCount,
    int AttemptedTileCount,
    int CompletedTileCount,
    long SourcePixelCount,
    long PreparedPixelCount,
    long EstimatedPeakBytes,
    TimeSpan Duration,
    string Provider,
    OcrExecutionStatus Status,
    string? DiagnosticCode)
{
    public const string SchemaVersion = "tarkov-companion.tesseract-ocr-execution.v1";

    /// <summary>Non-empty provider lines accepted under the line ceiling.</summary>
    public int ProviderLineCount { get; init; }

    /// <summary>Accepted lines whose text was cut to the per-line ceiling.</summary>
    public int TruncatedLineCount { get; init; }
}

internal static class OcrExecutionBudget
{
    public static string? Check(
        CapturedImage image,
        PixelRect region,
        int scale,
        long maximumSourcePixels,
        long maximumInputBytes,
        long maximumPreparedPixels,
        long maximumEstimatedPeakBytes,
        out long sourcePixels,
        out long preparedPixels,
        out long estimatedPeakBytes)
    {
        sourcePixels = checked((long)image.Width * image.Height);
        preparedPixels = checked((long)region.Width * region.Height * scale * scale);
        // The managed PGM and native decoder may each hold a prepared grayscale copy at the
        // same time. The source buffer already belongs to the caller and remains live too.
        estimatedPeakBytes = checked(image.Pixels.Length + (preparedPixels * 2) + 64);

        if (sourcePixels > maximumSourcePixels || image.Pixels.Length > maximumInputBytes)
        {
            return "ocr_input_limit_exceeded";
        }

        if (preparedPixels > maximumPreparedPixels)
        {
            return "ocr_prepared_pixel_limit_exceeded";
        }

        return estimatedPeakBytes > maximumEstimatedPeakBytes
            ? "ocr_memory_limit_exceeded"
            : null;
    }
}
