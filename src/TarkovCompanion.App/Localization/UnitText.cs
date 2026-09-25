using System.Globalization;

namespace TarkovCompanion.App.Localization;

/// <summary>
/// Numbers with a unit: roubles, kilograms and the k/M short forms (#314).
/// </summary>
/// <remarks>
/// One place for the suffixes, so a language that writes "12 345 ₽" or "12 тыс." changes a table
/// entry rather than every view model that used to append "₽" or "k" by hand. The digits are
/// formatted in the current culture, as every label was before; the unit and where it goes come
/// from the string table.
/// </remarks>
public static class UnitText
{
    /// <summary>"₽12,345".</summary>
    public static string Roubles(long value, CultureInfo? culture = null) =>
        UiText.Format("Units.Roubles", value.ToString("N0", culture ?? CultureInfo.CurrentCulture));

    /// <summary>
    /// "12,345 ₽": the older pages' order, kept so their English does not move. A language table
    /// may give both the same order.
    /// </summary>
    public static string RoublesAfter(long value, CultureInfo? culture = null) =>
        UiText.Format("Units.RoublesAfter", value.ToString("N0", culture ?? CultureInfo.CurrentCulture));

    /// <summary>
    /// "₽1.2M", "₽45k", "₽1.5k", "₽950": whole thousands from ten thousand up, one decimal below
    /// that and for millions. Below <paramref name="shortFrom"/> the full number is written.
    /// </summary>
    public static string RoublesShort(long value, CultureInfo? culture = null, long shortFrom = 1_000) =>
        UiText.Format("Units.Roubles", Short(value, culture, shortFrom));

    /// <summary>"1.2M", "45k", "1.5k", "950", with no currency; a loss is shortened the same way.</summary>
    public static string Short(long value, CultureInfo? culture = null, long shortFrom = 1_000)
    {
        culture ??= CultureInfo.CurrentCulture;
        var size = Math.Abs(value);
        return size switch
        {
            >= 1_000_000 => UiText.Format("Units.Millions", (value / 1_000_000d).ToString("0.#", culture)),
            >= 10_000 when size >= shortFrom => UiText.Format("Units.Thousands", (value / 1_000d).ToString("0", culture)),
            >= 1_000 when size >= shortFrom => UiText.Format("Units.Thousands", (value / 1_000d).ToString("0.#", culture)),
            _ => value.ToString("N0", culture),
        };
    }

    /// <summary>"12k" for a figure already in whole thousands.</summary>
    public static string Thousands(long thousands, CultureInfo? culture = null) =>
        UiText.Format("Units.Thousands", thousands.ToString("N0", culture ?? CultureInfo.CurrentCulture));

    /// <summary>"1.25 kg"; <paramref name="digits"/> is the number's format ("N2" writes "1.20 kg").</summary>
    public static string Kilograms(double value, CultureInfo? culture = null, string digits = "0.##") =>
        UiText.Format("Units.Kilograms", value.ToString(digits, culture ?? CultureInfo.CurrentCulture));

    /// <summary>
    /// "42 s", "3 min 5 s", "2 h 10 min": an elapsed or remaining time, shortest honest form.
    /// </summary>
    /// <remarks>
    /// Replaces <c>GroupSessionService.Ago</c>'s "42s"/"3m 5s", which the Application layer
    /// built in English and every language then printed as it was. The units come from the table.
    /// A negative span (a clock that stepped back) reads as zero rather than "-3 s".
    /// </remarks>
    public static string Duration(TimeSpan span, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var whole = span < TimeSpan.Zero ? TimeSpan.Zero : span;
        string N(long value) => value.ToString("N0", culture);
        if (whole < TimeSpan.FromMinutes(1))
        {
            return UiText.Format("Units.DurationSeconds", N((long)whole.TotalSeconds));
        }

        if (whole < TimeSpan.FromHours(1))
        {
            return whole.Seconds == 0
                ? UiText.Format("Units.DurationMinutes", N((long)whole.TotalMinutes))
                : UiText.Format("Units.DurationMinutesSeconds", N((long)whole.TotalMinutes), N(whole.Seconds));
        }

        return UiText.Format("Units.DurationHoursMinutes", N((long)whole.TotalHours), N(whole.Minutes));
    }
}
