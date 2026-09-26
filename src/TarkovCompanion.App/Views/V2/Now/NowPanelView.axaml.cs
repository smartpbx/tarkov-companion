using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.App.Views.V2.Now;

/// <summary>[#712 0-4] The Raid page's Now panel; see <see cref="ViewModels.V2.Now.NowPanelViewModel"/>.</summary>
public sealed partial class NowPanelView : UserControl
{
    public NowPanelView() => AvaloniaXamlLoader.Load(this);
}
