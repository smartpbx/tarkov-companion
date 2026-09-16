using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Views.V2.MapRenderer;

/// <summary>Responsive pointer, touch, and keyboard handoff for the canonical map view.</summary>
public sealed partial class MapSceneRendererView : UserControl
{
    private const double CompactWidth = 860;
    private const double NarrowHeaderWidth = 600;
    private const double DragThreshold = 4;

    private bool _pointerDown;
    private bool _dragging;
    private bool _viewportEventsAttached;
    private Point _pointerStart;

    public MapSceneRendererView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += RendererDataContextChanged;
    }

    /// <summary>
    /// A plain click (not a drag or a pan) landed on the plan, at that scene point.
    /// </summary>
    /// <remarks>
    /// Raised whatever the click hit, selection included, so a host arming "place a mark here"
    /// does not have to duplicate this view's own hit-testing to find out where the pointer was.
    /// A host that is not placing anything simply leaves this unhandled.
    /// </remarks>
    public event EventHandler<MapScenePoint>? PlanClicked;

    /// <summary>
    /// A right-click (or equivalent secondary gesture) landed on an object on the plan.
    /// </summary>
    /// <remarks>
    /// Raised only when the gesture actually hit something; a right-click on bare map raises
    /// nothing, so a host wiring "right-click removes a mark" never has to check what a bare-map
    /// right-click used to do before this existed.
    /// </remarks>
    public event EventHandler<MapSceneObjectId>? MarkerRightClicked;

    /// <summary>Wires named XAML controls only after they exist in the visual tree.</summary>
    /// <remarks>
    /// Avalonia can finish the code-behind constructor before generated <c>x:Name</c> fields are
    /// assigned. Subscribing to PlanViewport in the constructor made the packaged gallery exit
    /// before it showed a window even though compiled-XAML and unit builds succeeded.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        base.OnAttachedToVisualTree(eventArgs);
        if (!_viewportEventsAttached && PlanViewport is not null)
        {
            _viewportEventsAttached = true;
            SizeChanged += RendererSizeChanged;
            PlanViewport.SizeChanged += PlanViewportSizeChanged;
        }

        UpdateResponsiveLayout();
        UpdateViewport();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        if (_viewportEventsAttached)
        {
            SizeChanged -= RendererSizeChanged;
            if (PlanViewport is not null)
            {
                PlanViewport.SizeChanged -= PlanViewportSizeChanged;
            }

            _viewportEventsAttached = false;
        }

        base.OnDetachedFromVisualTree(eventArgs);
    }

    private void RendererDataContextChanged(object? sender, EventArgs eventArgs) => UpdateViewport();

    private void RendererSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        UpdateResponsiveLayout();
        UpdateViewport();
    }

    private void UpdateResponsiveLayout()
    {
        if (RendererHeader is null || RendererTitle is null || RendererCommands is null ||
            RendererBody is null || PlanViewport is null || DetailsPanel is null)
        {
            return;
        }

        var compact = Bounds.Width < CompactWidth;
        var narrowHeader = Bounds.Width < NarrowHeaderWidth;
        RendererHeader.ColumnDefinitions = new(narrowHeader ? "*" : "*,Auto");
        RendererHeader.RowDefinitions = new(narrowHeader ? "Auto,Auto" : "Auto");
        Grid.SetColumn(RendererTitle, 0);
        Grid.SetRow(RendererTitle, 0);
        Grid.SetColumn(RendererCommands, narrowHeader ? 0 : 1);
        Grid.SetRow(RendererCommands, narrowHeader ? 1 : 0);
        RendererBody.ColumnDefinitions = new(compact ? "*" : "2*,*");
        RendererBody.RowDefinitions = new(compact ? "Auto,Auto" : "Auto");
        Grid.SetColumn(PlanViewport, 0);
        Grid.SetRow(PlanViewport, 0);
        Grid.SetColumn(DetailsPanel, compact ? 0 : 1);
        Grid.SetRow(DetailsPanel, compact ? 1 : 0);
        DetailsPanel.MaxHeight = compact ? 420 : 700;
    }

    private void PlanViewportSizeChanged(object? sender, SizeChangedEventArgs eventArgs) => UpdateViewport();

    private void UpdateViewport()
    {
        if (PlanViewport is not null && DataContext is MapSceneRendererViewModel renderer)
        {
            renderer.SetViewportSize(PlanViewport.Bounds.Width, PlanViewport.Bounds.Height);
        }
    }

    private void PlanPointerPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        var current = eventArgs.GetCurrentPoint(PlanViewport);
        if (current.Properties.IsRightButtonPressed)
        {
            // Checked here rather than through the same TrySelectAt path a left click uses: a
            // right click never changes selection, only asks what, if anything, it landed on.
            if (DataContext is MapSceneRendererViewModel rightClickRenderer)
            {
                var position = eventArgs.GetPosition(PlanViewport);
                if (rightClickRenderer.TryHitObjectAt(position.X, position.Y, out var objectId))
                {
                    MarkerRightClicked?.Invoke(this, objectId);
                    eventArgs.Handled = true;
                }
            }

            return;
        }

        if (!current.Properties.IsLeftButtonPressed ||
            (eventArgs.Source as StyledElement)?.DataContext is MapSceneRendererObjectViewModel)
        {
            return;
        }

        _pointerDown = true;
        _dragging = false;
        _pointerStart = eventArgs.GetPosition(PlanViewport);
        eventArgs.Pointer.Capture(PlanViewport);
        eventArgs.Handled = true;
    }

    private void PlanPointerMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_pointerDown)
        {
            return;
        }

        var current = eventArgs.GetPosition(PlanViewport);
        var delta = current - _pointerStart;
        if (!_dragging && Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        _dragging = true;
        eventArgs.Handled = true;
    }

    private void PlanPointerReleased(object? sender, PointerReleasedEventArgs eventArgs)
    {
        if (!_pointerDown)
        {
            return;
        }

        _pointerDown = false;
        eventArgs.Pointer.Capture(null);
        var current = eventArgs.GetPosition(PlanViewport);
        if (DataContext is MapSceneRendererViewModel renderer)
        {
            if (_dragging)
            {
                var delta = current - _pointerStart;
                renderer.RequestPan(delta.X, delta.Y);
            }
            else
            {
                if (!renderer.TrySelectAt(current.X, current.Y))
                {
                    renderer.ClearSelection();
                }

                if (renderer.TryScenePointAt(current.X, current.Y, out var point))
                {
                    PlanClicked?.Invoke(this, point);
                }
            }
        }

        _dragging = false;
        eventArgs.Handled = true;
    }

    private void PlanPointerWheelChanged(object? sender, PointerWheelEventArgs eventArgs)
    {
        if (DataContext is not MapSceneRendererViewModel renderer || eventArgs.Delta.Y == 0)
        {
            return;
        }

        renderer.RequestZoom(eventArgs.Delta.Y);
        eventArgs.Handled = true;
    }

    private void RendererKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (DataContext is not MapSceneRendererViewModel renderer)
        {
            return;
        }

        if (eventArgs.Key == Key.Escape && renderer.HasSelection)
        {
            renderer.ClearSelection();
            eventArgs.Handled = true;
            return;
        }

        // Plain arrows, digits, and letters retain their normal focus, scrolling, and assistive
        // technology behavior. Map shortcuts are explicit Alt combinations.
        if (eventArgs.KeyModifiers != KeyModifiers.Alt)
        {
            return;
        }

        switch (eventArgs.Key)
        {
            case Key.F:
                renderer.FitPlanCommand.Execute(null);
                break;
            case Key.D1:
                renderer.RequestMode(MapSceneMode.Flat2D);
                break;
            case Key.D2:
                renderer.RequestMode(MapSceneMode.FloorStack2D);
                break;
            case Key.D3:
                renderer.RequestMode(MapSceneMode.Interior3D);
                break;
            default:
                return;
        }

        eventArgs.Handled = true;
    }
}
