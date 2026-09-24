using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Application.Services.Shell;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>Hosts the raid cockpit: mark placement and the canonical map renderer. The map
/// selector itself now lives in the shell's top bar (v2-shell-topbar-map).</summary>
public sealed partial class RaidCockpitView : UserControl
{
    private bool _panelDrag;

    public RaidCockpitView()
    {
        AvaloniaXamlLoader.Load(this);
        SizeChanged += CockpitSizeChanged;
        if (this.FindControl<Grid>("MapColumn") is { } mapColumn)
        {
            mapColumn.SizeChanged += MapColumnSizeChanged;
        }

        DataContextChanged += CockpitDataContextChanged;
    }

    private RaidCockpitViewModel? _cockpit;

    /// <summary>
    /// [#286] The renderer's own DataContext is the scene, so the cockpit's Draw mode is handed
    /// to it here rather than bound through the element whose context was overridden.
    /// </summary>
    private void CockpitDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_cockpit is not null)
        {
            _cockpit.PropertyChanged -= CockpitPropertyChanged;
        }

        _cockpit = DataContext as RaidCockpitViewModel;
        if (_cockpit is not null)
        {
            _cockpit.PropertyChanged += CockpitPropertyChanged;
        }

        SyncDrawMode();
        WatchRendererForInspect();
    }

    private void CockpitPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(RaidCockpitViewModel.IsDrawMode)
            or nameof(RaidCockpitViewModel.IsClickMode) or null)
        {
            SyncDrawMode();
        }

        if (eventArgs.PropertyName is nameof(RaidCockpitViewModel.Renderer) or null)
        {
            WatchRendererForInspect();
        }

        if (eventArgs.PropertyName is nameof(RaidCockpitViewModel.Inspection) or null)
        {
            PlaceInspectPopover();
        }
    }

    private void SyncDrawMode()
    {
        if (this.FindControl<MapSceneRendererView>("MapRenderer") is { } renderer)
        {
            renderer.IsDrawing = _cockpit?.IsDrawMode == true;
            renderer.IsClickMode = _cockpit?.IsClickMode == true;
        }
    }

    private System.ComponentModel.INotifyPropertyChanged? _inspectWatched;

    /// <summary>[#286] The popover stays beside its spot through pan, zoom and a turn.</summary>
    private void WatchRendererForInspect()
    {
        if (_inspectWatched is not null)
        {
            _inspectWatched.PropertyChanged -= InspectRendererChanged;
        }

        _inspectWatched = _cockpit?.Renderer;
        if (_inspectWatched is not null)
        {
            _inspectWatched.PropertyChanged += InspectRendererChanged;
        }
    }

    private void InspectRendererChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs eventArgs)
    {
        if (_cockpit?.HasInspection == true)
        {
            PlaceInspectPopover();
        }
    }

    /// <summary>
    /// [#286] Pins the Inspect popover just right of and below its spot, flipped to the other side
    /// where the map card ends, so it never covers the spot it describes.
    /// </summary>
    private void PlaceInspectPopover()
    {
        if (_cockpit is not { Inspection: { } inspection, Renderer: { } scene } ||
            this.FindControl<Canvas>("InspectLayer") is not { } layer ||
            this.FindControl<Border>("InspectPopover") is not { } popover ||
            this.FindControl<MapSceneRendererView>("MapRenderer")?.FindControl<Border>("PlanViewport") is not { } plan ||
            !scene.TryViewportPointAt(inspection.Point, out var x, out var y) ||
            plan.TranslatePoint(new Point(x, y), layer) is not { } at)
        {
            return;
        }

        const double Offset = 14;
        popover.Measure(Size.Infinity);
        var size = popover.DesiredSize;
        var left = at.X + Offset + size.Width > layer.Bounds.Width ? at.X - Offset - size.Width : at.X + Offset;
        var top = at.Y + Offset + size.Height > layer.Bounds.Height ? at.Y - Offset - size.Height : at.Y + Offset;
        Canvas.SetLeft(popover, Math.Max(4, left));
        Canvas.SetTop(popover, Math.Max(4, top));
    }

    private void RendererModeClicked(object? sender, MapScenePoint point) =>
        _cockpit?.ModeClicked(point);

    private void RouteSettingsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (_cockpit is { } cockpit && sender is Control button)
        {
            RaidMarkMenu.ForNewRoute(cockpit).ShowAt(button);
        }
    }

    private void RendererStrokeDrawn(object? sender, IReadOnlyList<MapScenePoint> points) =>
        _cockpit?.AddDrawing(points);

    private void RendererModeEscaped(object? sender, EventArgs eventArgs) =>
        _cockpit?.SetInteractionMode(MapInteractionMode.Navigate);

    private void DrawSettingsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (_cockpit is { } cockpit && sender is Control button)
        {
            RaidMarkMenu.ForNewDrawings(cockpit).ShowAt(button);
        }
    }

    /// <summary>
    /// [#266] The Raid plan's share of a narrow cockpit, and the strip's second row.
    /// </summary>
    /// <remarks>
    /// Interface scale went to 200%, and a 1920-wide window became a 960-wide shell: the 360-wide
    /// plan left the map 390 wide and the strip clipped Follow in half. The panel keeps the width
    /// the player dragged, and gives way only when the map would be left under 600 wide.
    /// </remarks>
    private void CockpitSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        if (this.FindControl<Border>("ContextPanel") is { } panel)
        {
            panel.MaxWidth = ShellLayout.SidePanelMaximum(
                eventArgs.NewSize.Width,
                RaidCockpitViewModel.MinimumContextPanelWidth);
        }
    }

    private void MapColumnSizeChanged(object? sender, SizeChangedEventArgs eventArgs)
    {
        if (this.FindControl<Grid>("ControlStrip") is not { } strip ||
            this.FindControl<Grid>("StripSecondRow") is not { } secondRow ||
            this.FindControl<Control>("TrafficChip") is not { } chip ||
            this.FindControl<Control>("PresentationControls") is not { } presentation)
        {
            return;
        }

        // Width only: moving controls to the second row changes the strip's height, never the
        // column's width, so this cannot flip back and forth. A row of its own rather than a
        // second row of the same Grid: a control spanning its Auto columns widened them, and the
        // chip was left a column one letter wide.
        // [#838] Width only as well, for the same reason: the column does not depend on the strip.
        presentation.Classes.Set("v2-compact", !ShellLayout.ControlStripFitsFullModeLabels(eventArgs.NewSize.Width));
        var oneRow = ShellLayout.ControlStripFitsOneRow(eventArgs.NewSize.Width);
        var host = oneRow ? strip : secondRow;
        if (chip.Parent == host)
        {
            return;
        }

        ((Panel)chip.Parent!).Children.Remove(chip);
        ((Panel)presentation.Parent!).Children.Remove(presentation);
        Grid.SetColumn(chip, oneRow ? 2 : 0);
        Grid.SetColumn(presentation, 1);
        host.Children.Add(presentation);
        host.Children.Add(chip);
        secondRow.IsVisible = !oneRow;
    }

    /// <summary>
    /// A right-click on bare map: a ping, or a waypoint when Shift is held.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] There is nothing to arm any more. The renderer raises this only when
    /// the gesture hit no object — anything under the pointer is a removal instead — so this and
    /// <see cref="RendererMarkerRightClicked"/> cannot both fire for one press.
    /// </remarks>
    private void RendererPlanRightClicked(object? sender, MapPlanGesture gesture)
    {
        if (DataContext is not RaidCockpitViewModel cockpit)
        {
            return;
        }

        // #289: Ctrl asks which lifetime, for the times a ping or a forever waypoint is wrong.
        if (gesture.IsMenu && sender is Control host)
        {
            RaidMarkMenu.ForPlacement(lifetime => cockpit.PlaceMarkAt(gesture.Point, lifetime), cockpit.NewMarkScope)
                .ShowAt(host, showAtPointer: true);
            return;
        }

        cockpit.PlaceMarkAt(gesture.Point, RaidCockpitViewModel.MarkKindFor(gesture.IsSecondary));
    }

    private void RendererMarkerRightClicked(object? sender, MapSceneObjectId objectId)
    {
        if (DataContext is not RaidCockpitViewModel cockpit)
        {
            return;
        }

        // [#286] A line of ours opens its own menu; a squadmate's is theirs to remove.
        if (cockpit.OwnDrawing(objectId) is { } drawing && sender is Control drawingHost)
        {
            RaidMarkMenu.ForDrawing(cockpit, drawing).ShowAt(drawingHost, showAtPointer: true);
            return;
        }

        // #289: a mark of ours opens its menu (Remove first, then scope and lifetime); anything
        // else keeps its one-gesture meaning.
        if (cockpit.OwnMarkRow(objectId) is { } row && sender is Control host)
        {
            RaidMarkMenu.ForMark(row).ShowAt(host, showAtPointer: true);
            return;
        }

        cockpit.RemoveMarkAt(objectId);
    }

    private void MarkOptionsClicked(object? sender, RoutedEventArgs eventArgs)
    {
        if (sender is Control { DataContext: RaidMarkRowViewModel row } button)
        {
            RaidMarkMenu.ForMark(row).ShowAt(button);
        }
    }

    /// <summary>
    /// Dragging the Raid plan's edge.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] Not a GridSplitter: the panel's width is a remembered number the
    /// view model owns, and a splitter would own it instead and forget it on every rebuild.
    /// Dragging left widens the panel, because the handle is on the panel's left edge, so the
    /// width is the distance from the pointer to the workspace's right edge.
    /// </remarks>
    private void PanelHandlePressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (sender is not Border handle ||
            !eventArgs.GetCurrentPoint(handle).Properties.IsLeftButtonPressed ||
            DataContext is not RaidCockpitViewModel { ShowsContextPanel: true })
        {
            return;
        }

        _panelDrag = true;
        eventArgs.Pointer.Capture(handle);
        eventArgs.Handled = true;
    }

    private void PanelHandleMoved(object? sender, PointerEventArgs eventArgs)
    {
        if (!_panelDrag || DataContext is not RaidCockpitViewModel cockpit)
        {
            return;
        }

        cockpit.ResizeContextPanel(Bounds.Width - eventArgs.GetPosition(this).X);
        eventArgs.Handled = true;
    }

    // Two handlers rather than one: PointerReleased and PointerCaptureLost carry different
    // argument types, and AXAML matches an event handler by its exact signature.
    private void PanelHandleReleased(object? sender, PointerReleasedEventArgs eventArgs) =>
        EndPanelDrag(eventArgs.Pointer);

    private void PanelHandleCaptureLost(object? sender, PointerCaptureLostEventArgs eventArgs) =>
        EndPanelDrag(eventArgs.Pointer);

    private void EndPanelDrag(IPointer pointer)
    {
        if (!_panelDrag)
        {
            return;
        }

        _panelDrag = false;
        pointer.Capture(null);
    }
}
