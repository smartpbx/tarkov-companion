using System.Globalization;
using Avalonia.Data.Converters;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>
/// [#307] The "MODELLED TRAFFIC · NOT LIVE" chip's text width, from its own font size.
/// </summary>
/// <remarks>
/// #774 capped the chip at 210 so the strip's presentation controls kept their room at 200% text,
/// but at 100% the words need more than that and the chip broke onto two lines on the 1920x1080
/// screen. Up to 125% of the 14px body size the text keeps one line; above it the old cap stands
/// (210 less the chip's padding and border), so larger text still wraps inside the chip.
/// </remarks>
public sealed class TrafficChipWidthConverter : IValueConverter
{
    public static TrafficChipWidthConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double size && size <= 17.5 ? double.PositiveInfinity : 192d;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
