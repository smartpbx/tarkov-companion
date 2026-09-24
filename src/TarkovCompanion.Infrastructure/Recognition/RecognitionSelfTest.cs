using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class RecognitionSelfTest(
    IOcrEngine ocrEngine,
    IRecognitionCatalogRepository catalogRepository,
    TimeProvider? timeProvider = null) : IRecognitionSelfTest
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<RecognitionSelfTestResult> RunAsync(CancellationToken cancellationToken)
    {
        var capabilities = new List<RecognitionCapabilityStatus>();
        var ocr = ocrEngine is IOcrEngineStatus status
            ? status.Availability
            : OcrEngineAvailability.Unavailable(ocrEngine.GetType().Name, OcrUnavailableReason.NoAvailabilityState, "Provider does not expose availability state.");
        capabilities.Add(new(
            "offline-ocr",
            ocr.IsAvailable,
            ocr.Provider,
            ocr.IsAvailable
                ? "Provider initialized locally; recognition accuracy is measured separately by rendered-pixel tests."
                : ocr.Reason ?? "Provider unavailable."));

        var catalog = await catalogRepository.LoadAsync(cancellationToken).ConfigureAwait(false);
        capabilities.Add(new(
            "canonical-item-catalog",
            catalog.Count > 0,
            catalogRepository.GetType().Name,
            catalog.Count > 0 ? "Loaded " + catalog.Count + " canonical items." : "The canonical item catalog is empty."));
        capabilities.Add(new(
            "icon-fallback",
            false,
            "disabled",
            "No licensed runtime-cached fingerprint repository is configured; no icon result will be fabricated."));

        return new(
            _timeProvider.GetUtcNow(),
            ocr.IsAvailable && catalog.Count > 0,
            capabilities);
    }
}
