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

        // A second reading of the panel on its own, untouched. Measured against a real screen:
        // reading the whole 3840x1080 frame returned 240 lines of which the exit names were not
        // among the first dozen, and reading the panel alone returned sixteen with every name
        // legible. The bright-half preparation that suits a full frame also hurts here, because
        // the names are drawn lighter and smaller than the slot labels beside them and thin
        // pale text is what a threshold eats first.
        //
        // Added to the full-frame reading rather than replacing it. If the panel has moved, or
        // this is some other screen entirely, the frame still answers.
        var panel = await _ocrEngine
            .RecognizeAsync(
                image,
                new OcrRequest(ScanContext.ExtractList, PanelRegion(image))
                {
                    Preparation = OcrPreparation.AsCaptured,
                },
                cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<OcrLine> lines = ocr.Lines;
        if (panel.IsAvailable && panel.Lines.Count > 0)
        {
            lines = [.. ocr.Lines, .. panel.Lines];
        }

        var matched = new Dictionary<string, ObservedExtract>(StringComparer.Ordinal);
        var ambiguous = new List<string>();
        var unmatched = new List<string>();
        var transits = new List<string>();
        foreach (var line in lines)
        {
            // The slot label comes off first and says what the row is. Every row on this panel
            // carries one, on the same line as the name, and against a catalog entry that has
            // none it is eight to ten characters of dead weight that sank every short name.
            var (text, kind) = ExtractLineMatcher.StripRowPrefix(line.Text);
            if (kind == ExtractLineMatcher.RowKind.Transit)
            {
                // A way to another map, which no extract catalog contains. Kept as its own
                // list rather than matched and failed.
                if (text.Length > 0)
                {
                    transits.Add(text);
                }

                continue;
            }

            var (observed, status) = ParseExtractLine(ExtractLineMatcher.StripTrailingMeasure(text));
            if (observed.Length == 0 || IsHeader(observed))
            {
                continue;
            }

            var ranked = currentMap.Extracts
                .Select(extract => new
                {
                    Extract = extract,
                    Similarity = ExtractLineMatcher.Score(
                        observed,
                        _normalizer.NormalizeForLookup(extract.Name),
                        _normalizer.NormalizeForLookup(ExtractLineMatcher.WithoutQualifier(extract.Name))),
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

            // Two exits whose names differ by one character are near-identical to a fuzzy
            // score however clean the reading was, so the lead rule discarded both of them
            // every time. Woods has ZB-014 and ZB-016 and Customs has two dorms; a line that
            // reads as one of them exactly is not ambiguous, it is that one.
            if (ranked.Length > 1 &&
                best.Similarity - ranked[1].Similarity < RecognitionThresholds.MinimumRunnerUpLead &&
                !IsExact(observed, best.Extract.Name))
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
        // Two readings of the same screen produce the same line twice. The matched exits are
        // already keyed by id; these are not, and a diagnostic that says the same thing twice
        // reads as two problems.
        return new(
            active,
            observations,
            [.. ambiguous.Distinct(StringComparer.CurrentCultureIgnoreCase)],
            [.. unmatched.Distinct(StringComparer.CurrentCultureIgnoreCase)],
            true,
            ambiguous.Count > 0 || unmatched.Count > 0 ? "extracts_partial" : null)
        {
            Transits = [.. transits.Distinct(StringComparer.CurrentCultureIgnoreCase)],
        };
    }

    /// <summary>
    /// Where the extract panel is drawn, measured from the top-right corner.
    /// </summary>
    /// <remarks>
    /// Measured, not assumed: x 0.850..0.995 and y 0.005..0.50 of a 3840x1080 frame, with ten
    /// rows on it. Expressed here in units of frame height from the right and top edges,
    /// because the installation it was measured on runs 32:9 and a fraction of the width would
    /// put this somewhere else entirely on an ordinary screen. The same mistake put the health
    /// widget in a patch of grass.
    ///
    /// Generous on all three sides. The panel grows downwards with the number of exits, and the
    /// cost of being too wide is a few milliseconds while the cost of being too narrow is a
    /// screen that reads as empty.
    /// </remarks>
    public static PixelRect PanelRegion(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var width = Math.Min(image.Width, (int)Math.Round(image.Height * 0.60));
        var height = Math.Min(image.Height, (int)Math.Round(image.Height * 0.60));
        return new(image.Width - width, 0, width, height);
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

    /// <summary>
    /// Whether the line reads as this exit's name and no other, character for character.
    /// </summary>
    /// <remarks>
    /// The one thing that breaks a tie between two names that only a character apart. Compared
    /// after normalisation on both sides and against the bracketed and unbracketed forms, so
    /// "Power line passage" is exact for "Power Line Passage (Flare)".
    /// </remarks>
    private bool IsExact(string observed, string name) =>
        string.Equals(observed, _normalizer.NormalizeForLookup(name), StringComparison.Ordinal) ||
        string.Equals(
            observed,
            _normalizer.NormalizeForLookup(ExtractLineMatcher.WithoutQualifier(name)),
            StringComparison.Ordinal);

    private static bool IsHeader(string value) =>
        value is "extracts" or "exfil" or "find an extraction point" or "double press o";
}
