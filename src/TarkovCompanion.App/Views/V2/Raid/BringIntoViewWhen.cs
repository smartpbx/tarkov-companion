using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TarkovCompanion.App.Views.V2.Raid;

/// <summary>
/// Scrolls a control into view when a bound flag turns true: an Extract options row whose extract
/// was just picked on the map.
/// </summary>
/// <remarks>
/// The list scrolls inside its own card, and the card inside the side panel, so a row picked on
/// the map was often highlighted out of sight. BringIntoView asks every scrolling ancestor in
/// turn. Posted rather than immediate: the row may only just have been realised, or its card only
/// just opened, and has no layout yet to scroll to.
/// </remarks>
public static class BringIntoViewWhen
{
    public static readonly AttachedProperty<bool> SelectedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Selected", typeof(BringIntoViewWhen));

    static BringIntoViewWhen()
    {
        SelectedProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            if (change.NewValue is true)
            {
                Dispatcher.UIThread.Post(() => control.BringIntoView(), DispatcherPriority.Background);
            }
        });
    }

    public static bool GetSelected(Control control) => control.GetValue(SelectedProperty);

    public static void SetSelected(Control control, bool value) => control.SetValue(SelectedProperty, value);
}
