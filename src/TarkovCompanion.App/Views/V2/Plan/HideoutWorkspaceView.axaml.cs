using Avalonia.Controls;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.App.Views.V2.Plan;

public sealed partial class HideoutWorkspaceView : UserControl
{
    public HideoutWorkspaceView()
    {
        // InitializeComponent, not AvaloniaXamlLoader.Load: only the generated method assigns the
        // x:Name fields the fit below needs.
        InitializeComponent();
        SizeChanged += (_, e) => ApplyFit(e.NewSize.Width);
    }

    /// <summary>[#832] The station list and the two sections narrow with the page; see <see cref="PageFit"/>.</summary>
    private void ApplyFit(double pageWidth)
    {
        HideoutBody.ColumnDefinitions[0].Width = new GridLength(PageFit.HideoutList(pageWidth));
        var section = PageFit.HideoutSection(pageWidth);
        HideoutNeedsColumn.Width = section;
        HideoutNeedsSection.Width = section;
        HideoutUpgradesColumn.Width = section;
    }
}
