using System.Globalization;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// Presentation helpers accept culture and timezone explicitly so a workstation setting cannot
/// silently turn a stored UTC fact into a differently labelled claim. Callers format complete
/// resource messages around these values rather than concatenating localized fragments.
/// </summary>
public static class V2PresentationFormatting
{
    public static string DateTime(DateTimeOffset utc, CultureInfo culture, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTime(utc, timeZone).ToString("f", culture);

    public static string Number(decimal value, CultureInfo culture) => value.ToString("N", culture);

    public static string Currency(decimal value, CultureInfo culture) => value.ToString("C", culture);

    public static string PseudoLocalize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var translated = source
            .Replace('a', 'å').Replace('e', 'ë').Replace('i', 'ï').Replace('o', 'ö').Replace('u', 'û')
            .Replace('A', 'Å').Replace('E', 'Ë').Replace('I', 'Ï').Replace('O', 'Ö').Replace('U', 'Û');
        return $"[{translated}]";
    }
}
