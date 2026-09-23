using Avalonia;
using Avalonia.Controls;
using Avalonia.Metadata;
using Avalonia.Threading;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// Holds one control out of the page until the turn after the page itself was built, then
/// shows it filling the space it is given.
/// </summary>
/// <remarks>
/// [#678] The first visit to Raid built the map view (its chrome, canvas and layers) in the
/// same turn as the rest of the page, and that one turn held input for over 300 ms even once
/// the marks and the side panel were staged (<see cref="StagedList"/>, <see cref="StagedStack"/>).
/// Here the map view is created with the page but joins the tree (and is styled, templated and
/// measured) on the next turn, so the page's frame paints first and the map follows a frame
/// later. Once shown it stays; a later map switch reuses it.
/// </remarks>
public sealed class StagedHost : Control
{
    private Control? _pending;
    private Control? _child;
    private bool _attached;
    private bool _revealScheduled;

    /// <summary>The control to show once the page has painted.</summary>
    [Content]
    public Control? Pending
    {
        get => _child ?? _pending;
        set
        {
            if (_child is not null)
            {
                LogicalChildren.Remove(_child);
                VisualChildren.Remove(_child);
                _child = null;
            }

            _pending = value;
            ScheduleReveal();
        }
    }

    /// <summary>True until the held control has joined the page.</summary>
    public bool IsStaging => _pending is not null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ScheduleReveal();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_child is null)
        {
            return default;
        }

        _child.Measure(availableSize);
        return _child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _child?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void ScheduleReveal()
    {
        if (_revealScheduled || !_attached || _pending is null)
        {
            return;
        }

        _revealScheduled = true;
        Dispatcher.UIThread.Post(Reveal, DispatcherPriority.Background);
    }

    private void Reveal()
    {
        _revealScheduled = false;
        if (!_attached || _pending is not { } child)
        {
            return;
        }

        _pending = null;
        _child = child;
        LogicalChildren.Add(child);
        VisualChildren.Add(child);
        InvalidateMeasure();
    }
}
