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
    private ScrollViewer? _viewport;
    private bool _subscribedToViewport;

    /// <summary>
    /// The scrolling viewport, resolved by name rather than through a generated field.
    /// </summary>
    /// <remarks>
    /// Every view here loads its XAML with AvaloniaXamlLoader.Load rather than the generated
    /// initializer, and that path does not populate x:Name backing fields. The field was
    /// therefore always null: clicking the map dereferenced it and killed the process, and
    /// panning could never have worked. Resolving through the name scope is what actually
    /// finds the control.
    /// </remarks>
    private ScrollViewer? Viewport => _viewport ??= this.FindControl<ScrollViewer>("ViewportScrollViewer");

    /// <summary>The unscaled map canvas, resolved by name for the same reason as the viewport.</summary>
    private Canvas? Surface => _surface ??= this.FindControl<Canvas>("MapSurface");

    /// <summary>
    /// How far the pointer may move before a press is a drag rather than a click.
    /// </summary>
    /// <remarks>
    /// Without this a click on a marker that moved the pointer by a pixel panned the map by a
    /// pixel and selected nothing, which is most clicks.
    /// </remarks>
    private const double DragThreshold = 3;

    /// <summary>Created on first use, after the platform is up, and kept for the window's life.</summary>
    private static readonly Lazy<Cursor> PanningCursor = new(() => new Cursor(StandardCursorType.SizeAll));

    private Canvas? _surface;
    private bool _pointerDown;
    private bool _dragging;
    private MapOverlayElementViewModel? _pressedMarker;
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
        if (!_subscribedToViewport && Viewport is not null)
        {
            _subscribedToViewport = true;
            Viewport.SizeChanged += ViewportSizeChanged;
        }

        FitAndCentre();
    }

    private void MapDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_boundViewModel is not null)
        {
            _boundViewModel.FitRequested -= FitRequested;
            _boundViewModel.PlayerFollowRequested -= PlayerFollowRequested;
        }

        _boundViewModel = DataContext as MapViewModel;
        if (_boundViewModel is not null)
        {
            _boundViewModel.FitRequested += FitRequested;
            _boundViewModel.PlayerFollowRequested += PlayerFollowRequested;
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

    private void PlayerFollowRequested(object? sender, EventArgs eventArgs) => CentreOnPlayer();

    /// <summary>
    /// Puts the player in the middle of the panel, at a readable scale.
    /// </summary>
    /// <remarks>
    /// A whole map fitted to the panel is the right view before a raid and the wrong one during
    /// it: at that scale the player is a dot among street names. When a screenshot arrives the
    /// view moves to them and zooms to something a person can actually read, which is what
    /// makes this a panel you glance at rather than one you operate.
    ///
    /// It only zooms in, never out. Somebody who has deliberately zoomed further in to read a
    /// building should not be pulled back out by the next screenshot.
    /// </remarks>
    private void CentreOnPlayer()
    {
        if (Viewport is null || DataContext is not MapViewModel viewModel ||
            viewModel.PlayerMarkers.Count == 0)
        {
            return;
        }

        if (viewModel.ZoomScale < PlayerFollowZoom)
        {
            viewModel.SetFollowZoom(PlayerFollowZoom);
        }

        // The scaled content has to be measured before the offset means anything, exactly as
        // it does when fitting.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (Viewport is null || DataContext is not MapViewModel model ||
                    model.PlayerMarkers.Count == 0)
                {
                    return;
                }

                var marker = model.PlayerMarkers[0];
                var scale = model.ZoomScale;
                var viewport = Viewport.Viewport;
                var extent = Viewport.Extent;
                Viewport.Offset = new(
                    Math.Clamp((marker.CenterX * scale) - (viewport.Width / 2), 0, Math.Max(0, extent.Width - viewport.Width)),
                    Math.Clamp((marker.CenterY * scale) - (viewport.Height / 2), 0, Math.Max(0, extent.Height - viewport.Height)));
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// The scale the view settles on when it follows the player.
    /// </summary>
    /// <remarks>
    /// Chosen so street names and building labels are legible from a second monitor, which is
    /// the distance this is read from.
    /// </remarks>
    private const double PlayerFollowZoom = 1.0;

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

        if (Viewport is null)
        {
            return;
        }

        var available = Viewport.Bounds.Size;
        if (available.Width <= 0 || available.Height <= 0)
        {
            return;
        }

        viewModel.ApplyFit(available.Width - ViewportPadding, available.Height - ViewportPadding);

        // Centring has to wait for the resized content to be measured, otherwise the
        // scrollable extent is still the previous one and the offset is clamped away.
        Dispatcher.UIThread.Post(CentreViewport, DispatcherPriority.Background);
    }

    /// <summary>
    /// Centres the view on the drawn map rather than on the middle of the tile grid.
    /// </summary>
    /// <remarks>
    /// Upstream bounds extend past the artwork, so the grid's centre can sit well away from
    /// anything visible. Centring on the loaded tiles is what puts the map in front of the
    /// player.
    /// </remarks>
    private void CentreViewport()
    {
        if (Viewport is null || DataContext is not MapViewModel viewModel)
        {
            return;
        }

        var extent = Viewport.Extent;
        var viewport = Viewport.Viewport;
        var content = viewModel.ContentBounds;
        if (content.Width <= 0 || content.Height <= 0)
        {
            Viewport.Offset = new(
                Math.Max(0, (extent.Width - viewport.Width) / 2),
                Math.Max(0, (extent.Height - viewport.Height) / 2));
            return;
        }

        var scale = viewModel.ZoomScale;
        var centreX = (content.X + (content.Width / 2)) * scale;
        var centreY = (content.Y + (content.Height / 2)) * scale;
        Viewport.Offset = new(
            Math.Clamp(centreX - (viewport.Width / 2), 0, Math.Max(0, extent.Width - viewport.Width)),
            Math.Clamp(centreY - (viewport.Height / 2), 0, Math.Max(0, extent.Height - viewport.Height)));
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
        if (Viewport is null)
        {
            return;
        }

        ZoomAbout(eventArgs.GetPosition(Viewport), eventArgs.Delta.Y);
        eventArgs.Handled = true;
    }

    /// <summary>
    /// Zooms so that the point under the pointer stays under the pointer.
    /// </summary>
    /// <remarks>
    /// Zooming about the corner of the viewport, which is what changing the scale alone does,
    /// sends whatever the player was looking at off the screen and leaves them dragging it
    /// back. Anchoring the zoom on the pointer is the single biggest difference between a map
    /// that is operated and one that is fought with.
    ///
    /// The scrollable extent only grows once the resized canvas has been laid out, and an
    /// offset set before that is clamped to the old extent, which is what made zooming in near
    /// an edge jump. Laying out first is what makes the new offset land.
    /// </remarks>
    private void ZoomAbout(Point viewportPoint, double wheelDelta)
    {
        if (DataContext is not MapViewModel viewModel || Viewport is null)
        {
            return;
        }

        var anchor = Surface is { } surface ? Viewport.TranslatePoint(viewportPoint, surface) : null;
        viewModel.ChangeZoom(wheelDelta);
        if (anchor is not { } content)
        {
            return;
        }

        Viewport.UpdateLayout();
        var scale = viewModel.ZoomScale;
        Viewport.Offset = new(
            (content.X * scale) - viewportPoint.X,
            (content.Y * scale) - viewportPoint.Y);
    }

    /// <summary>
    /// Starts a press that will turn out to be a drag, a click on a marker, or a click on
    /// bare map, and can only be told apart on release.
    /// </summary>
    /// <remarks>
    /// The marker is read off the pressed element's data context now rather than on release,
    /// because by then the pointer is captured by the canvas and the source is the canvas.
    /// </remarks>
    private void ViewportPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        // Right-click marks a place for the group, which is the gesture his client uses and
        // the one nobody has to be taught. It is read on press rather than release because it
        // never becomes a drag: nothing pans with the right button.
        if (eventArgs.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            // The same button both ways round, on the thing under it. Right-clicking bare map
            // means "mark here"; right-clicking a mark means "that one is done with". There is
            // no ambiguity to resolve — a place either already has a mark on it or does not —
            // and the alternative is a context menu, which is a second click and a read in the
            // middle of a raid.
            if ((eventArgs.Source as StyledElement)?.DataContext is GroupMarkViewModel mark)
            {
                (DataContext as MapViewModel)?.RemoveMark(mark);
                eventArgs.Handled = true;
                return;
            }

            // Through the name scope, not the generated field. Every view here loads its XAML
            // with AvaloniaXamlLoader.Load, which never populates an x:Name backing field, so
            // MapSurface was always null and the position came back relative to the window
            // instead of the canvas. The mark was then computed from a point that had the
            // sidebar and the title bar in it, which is how a gesture that ran every time
            // managed to put nothing anywhere.
            if (Surface is { } surface)
            {
                MarkForGroup(eventArgs.GetPosition(surface), eventArgs.KeyModifiers);
            }

            eventArgs.Handled = true;
            return;
        }

        if (!eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (eventArgs.ClickCount == 2)
        {
            // A double click is the fastest way back to the whole map from wherever a drag
            // and a zoom have left it.
            _pointerDown = false;
            (DataContext as MapViewModel)?.RequestFit();
            eventArgs.Handled = true;
            return;
        }

        _pointerDown = true;
        _dragging = false;
        _pressedMarker = (eventArgs.Source as StyledElement)?.DataContext as MapOverlayElementViewModel;
        _panStart = eventArgs.GetPosition(this);
        _panOffset = Viewport?.Offset ?? default;
        eventArgs.Pointer.Capture(sender as Control);
        eventArgs.Handled = true;
    }

    /// <summary>
    /// Marks the clicked place for the group.
    /// </summary>
    /// <remarks>
    /// Shift makes it a ping instead of a waypoint, which is the difference between "look
    /// here" and "we are going here". Both are one gesture away because deciding which you
    /// meant is not something to do through a menu while somebody is shooting at you.
    ///
    /// Fire and forget. The mark comes back on the next exchange like everybody else's, so one
    /// code path draws every mark and the sender never sees a version of the group's state
    /// that the group does not have.
    /// </remarks>
    private void MarkForGroup(Point canvasPoint, KeyModifiers modifiers)
    {
        if (DataContext is not MapViewModel viewModel)
        {
            return;
        }

        _ = viewModel.MarkForGroupAsync(canvasPoint, isPing: modifiers.HasFlag(KeyModifiers.Shift));
    }

    private void ViewportPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_pointerDown || Viewport is null)
        {
            return;
        }

        var current = eventArgs.GetPosition(this);
        var deltaX = current.X - _panStart.X;
        var deltaY = current.Y - _panStart.Y;
        if (!_dragging)
        {
            if (Math.Abs(deltaX) < DragThreshold && Math.Abs(deltaY) < DragThreshold)
            {
                return;
            }

            // The cursor changes only once a drag is under way, so a click never flashes it.
            _dragging = true;
            if (sender is Control surface)
            {
                surface.Cursor = PanningCursor.Value;
            }
        }

        Viewport.Offset = new(_panOffset.X - deltaX, _panOffset.Y - deltaY);
        (DataContext as MapViewModel)?.ReportManualPan();
        eventArgs.Handled = true;
    }

    private void ViewportPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_pointerDown)
        {
            return;
        }

        _pointerDown = false;
        eventArgs.Pointer.Capture(null);
        if (sender is Control surface)
        {
            surface.ClearValue(CursorProperty);
        }

        if (!_dragging && DataContext is MapViewModel viewModel)
        {
            if (_pressedMarker is { } marker)
            {
                viewModel.SelectMarker(marker);
            }
            else
            {
                viewModel.ClearSelection();
            }
        }

        _dragging = false;
        _pressedMarker = null;
        eventArgs.Handled = true;
    }

    /// <summary>The buttons zoom about the middle of the panel, which is where the eye is.</summary>
    private void ZoomInClick(object? sender, RoutedEventArgs eventArgs) => ZoomFromButton(1);

    private void ZoomOutClick(object? sender, RoutedEventArgs eventArgs) => ZoomFromButton(-1);

    private void ZoomFromButton(double wheelDelta)
    {
        if (Viewport is null)
        {
            return;
        }

        ZoomAbout(new Point(Viewport.Bounds.Width / 2, Viewport.Bounds.Height / 2), wheelDelta);
    }

    /// <summary>
    /// Turns following on or off, and moves to the player immediately when turned on.
    /// </summary>
    /// <remarks>
    /// Waiting for the next screenshot before honouring the button would make it look broken,
    /// since a player may not take another for several minutes.
    /// </remarks>
    private async void ArtworkClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel)
        {
            await RunGuardedAsync(viewModel, viewModel.ToggleArtworkAsync);
        }
    }

    private void AutoFloorClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel)
        {
            viewModel.ToggleAutoFloor();
        }
    }

    private void FollowClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MapViewModel viewModel)
        {
            return;
        }

        viewModel.ToggleFollowPlayer();
        if (viewModel.FollowsPlayer)
        {
            CentreOnPlayer();
        }
    }

    private void FitClick(object? sender, RoutedEventArgs eventArgs) =>
        (DataContext as MapViewModel)?.RequestFit();

    private async void GroupNamesClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel)
        {
            await RunGuardedAsync(viewModel, viewModel.ToggleGroupNamesAsync);
        }
    }

    private async void RotateClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel)
        {
            await RunGuardedAsync(viewModel, viewModel.RotateAsync);
        }
    }

    private async void FloorVariantClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel)
        {
            await RunGuardedAsync(viewModel, viewModel.UseFloorVariantAsync);
        }
    }

    private async void AttributionClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await RunGuardedAsync(viewModel, () => launcher.LaunchUriAsync(viewModel.AttributionUri));
        }
    }

    private async void LicenseClick(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MapViewModel viewModel && TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
        {
            await RunGuardedAsync(viewModel, () => launcher.LaunchUriAsync(viewModel.LicenseUri));
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
