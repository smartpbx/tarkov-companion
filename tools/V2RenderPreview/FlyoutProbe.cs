using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [Issue 574] Opens one button's flyout and saves what it draws as `<out>.flyout.png`.
/// </summary>
/// <remarks>
/// The Layers and View menus on the Raid strip were only ever judged from their XAML, because a
/// flyout is a popup and the headless platform gives a popup a surface of its own: it is not in
/// the window's frame. `--open-flyout <automation id>` shows the flyout of the button with that
/// id and captures the popup's own top level, so a menu's row heights and columns can be looked at.
/// </remarks>
internal static class FlyoutProbe
{
    /// <param name="scrollToEnd">[#266] Scrolls the menu to its foot first: the Layers menu's traffic
    /// legend sits below 520 pixels of layer switches.</param>
    public static void Save(Window window, string automationId, string outputPath, Action<int> pump, bool scrollToEnd = false)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(candidate => AutomationProperties.GetAutomationId(candidate) == automationId)
            ?? throw new ArgumentException($"No button has the automation id '{automationId}'.");
        if (button.Flyout is not Flyout { Content: Control content } flyout)
        {
            throw new ArgumentException($"'{automationId}' has no flyout with a control for its content.");
        }

        flyout.ShowAt(button);
        pump(30);
        if (scrollToEnd && (content as ScrollViewer ?? content.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()) is { } scroller)
        {
            scroller.ScrollToEnd();
            pump(10);
        }

        var host = TopLevel.GetTopLevel(content)
            ?? throw new InvalidOperationException("The flyout opened without a top level.");
        using var frame = host.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The flyout's top level produced no frame.");
        var path = Path.ChangeExtension(outputPath, ".flyout.png");
        using (var stream = File.Create(path))
        {
            frame.Save(stream, new PngBitmapEncoderOptions());
        }

        Console.WriteLine($"Saved {path} ({frame.PixelSize.Width}x{frame.PixelSize.Height}, host {host.GetType().Name}).");
        flyout.Hide();
        pump(5);
    }
}
