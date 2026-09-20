using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>Hosts the raid cockpit: mark placement and the canonical map renderer. The map
/// selector itself now lives in the shell's top bar (v2-shell-topbar-map).</summary>
public sealed partial class RaidCockpitView : UserControl
{
    /// <summary>How long after the pointer leaves the map the floating controls fade.</summary>
    /// <remarks>
    /// [V2 rough package 22] Long enough that crossing the map on the way to a control does not
    /// make it vanish, short enough that a glance at the map mid-raid is a map and not a row of
    /// buttons. The same three seconds V1 settled on (#388).
    /// </remarks>
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(3);

    private const string IdleClass = "v2-raid-idle";

    private readonly DispatcherTimer _idleTimer;
    private bool _panelDrag;

    public RaidCockpitView()
    {
        AvaloniaXamlLoader.Load(this);
        _idleTimer = new(DispatcherPriority.Background) { Interval = IdleAfter };
        _idleTimer.Tick += IdleElapsed;
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
        if (DataContext is RaidCockpitViewModel cockpit)
        {
            cockpit.PlaceMarkAt(gesture.Point, RaidCockpitViewModel.MarkKindFor(gesture.IsSecondary));
        }
    }

    private void RendererMarkerRightClicked(object? sender, MapSceneObjectId objectId)
    {
        if (DataContext is RaidCockpitViewModel cockpit)
        {
            cockpit.RemoveMarkAt(objectId);
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

    private void MapPointerActive(object? sender, PointerEventArgs eventArgs)
    {
        _idleTimer.Stop();
        SetIdle(false);
    }

    private void MapPointerLeft(object? sender, PointerEventArgs eventArgs)
    {
        _idleTimer.Stop();
        if (DataContext is RaidCockpitViewModel { HideControlsWhenIdle: true })
        {
            _idleTimer.Start();
        }
    }

    private void IdleElapsed(object? sender, EventArgs eventArgs)
    {
        _idleTimer.Stop();
        SetIdle(DataContext is RaidCockpitViewModel { HideControlsWhenIdle: true });
    }

    /// <summary>
    /// Fades the floating group, and only that: it keeps its hit testing and its place in the
    /// automation tree, so a keyboard user or a screen reader never loses a control to a timer.
    /// </summary>
    private void SetIdle(bool idle)
    {
        if (this.FindControl<Border>("ViewControlsGroup") is not { } group)
        {
            return;
        }

        if (idle && !group.Classes.Contains(IdleClass))
        {
            group.Classes.Add(IdleClass);
        }
        else if (!idle)
        {
            group.Classes.Remove(IdleClass);
        }
    }
}
