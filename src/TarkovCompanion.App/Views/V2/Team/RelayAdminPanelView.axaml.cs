using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.App.Views.V2.Team;

/// <summary>[#920] The relay owner's admin panel; see <c>RelayAdminPanelViewModel</c>.</summary>
public sealed partial class RelayAdminPanelView : UserControl
{
    public RelayAdminPanelView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
