using Avalonia.Controls;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>The right-click and Options menus for a mark: scope and lifetime (#289).</summary>
/// <remarks>
/// Built in code because the map's right-click and a row's Options button show the same menu, and
/// a menu declared in two templates is two menus that drift.
/// </remarks>
internal static class RaidMarkMenu
{
    public static MenuFlyout ForMark(RaidMarkRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var menu = new MenuFlyout();
        menu.Items.Add(Item(RaidText.Remove, null, () => row.RemoveCommand.Execute(null)));
        menu.Items.Add(new Separator());
        foreach (var scope in new[] { RaidMarkScope.Private, RaidMarkScope.Squad })
        {
            menu.Items.Add(Item(RaidText.MarkScope(scope), row.Scope == scope, () => _ = row.ChooseScopeAsync(scope)));
        }

        menu.Items.Add(new Separator());
        foreach (var lifetime in RaidMarkLifetimes.All)
        {
            menu.Items.Add(Item(RaidText.MarkLifetime(lifetime), row.Lifetime == lifetime, () => _ = row.ChooseLifetimeAsync(lifetime)));
        }

        return menu;
    }

    public static MenuFlyout ForPlacement(Action<RaidMarkLifetime> place, RaidMarkScope scope)
    {
        ArgumentNullException.ThrowIfNull(place);
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = RaidText.PlaceFor(RaidText.MarkScope(scope)), IsEnabled = false });
        foreach (var lifetime in RaidMarkLifetimes.All)
        {
            menu.Items.Add(Item(RaidText.MarkLifetime(lifetime), null, () => place(lifetime)));
        }

        return menu;
    }

    /// <summary>[#286] A line of ours: Remove, Clear my drawings, then its scope and lifetime.</summary>
    public static MenuFlyout ForDrawing(RaidCockpitViewModel cockpit, RaidDrawing drawing)
    {
        ArgumentNullException.ThrowIfNull(cockpit);
        ArgumentNullException.ThrowIfNull(drawing);
        var menu = new MenuFlyout();
        menu.Items.Add(Item(RaidText.Remove, null, () => cockpit.RemoveDrawing(drawing.Id)));
        menu.Items.Add(Item(RaidText.ClearMyDrawings, null, () => cockpit.ClearMyDrawingsCommand.Execute(null)));
        menu.Items.Add(new Separator());
        foreach (var scope in new[] { RaidMarkScope.Private, RaidMarkScope.Squad })
        {
            menu.Items.Add(Item(RaidText.MarkScope(scope), drawing.Scope == scope, () => cockpit.SetDrawingOptions(drawing.Id, scope, drawing.Lifetime)));
        }

        menu.Items.Add(new Separator());
        foreach (var lifetime in DrawingLifetimes)
        {
            menu.Items.Add(Item(RaidText.MarkLifetime(lifetime), drawing.Lifetime == lifetime, () => cockpit.SetDrawingOptions(drawing.Id, drawing.Scope, lifetime)));
        }

        return menu;
    }

    /// <summary>[#286] What the next line will be: the Marks card's scope, and a lifetime.</summary>
    public static MenuFlyout ForNewDrawings(RaidCockpitViewModel cockpit)
    {
        ArgumentNullException.ThrowIfNull(cockpit);
        var menu = new MenuFlyout();
        menu.Items.Add(Item(RaidText.MarkScope(RaidMarkScope.Private), cockpit.NewMarksAreJustMe, () => cockpit.NewMarksJustMeCommand.Execute(null)));
        menu.Items.Add(Item(RaidText.MarkScope(RaidMarkScope.Squad), cockpit.NewMarksAreSquad, () => cockpit.NewMarksSquadCommand.Execute(null)));
        menu.Items.Add(new Separator());
        foreach (var lifetime in DrawingLifetimes)
        {
            menu.Items.Add(Item(RaidText.MarkLifetime(lifetime), cockpit.NewDrawingLifetime == lifetime, () => cockpit.ChooseDrawingLifetime(lifetime)));
        }

        return menu;
    }

    /// <summary>A 45-second line is a scribble nobody finishes reading; the rest of the marks' lifetimes fit.</summary>
    private static readonly RaidMarkLifetime[] DrawingLifetimes =
        [RaidMarkLifetime.ThisRaid, RaidMarkLifetime.FiveMinutes, RaidMarkLifetime.FifteenMinutes, RaidMarkLifetime.UntilRemoved];

    /// <param name="isChecked">Null for a plain action; otherwise a radio choice and whether it is the current one.</param>
    private static MenuItem Item(string header, bool? isChecked, Action action)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = isChecked is null ? MenuItemToggleType.None : MenuItemToggleType.Radio,
            IsChecked = isChecked == true,
        };
        item.Click += (_, _) => action();
        return item;
    }
}
