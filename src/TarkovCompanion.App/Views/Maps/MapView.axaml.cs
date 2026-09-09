using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.Views.Maps;

public sealed partial class MapView : UserControl
{
    private bool _isPanning;
    private Point _panStart;
    private Vector _panOffset;

    public MapView() => AvaloniaXamlLoader.Load(this);

    private async void LocationSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && sender is ComboBox { SelectedItem: MapLocation location } &&
            !ReferenceEquals(viewModel.SelectedLocation, location))
        {
            await viewModel.SelectLocationAsync(location);
        }
    }

    private async void VariantSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && sender is ComboBox { SelectedItem: MapVariant variant } &&
            !ReferenceEquals(viewModel.SelectedVariant, variant))
        {
            await viewModel.SelectVariantAsync(variant);
        }
    }

    private async void FloorSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && sender is ComboBox { SelectedItem: MapFloorDefinition floor } &&
            !ReferenceEquals(viewModel.SelectedFloor, floor))
        {
            await viewModel.SelectFloorAsync(floor);
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
        _panOffset = ViewportScrollViewer.Offset;
        eventArgs.Pointer.Capture(sender as Control);
        eventArgs.Handled = true;
    }

    private void ViewportPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_isPanning)
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

    private async void AttributionClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(viewModel.AttributionUri);
        }
    }

    private async void LicenseClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(viewModel.LicenseUri);
        }
    }
}
