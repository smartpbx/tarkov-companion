using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>The sections #292 asks for, in the order they are offered.</summary>
public enum V2SetupSection
{
    GameProfile = 1,
    Recognition,
    Data,
    TeamDevices,
    Updates,
    Privacy,
    Appearance,
    Displays,
    Diagnostics,
}

/// <summary>One clickable section tab, the same shape as the shell's other selectable rows.</summary>
public sealed class V2SetupSectionTabViewModel : BindableViewModel
{
    private bool _isCurrent;

    public V2SetupSectionTabViewModel(V2SetupSection section, string labelKey, Action<V2SetupSection> select)
    {
        ArgumentNullException.ThrowIfNull(select);
        Section = section;
        LabelKey = labelKey;
        SelectCommand = new DelegateCommand(() => select(section));
    }

    public V2SetupSection Section { get; }

    private string LabelKey { get; }

    public string Label => V2ShellText.Get(LabelKey);

    public string DisplayLabel => IsCurrent ? $"› {Label}" : Label;

    public string AutomationId => $"v2-setup-section-{Section.ToString().ToLowerInvariant()}";

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }

    public ICommand SelectCommand { get; }
}

/// <summary>
/// The native V2 Setup page (#292). Every value it shows comes from the same view models V1's
/// Settings page already binds; this only arranges them into sections and adds none of its own.
/// </summary>
/// <remarks>
/// A section with nothing wired yet says so in one line rather than drawing a control that does
/// nothing - Team &amp; Devices and Displays are both that shape today, since their real controls
/// already live on the Team page and in automatic window placement (#316) respectively.
/// </remarks>
public sealed class V2SetupWorkspaceViewModel : BindableViewModel
{
    private static readonly IReadOnlyDictionary<string, V2SetupSection> ReadinessSectionMap = new Dictionary<string, V2SetupSection>(StringComparer.Ordinal)
    {
        ["game-log"] = V2SetupSection.GameProfile,
        ["screenshots"] = V2SetupSection.GameProfile,
        ["text-recognition"] = V2SetupSection.Recognition,
        ["game-data"] = V2SetupSection.Data,
    };

    private readonly Action<V2RouteId> _navigate;
    private V2SetupSection _selected = V2SetupSection.GameProfile;

    public V2SetupWorkspaceViewModel(SettingsPageViewModel? settings, GroupPageViewModel? group, MainWindowViewModel? legacy, Action<V2RouteId> navigate)
    {
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        Settings = settings;
        Group = group;
        Legacy = legacy;
        OpenTeamCommand = new DelegateCommand(() => _navigate(V2Routes.Team));
        DecreaseScaleCommand = new DelegateCommand(() => Legacy?.StepInterfaceScale(-1));
        IncreaseScaleCommand = new DelegateCommand(() => Legacy?.StepInterfaceScale(1));
        ResetScaleCommand = new DelegateCommand(() => Legacy?.ResetInterfaceScale());
        Sections =
        [
            new(V2SetupSection.GameProfile, "V2.Setup.Section.GameProfile", Select),
            new(V2SetupSection.Recognition, "V2.Setup.Section.Recognition", Select),
            new(V2SetupSection.Data, "V2.Setup.Section.Data", Select),
            new(V2SetupSection.TeamDevices, "V2.Setup.Section.TeamDevices", Select),
            new(V2SetupSection.Updates, "V2.Setup.Section.Updates", Select),
            new(V2SetupSection.Privacy, "V2.Setup.Section.Privacy", Select),
            new(V2SetupSection.Appearance, "V2.Setup.Section.Appearance", Select),
            new(V2SetupSection.Displays, "V2.Setup.Section.Displays", Select),
            new(V2SetupSection.Diagnostics, "V2.Setup.Section.Diagnostics", Select),
        ];
        Sections[0].IsCurrent = true;
    }

    public SettingsPageViewModel? Settings { get; }

    public GroupPageViewModel? Group { get; }

    public MainWindowViewModel? Legacy { get; }

    public IReadOnlyList<V2SetupSectionTabViewModel> Sections { get; }

    public ICommand OpenTeamCommand { get; }

    public ICommand DecreaseScaleCommand { get; }

    public ICommand IncreaseScaleCommand { get; }

