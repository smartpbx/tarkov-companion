using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Platform.Windows.Ocr;

/// <summary>The bounded outcome of one Windows OCR request.</summary>
public enum WindowsOcrExecutionStatus
{
    Complete,

    /// <summary>Every planned tile ran, nothing degraded the read, and no text came back.</summary>
    Empty,
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
/// <remarks>
/// Planned tiles are the whole tile plan. Attempted tiles are those whose native read actually
/// started, and only they appear in <see cref="Tiles"/>; a tile skipped because the deadline had
/// already passed is planned but not attempted. Completed tiles returned lines.
/// </remarks>
public sealed record WindowsOcrExecution(
    OcrResult Result,
    PixelRect SourceRegion,
    int SourceWidth,
    int SourceHeight,
    int Scale,
    int PlannedTileCount,
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

    /// <summary>Non-empty provider lines accepted under the line ceiling, before deduplication.</summary>
    public int ProviderLineCount { get; init; }

    /// <summary>Accepted lines whose text was cut to the per-line ceiling.</summary>
    public int TruncatedLineCount { get; init; }
}

/// <summary>Hard per-frame limits for the Windows OCR provider.</summary>
public sealed record WindowsMediaOcrOptions
{
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public long MaximumSourcePixels { get; init; } = 40_000_000;

    public long MaximumInputBytes { get; init; } = 192L * 1024 * 1024;

    /// <summary>
    /// The caller's source buffer plus both copies of the largest tile: the managed BGRA staging
    /// buffer and the SoftwareBitmap Windows reads. Windows' own recognizer working memory is not
    /// observable and is not included.
    /// </summary>
    public long MaximumEstimatedPeakBytes { get; init; } = 256L * 1024 * 1024;

    public int TileOverlap { get; init; } = 128;

    public int MaximumTiles { get; init; } = 64;

    /// <summary>The most provider lines one request accepts across all tiles.</summary>
    public int MaximumLines { get; init; } = 4_096;

    /// <summary>The longest text one accepted line keeps.</summary>
    public int MaximumLineTextLength { get; init; } = 1_024;

    internal int? MaximumTileDimension { get; init; }
}
