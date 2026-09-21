using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.App.Views.V2.Setup;

public sealed partial class V2SetupDatabaseStatusView : UserControl
{
    public V2SetupDatabaseStatusView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Opens the backup folder in the OS file manager, the same way <c>V2SetupWorkspaceView</c>'s
    /// own "Get the installer" already opens an address: the window's own launcher, never a
    /// process this application starts itself.
    /// </summary>
    private void OnOpenBackupFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is SetupDatabaseStatusViewModel { BackupFolderPath: { } folder })
        {
            // The folder path is on the page as text (inside the backup line), so a file manager
            // that will not open is not the end of the road and not worth a dialog. Through
            // ShellLauncher so the file manager does not inherit the install folder (#599).
            TarkovCompanion.App.Services.ShellLauncher.TryOpenFolder(folder);
        }
    }
}
