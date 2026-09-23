using Avalonia.Controls;
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
        menu.Items.Add(Item("Remove", null, () => row.RemoveCommand.Execute(null)));
        menu.Items.Add(new Separator());
        foreach (var scope in new[] { RaidMarkScope.Private, RaidMarkScope.Squad })
        {
            menu.Items.Add(Item(RaidMarkLifetimes.ScopeName(scope), row.Scope == scope, () => _ = row.ChooseScopeAsync(scope)));
        }

        menu.Items.Add(new Separator());
        foreach (var lifetime in RaidMarkLifetimes.All)
        {
            menu.Items.Add(Item(RaidMarkLifetimes.Name(lifetime), row.Lifetime == lifetime, () => _ = row.ChooseLifetimeAsync(lifetime)));
        }

        return menu;
    }

    public static MenuFlyout ForPlacement(Action<RaidMarkLifetime> place, RaidMarkScope scope)
    {
        ArgumentNullException.ThrowIfNull(place);
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = $"Place for {RaidMarkLifetimes.ScopeName(scope)}", IsEnabled = false });
        foreach (var lifetime in RaidMarkLifetimes.All)
        {
            menu.Items.Add(Item(RaidMarkLifetimes.Name(lifetime), null, () => place(lifetime)));
        }

        return menu;
    }

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
