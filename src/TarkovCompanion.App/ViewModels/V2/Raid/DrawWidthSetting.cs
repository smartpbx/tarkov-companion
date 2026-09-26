using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Workspaces;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[#919] The remembered width of the next line drawn on the Raid map.</summary>
/// <remarks>
/// Stored as the width in pixels. A hand-edited layout file can hold anything, so only one of the
/// three offered widths is read back; anything else is Medium.
/// </remarks>
internal sealed class DrawWidthSetting
{
    private readonly IWorkspaceLayoutStore? _store;

    public DrawWidthSetting(IWorkspaceLayoutStore? store)
    {
        _store = store;
        Value = Parse(store?.Get(WorkspaceLayoutKeys.RaidDrawWidth));
    }

    public int Value { get; private set; }

    /// <summary>Reads the stored width again, after Backup &amp; reset replaced it.</summary>
    public void Reload() => Value = Parse(_store?.Get(WorkspaceLayoutKeys.RaidDrawWidth));

    public int Set(int width)
    {
        Value = RaidDrawingWidths.Choices.Contains(width) ? width : RaidDrawingWidths.Medium;
        _store?.Set(WorkspaceLayoutKeys.RaidDrawWidth, Value.ToString(CultureInfo.InvariantCulture));
        return Value;
    }

    internal static int Parse(string? stored) =>
        int.TryParse(stored, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) &&
        RaidDrawingWidths.Choices.Contains(width)
            ? width
            : RaidDrawingWidths.Medium;
}

/// <summary>[#919] One of the Draw bar's three line widths.</summary>
public sealed class DrawWidthChoiceViewModel(int width, bool isSelected, ICommand selectCommand)
{
    public int Width { get; } = width;

    public string Label { get; } = width switch
    {
        RaidDrawingWidths.Thin => RaidText.LineThin,
        RaidDrawingWidths.Thick => RaidText.LineThick,
        _ => RaidText.LineMedium,
    };

    public bool IsSelected { get; } = isSelected;

    public ICommand SelectCommand { get; } = selectCommand;

    public string AutomationId => string.Create(CultureInfo.InvariantCulture, $"v2-raid-draw-width-{Width}");
}
