using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels;

namespace TarkovCompanion.App.Views.Pages;

/// <summary>
/// The V1 shell, as its own control so that a V2 launch never builds it.
/// </summary>
/// <remarks>
/// [#294] The rail toggle's handler moved here with the markup that raises it. An event handler
/// written inside a <c>DataTemplate</c> is resolved against the template's own root, not the
/// window's, so leaving the button in MainWindow's template and the method on MainWindow would
/// have compiled and then done nothing at runtime — the rail would simply stop collapsing.
/// </remarks>
public sealed partial class LegacyShellView : UserControl
{
    public LegacyShellView() => AvaloniaXamlLoader.Load(this);

    private void RailToggleClick(object? sender, RoutedEventArgs eventArgs) =>
        (DataContext as MainWindowViewModel)?.ToggleRail();
}
