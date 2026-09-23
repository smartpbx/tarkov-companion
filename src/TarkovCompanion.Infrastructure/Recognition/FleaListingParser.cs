using System.Globalization;
using System.Text.RegularExpressions;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;
using ItemConditionKind = TarkovCompanion.Core.Abstractions.V2.ItemConditionKind;
using ItemConditionReading = TarkovCompanion.Core.Abstractions.V2.ItemConditionReading;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed class FleaListingParser
{
    private static readonly Regex PricePattern = new(
        // Not straight after an "x" or a digit: "x3 189 999 ₽" is three of something at 189 999,
        // not a price of 3 189 999.
        @"(?<![x×\d])(?<price>\d[\d\s,.\u00a0]*)\s*(?<currency>₽|RUB(?:LES?)?|€|EUR(?:OS?)?|\$|USD(?:OLLARS?)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex QuantityPattern = new(
        @"(?:^|\s)(?:[x×]|qty\s*:?)\s*(?<quantity>\d{1,4})(?:\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // The two halves of a price the engine read as separate fragments: the digits, and the glyph.
    private static readonly Regex BareNumberPattern = new(
        @"^\d[\d\s,.\u00a0]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LoneCurrencyPattern = new(
        @"^(?:₽|RUB(?:LES?)?|€|EUR(?:OS?)?|\$|USD(?:OLLARS?)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ConditionPattern = new(
        @"(?<kind>durability|dur|condition|uses?|charges?|resource)\s*:?\s*(?<current>\d{1,4}(?:[.,]\d{1,2})?)\s*/\s*(?<maximum>\d{1,4}(?:[.,]\d{1,2})?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// How far, in heights of its price line, a line may sit from the price it is attached to.
    /// </summary>
    /// <remarks>
    /// This only binds where a price has no neighbour to share the space with (the first and last
    /// row of a page, or a page with one row): between two prices a line belongs to the nearer, so
    /// no reach is needed there. The 2026-09-22 3840x1080 browse screenshot confirmed separate
    /// name, total/limit and RUB/EUR price runs, but its OCR line boxes have not been measured, so
    /// this reach remains conservative rather than pretending the visible row pitch calibrated it.
    /// </remarks>
    private const double ReachInLineHeights = 2.5;

    /// <summary>
    /// How much nearer the closest price must be than the next, in line heights, before a line is
    /// attached to it. A line midway between two rows belongs to neither.
    /// </summary>
    private const double AmbiguousWithinLineHeights = 0.25;

    private readonly OcrTextNormalizer _normalizer;

    public FleaListingParser(OcrTextNormalizer? normalizer = null)
    {
        _normalizer = normalizer ?? new OcrTextNormalizer();
    }

    public IReadOnlyList<FleaListing> ParseVisible(
        OcrResult ocr,
        CapturedImage image,
        IReadOnlyDictionary<string, CurrencyRoubleRate>? currencyRates = null,
        CancellationToken cancellationToken = default)
    {
        var listings = new List<FleaListing>();
        ParseVisibleInto(listings, ocr, image, currencyRates, cancellationToken);
        return Order(listings);
    }

    /// <summary>
    /// Parses rows into <paramref name="listings"/> as it goes, so work stopped between rows
    /// keeps the rows it already parsed.
    /// </summary>
    /// <remarks>
    /// A row is found by its price and joined by where things are on the page, never by the order
    /// the engine listed them. The engine returns a line per run of text it saw, so a row's price,
    /// its quantity and its name can arrive as separate lines in any order, and a parser that
    /// needed price and quantity on the same line could not read a row that had them apart, which
    /// is what a real row looks like. Each price is a row; every other line goes to the row whose
    /// price is nearest vertically, and to none when two are equally near or when it is too far
    /// from any (see <see cref="ReachInLineHeights"/>). A row whose price is cut off by the frame
    /// has no price to find and is left out, and its quantity cannot leak into the row above it.
    /// </remarks>
    internal void ParseVisibleInto(
        List<FleaListing> listings,
        OcrResult ocr,
        CapturedImage image,
        IReadOnlyDictionary<string, CurrencyRoubleRate>? currencyRates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(ocr);
        CapturedImagePixels.Validate(image);
        var frame = new PixelRect(0, 0, image.Width, image.Height);

        var fragments = JoinSplitPrices(ocr.Lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text) && CapturedImagePixels.Intersects(frame, line.Bounds))
            .ToList());

        var anchors = new List<PriceAnchor>();
        foreach (var fragment in fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var priceMatch = PricePattern.Match(fragment.Text);
            if (!priceMatch.Success)
            {
                continue;
            }

            var priceText = _normalizer.NormalizeNumber(priceMatch.Groups["price"].Value);
            var currencyCode = CurrencyCode(priceMatch.Groups["currency"].Value);
            var rate = CurrencyRate(currencyCode, currencyRates);
            if (rate is not null &&
                long.TryParse(priceText, NumberStyles.None, CultureInfo.InvariantCulture, out var price) &&
                price > 0)
            {
                try
                {
                    anchors.Add(new(fragment, checked(price * rate.RoublesPerUnit), price, currencyCode, rate, priceMatch, []));
                }
                catch (OverflowException)
                {
                    // An OCR-corrupted number outside Int64 is not an offer price.
                }
            }
        }

        anchors.Sort((left, right) => CenterY(left.Line.Bounds).CompareTo(CenterY(right.Line.Bounds)));
        foreach (var fragment in fragments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (anchors.Any(anchor => ReferenceEquals(anchor.Line, fragment)))
            {
                continue;
            }

            Attach(anchors, fragment);
        }

        foreach (var anchor in anchors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            listings.Add(ToListing(anchor));
        }
    }

    private static CurrencyRoubleRate? CurrencyRate(
        string currencyCode,
        IReadOnlyDictionary<string, CurrencyRoubleRate>? currencyRates)
    {
        if (currencyCode == "RUB")
        {
            return new("RUB", 1, new DataProvenance("roubles", DateTimeOffset.UnixEpoch));
        }

        return currencyRates is not null && currencyRates.TryGetValue(currencyCode, out var rate) && rate.RoublesPerUnit > 0
            ? rate
            : null;
    }

    private static string CurrencyCode(string text) => text.Trim().ToUpperInvariant() switch
    {
        "₽" or "RUBLE" or "RUBLES" or "RUB" => "RUB",
        "€" or "EURO" or "EUROS" or "EUR" => "EUR",
        "$" or "USDOLLAR" or "USDOLLARS" or "USD" => "USD",
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "Unsupported flea currency."),
    };

    private static void Attach(List<PriceAnchor> anchors, OcrLine fragment)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        var centre = CenterY(fragment.Bounds);
        var nearest = -1;
        var nearestDistance = double.MaxValue;
        var nextDistance = double.MaxValue;
        for (var index = 0; index < anchors.Count; index++)
        {
            var distance = Math.Abs(CenterY(anchors[index].Line.Bounds) - centre);
            if (distance < nearestDistance)
            {
                nextDistance = nearestDistance;
                nearestDistance = distance;
                nearest = index;
            }
            else if (distance < nextDistance)
            {
                nextDistance = distance;
            }
        }

        var owner = anchors[nearest];
        var height = Math.Max(1, owner.Line.Bounds.Height);
        if (nearestDistance > ReachInLineHeights * height ||
            nextDistance - nearestDistance < AmbiguousWithinLineHeights * height)
        {
            return;
        }

        owner.Attached.Add(fragment);
    }

    private static FleaListing ToListing(PriceAnchor anchor)
    {
        // A quantity beside the price is the row's own; one on another line counts when the row
        // has one only. Two different quantities on a row are two readings and neither is used.
        var found = new List<(int Quantity, OcrLine Line)>();
        var priceless = anchor.Line.Text.Remove(anchor.Price.Index, anchor.Price.Length);
        AddQuantity(found, priceless, anchor.Line);
        foreach (var fragment in anchor.Attached.OrderBy(item => item.Bounds.Y).ThenBy(item => item.Bounds.X))
        {
            AddQuantity(found, fragment.Text, fragment);
        }

        int? quantity = null;
        var contributors = new List<OcrLine> { anchor.Line };
        if (found.Select(item => item.Quantity).Distinct().Count() == 1)
        {
            quantity = found[0].Quantity;
            contributors.AddRange(found.Select(item => item.Line));
        }

        var condition = ReadCondition(anchor);
        if (condition is { Line: { } conditionLine })
        {
            contributors.Add(conditionLine);
        }

        var bounds = anchor.Attached.Aggregate(anchor.Line.Bounds, (union, fragment) => Union(union, fragment.Bounds));
        var text = string.Join(
            " | ",
            anchor.Attached.Append(anchor.Line)
                .OrderBy(item => item.Bounds.Y)
                .ThenBy(item => item.Bounds.X)
                .Select(item => item.Text.Trim()));

        // A row read by an engine that does not score is still a row that was read. The
        // small discount is about the parse rather than the reading, so it applies either
        // way, and a missing opinion becomes a confident one rather than a worthless one. A
        // value read from a second line is only as sure as the least sure line it came from.
        var confidence = new Confidence(Math.Clamp(contributors.Min(line => line.Confidence?.Value ?? 1) * 0.98, 0, 1));
        return new(
            anchor.Value,
            quantity,
            confidence,
            bounds,
            text,
            anchor.CurrencyCode,
            anchor.OriginalValue,
            anchor.Rate.RoublesPerUnit,
            anchor.CurrencyCode == "RUB" ? null : anchor.Rate.Provenance,
            condition?.Reading);
    }

    private static (ItemConditionReading Reading, OcrLine Line)? ReadCondition(PriceAnchor anchor)
    {
        var readings = new List<(ItemConditionReading Reading, OcrLine Line)>();
        foreach (var line in anchor.Attached.Prepend(anchor.Line))
        {
            var match = ConditionPattern.Match(line.Text);
            if (!match.Success ||
                !TryConditionNumber(match.Groups["current"].Value, out var current) ||
                !TryConditionNumber(match.Groups["maximum"].Value, out var maximum) ||
                maximum <= 0 || current < 0 || current > maximum)
            {
                continue;
            }

            var kind = match.Groups["kind"].Value.ToLowerInvariant() switch
            {
                "use" or "uses" => ItemConditionKind.Uses,
                "charge" or "charges" => ItemConditionKind.Charges,
                "resource" => ItemConditionKind.Resource,
                _ => ItemConditionKind.Durability,
            };
            readings.Add((new ItemConditionReading(kind, current, maximum), line));
        }

        return readings
            .DistinctBy(value => value.Reading)
            .Take(2)
            .ToArray() switch
        {
            [var only] => only,
            _ => null,
        };
    }

    private static bool TryConditionNumber(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    private static void AddQuantity(List<(int Quantity, OcrLine Line)> found, string text, OcrLine line)
    {
        var match = QuantityPattern.Match(text);
        if (match.Success &&
            int.TryParse(match.Groups["quantity"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) &&
            quantity > 0)
        {
            found.Add((quantity, line));
        }
    }

    /// <summary>
    /// Puts a price back together when the engine read its digits and its glyph as two lines on
    /// one visual line.
    /// </summary>
    private static List<OcrLine> JoinSplitPrices(List<OcrLine> lines)
    {
        var joined = new List<OcrLine>(lines);
        for (var glyphIndex = joined.Count - 1; glyphIndex >= 0; glyphIndex--)
        {
            var glyph = joined[glyphIndex];
            if (!LoneCurrencyPattern.IsMatch(glyph.Text.Trim()))
            {
                continue;
            }

            var digitsIndex = joined.FindIndex(candidate =>
                !ReferenceEquals(candidate, glyph) &&
                BareNumberPattern.IsMatch(candidate.Text.Trim()) &&
                SitsRightAfter(candidate.Bounds, glyph.Bounds));
            if (digitsIndex < 0)
            {
                continue;
            }

            var digits = joined[digitsIndex];
            var confidence = digits.Confidence is { } left && glyph.Confidence is { } right
                ? new Confidence(Math.Min(left.Value, right.Value))
                : digits.Confidence ?? glyph.Confidence;
            joined[digitsIndex] = new OcrLine(
                digits.Text.Trim() + " " + glyph.Text.Trim(),
                Union(digits.Bounds, glyph.Bounds),
                confidence);
            joined.RemoveAt(glyphIndex);
        }

        return joined;
    }

    // The glyph starts where the digits end, on the same visual line: at least 60% of the shorter
    // one's height in common, and no further away than three of them.
    private static bool SitsRightAfter(PixelRect digits, PixelRect glyph)
    {
        var shorter = Math.Max(1, Math.Min(digits.Height, glyph.Height));
        var shared = Math.Min(digits.Y + digits.Height, glyph.Y + glyph.Height) - Math.Max(digits.Y, glyph.Y);
        var gap = glyph.X - (digits.X + digits.Width);
        return shared >= 0.6 * shorter && gap >= -0.5 * shorter && gap <= 3 * shorter;
    }

    private static double CenterY(PixelRect bounds) => bounds.Y + (bounds.Height / 2.0);

    private static PixelRect Union(PixelRect left, PixelRect right)
    {
        var x = Math.Min(left.X, right.X);
        var y = Math.Min(left.Y, right.Y);
        return new(
            x,
            y,
            Math.Max(left.X + left.Width, right.X + right.Width) - x,
            Math.Max(left.Y + left.Height, right.Y + right.Height) - y);
    }

    private sealed record PriceAnchor(
        OcrLine Line,
        long Value,
        long OriginalValue,
        string CurrencyCode,
        CurrencyRoubleRate Rate,
        Match Price,
        List<OcrLine> Attached);

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
    private readonly IItemMarketFactSource? _market;

    public FleaRecognitionService(
        IOcrEngine ocrEngine,
        FleaListingParser? parser = null,
        OcrPipelineOptions? pipelineOptions = null,
        IItemMarketFactSource? market = null)
    {
        _ocrEngine = ocrEngine ?? throw new ArgumentNullException(nameof(ocrEngine));
        _parser = parser ?? new FleaListingParser();
        _pipeline = OcrPipelineDeadline.Validate(pipelineOptions);
        _market = market;
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
            IReadOnlyDictionary<string, CurrencyRoubleRate>? currencyRates = null;
            if (_market is not null)
            {
                try
                {
                    currencyRates = await _market.GetCurrencyRoubleRatesAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    currencyRates = null;
                }
            }

            _parser.ParseVisibleInto(parsed, ocr, image, currencyRates, deadline.Token);
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
