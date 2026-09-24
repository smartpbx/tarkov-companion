using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;

namespace TarkovCompanion.App.Views.V2.Plan;

public sealed partial class LoadoutWorkspaceView : UserControl
{
    public LoadoutWorkspaceView() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// #307: the suggestions are re-read each time the page is shown, since a quest handed in or a
    /// stash scanned on another page changes them.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is LoadoutPageViewModel { Suggestions: { } suggestions })
        {
            suggestions.RefreshAsync(CancellationToken.None).Observe("loadout", "suggestions");
        }
    }
}
