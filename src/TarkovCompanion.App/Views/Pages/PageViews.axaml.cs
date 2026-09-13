using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Quests;

namespace TarkovCompanion.App.Views.Pages;

public sealed partial class RaidView : UserControl
{
    public RaidView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class ScannerView : UserControl
{
    public ScannerView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class ItemsView : UserControl
{
    public ItemsView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class AmmoView : UserControl
{
    public AmmoView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class KeysView : UserControl
{
    public KeysView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class FleaView : UserControl
{
    public FleaView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class QuestsView : UserControl
{
    public QuestsView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Records loyalty with one trader.
    /// </summary>
    /// <remarks>
    /// One-way and handled here rather than two-way bound, the same as the hideout stepper:
    /// the rows are rebuilt after a save, and a two-way binding would read that rebuild as
    /// another edit. What the spinner shows is what the profile holds, never what somebody
    /// typed but could not be saved.
    /// </remarks>
    private void TraderLevelChanged(object? sender, NumericUpDownValueChangedEventArgs eventArgs)
    {
        if (sender is NumericUpDown { Tag: string traderId } &&
            eventArgs.NewValue is { } level &&
            DataContext is QuestsPageViewModel viewModel)
        {
            _ = viewModel.SetTraderLevelAsync(traderId, level);
        }
    }
}

public sealed partial class HideoutView : UserControl
{
    public HideoutView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Records the level a station has been built to.
    /// </summary>
    /// <remarks>
    /// Through the event rather than a two-way binding. The page reloads when a level is saved,
    /// which rebuilds every row, and a two-way binding would see the rebuild as another edit and
    /// save it again. The station id rides on the control's Tag because the handler is on the
    /// view rather than on the row.
    ///
    /// The stepper itself stays one-way and is corrected by the reload: what it shows is what
    /// the profile holds, never what somebody typed but could not be saved.
    /// </remarks>
    private void StationLevelChanged(object? sender, NumericUpDownValueChangedEventArgs eventArgs)
    {
        if (sender is NumericUpDown { Tag: string stationId } &&
            eventArgs.NewValue is { } level &&
            DataContext is HideoutPageViewModel viewModel)
        {
            _ = viewModel.SetStationLevelAsync(stationId, level);
        }
    }
}

public sealed partial class EventsView : UserControl
{
    public EventsView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class LoadoutView : UserControl
{
    public LoadoutView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class GroupView : UserControl
{
    public GroupView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class SquadView : UserControl
{
    public SquadView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class HistoryView : UserControl
{
    public HistoryView() => AvaloniaXamlLoader.Load(this);
}
