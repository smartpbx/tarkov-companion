using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Shell;

namespace TarkovCompanion.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += RestoreLayout;
        Closing += RememberLayout;
    }

    /// <summary>
    /// Opens the window where it was left, on a screen that exists.
    /// </summary>
    /// <remarks>
    /// The window opened at 1500 by 900 every launch, wherever the operating system put it, so
    /// a companion that shares a screen with the game was dragged back into its slot every
    /// time. The original handoff spec asked for placement memory and it was never built.
    ///
    /// On Opened rather than in the constructor, because the screens are not known until the
    /// window has a handle — and the whole reason this is not simply "restore what was saved"
    /// is the monitor that has been unplugged since.
    ///
    /// Bounds before the maximize, deliberately. Setting the state first and the bounds after
    /// would store the screen's size as the restore size, which is how somebody un-maximizes
    /// once and gets a window the size of their monitor for ever.
    /// </remarks>
    private async void RestoreLayout(object? sender, EventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var screens = Screens.All
            .Select(screen => new ScreenBounds(
                screen.WorkingArea.X,
                screen.WorkingArea.Y,
                screen.WorkingArea.X + screen.WorkingArea.Width,
                screen.WorkingArea.Y + screen.WorkingArea.Height))
            .ToArray();

        var layout = await viewModel.LoadLayoutAsync(screens);
        Width = layout.Width;
        Height = layout.Height;
        if (layout is { Left: { } left, Top: { } top })
        {
            Position = new((int)left, (int)top);
        }

        if (layout.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        // Only once the window is where it belongs. Subscribing earlier would record the
        // operating system's own opening position over the one being restored.
        PositionChanged += BoundsChanged;
        SizeChanged += BoundsChanged;
    }

    private void BoundsChanged(object? sender, EventArgs eventArgs) => Remember();

    private void RememberLayout(object? sender, WindowClosingEventArgs eventArgs) => Remember();

    private void Remember() => (DataContext as MainWindowViewModel)?.RecordBounds(
        Width,
        Height,
        Position.X,
        Position.Y,
        WindowState == WindowState.Maximized);

    private void RailToggleClick(object? sender, RoutedEventArgs eventArgs) =>
        (DataContext as MainWindowViewModel)?.ToggleRail();
}
