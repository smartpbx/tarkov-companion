using TarkovCompanion.App.Localization;
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

    public string PreviewHeading => SetupText.AccessibilityPreviewHeading;
    public string PreviewHint => SetupText.AccessibilityPreviewHint;
    public string PreviewSampleHeading => SetupText.AccessibilityPreviewSampleHeading;
    public string PreviewSampleBody => SetupText.AccessibilityPreviewSampleBody;
    public string PreviewSampleButton => SetupText.AccessibilityPreviewSampleButton;
    public string PreviewSampleField => SetupText.AccessibilityPreviewSampleField;
    public string PreviewSampleReady => SetupText.AccessibilityPreviewSampleReady;
    public string PreviewSampleFailed => SetupText.AccessibilityPreviewSampleFailed;
    public string ShortcutsHeading => SetupText.AccessibilityShortcutsHeading;
    public string ShortcutsHint => SetupText.AccessibilityShortcutsHint;
}
