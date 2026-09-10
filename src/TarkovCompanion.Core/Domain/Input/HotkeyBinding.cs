using System.Diagnostics.CodeAnalysis;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Core.Domain.Input;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// A user-chosen global hotkey, stored and displayed as text such as "Ctrl + Alt + S".
/// </summary>
/// <remarks>
/// The binding is deliberately platform-independent: it names a key rather than carrying an
/// operating-system key code, and converts to a <see cref="HotkeyGesture"/> only when a
/// platform actually registers it.
/// </remarks>
public sealed record HotkeyBinding(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>Windows MOD_NOREPEAT. Holding the key must not repeat the scan.</summary>
    private const uint NoRepeat = 0x4000;

    public static HotkeyBinding DefaultScan { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "S");

    /// <summary>The canonical key names a binding may use, in presentation order.</summary>
    public static IReadOnlyList<string> AvailableKeys { get; } = HotkeyKeys.Names;

    public string DisplayName => Describe(Modifiers, Key);

    /// <summary>
    /// Whether this binding is safe to register globally.
    /// </summary>
    /// <remarks>
    /// A global hotkey is taken from every application, so a bare letter or digit would make
    /// the key unusable everywhere while the companion runs. Function keys are exempt because
    /// they are the conventional choice for an unmodified companion shortcut.
    /// </remarks>
    public bool IsValid(out string reason)
    {
        if (!HotkeyKeys.TryGetVirtualKey(Key, out _))
        {
            reason = string.IsNullOrWhiteSpace(Key)
                ? "Choose a key for the shortcut."
                : $"'{Key}' is not a key this shortcut can use.";
            return false;
        }

        if (Modifiers == HotkeyModifiers.None && !HotkeyKeys.IsFunctionKey(Key))
        {
            reason = $"Add Ctrl, Alt or Shift to {Key}. Without a modifier the key would be taken from every other application.";
            return false;
        }

        if (Modifiers == HotkeyModifiers.Windows)
        {
            reason = "Windows on its own is reserved by the operating system. Add Ctrl, Alt or Shift.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Converts to the gesture a platform hotkey service registers.</summary>
    public HotkeyGesture ToGesture()
    {
        if (!IsValid(out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var modifiers = NoRepeat;
        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            modifiers |= 0x0001;
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            modifiers |= 0x0002;
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            modifiers |= 0x0004;
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            modifiers |= 0x0008;
        }

        HotkeyKeys.TryGetVirtualKey(Key, out var virtualKey);
        return new(modifiers, virtualKey, DisplayName);
    }

    /// <summary>Reads a binding written as "Ctrl+Alt+S". Separators and case are forgiving.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var rawPart in text.Split(['+', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "WIN" or "WINDOWS" or "META" or "CMD":
                    modifiers |= HotkeyModifiers.Windows;
                    break;
                default:
                    if (key is not null || !HotkeyKeys.TryNormalize(rawPart, out var normalized))
                    {
                        return false;
                    }

                    key = normalized;
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        binding = new(modifiers, key);
        return true;
    }

    public override string ToString() => DisplayName;

    private static string Describe(HotkeyModifiers modifiers, string key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(string.IsNullOrWhiteSpace(key) ? "(none)" : key);
        return string.Join(" + ", parts);
    }
}
