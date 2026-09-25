using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// [#902 P6] What Setup shows that the shell owns: the Capture shortcut, What's new, and Learn mode.
/// </summary>
/// <remarks>
/// These were bound through <c>$parent[V2ShellView]</c>, so they drew only inside the running shell
/// and a test that hosted the Setup view could not see them. The shell hands itself over once, and
/// each switch here is the same state as the palette command or Plan chip it mirrors (one home per
/// setting; a second control only as a shortcut to it).
/// </remarks>
public sealed partial class V2SetupWorkspaceViewModel
{
    /// <summary>The shell this page sits in, or null in a Setup built without one.</summary>
    public V2ShellViewModel? Shell { get; private set; }

    public bool HasShell => Shell is not null;

    public void AttachShell(V2ShellViewModel shell)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        OnPropertyChanged(nameof(Shell));
        OnPropertyChanged(nameof(HasShell));
    }

    /// <summary>"Explain recommendations": the Learn mode Plan's chip also switches.</summary>
    public LearnModeSetting? LearnMode { get; private set; }

    public bool HasLearnMode => LearnMode is not null;

    public void AttachLearnMode(LearnModeSetting learnMode)
    {
        LearnMode = learnMode ?? throw new ArgumentNullException(nameof(learnMode));
        OnPropertyChanged(nameof(LearnMode));
        OnPropertyChanged(nameof(HasLearnMode));
    }

    public string LearnModeTitle => SetupText.ProgressLearnTitle;

    public string LearnModeLine => SetupText.ProgressLearnLine;

    public string CaptureShortcutLine => SetupText.CaptureShortcutLine;

    public string RecognitionHeading => SetupText.GameCaptureRecognitionHeading;

    public string CleanupHeading => SetupText.GameCaptureCleanupHeading;

    public string LootScanHeading => SetupText.GameCaptureLootHeading;

    public string FoldersHeading => SetupText.GameCaptureFoldersHeading;

    public string MoveProgressHeading => SetupText.ProgressMoveHeading;

    public string MergeProgressHeading => SetupText.ProgressMergeHeading;

    public string SquadHeading => SetupText.DataNetworkSquadHeading;

    public string GameDataHeading => SetupText.DataNetworkGameDataHeading;

    public string WindowHeading => SetupText.AppearanceWindowHeading;

    public string FeaturesHeading => SetupText.UpdatesFeaturesHeading;

    public string DiagnosticsHeading => SetupText.UpdatesDiagnosticsHeading;
}
