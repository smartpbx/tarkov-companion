using System.Globalization;
using Avalonia.Data.Converters;
using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>[#902 P4] "50k · Per item" as the Layers menu's loot row: "50k · Per item · Edit".</summary>
public sealed class RaidLootFilterEditConverter : IValueConverter
{
    public static RaidLootFilterEditConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        RaidText.LootFilterEdit(value as string ?? string.Empty);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
