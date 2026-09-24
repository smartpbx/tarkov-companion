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

    /// <summary>"1.25 kg".</summary>
    public static string Kilograms(double value, CultureInfo? culture = null) =>
        UiText.Format("Units.Kilograms", value.ToString("0.##", culture ?? CultureInfo.CurrentCulture));
}
