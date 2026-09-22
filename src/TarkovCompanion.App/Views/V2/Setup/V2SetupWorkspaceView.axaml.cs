using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
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
    private void OnGetInstaller(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is V2SetupWorkspaceViewModel { Settings.InstallerLocation: { } installer })
        {
            // The address is on the page as text, so a browser that will not open is not the end
            // of the road and is not worth a dialog. Through ShellLauncher rather than the
            // window's launcher so the browser does not inherit the install folder (#599).
            TarkovCompanion.App.Services.ShellLauncher.TryOpen(installer);
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

        if (page.QuestSync is { } questSync)
        {
            questSync.ChooseFiles = PickQuestScreenshotsAsync;
        }
    }

    private async Task<IReadOnlyList<string>> PickQuestScreenshotsAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
        {
            return [];
        }

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = "Choose TASKS screenshots",
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        }).ConfigureAwait(true);
        return picked.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Select(path => path!).ToArray();
    }

    private async Task Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }
}
