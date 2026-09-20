using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.App.Views.V2.MapRenderer;

/// <summary>
/// The plan's presentation mode and floor controls, for whichever host wants to place them.
/// </summary>
/// <remarks>
/// The data context is a <see cref="ViewModels.V2.MapRenderer.MapSceneRendererViewModel"/>. The
/// renderer view hosts one in its floating pill; the Raid page hosts one in its bottom strip and
/// asks the renderer to keep its pill off the map (<see cref="MapSceneRendererView.DocksPresentation"/>).
/// </remarks>
public sealed partial class MapPresentationControls : UserControl
{
    public MapPresentationControls() => AvaloniaXamlLoader.Load(this);
}
