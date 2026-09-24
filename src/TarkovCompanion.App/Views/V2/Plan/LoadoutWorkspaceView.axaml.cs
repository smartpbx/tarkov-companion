using Avalonia;
using Avalonia.Controls;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.App.Views.V2.Plan;

public sealed partial class LoadoutWorkspaceView : UserControl
{
    public LoadoutWorkspaceView()
    {
        // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated method assigns the
        // x:Name fields the fit below needs.
        InitializeComponent();
        SizeChanged += (_, e) =>
            SuggestionsHeader.Classes.Set("v2-loadout-compact", PageFit.LoadoutSuggestionsStacked(e.NewSize.Width));
    }

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
