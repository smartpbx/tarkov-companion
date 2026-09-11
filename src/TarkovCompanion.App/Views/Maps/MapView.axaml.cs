using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.Views.Maps;

public sealed partial class MapView : UserControl
{
    /// <summary>Room left around a fitted map so its edge is not flush with the panel.</summary>
    private const double ViewportPadding = 16;

    private MapViewModel? _boundViewModel;
    private bool _subscribedToViewport;
    private bool _isPanning;
    private Point _panStart;
    private Vector _panOffset;

    public MapView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += MapDataContextChanged;
    }

    /// <summary>
    /// Subscribes to the viewport once the control is actually in the tree.
    /// </summary>
    /// <remarks>
    /// Named controls are not available in the constructor here, and touching one there
    /// threw a NullReferenceException while the main window was being built, so no window
    /// ever appeared.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        if (!_subscribedToViewport && ViewportScrollViewer is not null)
        {
            _subscribedToViewport = true;
            ViewportScrollViewer.SizeChanged += ViewportSizeChanged;
        }

        FitAndCentre();
    }

    private void MapDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.FitRequested -= FitRequested;
        }

        _boundViewModel = DataContext as MapViewModel;
        if (_boundViewModel is not null)
        {
            _boundViewModel.FitRequested += FitRequested;
            FitAndCentre();
        }
    }

    private void ViewportSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        if (_boundViewModel?.IsAutoFit == true)
        {
            FitAndCentre();
        }
    }

    private void FitRequested(object? sender, EventArgs eventArgs) => FitAndCentre();

    /// <summary>
    /// Scales the map to the panel and puts the middle of it in the middle of the view.
    /// </summary>
    /// <remarks>
    /// A tile grid is laid out in upstream pixel coordinates and is routinely several times
    /// the panel's size, with empty tiles at the top-left. Opening at 100% therefore showed
    /// blank space and left the player to hunt for the map by dragging.
    /// </remarks>
    private void FitAndCentre()
    {
        if (DataContext is not MapViewModel viewModel)
        {
            return;
        }

        if (ViewportScrollViewer is null)
        {
            return;
        }

        var available = ViewportScrollViewer.Bounds.Size;
        if (available.Width <= 0 || available.Height <= 0)
        {
            return;
        }

        viewModel.ApplyFit(available.Width - ViewportPadding, available.Height - ViewportPadding);

        // Centring has to wait for the resized content to be measured, otherwise the
        // scrollable extent is still the previous one and the offset is clamped away.
        Dispatcher.UIThread.Post(CentreViewport, DispatcherPriority.Background);
    }

    private void CentreViewport()
    {
        if (ViewportScrollViewer is null)
        {
            return;
        }

        var extent = ViewportScrollViewer.Extent;
        var viewport = ViewportScrollViewer.Viewport;
        ViewportScrollViewer.Offset = new(
            Math.Max(0, (extent.Width - viewport.Width) / 2),
            Math.Max(0, (extent.Height - viewport.Height) / 2));
    }

    private async void LocationSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && sender is ComboBox { SelectedItem: MapLocation location } &&
            !ReferenceEquals(viewModel.SelectedLocation, location))
        {
            await RunGuardedAsync(viewModel, () => viewModel.SelectLocationAsync(location));
        }
    }

    private async void VariantSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && sender is ComboBox { SelectedItem: MapVariant variant } &&
            !ReferenceEquals(viewModel.SelectedVariant, variant))
        {
            await RunGuardedAsync(viewModel, () => viewModel.SelectVariantAsync(variant));
        }
    }

    private async void FloorSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && sender is ComboBox { SelectedItem: MapFloorDefinition floor } &&
            !ReferenceEquals(viewModel.SelectedFloor, floor))
        {
            await RunGuardedAsync(viewModel, () => viewModel.SelectFloorAsync(floor));
        }
    }

    private void OverlayVisibilityChanged(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel &&
            sender is ToggleButton { Tag: MapOverlayKind kind, IsChecked: { } isChecked })
        {
            viewModel.ToggleOverlay(kind, isChecked);
        }
    }

    private void OverlayHighlightClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel &&
            sender is Button { Tag: MapOverlayKind kind, DataContext: MapOverlayViewModel overlay })
        {
            viewModel.HighlightOverlay(overlay.IsHighlighted ? null : kind);
        }
    }

    private void ViewportPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel)
        {
            viewModel.ChangeZoom(eventArgs.Delta.Y);
            eventArgs.Handled = true;
        }
    }

    private void ViewportPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panStart = eventArgs.GetPosition(this);
        _panOffset = ViewportScrollViewer?.Offset ?? default;
        eventArgs.Pointer.Capture(sender as Control);
        eventArgs.Handled = true;
    }

    private void ViewportPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_isPanning)
        {
            return;
        }

        if (ViewportScrollViewer is null)
        {
            return;
        }

        var current = eventArgs.GetPosition(this);
        ViewportScrollViewer.Offset = new(
            _panOffset.X - (current.X - _panStart.X),
            _panOffset.Y - (current.Y - _panStart.Y));
        eventArgs.Handled = true;
    }

    private void ViewportPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_isPanning)
        {
            return;
        }

        _isPanning = false;
        eventArgs.Pointer.Capture(null);
        eventArgs.Handled = true;
    }

    private void ZoomInClick(object? sender, RoutedEventArgs eventArgs) =>
        (DataContext as MapViewModel)?.ChangeZoom(1);

    private void ZoomOutClick(object? sender, RoutedEventArgs eventArgs) =>
        (DataContext as MapViewModel)?.ChangeZoom(-1);

    private void FitClick(object? sender, RoutedEventArgs eventArgs) =>
        (DataContext as MapViewModel)?.RequestFit();

    private async void AttributionClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await RunGuardedAsync(viewModel, () => launcher.LaunchUriAsync(viewModel.AttributionUri).AsTask());
        }
    }

    private async void LicenseClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await RunGuardedAsync(viewModel, () => launcher.LaunchUriAsync(viewModel.LicenseUri).AsTask());
        }
    }

    /// <summary>
    /// Runs an event handler's work without letting a failure kill the application.
    /// </summary>
    /// <remarks>
    /// These are all `async void` handlers, which is what an Avalonia event handler has to
    /// be. An exception escaping one is raised on the dispatcher and terminates the process,
    /// so a single unreadable preferences file or a missing browser could take the window
    /// down mid-raid. The failure belongs in the map's status line instead.
    /// </remarks>
    private static async Task RunGuardedAsync(MapViewModel viewModel, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CrashLog.Write("map-interaction", exception);
            viewModel.ReportInteractionFailure(exception.Message);
        }
    }
}
