using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Metadata;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;

namespace TarkovCompanion.App.Views.V2.MapRenderer;

/// <summary>
/// Builds the drawing inside a mark on the plan from the one template for its kind: a person, a
/// pin, a ping or the boxed chip.
/// </summary>
/// <remarks>
/// [#678] The mark's template used to hold all four drawings and hide three of them, so every
/// mark built, styled and bound about forty controls to show ten. A map switch builds up to 280
/// marks at once, and that building, and the collections it set off, held the interface thread
/// for most of the 0.9 to 2.5 s a switch took. A mark never changes kind (its icon is fixed when
/// it is made), so choosing the template once is enough.
/// </remarks>
public sealed class MapMarkKindTemplates : IDataTemplate
{
    public IDataTemplate? Person { get; set; }

    public IDataTemplate? Pin { get; set; }

    public IDataTemplate? Ping { get; set; }

    [Content]
    public IDataTemplate? Chip { get; set; }

    public bool Match(object? data) => data is MapSceneRendererObjectViewModel;

    public Control? Build(object? param) => param is MapSceneRendererObjectViewModel mark
        ? (mark.IsPersonIcon ? Person : mark.IsPinMark ? Pin : mark.IsPingMark ? Ping : Chip)?.Build(mark)
        : null;
}

/// <summary>
/// The glyph a boxed chip draws for its icon, looked up once instead of one hidden Path per icon.
/// </summary>
/// <remarks>
/// [#678] The chip held nine Paths, one per icon, eight of them hidden. The glyphs are the same
/// fixed V2.Icon geometries those Paths named (V2Icons.axaml); they do not change with the theme.
/// </remarks>
public sealed class MapMarkIconGeometry : IValueConverter
{
    public static MapMarkIconGeometry Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MapSceneMarkerIcon icon && KeyFor(icon) is { } key &&
        Avalonia.Application.Current is { } application &&
        application.TryGetResource(key, application.ActualThemeVariant, out var geometry)
            ? geometry as Geometry
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>The same icon each of the chip's old Paths drew, or none.</summary>
    internal static string? KeyFor(MapSceneMarkerIcon icon) => icon switch
    {
        MapSceneMarkerIcon.Extract => "V2.Icon.MapExtract",
        MapSceneMarkerIcon.Transit => "V2.Icon.MapTransit",
        MapSceneMarkerIcon.Spawn => "V2.Icon.Spawn",
        MapSceneMarkerIcon.Loot => "V2.Icon.Gem",
        MapSceneMarkerIcon.Hazard => "V2.Icon.Hazard",
        MapSceneMarkerIcon.Lock => "V2.Icon.Lock",
        MapSceneMarkerIcon.Switch => "V2.Icon.Gear",
        MapSceneMarkerIcon.Route => "V2.Icon.Route",
        MapSceneMarkerIcon.Risk => "V2.Icon.Hazard",
        MapSceneMarkerIcon.Generic => "V2.Icon.Target",
        _ => null,
    };
}
