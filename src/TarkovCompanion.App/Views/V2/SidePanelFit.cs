using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.App.Views.V2;

/// <summary>
/// [#832] Lets a fixed-width side panel give way when the page it sits in is narrow.
/// </summary>
/// <remarks>
/// Set <c>SidePanelFit.MainMinimum</c> on a panel that has a <c>Width</c>: that width stays while
/// the rest of the page keeps the minimum, and below it the panel narrows to
/// <c>SidePanelFit.Minimum</c>. The arithmetic is <see cref="PageFit.SidePanelWidth"/>. The page is
/// the nearest <see cref="UserControl"/>, whose width is the shell's less the rail.
/// </remarks>
public static class SidePanelFit
{
    /// <summary>What the rest of the page keeps before the panel narrows.</summary>
    public static readonly AttachedProperty<double> MainMinimumProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("MainMinimum", typeof(SidePanelFit), double.NaN);

    /// <summary>The narrowest the panel goes.</summary>
    public static readonly AttachedProperty<double> MinimumProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Minimum", typeof(SidePanelFit), 0);

    private static readonly AttachedProperty<double> FullWidthProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("FullWidth", typeof(SidePanelFit), double.NaN);

    static SidePanelFit()
    {
        MainMinimumProperty.Changed.AddClassHandler<Control>((panel, e) =>
        {
            panel.AttachedToVisualTree -= OnAttached;
            if (e.NewValue is double value && double.IsFinite(value))
            {
                panel.AttachedToVisualTree += OnAttached;
            }
        });
    }

    public static double GetMainMinimum(Control control) => control.GetValue(MainMinimumProperty);

    public static void SetMainMinimum(Control control, double value) => control.SetValue(MainMinimumProperty, value);

    public static double GetMinimum(Control control) => control.GetValue(MinimumProperty);

    public static void SetMinimum(Control control, double value) => control.SetValue(MinimumProperty, value);

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control panel || panel.FindAncestorOfType<UserControl>() is not { } page)
        {
            return;
        }

        if (!double.IsFinite(panel.GetValue(FullWidthProperty)))
        {
            panel.SetValue(FullWidthProperty, panel.Width);
        }

        void Apply(double pageWidth)
        {
            var full = panel.GetValue(FullWidthProperty);
            if (double.IsFinite(full) && pageWidth > 0)
            {
                panel.Width = PageFit.SidePanelWidth(pageWidth, full, GetMinimum(panel), GetMainMinimum(panel));
            }
        }

        void OnPageSize(object? s, SizeChangedEventArgs args) => Apply(args.NewSize.Width);

        void OnDetached(object? s, VisualTreeAttachmentEventArgs args)
        {
            page.SizeChanged -= OnPageSize;
            panel.DetachedFromVisualTree -= OnDetached;
        }

        page.SizeChanged += OnPageSize;
        panel.DetachedFromVisualTree += OnDetached;
        Apply(page.Bounds.Width);
    }
}
