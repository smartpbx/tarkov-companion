using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.App.Views.V2.Intel;

public sealed partial class AmmoWorkspaceView : UserControl
{
    private AmmoPageFit? _fit;

    public AmmoWorkspaceView()
    {
        // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated method assigns the
        // x:Name fields the fit below needs.
        InitializeComponent();
        SizeChanged += (_, e) => ApplyFit(e.NewSize);
    }

    /// <summary>
    /// [#832] Puts the round panel beside the table or under it; see <see cref="PageFit.Ammo"/>.
    /// </summary>
    /// <remarks>
    /// Under it, the page scrolls and the calibers and the table keep the page's height, so each
    /// still scrolls on its own instead of growing to every row it has.
    /// </remarks>
    private void ApplyFit(Size page)
    {
        var fit = PageFit.Ammo(page.Width);
        if (!fit.DetailBeside)
        {
            // Less the main grid's own top and bottom margins (4 and 16).
            AmmoMain.Height = Math.Max(0, page.Height - 20);
        }

        if (_fit == fit)
        {
            return;
        }

        _fit = fit;
        AmmoLists.ColumnDefinitions[0].MinWidth = fit.CaliberListMinimum;
        AmmoRoot.Classes.Set("v2-ammo-compact", fit.ClassesOnOwnLine);
        AmmoPage.VerticalScrollBarVisibility = fit.DetailBeside ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        Grid.SetColumn(AmmoDetail, fit.DetailBeside ? 1 : 0);
        Grid.SetRow(AmmoDetail, fit.DetailBeside ? 0 : 1);
        AmmoDetail.Width = fit.DetailBeside ? PageFit.AmmoDetailWidth : double.NaN;
        AmmoDetail.Margin = fit.DetailBeside ? default : new Thickness(16, 0, 16, 16);
        if (fit.DetailBeside)
        {
            AmmoMain.Height = double.NaN;
        }
    }
}
