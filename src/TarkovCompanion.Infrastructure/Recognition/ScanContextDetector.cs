using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed record ContextDetection(
    ScanContext Context,
    Confidence Confidence,
    double EstimatedUiScale,
    string Evidence);

public sealed class ScanContextDetector
{
    private const double DetectionThreshold = 0.55;
    private const double MinimumLead = 0.10;
    private readonly OcrTextNormalizer _normalizer;

    public ScanContextDetector(OcrTextNormalizer? normalizer = null)
    {
        _normalizer = normalizer ?? new OcrTextNormalizer();
    }

    public ContextDetection Detect(CapturedImage image, OcrResult ocr)
    {
        CapturedImagePixels.Validate(image);
        ArgumentNullException.ThrowIfNull(ocr);

        var frame = new PixelRect(0, 0, image.Width, image.Height);
        var visibleLines = ocr.Lines
            .Where(line => CapturedImagePixels.Intersects(frame, line.Bounds))
            .ToArray();
        var normalizedLines = visibleLines
            .Select(line => _normalizer.NormalizeForLookup(line.Text))
            .Where(line => line.Length > 0)
            .ToArray();
        var estimatedScale = EstimateScale(visibleLines);

        var scores = new Dictionary<ScanContext, double>
        {
            [ScanContext.SingleItem] = Score(normalizedLines,
                ("inspect", 0.65),
                ("durability", 0.25),
                ("ergonomics", 0.20),
                ("compatible with", 0.20),
                ("weight", 0.15)),
            [ScanContext.Container] = Score(normalizedLines,
                ("stash", 0.65),
                ("sorting table", 0.65),
                ("backpack", 0.25),
                ("tactical rig", 0.25),
                ("pockets", 0.20)),
            [ScanContext.ExtractList] = Score(normalizedLines,
                ("extracts", 0.70),
                ("exfil", 0.70),
                ("find an extraction point", 0.70),
                ("double press o", 0.55)),
            [ScanContext.FleaListings] = Score(normalizedLines,
                ("flea market", 0.75),
                ("filter by item", 0.45),
                ("purchase", 0.25),
                ("price", 0.20),
                ("trader rating", 0.20)),
        };

        var ranked = scores.OrderByDescending(pair => pair.Value).ToArray();
        var best = ranked[0];
        var runnerUp = ranked[1];
        if (best.Value < DetectionThreshold || best.Value - runnerUp.Value < MinimumLead)
        {
            return new(
                ScanContext.Unknown,
                new Confidence(Math.Clamp(1 - best.Value, 0, 1)),
                estimatedScale,
                $"no-unique-context; best={best.Key}:{best.Value:F2}; next={runnerUp.Key}:{runnerUp.Value:F2}");
        }

        return new(
            best.Key,
            new Confidence(best.Value),
            estimatedScale,
            $"ocr-anchors; score={best.Value:F2}; resolution={image.Width}x{image.Height}");
    }

    private static double Score(IReadOnlyList<string> lines, params (string Anchor, double Weight)[] anchors)
    {
        var score = anchors
            .Where(anchor => lines.Any(line => line.Contains(anchor.Anchor, StringComparison.Ordinal)))
            .Sum(anchor => anchor.Weight);
        return Math.Clamp(score, 0, 1);
    }

    private static double EstimateScale(IReadOnlyList<OcrLine> lines)
    {
        var heights = lines
            .Where(line => line.Bounds.Height is >= 8 and <= 80)
            .Select(line => line.Bounds.Height)
            .Order()
            .ToArray();
        if (heights.Length == 0)
        {
            return 1;
        }

        var median = heights[heights.Length / 2];
        return Math.Clamp(median / 20d, 0.75, 2.5);
    }
}
