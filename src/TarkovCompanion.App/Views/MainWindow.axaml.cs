using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Shell;

namespace TarkovCompanion.App.Views;

/// <summary>
/// The window: where it sits, and the one place a keystroke is turned into something happening.
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
        Opened += RestoreLayout;
        Closing += RememberLayout;
        // Tunnelling, so a key reaches this before a focused control decides it was theirs.
        // Escape in particular is claimed by several controls, and what somebody means by it
        // here is almost always the map's selection rather than the combo box they last used.
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// Opens the window where it was left, on a screen that exists.
    /// </summary>
    /// <remarks>
    /// The window opened at 1500 by 900 every launch, wherever the operating system put it, so
    /// a companion that shares a screen with the game was dragged back into its slot every
    /// time. The original handoff spec asked for placement memory and it was never built.
    ///
    /// On Opened rather than in the constructor, because the screens are not known until the
    /// window has a handle — and the whole reason this is not simply "restore what was saved"
    /// is the monitor that has been unplugged since.
    ///
    /// Bounds before the maximize, deliberately. Setting the state first and the bounds after
    /// would store the screen's size as the restore size, which is how somebody un-maximizes
    /// once and gets a window the size of their monitor for ever.
    /// </remarks>
    private async void RestoreLayout(object? sender, EventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var screens = Screens.All
            .Select(screen => new ScreenBounds(
                screen.WorkingArea.X,
                screen.WorkingArea.Y,
                screen.WorkingArea.X + screen.WorkingArea.Width,
                screen.WorkingArea.Y + screen.WorkingArea.Height))
            .ToArray();

        if (viewModel.PreviewShell is { } previewShell)
        {
            MinWidth = V2ShellWindowPlacement.MinimumWidth;
            MinHeight = V2ShellWindowPlacement.MinimumHeight;
            if (previewShell.RestoreWindow(screens) is { } preview)
            {
                Width = preview.Width;
                Height = preview.Height;
                if (preview is { Left: { } left, Top: { } top }) Position = new((int)left, (int)top);
                if (preview.IsMaximized) WindowState = WindowState.Maximized;
            }
        }
        else
        {
            var layout = await viewModel.LoadLayoutAsync(screens);
            Width = layout.Width;
            Height = layout.Height;
            if (layout is { Left: { } left, Top: { } top }) Position = new((int)left, (int)top);
            if (layout.IsMaximized) WindowState = WindowState.Maximized;
        }

        // Only once the window is where it belongs. Subscribing earlier would record the
        // operating system's own opening position over the one being restored.
        PositionChanged += BoundsChanged;
        SizeChanged += BoundsChanged;
        if (viewModel.PreviewShell is not null)
        {
            // Seed a normal restore rectangle even when this is the first launch and the next
            // window event is a maximize. Otherwise the only remembered bounds are the screen.
            Remember();
        }
    }

    private void BoundsChanged(object? sender, EventArgs eventArgs) => Remember();

    private void RememberLayout(object? sender, WindowClosingEventArgs eventArgs) => Remember();

    private void Remember()
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (viewModel.PreviewShell is { } preview)
        {
            preview.RecordWindow(Width, Height, Position.X, Position.Y, WindowState == WindowState.Maximized);
            return;
        }
        viewModel.RecordBounds(Width, Height, Position.X, Position.Y, WindowState == WindowState.Maximized);
    }

    private void RailToggleClick(object? sender, RoutedEventArgs eventArgs) =>
        (DataContext as MainWindowViewModel)?.ToggleRail();

    private void WindowKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.PreviewShell is { } preview)
        {
            if (TryV2Chord(eventArgs, out var chord))
            {
                var focusedId = FocusManager?.GetFocusedElement() is StyledElement focused
                    ? AutomationProperties.GetAutomationId(focused)
                    : null;
                eventArgs.Handled = preview.HandleKey(chord, focusedId);
            }

            // A V2 key not claimed by chrome still reaches the focused V2 control, but it never
            // drops into the hidden V1 shortcut table below.
            return;
        }

        // Ctrl and a digit are safe while typing, because they are not a character. Everything
        // below this is a character somebody may be in the middle of typing into a search box.
        if (eventArgs.KeyModifiers == KeyModifiers.Control)
        {
            if (PageIndex(eventArgs.Key) is { } page)
            {
                eventArgs.Handled = viewModel.NavigateTo(page);
                return;
            }

            // How large everything is drawn. Ctrl and a sign is what every application with a
            // zoom uses, and Ctrl+0 undoes it, so nobody has to be told. Held with Control so
            // they keep working while somebody is typing, where the bare + and − beside them
            // deliberately do not.
            switch (eventArgs.Key)
            {
                case Key.OemPlus or Key.Add:
                    viewModel.StepInterfaceScale(1);
                    break;
                case Key.OemMinus or Key.Subtract:
                    viewModel.StepInterfaceScale(-1);
                    break;
                case Key.D0 or Key.NumPad0:
                    viewModel.ResetInterfaceScale();
                    break;
                default:
                    return;
            }

            eventArgs.Handled = true;
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

    private static bool TryV2Chord(KeyEventArgs eventArgs, out V2KeyChord chord)
    {
        var key = eventArgs.Key switch
        {
            Key.D0 or Key.NumPad0 => "0", Key.D1 or Key.NumPad1 => "1", Key.D2 or Key.NumPad2 => "2",
            Key.D3 or Key.NumPad3 => "3", Key.D4 or Key.NumPad4 => "4", Key.D5 or Key.NumPad5 => "5",
            Key.D6 or Key.NumPad6 => "6", Key.D7 or Key.NumPad7 => "7", Key.D8 or Key.NumPad8 => "8",
            Key.D9 or Key.NumPad9 => "9", Key.Left => "Left", Key.Right => "Right", Key.Escape => "Escape",
            Key.F6 => "F6", Key.OemComma => ",", _ => eventArgs.Key.ToString(),
        };
        chord = new(key, eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control), eventArgs.KeyModifiers.HasFlag(KeyModifiers.Alt), eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift));
        return true;
    }

    /// <summary>
    /// The page a digit stands for, counting from zero, or null if the key is not a digit.
    /// </summary>
    /// <remarks>
    /// 1 to 9 are the first nine pages. Zero is deliberately not the tenth: Ctrl+0 is what
    /// every application with a zoom uses to undo it, browsers included, and none of them give
    /// it to a tab. Fourteen pages were never all going to have a shortcut, and the tenth was
    /// the most arbitrary of them; the size control is used by everybody.
    ///
    /// Both rows of digits, because a keyboard has two and somebody whose right hand is on the
    /// mouse is using the number pad.
    /// </remarks>
    private static int? PageIndex(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
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
