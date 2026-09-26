using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Application.Services.CaptureSessions;

/// <summary>
/// Signals that a configured capture source cannot currently be observed. The exception carries
/// no local path so it is safe to route through application health and diagnostics.
/// </summary>
public sealed class CaptureSourceUnavailableException()
    : IOException("The configured capture source is no longer available.");

public sealed class ScreenshotFileCaptureSource(string path, IScreenshotImageLoader loader) : ICaptureContentSource
{
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("A screenshot path is required.", nameof(path))
        : path;
    private readonly IScreenshotImageLoader _loader = loader ?? throw new ArgumentNullException(nameof(loader));

    public CaptureSourceKind SourceKind => CaptureSourceKind.GameWrittenScreenshot;

    /// <summary>[#712 1-1] The name is shaped like an in-raid shot, position blocks and all.</summary>
    /// <remarks>Read from the name only; the path itself never leaves this source.</remarks>
    public bool NameCarriesPosition { get; } =
        ScreenshotFilenameParser.Classify(path ?? string.Empty) == ScreenshotNameKind.InRaid;

    public async ValueTask<CaptureSourceReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        var image = await _loader.LoadAsync(_path, cancellationToken).ConfigureAwait(false);
        return image is null
            // Settlement and decode are separate observations. The file can disappear, change,
            // or still decode as incomplete after the watcher proved one stable shared read.
            // Each loader invocation is already memory/deadline bounded; the session's small
            // attempt cap is the owner of retry across those transient outcomes.
            ? CaptureSourceReadResult.Failure("decode_incomplete_or_unavailable", retryable: true)
            : CaptureSourceReadResult.Success(new(image));
    }

    public void Dispose()
    {
    }
}

public sealed class VisiblePixelCaptureSource(
    IScreenCaptureService capture,
    CaptureRequest request,
    CaptureSourceKind sourceKind = CaptureSourceKind.ExternalVisiblePixelCapture) : ICaptureContentSource
{
    private readonly IScreenCaptureService _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly CaptureRequest _request = request ?? throw new ArgumentNullException(nameof(request));

    public CaptureSourceKind SourceKind { get; } = Enum.IsDefined(sourceKind)
        ? sourceKind
        : throw new ArgumentOutOfRangeException(nameof(sourceKind));

    public async ValueTask<CaptureSourceReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var image = await _capture.CaptureAsync(_request, cancellationToken).ConfigureAwait(false);
            return CaptureSourceReadResult.Success(new(image));
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
        {
            return CaptureSourceReadResult.Failure("visible_capture_unavailable", retryable: false);
        }
    }

    public void Dispose()
    {
    }
}

public sealed class MemoryCaptureSource : ICaptureContentSource
{
    private CapturePixelLease? _pixels;

    public MemoryCaptureSource(CapturedImage image, CaptureSourceKind sourceKind)
    {
        _pixels = new(image);
        SourceKind = Enum.IsDefined(sourceKind)
            ? sourceKind
            : throw new ArgumentOutOfRangeException(nameof(sourceKind));
    }

    public CaptureSourceKind SourceKind { get; }

    // Unlike file and visible-screen sources, this buffer exists before the queue reads it.
    // Admission uses the exact lease size so queued decoded pixels cannot bypass the budget.
    internal long RetainedPixelBytes => Volatile.Read(ref _pixels)?.ByteLength ?? 0;

    public ValueTask<CaptureSourceReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = Interlocked.Exchange(ref _pixels, null);
        return ValueTask.FromResult(pixels is null
            ? CaptureSourceReadResult.Failure("capture_source_already_consumed", retryable: false)
            : CaptureSourceReadResult.Success(pixels));
    }

    public void Dispose() => Interlocked.Exchange(ref _pixels, null)?.Dispose();
}
