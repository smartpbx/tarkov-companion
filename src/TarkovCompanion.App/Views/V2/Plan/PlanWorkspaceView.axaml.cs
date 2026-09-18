using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels.V2.Plan;

namespace TarkovCompanion.App.Views.V2.Plan;

public sealed partial class PlanWorkspaceView : UserControl
{
    public PlanWorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Records the player's level, through the event rather than a two-way binding: the board is
    /// re-read after a save, which rebuilds the spinner's value, and a two-way binding would
    /// read that rebuild as another edit.
    /// </summary>
    private void LevelChanged(object? sender, NumericUpDownValueChangedEventArgs eventArgs)
    {
        if (eventArgs.NewValue is { } level && DataContext is PlanWorkspaceViewModel viewModel)
        {
            _ = viewModel.SetPlayerLevelAsync(level);
        }
    }

    private void TraderLevelChanged(object? sender, NumericUpDownValueChangedEventArgs eventArgs)
    {
        if (sender is NumericUpDown { Tag: string traderId } &&
            eventArgs.NewValue is { } level &&
            DataContext is PlanWorkspaceViewModel viewModel)
        {
            _ = viewModel.SetTraderLevelAsync(traderId, level);
        }
    }
}
