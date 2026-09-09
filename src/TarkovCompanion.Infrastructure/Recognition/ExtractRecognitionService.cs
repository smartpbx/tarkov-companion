using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class ExtractRecognitionService : IExtractRecognitionService
{
    private readonly IOcrEngine _ocrEngine;
    private readonly OcrTextNormalizer _normalizer;

    public ExtractRecognitionService(IOcrEngine ocrEngine, OcrTextNormalizer? normalizer = null)
    {
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _normalizer = normalizer ?? new OcrTextNormalizer();
    }

    public async Task<ExtractRecognitionResult> RecognizeAsync(
        CapturedImage image,
        MapDefinition currentMap,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentMap);
        CapturedImagePixels.Validate(image);
        var ocr = await _ocrEngine
            .RecognizeAsync(
                image,
                new OcrRequest(ScanContext.ExtractList, ContextRegions.For(image, ScanContext.ExtractList)),
                cancellationToken)
            .ConfigureAwait(false);

        var matched = new Dictionary<string, ActiveExtract>(StringComparer.Ordinal);
        var unmatched = new List<string>();
        foreach (var line in ocr.Lines)
        {
            var observed = NormalizeExtractLine(line.Text);
            if (observed.Length == 0 || IsHeader(observed))
            {
                continue;
            }

            var best = currentMap.Extracts
                .Select(extract => new
                {
                    Extract = extract,
                    Similarity = FuzzyTextSimilarity.Score(observed, _normalizer.NormalizeForLookup(extract.Name)),
                })
                .OrderByDescending(match => match.Similarity)
                .FirstOrDefault();
            if (best is null)
            {
                unmatched.Add(line.Text);
                continue;
            }

            var score = Math.Clamp((best.Similarity * 0.80) + (line.Confidence.Value * 0.20), 0, 1);
            if (score < RecognitionPolicy.AmbiguityThreshold)
            {
                unmatched.Add(line.Text);
                continue;
            }

            var recognition = new ActiveExtract(
                best.Extract.Id,
                best.Extract.Name,
                new Confidence(score),
                $"ocr:{ocr.Engine}; map={currentMap.Id}; observedUtc={image.CapturedUtc:O}");
            if (!matched.TryGetValue(recognition.ExtractId, out var existing) ||
                recognition.Confidence.Value > existing.Confidence.Value)
            {
                matched[recognition.ExtractId] = recognition;
            }
        }

        return new(
            matched.Values.OrderBy(extract => extract.Name, StringComparer.Ordinal).ToArray(),
            unmatched);
    }

    private string NormalizeExtractLine(string value)
    {
        var normalized = _normalizer.NormalizeForLookup(value);
        string[] statusSuffixes = [" closed", " pending", " available", " active"];
        foreach (var suffix in statusSuffixes)
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                return normalized[..^suffix.Length].Trim();
            }
        }

        return normalized;
    }

    private static bool IsHeader(string value) =>
        value is "extracts" or "exfil" or "find an extraction point" or "double press o";
}
