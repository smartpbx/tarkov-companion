using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels.V2.LootScan;

namespace TarkovCompanion.App.Views.V2.LootScan;

public sealed partial class LootScanView : UserControl
{
    private LootScanViewModel? _wiredViewModel;
    private bool _attached;

    public LootScanView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += LootScanDataContextChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        _attached = true;
        Wire(DataContext as LootScanViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        _attached = false;
        Unwire();
        base.OnDetachedFromVisualTree(eventArgs);
    }

    private void LootScanDataContextChanged(object? sender, EventArgs eventArgs)
    {
        Unwire();
        if (_attached)
        {
            Wire(DataContext as LootScanViewModel);
        }
    }

    private void Wire(LootScanViewModel? viewModel)
    {
        if (viewModel is null || ReferenceEquals(viewModel, _wiredViewModel))
        {
            return;
        }

        _wiredViewModel = viewModel;
        viewModel.PageNavigated += PageNavigated;
    }

    private void Unwire()
    {
        if (_wiredViewModel is null)
        {
            return;
        }

        _wiredViewModel.PageNavigated -= PageNavigated;
        _wiredViewModel = null;
    }

    private void PageNavigated(object? sender, EventArgs eventArgs)
    {
        if (!ReferenceEquals(sender, _wiredViewModel))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var announcement = this.GetVisualDescendants()
                .OfType<TextBlock>()
                .FirstOrDefault(control => string.Equals(
                    AutomationProperties.GetAutomationId(control),
                    "v2-loot-scan-page-announcement",
                    StringComparison.Ordinal));
            announcement?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }
}
