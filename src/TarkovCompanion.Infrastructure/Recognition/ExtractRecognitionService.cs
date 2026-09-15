using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class ExtractRecognitionService : IExtractRecognitionService
{
    private readonly IOcrEngine _ocrEngine;
    private readonly OcrTextNormalizer _normalizer;
    private readonly OcrPipelineOptions _pipeline;

    public ExtractRecognitionService(
        IOcrEngine ocrEngine,
        OcrTextNormalizer? normalizer = null,
        OcrPipelineOptions? pipelineOptions = null)
    {
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _normalizer = normalizer ?? new OcrTextNormalizer();
        _pipeline = OcrPipelineDeadline.Validate(pipelineOptions);
    }

    /// <summary>
    /// Reads the frame and then the panel, and matches both, under the frame's one deadline.
    /// </summary>
    /// <remarks>
    /// Inside a scan the deadline is the scan's, joined through its token; alone, this starts one.
    /// The two passes used to run on the caller's token after recognition had already spent its
    /// budget, so one capture could take the recognizer's thirty seconds and then these two
    /// readings' fifteen each. The deadline cutting the frame reading returns no extracts, as
    /// not available with <c>ocr_pipeline_timeout</c>; cutting the panel reading or the matching
    /// keeps what the frame already matched, with that code. The caller cancelling still throws.
    /// </remarks>
    public async Task<ExtractRecognitionResult> RecognizeAsync(
        CapturedImage image,
        MapDefinition currentMap,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentMap);
        CapturedImagePixels.Validate(image);
        using var deadline = OcrPipelineDeadline.Start(_pipeline, cancellationToken);
        OcrResult ocr;
        try
        {
            ocr = await _ocrEngine
                .RecognizeAsync(image, new OcrRequest(ScanContext.ExtractList), deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            return new([], [], [], [], false, OcrPipelineDeadline.DiagnosticCode);
        }

        if (!ocr.IsAvailable)
        {
            return new([], [], [], [], false, ocr.DiagnosticCode ?? "ocr_provider_unavailable");
        }

        var matched = new Dictionary<string, ObservedExtract>(StringComparer.Ordinal);
        var ambiguous = new List<string>();
        var unmatched = new List<string>();
        var catalogGaps = new List<string>();
        var transits = new List<string>();
        // Upstream lists an exit both factions can use once per faction, and the refresh stores
        // both rows. Two identical names tie at a similarity difference of exactly zero, which
        // is below MinimumRunnerUpLead, so the lead rule called every shared exit ambiguous and
        // discarded it — unless the read happened to be character-perfect, which on a real
        // panel it is not ("EXFILO1", "ZB-214"). Shared exits are the ones players most want,
        // and they were the ones that could never match.
        //
        // Collapsing them before ranking is the whole fix: one name is one candidate, and the
        // runner-up is then a genuinely different exit.
        var candidates = DistinctByName(currentMap.Extracts);

        // The frame's lines are matched before the panel is read, in the order both readings
        // were always matched in. A deadline that cuts the panel reading then keeps the exits
        // the frame already named, instead of cutting the matching of both.
        IReadOnlyList<OcrLine> lines = ocr.Lines;
        var degraded = OcrOutcome.Degradation(ocr);
        if (!MatchLines(ocr.Lines))
        {
            degraded ??= OcrPipelineDeadline.DiagnosticCode;
        }
        else if (!OcrOutcome.IsMemoryExhausted(ocr))
        {
            // A second reading of the panel on its own, untouched. Measured against a real screen:
            // reading the whole 3840x1080 frame returned 240 lines of which the exit names were not
            // among the first dozen, and reading the panel alone returned sixteen with every name
            // legible. The bright-half preparation that suits a full frame also hurts here, because
            // the names are drawn lighter and smaller than the slot labels beside them and thin
            // pale text is what a threshold eats first.
            //
            // Added to the full-frame reading rather than replacing it. If the panel has moved, or
            // this is some other screen entirely, the frame still answers.
            //
            // Both readings carry what degraded them, the frame first and then the panel. An
            // available frame that lost tiles or lines, and a panel reading that timed out or failed
            // outright, used to leave no trace on the result: the lines that did arrive matched, and
            // the scan called a half-read panel complete. A frame that ran out of memory is not
            // followed by a panel reading, as in the coordinator; it would ask for the same memory.
            try
            {
                var panel = await _ocrEngine
                    .RecognizeAsync(
                        image,
                        new OcrRequest(ScanContext.ExtractList, PanelRegion(image))
                        {
                            Preparation = OcrPreparation.AsCaptured,
                        },
                        deadline.Token)
                    .ConfigureAwait(false);
                degraded ??= OcrOutcome.Degradation(panel);
                if (panel.IsAvailable && panel.Lines.Count > 0)
                {
                    lines = [.. ocr.Lines, .. panel.Lines];
                    if (!MatchLines(panel.Lines))
                    {
                        degraded ??= OcrPipelineDeadline.DiagnosticCode;
                    }
                }
            }
            catch (OperationCanceledException) when (deadline.IsExpired)
            {
                degraded ??= OcrPipelineDeadline.DiagnosticCode;
            }
        }

        // Matches one reading's lines into the lists above, in order. False when the deadline
        // stopped it, with every line matched before that point kept.
        bool MatchLines(IReadOnlyList<OcrLine> source)
        {
            foreach (var line in source)
            {
                // Each line is scored against every exit on the map, over as many lines as a
                // reading returned.
                if (deadline.HasRunOut())
                {
                    return false;
                }

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

                var (observed, displayName, status) =
                    ParseExtractLine(ExtractLineMatcher.StripTrailingMeasure(text));
                if (observed.Length == 0 || IsHeader(observed))
                {
                    continue;
                }

                var ranked = candidates
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
                    if (kind == ExtractLineMatcher.RowKind.Extract)
                    {
                        AddCatalogGap(line, observed, displayName, status);
                    }
                    else
                    {
                        unmatched.Add(line.Text);
                    }

                    continue;
                }

                var best = ranked[0];
                // Same rule as the item resolver: an engine with no opinion is not an engine with a
                // bad one. Blended in as zero, the containment match that catches an abbreviated
                // exit name scored 0.688 against a threshold of 0.70 and never matched.
                var score = line.Confidence is { } reported
                    ? Math.Clamp((best.Similarity * 0.80) + (reported.Value * 0.20), 0, 1)
                    : best.Similarity;
                if (best.Similarity < 0.65 || score < RecognitionThresholds.Ambiguous)
                {
                    if (kind == ExtractLineMatcher.RowKind.Extract)
                    {
                        AddCatalogGap(line, observed, displayName, status);
                    }
                    else
                    {
                        unmatched.Add(line.Text);
                    }

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

            return true;
        }

        // A line with an EXFIL slot label is the game's own evidence that a row exists even when
        // the current catalog cannot name it. Keep that evidence, but cap it below the ordinary
        // match threshold: a missing catalog row and a badly read catalog row are deliberately
        // indistinguishable here. The map can list the offered name immediately and draw it only
        // when a reviewed coordinate exists.
        void AddCatalogGap(
            OcrLine line,
            string normalizedName,
            string displayName,
            ExtractStatus status)
        {
            var id = CatalogGapId(currentMap.Id, normalizedName);
            var recognition = new ObservedExtract(
                id,
                displayName,
                status,
                new Confidence(Math.Min(line.Confidence?.Value ?? 0.50, 0.60)),
                $"ocr:{ocr.Engine}; map={currentMap.Id}; status={status}; catalog=missing",
                image.CapturedUtc.ToUniversalTime());
            if (!matched.TryGetValue(id, out var existing) ||
                recognition.Confidence.Value > existing.Confidence.Value)
            {
                matched[id] = recognition;
            }

            catalogGaps.Add(line.Text);
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
        //
        // A degraded reading's code wins over "extracts_partial", as it does in the recogniser:
        // the unmatched and ambiguous lists already say which rows failed, and only the code can
        // say that rows may be missing from both.
        return new(
            active,
            observations,
            [.. ambiguous.Distinct(StringComparer.CurrentCultureIgnoreCase)],
            [.. unmatched.Distinct(StringComparer.CurrentCultureIgnoreCase)],
            true,
            degraded ?? (catalogGaps.Count > 0
                ? "extract_catalog_gap"
                : ambiguous.Count > 0 || unmatched.Count > 0
                    ? "extracts_partial"
                    : null))
        {
            Transits = [.. transits.Distinct(StringComparer.CurrentCultureIgnoreCase)],
            // As read, before StripRowPrefix and StripTrailingMeasure take anything off. What
            // this screen says about the raid clock is on a line that matching throws away.
            RawLines = [.. lines.Select(line => line.Text).Where(text => !string.IsNullOrWhiteSpace(text))],
            CatalogGapLines = [.. catalogGaps.Distinct(StringComparer.CurrentCultureIgnoreCase)],
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

    private (string NormalizedName, string DisplayName, ExtractStatus Status) ParseExtractLine(string value)
    {
        var displayName = value.Trim();
        var normalized = _normalizer.NormalizeForLookup(value);
        (string Word, ExtractStatus Status)[] statuses =
        [
            ("closed", ExtractStatus.Closed),
            ("pending", ExtractStatus.Pending),
            ("waiting", ExtractStatus.Pending),
            ("available", ExtractStatus.Active),
            ("active", ExtractStatus.Active),
            ("open", ExtractStatus.Active),
            ("unknown", ExtractStatus.Unknown),
        ];
        foreach (var (word, status) in statuses)
        {
            var suffix = " " + word;
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                var withoutStatus = displayName.EndsWith(word, StringComparison.OrdinalIgnoreCase)
                    ? displayName[..^word.Length].TrimEnd()
                    : displayName;
                return (normalized[..^suffix.Length].Trim(), withoutStatus, status);
            }
        }

        return (normalized, displayName, ExtractStatus.Active);
    }

    /// <summary>A stable local identity for the same unknown map/name across repeated scans.</summary>
    private static string CatalogGapId(string mapId, string normalizedName)
    {
        var bytes = Encoding.UTF8.GetBytes($"{mapId}\n{normalizedName}");
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        return $"catalog-gap:{mapId}:{digest[..16].ToLowerInvariant()}";
    }

    /// <summary>
    /// Whether the line reads as this exit's name and no other, character for character.
    /// </summary>
    /// <remarks>
    /// The one thing that breaks a tie between two names that only a character apart. Compared
    /// after normalisation on both sides and against the bracketed and unbracketed forms, so
    /// "Power line passage" is exact for "Power Line Passage (Flare)".
    /// </remarks>
    /// <summary>One row per exit name, keeping the one with a position where there is a choice.</summary>
    /// <remarks>
    /// The duplicate rows differ only by which faction the catalog lists them under, which is
    /// not something the extract panel says and not something a name match can use. Preferring
    /// a row that carries a position matters because that is what the map draws; beyond that
    /// the first is as good as the second.
    /// </remarks>
    public static IReadOnlyList<MapExtract> DistinctByName(IReadOnlyList<MapExtract> extracts) =>
        extracts
            .GroupBy(extract => extract.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(extract => extract.Position is not null) ?? group.First())
            .ToArray();

    private bool IsExact(string observed, string name) =>
        string.Equals(observed, _normalizer.NormalizeForLookup(name), StringComparison.Ordinal) ||
        string.Equals(
            observed,
            _normalizer.NormalizeForLookup(ExtractLineMatcher.WithoutQualifier(name)),
            StringComparison.Ordinal);

    private static bool IsHeader(string value) =>
        value is "extracts" or "exfil" or "find an extraction point" or "double press o";
}
