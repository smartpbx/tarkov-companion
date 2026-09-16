using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>Hosts the raid cockpit: map picker, mark placement, and the canonical map renderer.</summary>
public sealed partial class RaidCockpitView : UserControl
{
    public RaidCockpitView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void MapPickerSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        if (comboBox.SelectedItem is RaidMapPickerItemViewModel item)
        {
            item.SelectCommand.Execute(null);
        }

        // A picker rather than a persistent selection: the current map is already shown by the
        // renderer, and leaving an entry highlighted here would just be a second, staler copy of
        // that same fact.
        comboBox.SelectedItem = null;
    }

    private void RendererPlanClicked(object? sender, MapScenePoint point)
    {
        if (DataContext is RaidCockpitViewModel cockpit)
        {
            cockpit.PlaceArmedMarkAt(point);
        }
    }
}
