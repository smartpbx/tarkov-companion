using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TarkovCompanion.App.Views.Pages;

/// <summary>
/// The settings page.
/// </summary>
/// <remarks>
/// This used to be two hundred lines of key handling: a tunnelling key handler so a captured
/// combination could not press the button that started the capture, a modifier translation, and
/// a table mapping every key on a keyboard to the name a binding stores. All of it existed to
/// let somebody choose a scan shortcut, and the shortcut existed only because a scan driven by
/// the game's own screenshot key was logged and dropped instead of reaching the interface.
///
/// One wiring fix removed the reason for the shortcut, and the shortcut removed the reason for
/// all of this.
/// </remarks>
public sealed partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);
}
