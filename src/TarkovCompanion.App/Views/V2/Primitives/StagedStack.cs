using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Metadata;
using Avalonia.Threading;

namespace TarkovCompanion.App.Views.V2.Primitives;

/// <summary>
/// A vertical stack whose children join the page one per turn of the interface thread, after
/// the first <see cref="Immediate"/> of them.
/// </summary>
/// <remarks>
/// [#678] The Raid page's side panel is a dozen cards, about 1,100 controls on Customs. A control
/// is styled when it joins the tree and templated when it is first measured, so the whole panel
/// used to be built in the one turn that opened the page, and the first visit to Raid held input
/// for 1.3 to 1.7 s. The children here are created with the page (that part is cheap) but are
/// kept out of the tree until their turn: the page paints with the first cards, and each later
/// card is built between two input turns, top to bottom.
///
/// Names inside the cards stay in the page's name scope, so <c>#Name</c> bindings and
/// <c>FindControl</c> still find them; only their styling and layout wait.
/// </remarks>
public sealed class StagedStack : Control
{
    public static readonly StyledProperty<double> SpacingProperty =
        StackPanel.SpacingProperty.AddOwner<StagedStack>();

    public static readonly StyledProperty<int> ImmediateProperty =
        AvaloniaProperty.Register<StagedStack, int>(nameof(Immediate), 1);

    private readonly StackPanel _panel = new();
    private bool _revealScheduled;
    private bool _attached;

    public StagedStack()
    {
        LogicalChildren.Add(_panel);
        VisualChildren.Add(_panel);
    }

    /// <summary>The children, in order, whether or not they have joined the page yet.</summary>
    [Content]
    public AvaloniaList<Control> Items { get; } = [];

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>How many children join with the page itself, before any staging.</summary>
    public int Immediate
    {
        get => GetValue(ImmediateProperty);
        set => SetValue(ImmediateProperty, value);
    }

    /// <summary>True while some children have not joined the page yet.</summary>
    public bool IsStaging => _panel.Children.Count < Items.Count;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SpacingProperty)
        {
            _panel.Spacing = change.GetNewValue<double>();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        while (_panel.Children.Count < Math.Min(Immediate, Items.Count))
        {
            _panel.Children.Add(Items[_panel.Children.Count]);
        }

        ScheduleReveal();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _panel.Measure(availableSize);
        return _panel.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _panel.Arrange(new Rect(finalSize));
        return finalSize;
    }

    private void ScheduleReveal()
    {
        if (_revealScheduled || !IsStaging)
        {
            return;
        }

        _revealScheduled = true;
        Dispatcher.UIThread.Post(RevealNext, DispatcherPriority.Background);
    }

    private void RevealNext()
    {
        _revealScheduled = false;
        if (!IsStaging || !_attached)
        {
            return;
        }

        _panel.Children.Add(Items[_panel.Children.Count]);
        ScheduleReveal();
    }
}
