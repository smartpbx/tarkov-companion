using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>Hosts the raid cockpit: mark placement and the canonical map renderer. The map
/// selector itself now lives in the shell's top bar (v2-shell-topbar-map).</summary>
public sealed partial class RaidCockpitView : UserControl
{
    public RaidCockpitView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void RendererPlanClicked(object? sender, MapScenePoint point)
    {
        if (DataContext is RaidCockpitViewModel cockpit)
        {
            cockpit.PlaceArmedMarkAt(point);
        }
    }

    private void RendererMarkerRightClicked(object? sender, MapSceneObjectId objectId)
    {
        if (DataContext is RaidCockpitViewModel cockpit)
        {
            cockpit.RemoveMarkAt(objectId);
        }
    }
}
