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
