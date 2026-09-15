using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Platform.Windows.Ocr;

/// <summary>The bounded outcome of one Windows OCR request.</summary>
public enum WindowsOcrExecutionStatus
{
    Complete,
    Partial,
    TimedOut,
    Rejected,
    Unavailable,
    Failed,
}

/// <summary>What one native-resolution tile actually attempted.</summary>
public sealed record WindowsOcrTileExecution(
    int Ordinal,
    PixelRect SourceRegion,
    int Width,
    int Height,
    int Scale,
    TimeSpan Duration,
    WindowsOcrExecutionStatus Status,
    string? DiagnosticCode);

/// <summary>
/// A machine-readable account of one Windows OCR request, including work that only partially
/// completed. It contains text results and pixel geometry, never source pixels or a source path.
/// </summary>
public sealed record WindowsOcrExecution(
    OcrResult Result,
    PixelRect SourceRegion,
    int SourceWidth,
    int SourceHeight,
    int Scale,
    int TileCount,
    int AttemptedTileCount,
    int CompletedTileCount,
    long SourcePixelCount,
    long EstimatedPeakBytes,
    TimeSpan Duration,
    string Provider,
    WindowsOcrExecutionStatus Status,
    string? DiagnosticCode,
    IReadOnlyList<WindowsOcrTileExecution> Tiles)
{
    public const string SchemaVersion = "tarkov-companion.windows-ocr-execution.v1";
}

/// <summary>Hard per-frame limits for the Windows OCR provider.</summary>
public sealed record WindowsMediaOcrOptions
{
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public long MaximumSourcePixels { get; init; } = 40_000_000;

    public long MaximumInputBytes { get; init; } = 192L * 1024 * 1024;

    public long MaximumEstimatedPeakBytes { get; init; } = 256L * 1024 * 1024;

    public int TileOverlap { get; init; } = 128;

    public int MaximumTiles { get; init; } = 64;

    internal int? MaximumTileDimension { get; init; }
}
