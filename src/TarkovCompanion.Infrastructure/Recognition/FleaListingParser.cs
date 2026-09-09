using System.Globalization;
using System.Text.RegularExpressions;
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

    public IReadOnlyList<FleaListing> ParseVisible(OcrResult ocr, CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(ocr);
        CapturedImagePixels.Validate(image);
        var frame = new PixelRect(0, 0, image.Width, image.Height);
        var listings = new List<FleaListing>();

        foreach (var line in ocr.Lines.Where(line => CapturedImagePixels.Intersects(frame, line.Bounds)))
        {
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

            var confidence = new Confidence(Math.Clamp(line.Confidence.Value * 0.98, 0, 1));
            listings.Add(new(price, quantity, confidence, line.Bounds));
        }

        return listings
            .OrderBy(listing => listing.Bounds.Y)
            .ThenBy(listing => listing.Bounds.X)
            .ToArray();
    }
}
