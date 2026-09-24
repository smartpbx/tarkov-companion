using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace TarkovCompanion.App.Views.V2.Setup;

/// <summary>
/// #833: keeps a page's tab row out from under the shell's floating "Updated to … What's new"
/// banner.
/// </summary>
/// <remarks>
/// The banner floats over the top-right of the page instead of taking a row, because a row cost
/// the Raid map its minimum height (#314). At 200% text Setup's tabs wrap across the whole width
/// and the banner was drawn over them. Setting <c>ClearsWhatsNewBanner="True"</c> on a tab row
/// gives it a right margin as wide as the banner reaches into it, only while the banner shows and
/// only when the two share the same height band. The margin is measured against the row's parent,
/// not the row itself, so changing it cannot feed back into the next measurement.
/// </remarks>
public static class FloatingBannerClearance
{
    /// <summary>The banner's automation id in V2ShellView.axaml.</summary>
    public const string BannerAutomationId = "v2-whats-new-banner";

    /// <summary>
    /// Space kept between the last tab and the banner's left edge. None: a tab button's own
    /// padding already separates the two, and at 100% text "About" ends about 10 px short of the
    /// banner, so any gap wraps it onto a line of its own.
    /// </summary>
    public const double Gap = 0;

    public static readonly AttachedProperty<bool> ClearsWhatsNewBannerProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("ClearsWhatsNewBanner", typeof(FloatingBannerClearance));

    private static readonly AttachedProperty<Tracker?> TrackerProperty =
        AvaloniaProperty.RegisterAttached<Control, Tracker?>("ClearanceTracker", typeof(FloatingBannerClearance));

    static FloatingBannerClearance()
    {
        ClearsWhatsNewBannerProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.GetValue(TrackerProperty)?.Dispose();
            control.SetValue(TrackerProperty, e.NewValue is true ? new Tracker(control) : null);
        });
    }

    public static bool GetClearsWhatsNewBanner(Control control) => control.GetValue(ClearsWhatsNewBannerProperty);

    public static void SetClearsWhatsNewBanner(Control control, bool value) => control.SetValue(ClearsWhatsNewBannerProperty, value);

    /// <summary>
    /// How far in from the parent's right edge the row must stop. <paramref name="banner"/> is
    /// the banner's rectangle in the parent's coordinates, or null when it is hidden.
    /// </summary>
    public static double RightReserve(Rect? banner, double parentWidth, double rowTop, double rowBottom)
    {
        if (banner is not { } area || area.Width <= 0 || area.Height <= 0)
        {
            return 0;
        }

        // A banner wholly above or below the row does not cover it.
        if (area.Bottom <= rowTop || area.Top >= rowBottom)
        {
            return 0;
        }

        return Math.Max(0, parentWidth - area.Left + Gap);
    }

    private sealed class Tracker : IDisposable
    {
        private readonly Control _row;
        private readonly Thickness _original;
        private Control? _banner;
        private Visual? _parent;

        public Tracker(Control row)
        {
            _row = row;
            _original = row.Margin;
            row.AttachedToVisualTree += OnAttached;
            row.DetachedFromVisualTree += OnDetached;
            if (row.IsAttachedToVisualTree())
            {
                Attach();
            }
        }

        public void Dispose()
        {
            _row.AttachedToVisualTree -= OnAttached;
            _row.DetachedFromVisualTree -= OnDetached;
            Detach();
            _row.Margin = _original;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Attach();

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Detach();

        private void Attach()
        {
            Detach();
            _parent = _row.GetVisualParent();
            _banner = TopLevel.GetTopLevel(_row)?.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == BannerAutomationId);
            if (_parent is null || _banner is null)
            {
                return;
            }

            _banner.PropertyChanged += OnWatchedChanged;
            _parent.PropertyChanged += OnWatchedChanged;
            Update();
        }

        private void Detach()
        {
            if (_banner is not null)
            {
                _banner.PropertyChanged -= OnWatchedChanged;
            }

            if (_parent is not null)
            {
                _parent.PropertyChanged -= OnWatchedChanged;
            }

            _banner = null;
            _parent = null;
        }

        private void OnWatchedChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.BoundsProperty || e.Property == Visual.IsVisibleProperty)
            {
                Update();
            }
        }

        private void Update()
        {
            if (_parent is null || _banner is null)
            {
                return;
            }

            Rect? banner = null;
            if (_banner.IsEffectivelyVisible && _banner.TransformToVisual(_parent) is { } toParent)
            {
                banner = new Rect(_banner.Bounds.Size).TransformToAABB(toParent);
            }

            var reserve = RightReserve(banner, _parent.Bounds.Width, _row.Bounds.Top, _row.Bounds.Bottom);
            var right = Math.Max(_original.Right, reserve);
            if (Math.Abs(_row.Margin.Right - right) > 0.5)
            {
                _row.Margin = new Thickness(_original.Left, _original.Top, right, _original.Bottom);
            }
        }
    }
}
