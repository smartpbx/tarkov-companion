using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels.V2.Now;

namespace TarkovCompanion.App.Views.V2.Now;

/// <summary>[#712 0-4] The Raid page's Now panel; see <see cref="ViewModels.V2.Now.NowPanelViewModel"/>.</summary>
public sealed partial class NowPanelView : UserControl
{
    private NowPanelViewModel? _panel;
    private StackPanel? _blocks;
    private Size _foldedFor;
    private bool _refold = true;
    private bool _folding;
    private Control? _heldRow;
    private Control? _waypointRow;
    private long _waypointAt;

    public NowPanelView() => AvaloniaXamlLoader.Load(this);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_panel is not null)
        {
            _panel.PropertyChanged -= PanelChanged;
        }

        _panel = DataContext as NowPanelViewModel;
        if (_panel is not null)
        {
            _panel.PropertyChanged += PanelChanged;
        }

        Refold();
    }

    /// <summary>
    /// [#712 0-7] Folds the panel (<see cref="NowPanelViewModel.Fold"/>) until the blocks fit the
    /// room they are given, so the panel never scrolls and never clips a block's bottom lines.
    /// </summary>
    /// <remarks>
    /// Worked out again when the room changes or anything inside asks for a new measure (a new
    /// state, a text-size change, a template arriving), but never because of the fold itself:
    /// raising it invalidates this view, and starting again from nothing on that pass would fold
    /// and unfold for ever.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        _blocks ??= this.FindControl<StackPanel>("Blocks");
        if (_panel is { } panel && _blocks is { } blocks && (_refold || _foldedFor != availableSize))
        {
            _foldedFor = availableSize;
            _folding = true;
            try
            {
                for (var fold = 0; ; fold++)
                {
                    panel.Fold = fold;
                    Unmeasure(blocks);
                    base.MeasureOverride(availableSize);
                    if (fold >= NowPanelViewModel.MaximumFold || Fits(blocks))
                    {
                        break;
                    }
                }
            }
            finally
            {
                _folding = false;
                _refold = false;
            }
        }

        return base.MeasureOverride(availableSize);
    }

    protected override void OnMeasureInvalidated()
    {
        base.OnMeasureInvalidated();
        if (!_folding)
        {
            _refold = true;
        }
    }

    /// <summary>
    /// A fold hides lines deep inside a block, and measured here, inside this view's own measure,
    /// that change did not reach the blocks' size until a later pass: every fold up to the one
    /// that hid a whole block read as no help at all, and the panel folded three steps too far.
    /// </summary>
    private static void Unmeasure(StackPanel blocks)
    {
        foreach (var layoutable in blocks.GetVisualDescendants().OfType<Layoutable>())
        {
            layoutable.InvalidateMeasure();
        }

        blocks.InvalidateMeasure();
    }

    private static bool Fits(StackPanel blocks)
    {
        if (!blocks.IsVisible || LayoutInformation.GetPreviousMeasureConstraint(blocks) is not { } room || double.IsInfinity(room.Height))
        {
            return true;
        }

        blocks.Measure(room.WithHeight(double.PositiveInfinity));
        var needed = blocks.DesiredSize.Height;
        blocks.Measure(room);
        return needed <= room.Height + 0.5;
    }

    private void PanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPanelViewModel.State) or nameof(NowPanelViewModel.ShowsBrief) or null)
        {
            Refold();
        }
    }

    /// <summary>[#712 0-5] A tap on a SQUAD row pings that squadmate's spot, unless it ended a hold.</summary>
    private void SquadRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: NowSquadRowViewModel row } control)
        {
            if (ReferenceEquals(_heldRow, control))
            {
                _heldRow = null; // the release of a hold that already placed a waypoint
                return;
            }

            row.PingCommand.Execute(null);
        }
    }

    /// <summary>A hold (touch, pen or mouse) places a waypoint; the release that follows is not a ping.</summary>
    private void SquadRowHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == HoldingState.Started && sender is Control control)
        {
            _heldRow = control;
            PlaceWaypoint(control);
            e.Handled = true;
        }
    }

    /// <summary>A right-click (or the platform's own context gesture) places a waypoint.</summary>
    private void SquadRowContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is Control control)
        {
            PlaceWaypoint(control);
            e.Handled = true;
        }
    }

    /// <summary>One waypoint per gesture: a touch hold raises both Holding and ContextRequested.</summary>
    private void PlaceWaypoint(Control control)
    {
        var now = Environment.TickCount64;
        if (control.DataContext is not NowSquadRowViewModel row ||
            (ReferenceEquals(_waypointRow, control) && now - _waypointAt < 1000))
        {
            return;
        }

        _waypointRow = control;
        _waypointAt = now;
        row.WaypointCommand.Execute(null);
    }

    private void Refold()
    {
        _refold = true;
        InvalidateMeasure();
    }
}
