using Avalonia.Controls;
using Avalonia.Input.Platform;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.App.Views.V2.Setup;

/// <summary>The native V2 Setup page (#292): section tabs over the same view models V1 uses.</summary>
public sealed partial class V2SetupWorkspaceView : UserControl
{
    public V2SetupWorkspaceView()
    {
        AvaloniaXamlLoader.Load(this);

        // Same reasoning as SettingsView: a clipboard belongs to a window, so it is handed to the
        // view model here rather than the view model reaching for one itself.
        DataContextChanged += (_, _) => Wire();
        AttachedToVisualTree += (_, _) => Wire();
    }

    /// <summary>
    /// Opens the installer's address in the browser, for a build that was run from a folder.
    /// </summary>
    /// <remarks>
    /// Here rather than in the view model for the same reason as the clipboard: the launcher
    /// belongs to a window. The browser does the download, so the player sees what they are
    /// fetching and from where; this application never runs an installer it fetched itself.
    /// </remarks>
    private async void OnGetInstaller(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is V2SetupWorkspaceViewModel { Settings.InstallerLocation: { } installer }
            && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            try
            {
                await launcher.LaunchUriAsync(installer).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
                // The address is on the page as text, so a browser that will not open is not the
                // end of the road and is not worth a dialog.
            }
        }
    }

    private void Wire()
    {
        if (DataContext is not V2SetupWorkspaceViewModel page)
        {
            return;
        }

        if (page.Settings is { } settings)
        {
            settings.Clipboard = Copy;
        }

        // [V2 rough package 41] Copy result, from the same window's clipboard. Attached here
        // rather than inside the self-test view, because that view's data context is the
        // self-test and its own AttachedToVisualTree can fire before this page has one.
        if (page.SelfTest is { } selfTest)
        {
            selfTest.Clipboard = Copy;
        }
    }

    private async Task Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }
}
