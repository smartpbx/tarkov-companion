using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Readiness;

namespace TarkovCompanion.App.Views.V2.Shell;

/// <summary>[#712 1-13] The Raid page's readiness strip; see <see cref="ReadinessStripViewModel"/>.</summary>
/// <remarks>It lends the strip this window's folder picker, which only a window has.</remarks>
public sealed partial class ReadinessStripView : UserControl
{
    public ReadinessStripView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ReadinessStripViewModel strip)
            {
                strip.PickFolder = PickFolderAsync;
            }
        };
    }

    private async Task<string?> PickFolderAsync(ReadinessItemKind kind)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            Title = ReadinessText.PickerTitle(kind),
        }).ConfigureAwait(true);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
