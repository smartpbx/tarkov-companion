using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed record CoordinatedOcrResult(
    ContextDetection Detection,
    OcrResult FullFrame,
    OcrResult Contextual);

public sealed class OcrCoordinator
{
    private readonly IOcrEngine _engine;
    private readonly ScanContextDetector _contextDetector;

    public OcrCoordinator(IOcrEngine engine, ScanContextDetector contextDetector)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _contextDetector = contextDetector ?? throw new ArgumentNullException(nameof(contextDetector));
    }

    public async Task<CoordinatedOcrResult> RecognizeAsync(
        CapturedImage image,
        CancellationToken cancellationToken)
    {
        CapturedImagePixels.Validate(image);
        var fullFrame = await _engine
            .RecognizeAsync(image, new OcrRequest(ScanContext.Unknown), cancellationToken)
            .ConfigureAwait(false);
        var detection = _contextDetector.Detect(image, fullFrame);
        if (detection.Context == ScanContext.Unknown)
        {
            return new(detection, fullFrame, fullFrame);
        }

        var region = ContextRegions.For(image, detection.Context);
        var contextual = await _engine
            .RecognizeAsync(image, new OcrRequest(detection.Context, region), cancellationToken)
            .ConfigureAwait(false);
        return new(detection, fullFrame, contextual);
    }
}

public static class ContextRegions
{
    public static PixelRect For(CapturedImage image, ScanContext context) => context switch
    {
        ScanContext.SingleItem => CapturedImagePixels.FromNormalized(image, 0.18, 0.06, 0.64, 0.88),
        ScanContext.Container => CapturedImagePixels.FromNormalized(image, 0.03, 0.08, 0.94, 0.88),
        ScanContext.ExtractList => CapturedImagePixels.FromNormalized(image, 0.48, 0.04, 0.50, 0.92),
        ScanContext.FleaListings => CapturedImagePixels.FromNormalized(image, 0.06, 0.07, 0.90, 0.88),
        _ => new PixelRect(0, 0, image.Width, image.Height),
    };
}
