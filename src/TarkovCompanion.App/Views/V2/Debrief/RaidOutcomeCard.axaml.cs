using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace TarkovCompanion.App.Views.V2.Debrief;

/// <summary>The after-raid card (#712 0-8); a control of its own so the Now panel can host it too.</summary>
public sealed partial class RaidOutcomeCard : UserControl
{
    public RaidOutcomeCard()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

/// <summary>A recap line's icon name ("Clock") to its V2 icon geometry (V2.Icon.Clock).</summary>
public sealed class RecapIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } name
            && Avalonia.Application.Current?.TryFindResource($"V2.Icon.{name}", out var resource) == true
            && resource is Geometry geometry
            ? geometry
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
