using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.App.Views.V2.Plan;

public sealed partial class EventsWorkspaceView : UserControl
{
    private bool? _stacked;

    public EventsWorkspaceView()
    {
        // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated method assigns the
        // x:Name fields the fit below needs.
        InitializeComponent();
        SizeChanged += (_, e) => ApplyFit(PageFit.EventsStacked(e.NewSize.Width));
    }

    /// <summary>[#832] The editor and the applicable items beside each other, or one under the other.</summary>
    private void ApplyFit(bool stacked)
    {
        if (_stacked == stacked)
        {
            return;
        }

        _stacked = stacked;
        EventsDetail.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,*");
        EventsDetail.RowDefinitions = new RowDefinitions(stacked ? "Auto,Auto" : "*");
        Grid.SetColumn(EventsItems, stacked ? 0 : 1);
        Grid.SetRow(EventsItems, stacked ? 1 : 0);
        EventsDetailScroll.VerticalScrollBarVisibility = stacked ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }
}
