using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>One shortcut this window already handles, exactly as the command palette lists it.</summary>
public sealed record V2SetupShortcutViewModel(string Label, string Gesture);

/// <summary>
/// The Accessibility section (#292, #315): theme, colour vision, text size, density, reduced
/// motion and the focus ring, gathered under one heading with a live preview strip, plus the
/// keyboard shortcut table.
/// </summary>
/// <remarks>
/// Owns no preferences of its own. <see cref="Appearance"/> is the same view model the section
/// used to bind directly under the "Appearance" tab; #292 asks for an "Accessibility" section and
/// today's pass renames the tab and gathers its content here rather than drawing the same controls
/// twice (#315: one <c>WorkspacePreferenceService</c>, one writer). <see cref="Shortcuts"/> is the
/// same command list V2ShellCommands.For(...) builds for the command palette (V2ShellViewModel
/// maps it once, in <c>AttachAccessibility</c>), so this table can never list a gesture the window
/// does not actually handle.
/// </remarks>
public sealed class V2SetupAccessibilityViewModel(
    V2AppearanceSettingsViewModel appearance,
    IReadOnlyList<V2SetupShortcutViewModel> shortcuts)
{
    public V2AppearanceSettingsViewModel Appearance { get; } =
        appearance ?? throw new ArgumentNullException(nameof(appearance));

    public IReadOnlyList<V2SetupShortcutViewModel> Shortcuts { get; } =
        shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));

    public bool HasShortcuts => Shortcuts.Count > 0;

    public string PreviewHeading => V2ShellText.Get("V2.Setup.Accessibility.PreviewHeading");
    public string PreviewHint => V2ShellText.Get("V2.Setup.Accessibility.PreviewHint");
    public string PreviewSampleHeading => V2ShellText.Get("V2.Setup.Accessibility.PreviewSampleHeading");
    public string PreviewSampleBody => V2ShellText.Get("V2.Setup.Accessibility.PreviewSampleBody");
    public string PreviewSampleButton => V2ShellText.Get("V2.Setup.Accessibility.PreviewSampleButton");
    public string PreviewSampleField => V2ShellText.Get("V2.Setup.Accessibility.PreviewSampleField");
    public string PreviewSampleReady => V2ShellText.Get("V2.Setup.Accessibility.PreviewSampleReady");
    public string PreviewSampleFailed => V2ShellText.Get("V2.Setup.Accessibility.PreviewSampleFailed");
    public string ShortcutsHeading => V2ShellText.Get("V2.Setup.Accessibility.ShortcutsHeading");
    public string ShortcutsHint => V2ShellText.Get("V2.Setup.Accessibility.ShortcutsHint");
}
