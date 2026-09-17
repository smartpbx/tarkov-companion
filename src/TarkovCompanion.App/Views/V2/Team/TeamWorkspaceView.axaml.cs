using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Views.V2.Team;

public sealed partial class TeamWorkspaceView : UserControl
{
    public TeamWorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Right-clicking a group mark on the centre map removes it, as on the Raid map.</summary>
    private void MapMarkerRightClicked(object? sender, MapSceneObjectId objectId)
    {
        if (DataContext is TeamWorkspaceViewModel team)
        {
            team.RemoveMarkAt(objectId);
        }
    }
}
