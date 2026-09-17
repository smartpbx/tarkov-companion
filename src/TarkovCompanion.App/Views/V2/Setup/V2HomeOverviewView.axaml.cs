using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.App.Views.V2.Setup;

/// <summary>The Setup overview (package 17): the home dashboard from the home/setup concept.</summary>
public sealed partial class V2HomeOverviewView : UserControl
{
    public V2HomeOverviewView() => AvaloniaXamlLoader.Load(this);
}
