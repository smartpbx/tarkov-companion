using Avalonia.Controls;
using Avalonia.Input.Platform;
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

    private void Wire()
    {
        if (DataContext is V2SetupWorkspaceViewModel { Settings: { } settings })
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