    public ICommand ResetScaleCommand { get; }

    public string SectionsRegionName => V2ShellText.Get("V2.Setup.Sections.Region");
    public string ScreenshotFolderLabel => V2ShellText.Get("V2.Setup.GameProfile.ScreenshotFolderLabel");
    public string LogFolderLabel => V2ShellText.Get("V2.Setup.GameProfile.LogFolderLabel");
    public string SaveFoldersLabel => V2ShellText.Get("V2.Setup.GameProfile.SaveLabel");
    public string RecognitionRuntimeWarning => V2ShellText.Get("V2.Setup.Recognition.RuntimeWarning");
    public string SyncLabel => V2ShellText.Get("V2.Setup.Data.SyncLabel");
    public string OpenTeamLabel => V2ShellText.Get("V2.Setup.TeamDevices.OpenLabel");
    public string CheckUpdateLabel => V2ShellText.Get("V2.Setup.Updates.CheckLabel");
    public string DownloadUpdateLabel => V2ShellText.Get("V2.Setup.Updates.DownloadLabel");
    public string RestartUpdateLabel => V2ShellText.Get("V2.Setup.Updates.RestartLabel");
    public string ScreenshotIntro => V2ShellText.Get("V2.Setup.Privacy.ScreenshotIntro");
    public string RetentionLabel => V2ShellText.Get("V2.Setup.Privacy.RetentionLabel");
    public string DebugCaptureLabel => V2ShellText.Get("V2.Setup.Privacy.DebugCapture");
    public string ScaleLabel => V2ShellText.Get("V2.Setup.Appearance.ScaleLabel");
    public string SmallerLabel => V2ShellText.Get("V2.Setup.Appearance.SmallerLabel");
    public string LargerLabel => V2ShellText.Get("V2.Setup.Appearance.LargerLabel");
    public string ResetLabel => V2ShellText.Get("V2.Setup.Appearance.ResetLabel");
    public string DisplaysInfo => V2ShellText.Get("V2.Setup.Displays.Info");
    public string CopyDiagnosticsLabel => V2ShellText.Get("V2.Setup.Diagnostics.CopyLabel");
    public string ReportProblemLabel => V2ShellText.Get("V2.Setup.Diagnostics.ReportLabel");

    public V2SetupSection Selected
    {
        get => _selected;
        private set
        {
            if (!SetProperty(ref _selected, value))
            {
                return;
            }

            foreach (var section in Sections)
            {
                section.IsCurrent = section.Section == value;
            }

            OnPropertyChanged(nameof(IsGameProfileSelected));
            OnPropertyChanged(nameof(IsRecognitionSelected));
            OnPropertyChanged(nameof(IsDataSelected));
            OnPropertyChanged(nameof(IsTeamDevicesSelected));
            OnPropertyChanged(nameof(IsUpdatesSelected));
            OnPropertyChanged(nameof(IsPrivacySelected));
            OnPropertyChanged(nameof(IsAppearanceSelected));
            OnPropertyChanged(nameof(IsDisplaysSelected));
            OnPropertyChanged(nameof(IsDiagnosticsSelected));
        }
    }

    public bool IsGameProfileSelected => Selected == V2SetupSection.GameProfile;
    public bool IsRecognitionSelected => Selected == V2SetupSection.Recognition;
    public bool IsDataSelected => Selected == V2SetupSection.Data;
    public bool IsTeamDevicesSelected => Selected == V2SetupSection.TeamDevices;
    public bool IsUpdatesSelected => Selected == V2SetupSection.Updates;
    public bool IsPrivacySelected => Selected == V2SetupSection.Privacy;
    public bool IsAppearanceSelected => Selected == V2SetupSection.Appearance;
    public bool IsDisplaysSelected => Selected == V2SetupSection.Displays;
    public bool IsDiagnosticsSelected => Selected == V2SetupSection.Diagnostics;

    public void Select(V2SetupSection section) => Selected = section;

    /// <summary>Where the Home readiness checklist sends a check's Open button, when it names one.</summary>
    public static bool TryMapReadinessCheck(string checkId, out V2SetupSection section) =>
        ReadinessSectionMap.TryGetValue(checkId, out section);
}
