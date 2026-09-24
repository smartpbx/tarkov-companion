using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.Intel;

namespace TarkovCompanion.App.Views.V2.Intel;

public sealed partial class FleaWorkspaceView : UserControl
{
    /// <summary>
    /// #284: "Offers as seen at 14:32" turns into "… 7 min ago, may be gone" while the page is open,
    /// so a ranking left on screen does not keep reading as fresh.
    /// </summary>
    private static readonly TimeSpan AgeTick = TimeSpan.FromSeconds(30);

    private DispatcherTimer? _ageTimer;

    public FleaWorkspaceView() => AvaloniaXamlLoader.Load(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RefreshScanAge();
        _ageTimer ??= new DispatcherTimer(AgeTick, DispatcherPriority.Background, (_, _) => RefreshScanAge());
        _ageTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _ageTimer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void RefreshScanAge() => (DataContext as FleaWorkspaceViewModel)?.Scan?.RefreshAge();
}
