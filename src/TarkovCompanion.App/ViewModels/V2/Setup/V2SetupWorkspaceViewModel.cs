using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>The sections #292 asks for, in the order they are offered.</summary>
public enum V2SetupSection
{
    /// <summary>V2 rough package 17 (home): the dashboard the Setup page opens on.</summary>
    Overview = 1,
    GameProfile,
    Recognition,
    Data,
    TeamDevices,
    Updates,
    Privacy,
    Appearance,
    Displays,
    Diagnostics,

    /// <summary>Package 29 (parity): the quest-progress exchange and TarkovTracker import V1 kept in Settings.</summary>
    Progress,

    /// <summary>V2 rough package 43 (#314): the five notifications, each with a switch and a test.</summary>
    Notifications,
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
///
/// Package 29 (parity) walked V1's Settings page against this one. The quest-progress exchange and
/// the TarkovTracker import were the only whole features Setup lacked, so they are the Progress
/// section; they bind the same <see cref="SettingsPageViewModel.Quests"/> view model V1 does,
/// because the import has to keep its preview, confirm and undo together (ADR 0004).
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
    private V2SetupSection _selected = V2SetupSection.Overview;

    public V2SetupWorkspaceViewModel(
        SettingsPageViewModel? settings,
        GroupPageViewModel? group,
        MainWindowViewModel? legacy,
        Action<V2RouteId> navigate,
        // V2 rough package 41 (#292, #281): the self-test lives in Diagnostics, beside the
        // Copy diagnostics it now feeds. Optional so the shells that build Setup without a
        // composed application still build.
        SetupSelfTestViewModel? selfTest = null,
        // V2 rough package 43 (#314): Notifications is its own section rather than a corner of
        // Privacy, because it is the only page that decides what interrupts a raid. Optional for
        // the same reason the self-test is: a shell built without a composed application still
        // has to build.
        SetupNotificationsViewModel? notifications = null)
    {
        _navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
        Settings = settings;
        Group = group;
        Legacy = legacy;
        SelfTest = selfTest;
        Notifications = notifications;
        OpenTeamCommand = new DelegateCommand(() => _navigate(V2Routes.Team));
        DecreaseScaleCommand = new DelegateCommand(() => Legacy?.StepInterfaceScale(-1));
        IncreaseScaleCommand = new DelegateCommand(() => Legacy?.StepInterfaceScale(1));
        ResetScaleCommand = new DelegateCommand(() => Legacy?.ResetInterfaceScale());
        Overview = new(navigate, Select);
        Sections =
        [
            new(V2SetupSection.Overview, "V2.Setup.Section.Overview", Select),
            new(V2SetupSection.GameProfile, "V2.Setup.Section.GameProfile", Select),
            new(V2SetupSection.Recognition, "V2.Setup.Section.Recognition", Select),
            new(V2SetupSection.Data, "V2.Setup.Section.Data", Select),
            new(V2SetupSection.Progress, "V2.Setup.Section.Progress", Select),
            new(V2SetupSection.TeamDevices, "V2.Setup.Section.TeamDevices", Select),
            new(V2SetupSection.Updates, "V2.Setup.Section.Updates", Select),
            new(V2SetupSection.Privacy, "V2.Setup.Section.Privacy", Select),
            new(V2SetupSection.Notifications, "V2.Setup.Section.Notifications", Select),
            new(V2SetupSection.Appearance, "V2.Setup.Section.Appearance", Select),
            new(V2SetupSection.Displays, "V2.Setup.Section.Displays", Select),
            new(V2SetupSection.Diagnostics, "V2.Setup.Section.Diagnostics", Select),
        ];
        Sections[0].IsCurrent = true;
    }

    public SettingsPageViewModel? Settings { get; }

    public GroupPageViewModel? Group { get; }

    public MainWindowViewModel? Legacy { get; }

    /// <summary>The self-test Diagnostics offers, or null in a shell built without one.</summary>
    public SetupSelfTestViewModel? SelfTest { get; private set; }

    public bool HasSelfTest => SelfTest is not null;

