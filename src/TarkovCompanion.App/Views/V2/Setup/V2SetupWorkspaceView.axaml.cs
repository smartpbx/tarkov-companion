using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.App.Views.V2.Setup;

/// <summary>The native V2 Setup page (#292): section tabs over the same view models V1 uses.</summary>
public sealed partial class V2SetupWorkspaceView : UserControl
{
    private V2SetupWorkspaceViewModel? _wired;
    private (string? Screenshots, string? Logs) _savedFolders;

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

    /// <summary>
    /// [#902 P6] A game folder is saved when its field loses focus, and only when it changed. The
    /// Save button beside the two fields was the only one in Setup; every other row saves itself.
    /// </summary>
    private void OnFolderLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not V2SetupWorkspaceViewModel { Settings: { } settings })
        {
            return;
        }

        var chosen = (settings.ScreenshotFolder, settings.LogFolder);
        if (chosen == _savedFolders)
        {
            return;
        }

        _savedFolders = chosen;
        if (settings.SaveGameFoldersCommand.CanExecute(null))
        {
            settings.SaveGameFoldersCommand.Execute(null);
        }
    }

    /// <summary>A new section starts at its top; the tab row above the scroller never moves.</summary>
    private void OnPagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(V2SetupWorkspaceViewModel.Selected) &&
            this.FindControl<ScrollViewer>("SectionScroller") is { } scroller)
        {
            scroller.Offset = default;
        }
    }

    private void Wire()
    {
        if (DataContext is not V2SetupWorkspaceViewModel page)
        {
            return;
        }

        if (!ReferenceEquals(_wired, page))
        {
            if (_wired is not null)
            {
                _wired.PropertyChanged -= OnPagePropertyChanged;
            }

            _wired = page;
            page.PropertyChanged += OnPagePropertyChanged;
            _savedFolders = page.Settings is { } folders ? (folders.ScreenshotFolder, folders.LogFolder) : default;
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
            Title = SetupText.WorkspaceQuestSyncPickerTitle,
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
