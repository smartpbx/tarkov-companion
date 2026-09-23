using System.Globalization;
using System.Text.RegularExpressions;

namespace TarkovCompanion.Application.Services.Intelligence;

/// <summary>Turns the upstream caliber token into the name a player sees on ammunition.</summary>
/// <remarks>
/// json.tarkov.dev writes identifiers such as <c>Caliber556x45NATO</c>. The dimensions can be
/// reconstructed without guessing, while a few calibers are conventionally named for an inch
/// designation or a shotgun shell instead. Callers that already hold the catalog item should pass
/// its name: that is the forward-compatible answer for a new identifier this table has never seen.
/// No prefixed identifier is returned to presentation code.
/// </remarks>
public static partial class CaliberText
{
    private const string Prefix = "Caliber";
    private const string Unknown = "Unknown caliber";

    // These are the exceptions measured in the 2026-09-14 catalog. Everything else is derived
    // from the ammunition name when available, then from the identifier's stated dimensions.
    private static readonly IReadOnlyDictionary<string, string> ConventionalNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Caliber1143x23ACP"] = ".45 ACP",
            ["Caliber127x33"] = ".50 AE",
            ["Caliber127x99"] = ".50 BMG",
            ["Caliber12g"] = "12/70",
            ["Caliber20g"] = "20/70",
            ["Caliber366TKM"] = ".366 TKM",
            ["Caliber40mmRU"] = "40mm",
            ["Caliber556x45NATO"] = "5.56x45mm NATO",
            ["Caliber762x35"] = ".300 Blackout",
            ["Caliber784x49"] = ".308 ME",
            ["Caliber86x70"] = ".338 Lapua Magnum",
            ["Caliber9x19PARA"] = "9x19mm Parabellum",
            ["Caliber9x33R"] = ".357 Magnum",
        };

    public static string Describe(string caliber) => Describe(caliber, catalogItemName: null);

    /// <summary>
    /// Uses the item's own catalog name when it begins with a caliber, with a dimensional fallback
    /// for callers such as compatibility checks that only have the upstream identifier.
    /// </summary>
    public static string Describe(string caliber, string? catalogItemName)
    {
        ArgumentNullException.ThrowIfNull(caliber);
        var trimmed = caliber.Trim();
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Length == 0 ? Unknown : trimmed;
        }

        if (ConventionalNames.TryGetValue(trimmed, out var conventional))
        {
            return conventional;
        }

        if (CatalogName(catalogItemName) is { } fromCatalog)
        {
            return fromCatalog;
        }

        var token = trimmed[Prefix.Length..];
        var match = IdentifierPattern().Match(token);
        if (match.Success &&
            int.TryParse(match.Groups["left"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var left) &&
            int.TryParse(match.Groups["right"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var right))
        {
            var suffix = match.Groups["suffix"].Value;
            return $"{MetricLeft(left)}x{right.ToString(CultureInfo.InvariantCulture)}mm{MetricSuffix(suffix)}";
        }

        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _) && token.Length == 3)
        {
            return $"{token[..2]}.{token[2]}mm";
        }

        return Unknown;
    }

    private static string? CatalogName(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        var match = CatalogNamePattern().Match(itemName.Trim());
        return match.Success ? match.Groups["caliber"].Value : null;
    }

    private static string MetricLeft(int left) => left switch
    {
        >= 1000 => $"{left / 100}.{left % 100:00}",
        >= 100 and < 200 => $"{left / 10}.{left % 10}",
        >= 100 => $"{left / 100}.{left % 100:00}",
        46 or 57 or 58 or 68 or 86 or 93 => $"{left / 10}.{left % 10}",
        _ => left.ToString(CultureInfo.InvariantCulture),
    };

    private static string MetricSuffix(string suffix) => suffix.ToUpperInvariant() switch
    {
        "" or "MM" => string.Empty,
        "R" => " R",
        "TT" => " TT",
        "PM" => " PM",
        "PMM" => " PMM",
        _ => $" {suffix}",
    };

    [GeneratedRegex("^(?<left>\\d+)x(?<right>\\d+)(?<suffix>[A-Za-z]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(
        "^(?<caliber>(?:\\d+(?:\\.\\d+)?x\\d+(?:\\.\\d+)?mm(?: (?:R|TT|PMM?))?|\\d+/\\d+|\\d+mm|\\.\\d+ (?:ACP|AE|BMG|TKM|ME|Blackout|Lapua Magnum|Magnum)))\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CatalogNamePattern();
}
