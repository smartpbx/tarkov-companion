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
            .RecognizeAsync(image, new OcrRequest(ScanContext.ExtractList), cancellationToken)
            .ConfigureAwait(false);
        if (!ocr.IsAvailable)
        {
            return new([], [], [], [], false, ocr.DiagnosticCode ?? "ocr_provider_unavailable");
        }

        var matched = new Dictionary<string, ObservedExtract>(StringComparer.Ordinal);
        var ambiguous = new List<string>();
        var unmatched = new List<string>();
        foreach (var line in ocr.Lines)
        {
            var (observed, status) = ParseExtractLine(line.Text);
            if (observed.Length == 0 || IsHeader(observed))
            {
                continue;
            }

            var ranked = currentMap.Extracts
                .Select(extract => new
                {
                    Extract = extract,
                    Similarity = FuzzyTextSimilarity.Score(observed, _normalizer.NormalizeForLookup(extract.Name)),
                })
                .OrderByDescending(match => match.Similarity)
                .ThenBy(match => match.Extract.Name, StringComparer.Ordinal)
                .Take(2)
                .ToArray();
            if (ranked.Length == 0)
            {
                unmatched.Add(line.Text);
                continue;
            }

            var best = ranked[0];
            var score = Math.Clamp((best.Similarity * 0.80) + (line.Confidence.Value * 0.20), 0, 1);
            if (best.Similarity < 0.65 || score < RecognitionThresholds.Ambiguous)
            {
                unmatched.Add(line.Text);
                continue;
            }

            if (ranked.Length > 1 &&
                best.Similarity - ranked[1].Similarity < RecognitionThresholds.MinimumRunnerUpLead)
            {
                ambiguous.Add(line.Text);
                continue;
            }

            var recognition = new ObservedExtract(
                best.Extract.Id,
                best.Extract.Name,
                status,
                new Confidence(score),
                $"ocr:{ocr.Engine}; map={currentMap.Id}; status={status}",
                image.CapturedUtc.ToUniversalTime());
            if (!matched.TryGetValue(recognition.ExtractId, out var existing) ||
                recognition.Confidence.Value > existing.Confidence.Value)
            {
                matched[recognition.ExtractId] = recognition;
            }
        }

        var observations = matched.Values
            .OrderBy(extract => extract.Name, StringComparer.Ordinal)
            .ToArray();
        var active = observations
            .Where(extract => extract.Status == ExtractStatus.Active)
            .Select(extract => new ActiveExtract(
                extract.ExtractId,
                extract.Name,
                extract.Confidence,
                $"{extract.Source}; observedUtc={extract.ObservedUtc:O}"))
            .ToArray();
        return new(
            active,
            observations,
            ambiguous,
            unmatched,
            true,
            ambiguous.Count > 0 || unmatched.Count > 0 ? "extracts_partial" : null);
    }

    private (string Name, ExtractStatus Status) ParseExtractLine(string value)
    {
        var normalized = _normalizer.NormalizeForLookup(value);
        (string Suffix, ExtractStatus Status)[] statuses =
        [
            (" closed", ExtractStatus.Closed),
            (" pending", ExtractStatus.Pending),
            (" waiting", ExtractStatus.Pending),
            (" available", ExtractStatus.Active),
            (" active", ExtractStatus.Active),
            (" open", ExtractStatus.Active),
            (" unknown", ExtractStatus.Unknown),
        ];
        foreach (var (suffix, status) in statuses)
        {
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (normalized[..^suffix.Length].Trim(), status);
            }
        }

        return (normalized, ExtractStatus.Active);
    }

    private static bool IsHeader(string value) =>
        value is "extracts" or "exfil" or "find an extraction point" or "double press o";
}
