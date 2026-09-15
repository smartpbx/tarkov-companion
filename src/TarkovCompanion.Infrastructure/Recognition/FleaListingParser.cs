using System.Globalization;
using System.Text.RegularExpressions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class FleaListingParser
{
    private static readonly Regex PricePattern = new(
        @"(?<price>\d[\d\s,.\u00a0]*)\s*(?:₽|RUB(?:LES?)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex QuantityPattern = new(
        @"(?:^|\s)(?:x|qty\s*:?)\s*(?<quantity>\d{1,4})(?:\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly OcrTextNormalizer _normalizer;

    public FleaListingParser(OcrTextNormalizer? normalizer = null)
    {
        _normalizer = normalizer ?? new OcrTextNormalizer();
    }

    public IReadOnlyList<FleaListing> ParseVisible(
        OcrResult ocr,
        CapturedImage image,
        CancellationToken cancellationToken = default)
    {
        var listings = new List<FleaListing>();
        ParseVisibleInto(listings, ocr, image, cancellationToken);
        return Order(listings);
    }

    /// <summary>
    /// Parses rows into <paramref name="listings"/> as it goes, so work stopped between lines
    /// keeps the rows it already parsed.
    /// </summary>
    internal void ParseVisibleInto(
        List<FleaListing> listings,
        OcrResult ocr,
        CapturedImage image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(ocr);
        CapturedImagePixels.Validate(image);
        var frame = new PixelRect(0, 0, image.Width, image.Height);

        foreach (var line in ocr.Lines.Where(line => CapturedImagePixels.Intersects(frame, line.Bounds)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var priceMatch = PricePattern.Match(line.Text);
            if (!priceMatch.Success)
            {
                continue;
            }

            var priceText = _normalizer.NormalizeNumber(priceMatch.Groups["price"].Value);
            if (!long.TryParse(priceText, NumberStyles.None, CultureInfo.InvariantCulture, out var price) || price <= 0)
            {
                continue;
            }

            var quantityMatch = QuantityPattern.Match(line.Text);
            int? quantity = null;
            if (quantityMatch.Success &&
                int.TryParse(
                    quantityMatch.Groups["quantity"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedQuantity) &&
                parsedQuantity > 0)
            {
                quantity = parsedQuantity;
            }

            // A row read by an engine that does not score is still a row that was read. The
            // small discount is about the parse rather than the reading, so it applies either
            // way, and a missing opinion becomes a confident one rather than a worthless one.
            var confidence = new Confidence(Math.Clamp((line.Confidence?.Value ?? 1) * 0.98, 0, 1));
            listings.Add(new(price, quantity, confidence, line.Bounds));
        }
    }

    internal static IReadOnlyList<FleaListing> Order(IEnumerable<FleaListing> listings) =>
        listings
            .OrderBy(listing => listing.Bounds.Y)
            .ThenBy(listing => listing.Bounds.X)
            .ToArray();
}

public sealed class FleaRecognitionService : IFleaRecognitionService
{
    private readonly IOcrEngine _ocrEngine;
    private readonly FleaListingParser _parser;
    private readonly OcrPipelineOptions _pipeline;

    public FleaRecognitionService(
        IOcrEngine ocrEngine,
        FleaListingParser? parser = null,
        OcrPipelineOptions? pipelineOptions = null)
    {
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _parser = parser ?? new FleaListingParser();
        _pipeline = OcrPipelineDeadline.Validate(pipelineOptions);
    }

    /// <summary>Reads and parses the visible rows under the frame's one deadline.</summary>
    /// <remarks>
    /// Inside a scan the deadline is the scan's, joined through its token; alone, this starts one.
    /// The deadline cutting the reading returns no rows, as not available with
    /// <c>ocr_pipeline_timeout</c>; cutting the parse keeps the rows already parsed, with that
    /// code. The caller cancelling still throws.
    /// </remarks>
    public async Task<FleaRecognitionResult> RecognizeAsync(
        CapturedImage image,
        CancellationToken cancellationToken)
    {
        CapturedImagePixels.Validate(image);
        using var deadline = OcrPipelineDeadline.Start(_pipeline, cancellationToken);
        OcrResult ocr;
        try
        {
            ocr = await _ocrEngine
                .RecognizeAsync(image, new OcrRequest(ScanContext.FleaListings), deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            return new([], image.CapturedUtc.ToUniversalTime(), Confidence.Unknown, false,
                OcrPipelineDeadline.DiagnosticCode);
        }

        if (!ocr.IsAvailable)
        {
            return new([], image.CapturedUtc.ToUniversalTime(), Confidence.Unknown, false,
                ocr.DiagnosticCode ?? "ocr_provider_unavailable");
        }

        var parsed = new List<FleaListing>();
        string? cut = null;
        try
        {
            _parser.ParseVisibleInto(parsed, ocr, image, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsExpired)
        {
            cut = OcrPipelineDeadline.DiagnosticCode;
        }

        var listings = FleaListingParser.Order(parsed);
        var confidence = listings.Count == 0
            ? Confidence.Unknown
            : new Confidence(listings.Average(listing => listing.Confidence.Value));
        // An available read that lost tiles or lines still parses the rows that arrived, and used
        // to hand them on with no code at all, so a page read from half its tiles looked like the
        // whole page. Its code wins over "no_visible_flea_rows": a partial read that kept no rows
        // has not shown that the page had none.
        return new(
            listings,
            image.CapturedUtc.ToUniversalTime(),
            confidence,
            true,
            OcrOutcome.Degradation(ocr) ?? cut ?? (listings.Count == 0 ? "no_visible_flea_rows" : null));
    }
}
