namespace TarkovCompanion.App.Services.V2.Shell;

public enum V2ShellCommandKind
{
    Navigate = 1,
    Back,
    Forward,
    ToggleCapture,
    ToggleHealth,
    TogglePalette,
    FocusSearch,
    CopyAddress,
    CloseTransient,
    TogglePin,
    ToggleCaptureShortcut,
    ResetPreview,
    NextRegion,
    PreviousRegion,
}

/// <summary>Stable automation ids used by focus restoration and the packaged Windows smoke.</summary>
public static class V2ShellFocusTargets
{
    public const string Capture = "v2-shell-capture";
    public const string Health = "v2-shell-health";
    public const string Palette = "v2-shell-palette";
    public const string HeaderSearch = "v2-shell-header-search";
    public const string WorkspaceSearch = "v2-shell-workspace-search";
    public const string Address = "v2-shell-address";
    public const string CaptureDialog = "v2-shell-capture-dialog-title";
    public const string PaletteDialog = "v2-shell-palette-dialog-title";
    public const string PaletteQuery = "v2-shell-palette-query";
    public const string HealthDialog = "v2-shell-health-dialog-title";

    public static string Destination(V2RouteId route) => $"v2-shell-destination-{route.Value}";

    public static string Command(string id) => $"v2-shell-command-{id}";

    public static string SavedAddress(string prefix, int index) => $"v2-shell-{prefix}-{index}";
}

/// <summary>One command, the words it is offered with, and its documented shortcut, if any.</summary>
public sealed record V2ShellCommand(string Id, string LabelKey, V2ShellCommandKind Kind, string? Gesture, V2RouteId? Route = null);

/// <summary>A key press, reduced to what shortcut matching needs and nothing platform-specific.</summary>
/// <param name="Key">"1".."9", "A".."Z", "Left", "Right", "Escape", "F6", "Enter" or ",".</param>
public readonly record struct V2KeyChord(string Key, bool Control, bool Alt, bool Shift)
{
    public override string ToString() =>
        string.Concat(Control ? "Ctrl+" : string.Empty, Alt ? "Alt+" : string.Empty, Shift ? "Shift+" : string.Empty, Key);
}

/// <summary>
/// Every shell command, from one declarative list, so the palette documents what the keyboard does.
/// </summary>
/// <remarks>
/// There are no bare single-character shortcuts, which is WCAG 2.1.4 met by construction rather
/// than by review. Every printable key is held with Control or Alt; Escape and F6 are the only
/// unmodified keys, and neither is a character.
///
/// Alt+Shift+C is the #265 Capture candidate and nothing more. Windows gives Alt+Shift to input
/// language switching, so it can be turned off from the palette and the preview remembers that.
/// Nothing here registers a system-wide hotkey: these are window bindings, inert when the companion
/// does not have focus, and none of them sends anything to any other window.
/// </remarks>
public static class V2ShellCommands
{
    public const string CaptureShortcutId = "capture";

    public static IReadOnlyList<V2ShellCommand> For(V2ShellVariantDefinition variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        var commands = new List<V2ShellCommand>();
        for (var index = 0; index < variant.Destinations.Count; index++)
        {
            var destination = variant.Destinations[index];
            commands.Add(new(
                $"go.{destination.Route}",
                destination.LabelKey,
                V2ShellCommandKind.Navigate,
                index < 9 ? $"Ctrl+{index + 1}" : null,
                destination.Route));
        }

        commands.Add(new($"go.{variant.Setup.Route}", variant.Setup.LabelKey, V2ShellCommandKind.Navigate, "Ctrl+,", variant.Setup.Route));
        commands.AddRange(
        [
            new("back", "V2.Shell.Command.Back", V2ShellCommandKind.Back, "Alt+Left"),
            new("forward", "V2.Shell.Command.Forward", V2ShellCommandKind.Forward, "Alt+Right"),
            new(CaptureShortcutId, "V2.Shell.Command.Capture", V2ShellCommandKind.ToggleCapture, "Alt+Shift+C"),
            new("health", "V2.Shell.Command.Health", V2ShellCommandKind.ToggleHealth, "Ctrl+Shift+H"),
            new("palette", "V2.Shell.Command.Palette", V2ShellCommandKind.TogglePalette, "Ctrl+K"),
            new("search", "V2.Shell.Command.Search", V2ShellCommandKind.FocusSearch, "Ctrl+F"),
            new("copy-address", "V2.Shell.Command.CopyAddress", V2ShellCommandKind.CopyAddress, "Ctrl+L"),
            new("pin", "V2.Shell.Command.Pin", V2ShellCommandKind.TogglePin, "Ctrl+D"),
            new("close", "V2.Shell.Command.Close", V2ShellCommandKind.CloseTransient, "Escape"),
            new("next-region", "V2.Shell.Command.NextRegion", V2ShellCommandKind.NextRegion, "F6"),
            new("previous-region", "V2.Shell.Command.PreviousRegion", V2ShellCommandKind.PreviousRegion, "Shift+F6"),
            new("capture-shortcut", "V2.Shell.Command.CaptureShortcut", V2ShellCommandKind.ToggleCaptureShortcut, null),
            new("reset-preview", "V2.Shell.Command.ResetPreview", V2ShellCommandKind.ResetPreview, null),
        ]);
        return commands;
    }

    /// <summary>The command a chord runs, or null. A disabled Capture shortcut matches nothing.</summary>
    public static V2ShellCommand? Match(IReadOnlyList<V2ShellCommand> commands, V2KeyChord chord, bool captureShortcutEnabled)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var text = chord.ToString();
        return commands.FirstOrDefault(command =>
            command.Gesture is { } gesture &&
            string.Equals(gesture, text, StringComparison.OrdinalIgnoreCase) &&
            (captureShortcutEnabled || command.Id != CaptureShortcutId));
    }

    /// <summary>Whether a gesture is a bare printable character, which V2 never binds.</summary>
    public static bool IsBareCharacter(string gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var key = parts.Length == 0 ? gesture : parts[^1];
        var modified = gesture.Contains("Ctrl+", StringComparison.Ordinal) || gesture.Contains("Alt+", StringComparison.Ordinal);
        return key.Length == 1 && !modified;
    }
}
