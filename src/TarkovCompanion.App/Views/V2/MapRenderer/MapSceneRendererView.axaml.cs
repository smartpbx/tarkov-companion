using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Views.V2.MapRenderer;

/// <summary>Keyboard handoff for the renderer's command surface; touch uses the same buttons.</summary>
public sealed partial class MapSceneRendererView : UserControl
{
    public MapSceneRendererView() => AvaloniaXamlLoader.Load(this);

    private void RendererKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (DataContext is not MapSceneRendererViewModel renderer)
        {
            return;
        }

        switch (eventArgs.Key)
        {
            case Key.Right:
            case Key.Down:
                renderer.FocusNextObjectCommand.Execute(null);
                break;
            case Key.Left:
            case Key.Up:
                renderer.FocusPreviousObjectCommand.Execute(null);
                break;
            case Key.Escape:
                renderer.ClearSelectionCommand.Execute(null);
                break;
            case Key.F:
                renderer.FitPlanCommand.Execute(null);
                break;
            case Key.D1:
                renderer.RequestMode(MapSceneMode.Flat2D);
                break;
            case Key.D2:
                renderer.RequestMode(MapSceneMode.FloorStack2D);
                break;
            case Key.D3:
                renderer.RequestMode(MapSceneMode.Interior3D);
                break;
            default:
                return;
        }

        eventArgs.Handled = true;
    }
}
