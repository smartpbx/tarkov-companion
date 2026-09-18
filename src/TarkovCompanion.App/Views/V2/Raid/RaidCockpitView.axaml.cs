using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TarkovCompanion.App.ViewModels.V2.Raid;
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

    public RaidCockpitView()
    {
        AvaloniaXamlLoader.Load(this);
        _idleTimer = new(DispatcherPriority.Background) { Interval = IdleAfter };
        _idleTimer.Tick += IdleElapsed;
    }

    private void RendererPlanClicked(object? sender, MapScenePoint point)
    {
        if (DataContext is RaidCockpitViewModel cockpit)
        {
            cockpit.PlaceArmedMarkAt(point);
        }
    }

    private void RendererMarkerRightClicked(object? sender, MapSceneObjectId objectId)
    {
        if (DataContext is RaidCockpitViewModel cockpit)
        {
            cockpit.RemoveMarkAt(objectId);
        }
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