    /// <summary>V2 rough package 43 (#314): the notification switches, once one has been composed.</summary>
    public SetupNotificationsViewModel? Notifications { get; private set; }

    public bool HasNotifications => Notifications is not null;

    public void AttachNotifications(SetupNotificationsViewModel notifications)
    {
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        OnPropertyChanged(nameof(Notifications));
        OnPropertyChanged(nameof(HasNotifications));
    }

    /// <summary>
    /// Hands this page the self-test after construction.
    /// </summary>
    /// <remarks>
    /// The shell builds Setup in its base constructor, before the derived one has the composed
    /// self-test in hand — the same ordering that made Quests an assigned-after-construction
    /// property on the V1 Settings page.
    /// </remarks>
    public void AttachSelfTest(SetupSelfTestViewModel selfTest)
    {
        SelfTest = selfTest ?? throw new ArgumentNullException(nameof(selfTest));
        OnPropertyChanged(nameof(SelfTest));
        OnPropertyChanged(nameof(HasSelfTest));
    }

    public IReadOnlyList<V2SetupSectionTabViewModel> Sections { get; }

    /// <summary>The home dashboard (package 17); the shell feeds it readiness and the pages it summarises.</summary>
    public V2HomeOverviewViewModel Overview { get; }

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
    public string UpdateNowLabel => V2ShellText.Get("V2.Setup.Updates.UpdateNowLabel");
    public string GetInstallerLabel => V2ShellText.Get("V2.Setup.Updates.InstallerLabel");
    public string UpdateChannelLabel => V2ShellText.Get("V2.Setup.Updates.ChannelLabel");
    public string InstalledBuildLabel => V2ShellText.Get("V2.Setup.Updates.InstalledLabel");
    public string AvailableBuildLabel => V2ShellText.Get("V2.Setup.Updates.AvailableLabel");
    public string ScreenshotIntro => V2ShellText.Get("V2.Setup.Privacy.ScreenshotIntro");
    public string RetentionLabel => V2ShellText.Get("V2.Setup.Privacy.RetentionLabel");
    public string RetentionValueLabel => V2ShellText.Get("V2.Setup.Privacy.RetentionValueLabel");
    public string RecycleNote => V2ShellText.Get("V2.Setup.Privacy.RecycleNote");
    public string FolderPlaceholder => V2ShellText.Get("V2.Setup.GameProfile.FolderPlaceholder");
    public string ScanHint => V2ShellText.Get("V2.Setup.Recognition.ScanHint");
    public string OfflineNote => V2ShellText.Get("V2.Setup.Data.OfflineNote");
    public string ScaleHint => V2ShellText.Get("V2.Setup.Appearance.ScaleHint");
    public string ScaleScope => V2ShellText.Get("V2.Setup.Appearance.ScaleScope");
    public string LogLabel => V2ShellText.Get("V2.Setup.Diagnostics.LogLabel");
    public string RelayNote => V2ShellText.Get("V2.Setup.Diagnostics.RelayNote");
    public string ExchangeTitle => V2ShellText.Get("V2.Setup.Progress.ExchangeTitle");
    public string ExchangeNote => V2ShellText.Get("V2.Setup.Progress.ExchangeNote");
    public string ExchangePathPlaceholder => V2ShellText.Get("V2.Setup.Progress.ExchangePath");
    public string ExportLabel => V2ShellText.Get("V2.Setup.Progress.Export");
    public string PreviewImportLabel => V2ShellText.Get("V2.Setup.Progress.Preview");
    public string KeepLocalLabel => V2ShellText.Get("V2.Setup.Progress.KeepLocal");
    public string UseIncomingLabel => V2ShellText.Get("V2.Setup.Progress.UseIncoming");
    public string ApplyImportLabel => V2ShellText.Get("V2.Setup.Progress.Apply");
    public string UndoImportLabel => V2ShellText.Get("V2.Setup.Progress.Undo");
    public string ImportHistoryTitle => V2ShellText.Get("V2.Setup.Progress.HistoryTitle");
    public string TrackerTitle => V2ShellText.Get("V2.Setup.Progress.TrackerTitle");
    public string TrackerNote => V2ShellText.Get("V2.Setup.Progress.TrackerNote");
    public string TrackerTokenPlaceholder => V2ShellText.Get("V2.Setup.Progress.TrackerToken");
    public string TrackerConnectLabel => V2ShellText.Get("V2.Setup.Progress.TrackerConnect");
    public string TrackerRefreshLabel => V2ShellText.Get("V2.Setup.Progress.TrackerRefresh");
    public string TrackerDisconnectLabel => V2ShellText.Get("V2.Setup.Progress.TrackerDisconnect");
    public string ScaleLabel => V2ShellText.Get("V2.Setup.Appearance.ScaleLabel");
    public string SmallerLabel => V2ShellText.Get("V2.Setup.Appearance.SmallerLabel");
    public string LargerLabel => V2ShellText.Get("V2.Setup.Appearance.LargerLabel");
    public string ResetLabel => V2ShellText.Get("V2.Setup.Appearance.ResetLabel");
    public string DisplaysInfo => V2ShellText.Get("V2.Setup.Displays.Info");
    public string NotificationsIntro => V2ShellText.Get("V2.Setup.Notifications.Intro");
    public string NotificationsRaidNote => V2ShellText.Get("V2.Setup.Notifications.RaidNote");
    public string NotificationsTestLabel => V2ShellText.Get("V2.Setup.Notifications.TestLabel");
    public string SelfTestHeading => V2ShellText.Get("V2.Setup.Diagnostics.SelfTestHeading");
    public string SelfTestIntro => V2ShellText.Get("V2.Setup.Diagnostics.SelfTestIntro");
    public string SelfTestRunLabel => V2ShellText.Get("V2.Setup.Diagnostics.SelfTestRun");
    public string SelfTestStopLabel => V2ShellText.Get("V2.Setup.Diagnostics.SelfTestStop");
    public string SelfTestCopyLabel => V2ShellText.Get("V2.Setup.Diagnostics.SelfTestCopy");
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

