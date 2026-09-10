using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Core.Domain.Input;

namespace TarkovCompanion.App.Views.Pages;

public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);

        // Key handling is registered on the tunnelling route so a captured combination never
        // reaches the button as an activation. Space and Enter would otherwise press the
        // button again instead of being recorded.
        AddHandler(KeyDownEvent, CaptureHotkey, RoutingStrategies.Tunnel);
    }

    private void RecordHotkeyClick(object? sender, RoutedEventArgs arguments)
    {
        if (DataContext is not SettingsPageViewModel settings)
        {
            return;
        }

        if (settings.IsRecordingHotkey)
        {
            settings.CancelRecordingHotkey();
            return;
        }

        settings.BeginRecordingHotkey();
        this.FindControl<Button>("RecordHotkeyButton")?.Focus();
    }

    private void CaptureHotkey(object? sender, KeyEventArgs arguments)
    {
        if (DataContext is not SettingsPageViewModel settings || !settings.IsRecordingHotkey)
        {
            return;
        }

        if (arguments.Key is Key.Escape)
        {
            settings.CancelRecordingHotkey();
            arguments.Handled = true;
            return;
        }

        // Modifiers alone are not a shortcut; keep listening until a real key arrives.
        if (IsModifier(arguments.Key))
        {
            return;
        }

        if (!TryDescribeKey(arguments.Key, out var keyName))
        {
            return;
        }

        settings.RecordHotkey(new(Translate(arguments.KeyModifiers), keyName));
        arguments.Handled = true;
    }

    private static bool IsModifier(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin
        or Key.System;

    private static HotkeyModifiers Translate(KeyModifiers modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            result |= HotkeyModifiers.Windows;
        }

        return result;
    }

    /// <summary>Maps a pressed key to the canonical name a binding stores.</summary>
    private static bool TryDescribeKey(Key key, out string keyName)
    {
        keyName = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            >= Key.D0 and <= Key.D9 => key.ToString()[1..],
            >= Key.NumPad0 and <= Key.NumPad9 => key.ToString(),
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Up => "Up",
            Key.Right => "Right",
            Key.Down => "Down",
            Key.Multiply => "Multiply",
            Key.Add => "Add",
            Key.Subtract => "Subtract",
            Key.Decimal => "Decimal",
            Key.Divide => "Divide",
            Key.OemSemicolon => "Semicolon",
            Key.OemPlus => "Equals",
            Key.OemComma => "Comma",
            Key.OemMinus => "Minus",
            Key.OemPeriod => "Period",
            Key.OemQuestion => "Slash",
            Key.OemTilde => "Backtick",
            Key.OemOpenBrackets => "LeftBracket",
            Key.OemPipe => "Backslash",
            Key.OemCloseBrackets => "RightBracket",
            Key.OemQuotes => "Quote",
            _ => string.Empty,
        };

        return keyName.Length > 0;
    }
}
