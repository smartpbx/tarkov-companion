using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels;

namespace TarkovCompanion.App.Views;

/// <summary>
/// The window, and the one place a keystroke is turned into something happening.
/// </summary>
/// <remarks>
/// There was no KeyBinding, HotKey or KeyGesture anywhere in the application, and the player
/// arrives here by alt-tab from a fullscreen game with both hands already on the keyboard.
/// Everything worth doing needed a mouse and a target to hit with it.
///
/// Window-level bindings only. The backlog wants the last global hotkey gone, so nothing here
/// goes near RegisterHotKey: these work when the companion has focus and are inert when it does
/// not, which is the correct behaviour for an application that sits beside a game.
/// </remarks>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Tunnelling, so a key reaches this before a focused control decides it was theirs.
        // Escape in particular is claimed by several controls, and what somebody means by it
        // here is almost always the map's selection rather than the combo box they last used.
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void WindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        // Ctrl and a digit are safe while typing, because they are not a character. Everything
        // below this is a character somebody may be in the middle of typing into a search box.
        if (eventArgs.KeyModifiers == KeyModifiers.Control && PageIndex(eventArgs.Key) is { } page)
        {
            eventArgs.Handled = viewModel.NavigateTo(page);
            return;
        }

        if (eventArgs.Key == Key.Escape)
        {
            // Not marked handled when there was nothing to dismiss, so Escape still reaches
            // whatever else wanted it — a dropdown, a dialog — rather than being swallowed by
            // a handler that did nothing.
            eventArgs.Handled = viewModel.Dismiss();
            return;
        }

        if (IsTyping)
        {
            return;
        }

        switch (eventArgs.Key)
        {
            case Key.F:
                viewModel.Map.ToggleFollowPlayer();
                break;
            case Key.Home:
                viewModel.Map.RequestFit();
                break;
            case Key.OemPlus or Key.Add:
                viewModel.Map.RequestZoom(1);
                break;
            case Key.OemMinus or Key.Subtract:
                viewModel.Map.RequestZoom(-1);
                break;
            // Up is the floor above, which is the next one in the list: floors are published
            // lowest first, the way a lift describes them.
            case Key.PageUp:
                _ = viewModel.Map.StepFloorAsync(1);
                break;
            case Key.PageDown:
                _ = viewModel.Map.StepFloorAsync(-1);
                break;
            case Key.OemQuestion:
                FocusSearch();
                break;
            default:
                return;
        }

        eventArgs.Handled = true;
    }

    /// <summary>
    /// The page a digit stands for, counting from zero, or null if the key is not a digit.
    /// </summary>
    /// <remarks>
    /// 1 to 9 are the first nine pages and 0 is the tenth, which is how every application with
    /// numbered tabs has done it for twenty years. Both rows of digits, because a keyboard has
    /// two and somebody whose right hand is on the mouse is using the number pad.
    /// </remarks>
    private static int? PageIndex(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
        Key.D0 or Key.NumPad0 => 9,
        _ => null,
    };

    /// <summary>
    /// Whether the keystroke belongs to something somebody is writing in.
    /// </summary>
    /// <remarks>
    /// Without this, pressing F in the quest search box would toggle follow and eat the letter,
    /// and a minus sign could never be typed into anything. A spinner counts: its text is
    /// editable even though it holds a number.
    /// </remarks>
    private bool IsTyping => FocusManager?.GetFocusedElement() is TextBox or AutoCompleteBox or NumericUpDown;

    /// <summary>Puts the caret in the search box of whichever page has one.</summary>
    /// <remarks>
    /// Found by walking the visual tree rather than by name, because five pages have a search
    /// box and they are separate views with separate view models. The first TextBox on the page
    /// is the search box on all five; if that stops being true, this focuses a text box rather
    /// than the right one, which is a great deal less bad than focusing nothing.
    /// </remarks>
    private void FocusSearch() => this.GetVisualDescendants()
        .OfType<TextBox>()
        .FirstOrDefault(box => box.IsEffectivelyVisible && box.IsEnabled)
        ?.Focus();
}
