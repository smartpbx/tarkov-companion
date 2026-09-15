using System.Globalization;
using System.Text;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// Presentation helpers accept culture and timezone explicitly so a workstation setting cannot
/// silently turn a stored UTC fact into a differently labelled claim. Callers format complete
/// resource messages around these values rather than concatenating localized fragments.
/// </summary>
public static class V2PresentationFormatting
{
    private static readonly Dictionary<char, char> PseudoAccents = new()
    {
        ['a'] = 'å', ['c'] = 'ç', ['e'] = 'ë', ['i'] = 'ï', ['n'] = 'ñ', ['o'] = 'ö', ['u'] = 'û', ['y'] = 'ÿ',
        ['A'] = 'Å', ['C'] = 'Ç', ['E'] = 'Ë', ['I'] = 'Ï', ['N'] = 'Ñ', ['O'] = 'Ö', ['U'] = 'Û', ['Y'] = 'Ý',
    };

    public static string DateTime(DateTimeOffset utc, CultureInfo culture, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(utc, timeZone).ToString("f", culture);

    public static string Number(decimal value, CultureInfo culture) => value.ToString("N", culture);

    public static string Currency(decimal value, CultureInfo culture) => value.ToString("C", culture);

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
