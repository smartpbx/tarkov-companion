using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels;

namespace TarkovCompanion.App.Views.Pages;

/// <summary>
/// The settings page.
/// </summary>
/// <remarks>
/// This used to be two hundred lines of key handling: a tunnelling key handler so a captured
/// combination could not press the button that started the capture, a modifier translation, and
/// a table mapping every key on a keyboard to the name a binding stores. All of it existed to
/// let somebody choose a scan shortcut, and the shortcut existed only because a scan driven by
/// the game's own screenshot key was logged and dropped instead of reaching the interface.
///
/// One wiring fix removed the reason for the shortcut, and the shortcut removed the reason for
/// all of this.
/// </remarks>
public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);

        // A clipboard belongs to a window, and a view model that reached for one could not be
        // tested. Handed over here, once the view is attached and there is a window to ask.
        DataContextChanged += (_, _) => Wire();
        AttachedToVisualTree += (_, _) => Wire();
    }

    /// <summary>
    /// The three size controls, which act on the shell rather than on this page.
    /// </summary>
    /// <remarks>
    /// Found through the window, because how large everything is drawn is a property of the
    /// shell — giving this page's view model a reference to the shell to reach it would be a
    /// cycle for one number.
    /// </remarks>
    private MainWindowViewModel? Shell => (TopLevel.GetTopLevel(this) as Window)?.DataContext as MainWindowViewModel;

    private void LargerClick(object? sender, RoutedEventArgs eventArgs) => Shell?.StepInterfaceScale(1);

    private void SmallerClick(object? sender, RoutedEventArgs eventArgs) => Shell?.StepInterfaceScale(-1);

    private void ResetScaleClick(object? sender, RoutedEventArgs eventArgs) => Shell?.ResetInterfaceScale();

    private void Wire()
    {
        if (DataContext is SettingsPageViewModel settings)
        {
            settings.Clipboard = async text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text).ConfigureAwait(true);
                }
            };
        }
    }
}