            OnPropertyChanged(nameof(IsOverviewSelected));
            OnPropertyChanged(nameof(IsGameProfileSelected));
            OnPropertyChanged(nameof(IsRecognitionSelected));
            OnPropertyChanged(nameof(IsDataSelected));
            OnPropertyChanged(nameof(IsTeamDevicesSelected));
            OnPropertyChanged(nameof(IsUpdatesSelected));
            OnPropertyChanged(nameof(IsPrivacySelected));
            OnPropertyChanged(nameof(IsAppearanceSelected));
            OnPropertyChanged(nameof(IsDisplaysSelected));
            OnPropertyChanged(nameof(IsDiagnosticsSelected));
            OnPropertyChanged(nameof(IsProgressSelected));
            OnPropertyChanged(nameof(IsNotificationsSelected));
        }
    }

    public bool IsOverviewSelected => Selected == V2SetupSection.Overview;
    public bool IsGameProfileSelected => Selected == V2SetupSection.GameProfile;
    public bool IsRecognitionSelected => Selected == V2SetupSection.Recognition;
    public bool IsDataSelected => Selected == V2SetupSection.Data;
    public bool IsTeamDevicesSelected => Selected == V2SetupSection.TeamDevices;
    public bool IsUpdatesSelected => Selected == V2SetupSection.Updates;
    public bool IsPrivacySelected => Selected == V2SetupSection.Privacy;
    public bool IsAppearanceSelected => Selected == V2SetupSection.Appearance;
    public bool IsDisplaysSelected => Selected == V2SetupSection.Displays;
    public bool IsDiagnosticsSelected => Selected == V2SetupSection.Diagnostics;
    public bool IsProgressSelected => Selected == V2SetupSection.Progress;
    public bool IsNotificationsSelected => Selected == V2SetupSection.Notifications;

    public void Select(V2SetupSection section) => Selected = section;

    /// <summary>Where the Home readiness checklist sends a check's Open button, when it names one.</summary>
    public static bool TryMapReadinessCheck(string checkId, out V2SetupSection section) =>
        ReadinessSectionMap.TryGetValue(checkId, out section);
}
