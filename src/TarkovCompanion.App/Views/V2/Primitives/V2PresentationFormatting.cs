using System.Globalization;
using System.Text;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// Presentation helpers take every input that changes what a value means as an argument: the
/// culture shapes digits and dates, but a currency comes from the data and a displayed time always
/// names its UTC offset. A workstation setting therefore cannot silently turn a stored fact into a
/// differently labelled claim. Callers format complete resource messages around these values
/// rather than concatenating localized fragments.
/// </summary>
/// <remarks>
/// The first version formatted currency with the culture's own symbol, so a rouble price shown
/// under en-US read as dollars, and formatted a converted time with no zone at all.
/// </remarks>
public static class V2PresentationFormatting
{
    private const char NoBreakSpace = '\u00A0';

    private static readonly Dictionary<char, char> PseudoAccents = new()
    {
        ['a'] = 'å', ['c'] = 'ç', ['e'] = 'ë', ['i'] = 'ï', ['n'] = 'ñ', ['o'] = 'ö', ['u'] = 'û', ['y'] = 'ÿ',
        ['A'] = 'Å', ['C'] = 'Ç', ['E'] = 'Ë', ['I'] = 'Ï', ['N'] = 'Ñ', ['O'] = 'Ö', ['U'] = 'Û', ['Y'] = 'Ý',
    };

    // .NET currency patterns, indexed by NumberFormatInfo value, mapped to the same layout without
    // the separating space; the separator is then attached to the code as a no-break space.
    private static readonly int[] UnspacedPositivePattern = [0, 1, 0, 1];
    private static readonly int[] UnspacedNegativePattern = [0, 1, 2, 3, 4, 5, 6, 7, 5, 1, 7, 3, 2, 6, 0, 4, 2];

    /// <summary>
    /// Converts <paramref name="instant"/> to <paramref name="timeZone"/> and fills a localized
    /// whole-message <paramref name="template"/> whose <c>{dateTime}</c> is the culture's full date
    /// and short time and whose <c>{zone}</c> is the UTC offset in effect at that instant, such as
    /// <c>UTC+02:00</c>. A template that drops either placeholder throws rather than printing an
    /// unlabelled time.
    /// </summary>
    public static string DateTimeWithZone(DateTimeOffset instant, CultureInfo culture, TimeZoneInfo timeZone, string template)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(timeZone);

        var local = TimeZoneInfo.ConvertTime(instant, timeZone);
        return Message(template, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dateTime"] = local.ToString("f", culture),
            ["zone"] = local.ToString("'UTC'zzz", CultureInfo.InvariantCulture),
        });
    }

    public static string Number(decimal value, CultureInfo culture) => value.ToString("N", culture);

    /// <summary>
    /// Formats <paramref name="amount"/> in the ISO 4217 currency the data names. The culture
    /// supplies number shape: its separators, grouping, code placement, and negative-number
    /// convention. The code always replaces the culture's symbol and is joined to the digits by a
    /// no-break space, and <paramref name="decimalDigits"/> is the caller's choice rather than the
    /// culture's.
    /// </summary>
    public static string Currency(decimal amount, string isoCurrencyCode, int decimalDigits, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(isoCurrencyCode);
        ArgumentNullException.ThrowIfNull(culture);
        if (isoCurrencyCode.Length != 3 || !isoCurrencyCode.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException($"'{isoCurrencyCode}' is not an ISO 4217 alphabetic code.", nameof(isoCurrencyCode));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(decimalDigits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(decimalDigits, 28);

        // Rounding first decides the sign the pattern will use, and turns a negative amount that
        // rounds to zero into an unsigned zero.
        var rounded = Math.Round(amount, decimalDigits, MidpointRounding.AwayFromZero);
        if (rounded == 0m)
        {
            rounded = 0m;
        }

        var format = (NumberFormatInfo)culture.NumberFormat.Clone();
        format.CurrencyDecimalDigits = decimalDigits;
        format.CurrencyPositivePattern = UnspacedPositivePattern[format.CurrencyPositivePattern];
        format.CurrencyNegativePattern = UnspacedNegativePattern[format.CurrencyNegativePattern];

        var codeLeads = rounded < 0m ? format.CurrencyNegativePattern <= 3 : format.CurrencyPositivePattern == 0;
        format.CurrencySymbol = codeLeads ? isoCurrencyCode + NoBreakSpace : NoBreakSpace + isoCurrencyCode;
        return rounded.ToString("C", format);
    }

    /// <summary>
    /// Fills the named <c>{placeholder}</c> slots of a localized whole-message template. Every slot
    /// must have a value and every value must have a slot, so a translation cannot silently drop a
    /// zone, a unit, or a status word.
    /// </summary>
    public static string Message(string template, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);

        var builder = new StringBuilder(template.Length);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                throw new FormatException($"The template '{template}' has an unclosed placeholder.");
            }

            var name = template[(open + 1)..close];
            if (!values.TryGetValue(name, out var value))
            {
                throw new FormatException($"The template '{template}' names {{{name}}}, which has no value.");
            }

            builder.Append(template, index, open - index).Append(value);
            used.Add(name);
            index = close + 1;
        }

        var omitted = values.Keys.Where(name => !used.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        if (omitted.Length > 0)
        {
            throw new FormatException($"The template '{template}' omits {string.Join(", ", omitted.Select(name => "{" + name + "}"))}.");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Produces the qps-ploc form of an English resource: accented letters, brackets that expose
    /// truncation at either end, and 40% padding (rounded up) for the expansion a translated label
    /// has to survive. Format placeholders such as <c>{0}</c> are left intact.
    /// </summary>
    /// <remarks>
    /// The first qps-ploc file was written by hand and added two characters per string, which is
    /// not an expansion test. strings.qps-ploc.json is now generated from strings.en.json with
    /// exactly this rule, and a test compares the two.
    /// </remarks>
    public static string PseudoLocalize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var builder = new StringBuilder((source.Length * 2) + 4).Append('[');
        var placeholderDepth = 0;
        foreach (var character in source)
        {
            if (character == '{')
            {
                placeholderDepth++;
            }

            builder.Append(placeholderDepth == 0 && PseudoAccents.TryGetValue(character, out var accented) ? accented : character);

            if (character == '}' && placeholderDepth > 0)
            {
                placeholderDepth--;
            }
        }

        return builder.Append(' ').Append('·', ((source.Length * 2) + 4) / 5).Append(']').ToString();
    }
}
