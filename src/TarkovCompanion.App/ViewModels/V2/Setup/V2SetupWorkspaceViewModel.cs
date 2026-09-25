using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.Diagnostics;
using System.Windows.Input;
using TarkovCompanion.App.Services.V2.Setup;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Tablet;

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

    /// <summary>Renamed from Appearance by #292/#315: theme, colour vision, text size, density,
    /// reduced motion, the focus ring and interface scale, gathered under the heading #292's
    /// acceptance criteria actually ask for.</summary>
    Accessibility,
    Displays,
    Diagnostics,

    /// <summary>Package 29 (parity): the quest-progress exchange and TarkovTracker import V1 kept in Settings.</summary>
    Progress,

    /// <summary>V2 rough package 43 (#314): the six notifications, each with a switch and a test.</summary>
    Notifications,
    /// <summary>#292: what the data is and what leaves the machine, layered; the deep-link target for "why" beside a control.</summary>
    DataPrivacy,

    /// <summary>#292: what the app is, what it never does, and its notices.</summary>
    About,
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
public sealed partial class V2SetupWorkspaceViewModel : BindableViewModel
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
        OpenTeamCommand = new DelegateCommand(() => _navigate(V2Routes.Group));
        OpenQuestSyncCommand = new DelegateCommand(() => Select(V2SetupSection.Progress));
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
            new(V2SetupSection.Accessibility, "V2.Setup.Section.Accessibility", Select),
            new(V2SetupSection.Displays, "V2.Setup.Section.Displays", Select),
            new(V2SetupSection.Diagnostics, "V2.Setup.Section.Diagnostics", Select),
            new(V2SetupSection.DataPrivacy, "V2.Setup.Section.DataPrivacy", Select),
            new(V2SetupSection.About, "V2.Setup.Section.About", Select),
        ];
        Sections[0].IsCurrent = true;
        // #292: paths are hidden until asked for, and every path Setup prints goes through this one gate.
        Paths = new SetupPathDisclosureViewModel();
        WatchedFolders = new(() => Settings?.WatchedFolders, Paths, Settings, nameof(SettingsPageViewModel.WatchedFolders));
        DatabasePath = new(() => Settings?.DatabasePath, Paths, Settings, nameof(SettingsPageViewModel.DatabasePath));
        UpdateDataFolder = new(() => Settings?.UpdateDataFolder, Paths, Settings, nameof(SettingsPageViewModel.UpdateDataFolder));
        CompanionLogPath = new(() => Settings?.CompanionLogPath, Paths, Settings, nameof(SettingsPageViewModel.CompanionLogPath));
        if (settings is not null)
        {
            settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsPageViewModel.LastUpdateCheckFailed))
                {
                    OnPropertyChanged(nameof(CheckUpdateButtonLabel));
                }
            };
        }

        OpenPrivacyDetailCommand = new DelegateCommand(() => OpenSection(V2SetupSection.DataPrivacy, SetupAnchors.CaptureRetention));
        OpenSharingDetailCommand = new DelegateCommand(() => OpenSection(V2SetupSection.DataPrivacy, SetupAnchors.SharingScope));
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

    /// <summary>Screenshot onboarding in Progress, or null in a shell without runtime services.</summary>
    public QuestScreenshotSyncViewModel? QuestSync { get; private set; }

    public bool HasQuestSync => QuestSync is not null;

    public RecommendationHorizonSettingsViewModel? RecommendationHorizons { get; private set; }

    public bool HasRecommendationHorizons => RecommendationHorizons is not null;

    public void AttachRecommendationHorizons(RecommendationHorizonSettingsViewModel recommendations)
    {
        RecommendationHorizons = recommendations ?? throw new ArgumentNullException(nameof(recommendations));
        OnPropertyChanged(nameof(RecommendationHorizons));
        OnPropertyChanged(nameof(HasRecommendationHorizons));
    }

    /// <summary>The relay clock warning, shared verbatim with Team &gt; Tablet.</summary>
    public CompanionPairingViewModel? Pairing { get; private set; }

    public void AttachPairing(CompanionPairingViewModel pairing)
    {
        Pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        OnPropertyChanged(nameof(Pairing));
    }

    public bool ShowsQuestSyncOffer => QuestSync?.ShowEmptyOffer == true;

    public void AttachQuestSync(QuestScreenshotSyncViewModel questSync)
    {
        QuestSync = questSync ?? throw new ArgumentNullException(nameof(questSync));
        QuestSync.OpenPassiveReview = () =>
        {
            _navigate(V2Routes.Setup);
            Select(V2SetupSection.Progress);
        };
        OnPropertyChanged(nameof(QuestSync));
        OnPropertyChanged(nameof(HasQuestSync));
        QuestSync.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(QuestScreenshotSyncViewModel.ShowEmptyOffer))
            {
                OnPropertyChanged(nameof(ShowsQuestSyncOffer));
            }
        };
        QuestSync.RefreshOfferAsync().Observe("setup", "check empty quest progress");
    }

    public void AttachNotifications(SetupNotificationsViewModel notifications)
    {
        Notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        OnPropertyChanged(nameof(Notifications));
        OnPropertyChanged(nameof(HasNotifications));
    }

    /// <summary>[#309] Screenshot tidying's preview, dry run and ledger, or null in a shell built without them.</summary>
    public SetupCleanupViewModel? Cleanup { get; private set; }

    public bool HasCleanup => Cleanup is not null;

    /// <summary>The old one-press toggle shows only when there is no preview flow to go through instead.</summary>
    public bool NoCleanup => Cleanup is null;

    /// <summary>Hands this page the tidy preview after construction, for the reason <see cref="AttachSelfTest"/> gives.</summary>
    public void AttachCleanup(SetupCleanupViewModel cleanup)
    {
        Cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        OnPropertyChanged(nameof(Cleanup));
        OnPropertyChanged(nameof(HasCleanup));
        OnPropertyChanged(nameof(NoCleanup));
    }

    /// <summary>[#269] The profile list Game &amp; Profile opens with, or null in a shell built without one.</summary>
    public SetupProfilesViewModel? Profiles { get; private set; }

    public bool HasProfiles => Profiles is not null;

    /// <summary>Hands this page the profile list after construction, for the reason <see cref="AttachSelfTest"/> gives.</summary>
    public void AttachProfiles(SetupProfilesViewModel profiles)
    {
        Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        OnPropertyChanged(nameof(Profiles));
        OnPropertyChanged(nameof(HasProfiles));
    }

    /// <summary>
    /// The theme, colour-vision, text-scale, density and motion choices (#266, #315).
    /// </summary>
    /// <remarks>
    /// Null only in a shell built without a composed preference store, which is every unit test
    /// that predates this and the headless hosts that build Setup to check a binding. When it is
    /// null the section still shows the window-scale stepper rather than an empty card.
    /// </remarks>
    public V2AppearanceSettingsViewModel? Appearance { get; private set; }

    public bool HasAppearance => Appearance is not null;

    /// <summary>Hands this page the appearance choices, for the reason AttachSelfTest gives.</summary>
    public void AttachAppearance(V2AppearanceSettingsViewModel appearance)
    {
        Appearance = appearance ?? throw new ArgumentNullException(nameof(appearance));
        OnPropertyChanged(nameof(Appearance));
        OnPropertyChanged(nameof(HasAppearance));
    }

    /// <summary>The Accessibility section's own view model: <see cref="Appearance"/> plus the
    /// keyboard shortcut table, or null in a shell built without one.</summary>
    public V2SetupAccessibilityViewModel? Accessibility { get; private set; }

    public bool HasAccessibility => Accessibility is not null;

    /// <summary>Hands this page the Accessibility section, for the reason AttachSelfTest gives.</summary>
    public void AttachAccessibility(V2SetupAccessibilityViewModel accessibility)
    {
        Accessibility = accessibility ?? throw new ArgumentNullException(nameof(accessibility));
        OnPropertyChanged(nameof(Accessibility));
        OnPropertyChanged(nameof(HasAccessibility));
    }

    /// <summary>#292 task 2: "Reset this section", "Reset everything", export and import, shown
    /// on every section rather than owning one. Null in a shell built without one.</summary>
    public SetupSettingsAdminViewModel? SettingsAdmin { get; private set; }

    public bool HasSettingsAdmin => SettingsAdmin is not null;

    /// <summary>Hands this page the settings admin panel, for the reason AttachSelfTest gives.</summary>
    public void AttachSettingsAdmin(SetupSettingsAdminViewModel settingsAdmin)
    {
        SettingsAdmin = settingsAdmin ?? throw new ArgumentNullException(nameof(settingsAdmin));
        SettingsAdmin.SetCurrentSection(Selected);
        OnPropertyChanged(nameof(SettingsAdmin));
        OnPropertyChanged(nameof(HasSettingsAdmin));
    }

    /// <summary>#292 task 3: the database's migration state and verified backup, in Data. Null in
    /// a shell built without one.</summary>
    public SetupDatabaseStatusViewModel? DatabaseStatus { get; private set; }

    public bool HasDatabaseStatus => DatabaseStatus is not null;

    /// <summary>Hands this page the database status panel, for the reason AttachSelfTest gives.</summary>
    public void AttachDatabaseStatus(SetupDatabaseStatusViewModel databaseStatus)
    {
        DatabaseStatus = databaseStatus ?? throw new ArgumentNullException(nameof(databaseStatus));
        DatabaseStatus.RefreshAsync().ContinueWith(_ => { }, TaskScheduler.Default);
        OnPropertyChanged(nameof(DatabaseStatus));
        OnPropertyChanged(nameof(HasDatabaseStatus));
    }

    /// <summary>[#292] Whether file paths show in full. Off at every launch; nothing remembers it.</summary>
    public SetupPathDisclosureViewModel Paths { get; }

    public GatedPathText WatchedFolders { get; }

    public GatedPathText DatabasePath { get; }

    public GatedPathText UpdateDataFolder { get; }

    public GatedPathText CompanionLogPath { get; }

    /// <summary>[#292] The data detail, About, Data &amp; Privacy and Displays pages; null in a shell built without them.</summary>
    public SetupAdminViewModel? Admin { get; private set; }

    public bool HasAdmin => Admin is not null;

    public ICommand OpenPrivacyDetailCommand { get; }

    public ICommand OpenSharingDetailCommand { get; }

    /// <summary>Hands this page #292's pages after construction, for the reason <see cref="AttachSelfTest"/> gives.</summary>
    public void AttachAdmin(SetupAdminViewModel admin)
    {
        Admin = admin ?? throw new ArgumentNullException(nameof(admin));
        OnPropertyChanged(nameof(Admin));
        OnPropertyChanged(nameof(HasAdmin));
    }

    /// <summary>
    /// Opens a Setup section, and on About or Data &amp; Privacy the item named by <paramref name="anchor"/>,
    /// expanded. This is the deep link: a control elsewhere that wants to say "why" lands on the answer.
    /// </summary>
    /// <returns>False when the section is not there or has no such item; the section still opens.</returns>
    public bool OpenSection(V2SetupSection section, string? anchor = null)
    {
        Select(section);
        if (anchor is null || Admin is null)
        {
            return anchor is null;
        }

        return section switch
        {
            V2SetupSection.About => Admin.About.Open(anchor),
            V2SetupSection.DataPrivacy => Admin.DataPrivacy.Open(anchor),
            _ => false,
        };
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

    public ICommand OpenQuestSyncCommand { get; }

    public ICommand DecreaseScaleCommand { get; }

    public ICommand IncreaseScaleCommand { get; }

    public ICommand ResetScaleCommand { get; }

    public string ReasonHeading => SetupText.DataReasonLabel;
    public string UpdateNotesHeading => SetupText.UpdatesNotesHeading;
    public string GoingBackHeading => SetupText.UpdatesGoingBackHeading;
    public string GoingBackNote => SetupText.UpdatesGoingBack;
    public string OpenPrivacyDetailLabel => SetupText.InfoOpenPrivacy;
    public string OpenSharingDetailLabel => SetupText.InfoOpenSharing;

    /// <summary>The check button says "Try again" once a check has failed, so a failure has an obvious next step.</summary>
    public string CheckUpdateButtonLabel => Settings?.LastUpdateCheckFailed == true
        ? SetupText.UpdatesRetryLabel
        : CheckUpdateLabel;

    public string SectionsRegionName => SetupText.SectionsRegion;
    public string ScreenshotFolderLabel => SetupText.GameProfileScreenshotFolderLabel;
    public string LogFolderLabel => SetupText.GameProfileLogFolderLabel;
    public string SaveFoldersLabel => SetupText.GameProfileSaveLabel;
    public string RecognitionRuntimeWarning => SetupText.RecognitionRuntimeWarning;
    public string SyncLabel => SetupText.DataSyncLabel;
    public string OpenTeamLabel => SetupText.TeamDevicesOpenLabel;
    public string CheckUpdateLabel => SetupText.UpdatesCheckLabel;
    public string UpdateNowLabel => SetupText.UpdatesUpdateNowLabel;
    public string GetInstallerLabel => SetupText.UpdatesInstallerLabel;
    public string UpdateChannelLabel => SetupText.UpdatesChannelLabel;
    public string InstalledBuildLabel => SetupText.UpdatesInstalledLabel;
    public string AvailableBuildLabel => SetupText.UpdatesAvailableLabel;
    public string ScreenshotIntro => SetupText.PrivacyScreenshotIntro;
    public string RetentionLabel => SetupText.PrivacyRetentionLabel;
    public string RetentionValueLabel => SetupText.PrivacyRetentionValueLabel;
    public string RecycleNote => SetupText.PrivacyRecycleNote;
    public string FolderPlaceholder => SetupText.GameProfileFolderPlaceholder;
    public string ScanHint => SetupText.RecognitionScanHint;
    public string OfflineNote => SetupText.DataOfflineNote;
    public string ScaleHint => SetupText.AppearanceScaleHint;
    public string ScaleScope => SetupText.AppearanceScaleScope;
    /// <summary>[#454] Said once, on the launch after a run that died without shutting down.</summary>
    public string PreviousRunNotice { get; } = CrashBreadcrumbs.DescribePreviousRun();

    public bool HasPreviousRunNotice => PreviousRunNotice.Length > 0;

    public string LogLabel => SetupText.DiagnosticsLogLabel;
    public string RelayNote => SetupText.DiagnosticsRelayNote;
    public string ExchangeTitle => SetupText.ProgressExchangeTitle;
    public string ExchangeNote => SetupText.ProgressExchangeNote;
    public string ExchangePathPlaceholder => SetupText.ProgressExchangePath;
    public string ExportLabel => SetupText.ProgressExport;
    public string PreviewImportLabel => SetupText.ProgressPreview;
    public string KeepLocalLabel => SetupText.ProgressKeepLocal;
    public string UseIncomingLabel => SetupText.ProgressUseIncoming;
    public string ApplyImportLabel => SetupText.ProgressApply;
    public string UndoImportLabel => SetupText.ProgressUndo;
    public string ImportHistoryTitle => SetupText.ProgressHistoryTitle;
    public string TrackerTitle => SetupText.ProgressTrackerTitle;
    public string TrackerNote => SetupText.ProgressTrackerNote;
    public string TrackerTokenPlaceholder => SetupText.ProgressTrackerToken;
    public string TrackerConnectLabel => SetupText.ProgressTrackerConnect;
    public string TrackerRefreshLabel => SetupText.ProgressTrackerRefresh;
    public string TrackerDisconnectLabel => SetupText.ProgressTrackerDisconnect;
    public string ScaleLabel => SetupText.AppearanceScaleLabel;
    public string SmallerLabel => SetupText.AppearanceSmallerLabel;
    public string LargerLabel => SetupText.AppearanceLargerLabel;
    public string ResetLabel => SetupText.AppearanceResetLabel;
    public string DisplaysInfo => SetupText.DisplaysInfo;
    public string NotificationsIntro => SetupText.NotificationsIntro;
    public string NotificationsRaidNote => SetupText.NotificationsRaidNote;
    public string NotificationsTestLabel => SetupText.NotificationsTestLabel;
    public string SelfTestHeading => SetupText.DiagnosticsSelfTestHeading;
    public string SelfTestIntro => SetupText.DiagnosticsSelfTestIntro;
    public string SelfTestRunLabel => SetupText.DiagnosticsSelfTestRun;
    public string SelfTestStopLabel => SetupText.DiagnosticsSelfTestStop;
    public string SelfTestCopyLabel => SetupText.DiagnosticsSelfTestCopy;
    public string CopyDiagnosticsLabel => SetupText.DiagnosticsCopyLabel;
    public string ReportProblemLabel => SetupText.DiagnosticsReportLabel;

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
            OnPropertyChanged(nameof(IsAccessibilitySelected));
            OnPropertyChanged(nameof(IsDisplaysSelected));
            OnPropertyChanged(nameof(IsDiagnosticsSelected));
            OnPropertyChanged(nameof(IsProgressSelected));
            OnPropertyChanged(nameof(IsNotificationsSelected));
            OnPropertyChanged(nameof(IsDataPrivacySelected));
            OnPropertyChanged(nameof(IsAboutSelected));
            // Ages and monitors are read when the page is opened, not carried from the last visit.
            if (value == V2SetupSection.Data)
            {
                Admin?.Data.Refresh();
                // #292 task 3: the database's migration state and backup, read fresh each visit.
                if (DatabaseStatus is { } databaseStatus)
                {
                    databaseStatus.RefreshAsync().ContinueWith(_ => { }, TaskScheduler.Default);
                }
            }
            else if (value == V2SetupSection.Displays)
            {
                Admin?.Displays.RefreshCommand.Execute(null);
            }
            else if (value == V2SetupSection.About)
            {
                Admin?.About.Refresh();
            }
            else if (value == V2SetupSection.DataPrivacy)
            {
                Admin?.DataPrivacy.Refresh();
            }
            if (value == V2SetupSection.Privacy && Cleanup is { } cleanup)
            {
                cleanup.RefreshLedger();
                cleanup.LoadAsync().ContinueWith(_ => { }, TaskScheduler.Default);
            }

            // #292 task 2: "Reset this section" acts on whichever section is open now.
            SettingsAdmin?.SetCurrentSection(value);
        }
    }

    public bool IsOverviewSelected => Selected == V2SetupSection.Overview;
    public bool IsGameProfileSelected => Selected == V2SetupSection.GameProfile;
    public bool IsRecognitionSelected => Selected == V2SetupSection.Recognition;
    public bool IsDataSelected => Selected == V2SetupSection.Data;
    public bool IsTeamDevicesSelected => Selected == V2SetupSection.TeamDevices;
    public bool IsUpdatesSelected => Selected == V2SetupSection.Updates;
    public bool IsPrivacySelected => Selected == V2SetupSection.Privacy;
    public bool IsAccessibilitySelected => Selected == V2SetupSection.Accessibility;
    public bool IsDisplaysSelected => Selected == V2SetupSection.Displays;
    public bool IsDiagnosticsSelected => Selected == V2SetupSection.Diagnostics;
    public bool IsProgressSelected => Selected == V2SetupSection.Progress;
    public bool IsNotificationsSelected => Selected == V2SetupSection.Notifications;
    public bool IsDataPrivacySelected => Selected == V2SetupSection.DataPrivacy;
    public bool IsAboutSelected => Selected == V2SetupSection.About;

    public void Select(V2SetupSection section)
    {
        Selected = section;
        if (section == V2SetupSection.Data)
        {
            if (QuestCoverage is { } coverage)
            {
                coverage.RefreshAsync().Observe("setup", "refresh quest coverage");
            }

            if (LootCoverage is { } lootCoverage)
            {
                lootCoverage.RefreshAsync().Observe("setup", "refresh loot coverage");
            }
        }
    }

    /// <summary>The per-map loot-spawn coverage Data shows, or null in a shell built without one.</summary>
    public LootCoverageViewModel? LootCoverage { get; private set; }

    public bool HasLootCoverage => LootCoverage is not null;

    /// <summary>Hands this page the loot coverage report after construction, like <see cref="AttachQuestCoverage"/>.</summary>
    public void AttachLootCoverage(LootCoverageViewModel coverage)
    {
        LootCoverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        OnPropertyChanged(nameof(LootCoverage));
        OnPropertyChanged(nameof(HasLootCoverage));
    }

    /// <summary>The per-map quest objective coverage Data shows, or null in a shell built without one.</summary>
    public QuestCoverageViewModel? QuestCoverage { get; private set; }

    public bool HasQuestCoverage => QuestCoverage is not null;

    /// <summary>Hands this page the coverage report after construction, for the same ordering reason as <see cref="AttachSelfTest"/>.</summary>
    public void AttachQuestCoverage(QuestCoverageViewModel coverage)
    {
        QuestCoverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        OnPropertyChanged(nameof(QuestCoverage));
        OnPropertyChanged(nameof(HasQuestCoverage));
    }

    /// <summary>Where the Home readiness checklist sends a check's Open button, when it names one.</summary>
    public static bool TryMapReadinessCheck(string checkId, out V2SetupSection section) =>
        ReadinessSectionMap.TryGetValue(checkId, out section);
}
