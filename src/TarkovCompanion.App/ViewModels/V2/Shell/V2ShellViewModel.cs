using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Controls.ApplicationLifetimes;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Debrief;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.ReleaseExperience;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.App.ViewModels.V2.Tablet;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Personalization;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Shell;
using TarkovCompanion.Application.Services.Wiki;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

public enum V2ShellDialogKind
{
    None = 0,
    Capture,
    Commands,
    Health,
}

/// <summary>One labelled fact on the Intel result card.</summary>
public sealed record V2ShellIntelFactViewModel(string Label, string Value);

public sealed record V2NavigationContinuity(
    string? PlanId,
    string? ObjectiveId,
    string? PriorScan,
    string? CaptureCorrelationId,
    string InitiatingDevice)
{
    public static V2NavigationContinuity Desktop { get; } = new(null, null, null, null, V2NavigationContext.ThisDesktop);
}

/// <summary>The one provisional presentation over the already-composed V1 runtime.</summary>
/// <remarks>
/// This owns only preview navigation and chrome. It receives the existing V1 view model instead
/// of composing services again: starting a preview must not create a second watcher, database
/// connection, refresh loop, or capture path.
/// </remarks>
public sealed partial class V2ShellViewModel : BindableViewModel, IAsyncDisposable
{
    private readonly IRuntimeStateStore _runtime;
    private readonly IItemIntelService _intel;

    internal bool FleaPricesAreOffline => _runtime.Current.IsOffline;

    internal TimeProvider FleaClock => _clock;
    private readonly IWikiLinkOpener _wikiOpener;
    private readonly StashScanWorkspaceViewModel? _stashScan;
    private readonly DebriefWorkspaceViewModel? _debrief;
    /// <summary>How often the home overview re-reads recent raids while it is showing.</summary>
    private static readonly TimeSpan HomeOverviewRefresh = TimeSpan.FromSeconds(15);

    private readonly PlanWorkspaceViewModel? _plan;
    private bool _homeOverviewLoaded;
    private DateTimeOffset _homeOverviewLoadedUtc;
    private RaidLifecycleState _homeOverviewRaid;
    private readonly HideoutWorkspaceViewModel? _hideout;
    // V2 rough package 25 (#402): the Keep list, a Plan section beside Hideout.
    private readonly KeepListWorkspaceViewModel? _keep;
    private DateTimeOffset? _planDataUpdatedUtc;
    // v2r-team (package 9, wave 2): the Team workspace, shared by the Team/Group/Tablet routes.
    private readonly TeamWorkspaceViewModel? _team;
    private readonly V2ShellPreviewStore _preview;
    private readonly V2ShellPersistenceQueue _persistence;
    private readonly TimeProvider _clock;
    private readonly CoalescingDispatch _apply;

    /// <summary>[#678] The legacy pages' changes, one Refresh per turn rather than one per property.</summary>
    /// <remarks>
    /// Loading a map raises a dozen or more properties on the map model inside one turn (canvas
    /// size, zoom, tiles, floors, status), and each ran the whole shell Refresh inline: about
    /// 1.8 s of interface time over the stall tour's map switches.
    /// </remarks>
    private readonly DeferredDispatch _legacyApply;
    private readonly SynchronizationContext? _dispatcherContext;
    private readonly ConcurrentQueue<V2ShellPersistenceResult> _persistenceResults = new();
    private V2NavigationRail _navigationRail = V2NavigationRail.Labels;
    private readonly List<INotifyPropertyChanged> _legacyContextSources = [];
    /// <summary>[#294] Keeps the Setup mark in step with the updater for the life of the shell.</summary>
    private readonly IDisposable? _updateNotice;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly object _disposeSync = new();
    private string _address = string.Empty;
    private string _politeAnnouncement = string.Empty;
    private string _assertiveAnnouncement = string.Empty;
    private string _searchText = string.Empty;
    private string _paletteQuery = string.Empty;
    private V2ShellDialogKind _activeDialog;
    private string? _dialogInvoker;
    private bool _captureShortcutEnabled = true;
    private bool _surfaceInitialized;
    // V2 rough package 20: the global-problem banner is dismissible, per problem. Remembering the
    // identity (kind + detail) rather than a plain flag means a *different* problem raises the
    // banner again instead of staying silently hidden behind an earlier dismissal.
    private string _dismissedSurfaceIdentity = string.Empty;
    private bool _disposed;
    // V2 rough package 15 (shell chrome + Raid workspace): the #265 scaffold chrome (variant
    // labels, Back/Forward, address bar, pin/copy, the Commands button) confused Clayton as
    // production UI. It stays wired for --developer-mode diagnostics and the Windows page
    // gallery script (which now passes that flag for the scenarios that exercise it) rather than
    // being deleted outright.
    private readonly bool _developerMode;
    private V2WidthClass _widthClass = V2WidthClass.Expanded;
    private V2ShellWindowPlacement? _window;
    private V2NavigationContinuity _continuity = V2NavigationContinuity.Desktop;
    private int _regionIndex = -1;
    private int _resetInProgress;
    private Task? _disposeTask;
    private V2ReadinessCheck? _activeReadinessTarget;
    private V2FocusRequest? _playerActionStateFocus;
    private V2CaptureShellState _captureState = V2CaptureShellState.Empty;
    private LootScanViewModel? _lootScanResult;
    private V2CaptureShellState? _renderedCaptureState;
    private ScanIntent _selectedCaptureIntent = ScanIntent.Auto;
    private string _persistenceFailure = string.Empty;
    private V2ShellPersistenceOperationKind? _persistenceFailureKind;
    private bool _persistenceRetryPending;
    private V2ShellSuggestionKind _suggestionFilter = V2ShellSuggestionKind.All;
    private IReadOnlyList<V2PlannedItemSuggestion> _plannedSuggestions = [];
    private ITimer? _headerTimer;
    private string? _loadedIntelItemId;
    private V2ItemIntelResult? _intelResult;
    private bool _intelLoading;
    private CancellationTokenSource? _intelLoadCts;
    // v2r-pairing-tablet: null under the internal test constructor, which builds a V2 graph
    // without the desktop's paired-device authority. The one caller of it, "manage-pairing",
    // no-ops when it is null.
    private readonly CompanionPairingViewModel? _companionPairing;
    private QuestScreenshotSyncViewModel? _questSync;

    public V2ShellViewModel(
        AppCommandLine options,
        AppDataPaths paths,
        IRuntimeStateStore runtime,
        MainWindowViewModel legacy,
        IItemIntelService intel,
        IWikiLinkOpener wikiOpener,
        CompanionPairingViewModel companionPairing,
        StashScanWorkspaceViewModel? stashScan = null,
        DebriefWorkspaceViewModel? debrief = null,
        // V2 Raid cockpit (package 2): resolved by DI like every other registered service here;
        // optional so this constructor's shape does not change for a caller that predates it.
        RaidCockpitViewModel? raidCockpit = null,
        TimeProvider? clock = null,
        // V2 rough package 10 (Plan workspace + Hideout section): optional for the same reason
        // as stashScan/debrief above.
        PlanWorkspaceViewModel? plan = null,
        HideoutWorkspaceViewModel? hideout = null,
        // V2 rough package 25 (#402): optional for the same reason as plan/hideout above.
        KeepListWorkspaceViewModel? keep = null,
        // v2r-team (package 9, wave 2): same reasoning — optional so this shape does not change.
        TeamWorkspaceViewModel? team = null,
        // V2 rough package 41 (#292, #281): Setup's self-test, same reasoning again.
        SetupSelfTestViewModel? selfTest = null,
        // V2 rough package 43 (#314): Setup's notification switches, same reasoning again.
        SetupNotificationsViewModel? notifications = null,
        // [V2 rough package 60 — appearance] #266/#315: the stored theme/text-scale/density
        // record the Appearance section writes. Optional for the same reason as the rest.
        WorkspacePreferenceService? preferences = null,
        // [#269] Setup's profile list, same reasoning again.
        SetupProfilesViewModel? profiles = null,
        // [#292] Setup's data detail, About, Data & Privacy and Displays, same reasoning again.
        SetupAdminViewModel? admin = null,
        // [#292 task 2] Setup's reset/export/import panel, same reasoning again.
        SetupSettingsAdminViewModel? settingsAdmin = null,
        // [#292 task 3] Setup's database migration/backup status, same reasoning again.
        SetupDatabaseStatusViewModel? databaseStatus = null,
        // [#309] Setup's screenshot-tidy preview and ledger, same reasoning again.
        SetupCleanupViewModel? cleanup = null,
        // [Issue 379] Setup's per-map quest objective coverage, same reasoning again.
        QuestCoverageViewModel? questCoverage = null,
        // [Issue 318] Setup's per-map loot-spawn coverage, same reasoning again.
        LootCoverageViewModel? lootCoverage = null,
        // Package 33 (#287): the Intel landing page's four real sections, same reasoning again.
        IIntelLandingService? intelLanding = null,
        // #287 (Crafts & barters tab): same reasoning again.
        IIntelTradeCatalogService? intelTrade = null,
        // #307: one recursive acquisition planner shared by item detail and Crafts & barters.
        IAcquisitionChainPlanningService? acquisitionChains = null,
        // #287 (event state on items): same reasoning again.
        IIntelEventStateCatalog? intelEventStates = null,
        // #667: Setup's screenshot quest onboarding, absent in lightweight shell tests.
        QuestScreenshotSyncViewModel? questSync = null,
        // #274: persisted quest/hideout look-ahead controls in Setup > Progress.
        RecommendationHorizonSettingsViewModel? recommendationHorizons = null,
        // #314: the after-update banner and its player-facing change list.
        ReleaseExperienceViewModel? releaseExperience = null,
        LearnModeSetting? learnMode = null)
        : this(
            RequirePreview(options?.UiShell ?? throw new ArgumentNullException(nameof(options))),
            options.StartPage,
            runtime,
            legacy,
            raidCockpit,
            new V2ShellPreviewStore(
                (paths ?? throw new ArgumentNullException(nameof(paths))).Config,
                options.UiShell,
                clock),
            clock,
            save: null,
            reset: null,
            intel,
            wikiOpener,
            stashScan,
            debrief,
            plan,
            hideout,
            keep,
            team,
            options.DeveloperMode,
            intelLanding,
            intelTrade,
            acquisitionChains,
            intelEventStates,
            learnMode)
    {
        _companionPairing = companionPairing ?? throw new ArgumentNullException(nameof(companionPairing));
        ReleaseExperience = releaseExperience;
        SetupWorkspace?.AttachPairing(_companionPairing);
        if (selfTest is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachSelfTest(selfTest);
        }

        if (notifications is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachNotifications(notifications);
        }

        if (questSync is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachQuestSync(questSync);
            _questSync = questSync;
            _questSync.PropertyChanged += QuestSyncPropertyChanged;
        }

        if (recommendationHorizons is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachRecommendationHorizons(recommendationHorizons);
        }

        if (preferences is not null && SetupWorkspace is not null)
        {
            var appearance = new V2AppearanceSettingsViewModel(preferences);
            SetupWorkspace.AttachAppearance(appearance);
            // #292/#315: the Accessibility section reuses this same view model rather than a
            // second one, and lists exactly the shortcuts the command palette already offers so
            // the table can never say a gesture the window does not actually handle.
            SetupWorkspace.AttachAccessibility(new V2SetupAccessibilityViewModel(
                appearance,
                [.. Commands
                    .Where(command => command.Gesture is not null)
                    .Select(command => new V2SetupShortcutViewModel(V2ShellText.Get(command.LabelKey), command.Gesture!))]));
        }

        if (profiles is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachProfiles(profiles);
        }

        if (admin is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachAdmin(admin);
            AttachLootScanSettings(admin.LootScan);
        }

        if (settingsAdmin is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachSettingsAdmin(settingsAdmin);
        }

        if (databaseStatus is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachDatabaseStatus(databaseStatus);
        }

        if (cleanup is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachCleanup(cleanup);
        }

        if (questCoverage is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachQuestCoverage(questCoverage);
        }

        if (lootCoverage is not null && SetupWorkspace is not null)
        {
            SetupWorkspace.AttachLootCoverage(lootCoverage);
        }
    }

    /// <summary>Builds the actual shell behavior in tests without composing a second V1 graph.</summary>
    internal V2ShellViewModel(
        V2ShellMode mode,
        string configDirectory,
        IRuntimeStateStore runtime,
        TimeProvider? clock = null,
        Func<V2ShellPreviewState, CancellationToken, Task>? save = null,
        Func<CancellationToken, Task>? reset = null,
        IItemIntelService? intel = null,
        IWikiLinkOpener? wikiOpener = null,
        StashScanWorkspaceViewModel? stashScan = null,
        DebriefWorkspaceViewModel? debrief = null,
        PlanWorkspaceViewModel? plan = null,
        HideoutWorkspaceViewModel? hideout = null,
        KeepListWorkspaceViewModel? keep = null,
        TeamWorkspaceViewModel? team = null,
        bool developerMode = false,
        IIntelLandingService? intelLanding = null,
        IIntelTradeCatalogService? intelTrade = null,
        IAcquisitionChainPlanningService? acquisitionChains = null,
        IIntelEventStateCatalog? intelEventStates = null,
        LearnModeSetting? learnMode = null)
        : this(
            RequirePreview(mode),
            requestedAddress: null,
            runtime,
            legacy: null,
            raidCockpit: null,
            new V2ShellPreviewStore(configDirectory, mode, clock),
            clock,
            save,
            reset,
            intel,
            wikiOpener,
            stashScan,
            debrief,
            plan,
            hideout,
            keep,
            team,
            developerMode,
            intelLanding,
            intelTrade,
            acquisitionChains,
            intelEventStates,
            learnMode)
    {
    }

    private V2ShellViewModel(
        V2ShellMode mode,
        string? requestedAddress,
        IRuntimeStateStore runtime,
        MainWindowViewModel? legacy,
        RaidCockpitViewModel? raidCockpit,
        V2ShellPreviewStore preview,
        TimeProvider? clock,
        Func<V2ShellPreviewState, CancellationToken, Task>? save,
        Func<CancellationToken, Task>? reset,
        IItemIntelService? intel = null,
        IWikiLinkOpener? wikiOpener = null,
        StashScanWorkspaceViewModel? stashScan = null,
        DebriefWorkspaceViewModel? debrief = null,
        PlanWorkspaceViewModel? plan = null,
        HideoutWorkspaceViewModel? hideout = null,
        KeepListWorkspaceViewModel? keep = null,
        TeamWorkspaceViewModel? team = null,
        bool developerMode = false,
        IIntelLandingService? intelLanding = null,
        IIntelTradeCatalogService? intelTrade = null,
        IAcquisitionChainPlanningService? acquisitionChains = null,
        IIntelEventStateCatalog? intelEventStates = null,
        LearnModeSetting? learnMode = null)
    {
        _lifetimeToken = _lifetime.Token;
        _developerMode = developerMode;
        _runtime = runtime;
        _stashScan = stashScan;
        _debrief = debrief;
        _plan = plan;
        _hideout = hideout;
        _keep = keep;
        _team = team;
        _clock = clock ?? TimeProvider.System;
        _intel = intel ?? NullItemIntelService.Instance;
        _intelLanding = intelLanding ?? NullIntelLandingService.Instance;
        _intelTrade = intelTrade ?? NullIntelTradeCatalogService.Instance;
        _intelEventStateCatalog = intelEventStates ?? NullIntelEventStateCatalog.Instance;
        _wikiOpener = wikiOpener ?? NullWikiLinkOpener.Instance;
        Legacy = legacy;
        RaidCockpit = raidCockpit;
        Registry = V2RouteRegistry.Default;
        Variant = V2ShellVariants.For(mode);
        Router = new V2ShellRouter(Variant, Registry);
        if (legacy is not null && _stashScan is not null)
        {
            _stashScan.AttachLoadoutNavigation(async itemIds =>
            {
                if (await legacy.Loadout.LoadRecognizedItemsAsync(itemIds).ConfigureAwait(true) > 0)
                {
                    GoTo(V2Routes.Loadout, "v2-stash-open-loadout");
                }
            });
        }
        _preview = preview;
        _persistence = new(save ?? _preview.SaveAsync, reset ?? _preview.ResetAsync);
        _persistence.Completed += PersistenceCompleted;
        PrimaryDestinations = new ObservableCollection<V2ShellDestinationViewModel>(
            Variant.Destinations.Select(destination => new V2ShellDestinationViewModel(
                destination,
                route => GoTo(route, V2ShellFocusTargets.Destination(route)))));
        SetupDestination = new(
            Variant.Setup,
            route => GoTo(route, V2ShellFocusTargets.Destination(route)));
        // [#294] V1 marked its rail when a build was waiting; V2 marked nothing, so an update
        // arrived and told nobody who had not gone looking in Setup › Updates.
        _updateNotice = legacy is null ? null : MarkWhileUpdateWaits(legacy.Settings, SetupDestination);
        // #292: built once, from the same view models V1's Settings page binds. Null only in the
        // handful of tests above that build a shell without a legacy graph to adapt.
        SetupWorkspace = legacy is null ? null : new(legacy.Settings, legacy.Group, legacy, GoTo);
        // V2 rough package 28 (parity): Ammo, Keys and Flea adapt the V1 pages' view models, and
        // Intel searches ask for more hits than V1's cards could hold.
        if (legacy is not null)
        {
            legacy.Items.ResultLimit = IntelResultLimit;
            AmmoWorkspace = new(legacy.Ammo, id => OpenSuggestedItem(id, "v2-ammo-open-intel"), learnMode);
            KeysWorkspace = new(legacy.Keys, id => OpenSuggestedItem(id, "v2-keys-open-intel"), learnMode);
            FleaWorkspace = new(legacy.Flea, id => OpenSuggestedItem(id, "v2-flea-open-intel"));
        }

        // #287: Crafts & barters has no V1 page to adapt, so it is built from the trade catalog
        // service directly rather than gated behind a legacy graph.
        var chainPlanner = acquisitionChains ?? NullAcquisitionChainPlanningService.Instance;
        IntelAcquisitionChain = new(chainPlanner);
        CraftsBartersWorkspace = new(_intelTrade, chainPlanner, id => OpenSuggestedItem(id, "v2-crafts-open-intel"), learnMode);
        // V2 rough package 17 (home): the Setup overview summarises Plan, Debrief, privacy and the map.
        SetupWorkspace?.Overview.Attach(_plan, _debrief, legacy?.Settings, RaidCockpitWorkspace);
        if (legacy is not null)
        {
            _debrief?.UseMapNames(id => legacy.Map.Locations
                .FirstOrDefault(location => string.Equals(location.Id, id, StringComparison.OrdinalIgnoreCase))?.Name);
        }
        // V2 rough package 17 (team): the Team context panel's links move through this router.
        _team?.AttachNavigation(route => GoTo(route, V2ShellFocusTargets.Destination(route)));
        if (legacy is not null)
        {
            // Package 29 (parity): the in-game party is V1's Squad view model, which the legacy graph
            // already keeps current on every runtime snapshot.
            _team?.AttachParty(legacy.Squad);
            // Package 29 (parity): "Watch on map" in Debrief, which V1's History page did by opening
            // its replay bar on the Raid page. The same replay view model, now on the V2 Raid map.
            if (_debrief is not null)
            {
                _debrief.ReplayRequested += (_, request) => WatchRaidAsync(legacy, request).Observe("debrief", "open a replay");
            }
        }

        BackCommand = new DelegateCommand(Back);
        ForwardCommand = new DelegateCommand(Forward);
        CaptureCommand = new DelegateCommand(() => ToggleDialog(
            V2ShellDialogKind.Capture,
            V2ShellFocusTargets.Capture,
            V2ShellFocusTargets.CaptureDialog));
        HealthCommand = new DelegateCommand(() => ToggleDialog(
            V2ShellDialogKind.Health,
            V2ShellFocusTargets.Health,
            V2ShellFocusTargets.HealthDialog));
        DismissSurfaceBannerCommand = new DelegateCommand(DismissSurfaceBanner);
        CycleNavigationRailCommand = new DelegateCommand(CycleNavigationRail);
        ShowNavigationRailCommand = new DelegateCommand(ShowNavigationRail);
        PaletteCommand = new DelegateCommand(() => ToggleDialog(
            V2ShellDialogKind.Commands,
            V2ShellFocusTargets.Palette,
            V2ShellFocusTargets.PaletteDialog));
        AddressCommand = new DelegateCommand(OpenAddress);
        SearchCommand = new AsyncDelegateCommand(SearchAsync);
        PinCommand = new DelegateCommand(TogglePin);
        OpenIntelWikiCommand = new DelegateCommand(() => _wikiOpener.TryOpen(_intelResult?.WikiUri));
        CopyAddressCommand = new AsyncDelegateCommand(CopyAddressAsync);
        CloseTransientCommand = new DelegateCommand(CloseTransient);
        ResetPreviewCommand = new AsyncDelegateCommand(ResetPreviewAsync);
        ResetPreviewCommand.CanExecuteChanged += ResetPreviewCanExecuteChanged;
        RetryPersistenceCommand = new DelegateCommand(RetryPersistence);
        PaletteAddressCommand = new DelegateCommand(OpenPaletteAddress);
        ArmCaptureCommand = new DelegateCommand(ArmSelectedCaptureIntent);
        InitializeManualCapture();
        // V2 rough package 17 (scan): the Loot decision tab's "Scan loot" / "Scan again".
        ScanLootCommand = new DelegateCommand(() => StashScanRequested(this, ScanIntent.Loot));
        Commands = V2ShellCommands.For(Variant, Registry);
        CommandItems = new ObservableCollection<V2ShellCommandViewModel>(
            Commands.Select(command => new V2ShellCommandViewModel(command, CreateCommand(command))));
        CaptureIntents = Enum.GetValues<ScanIntent>()
            .Select(intent => new V2CaptureIntentViewModel(intent, SelectCaptureIntent))
            .ToArray();
        CaptureIntents.Single(intent => intent.Intent == SelectedCaptureIntent).SetSelected(true);
        SuggestionFilters = Enum.GetValues<V2ShellSuggestionKind>()
            .Select(kind => new V2ShellSuggestionFilterViewModel(kind, SelectSuggestionFilter))
            .ToArray();
        SuggestionFilters.Single(filter => filter.Kind == SuggestionFilter).IsSelected = true;
        BrowseCategories = V2BrowseCategoryViewModel.Create(Variant, GoTo);

        var synchronizationContext = SynchronizationContext.Current;
        _dispatcherContext = synchronizationContext?.GetType().Namespace?
            .StartsWith("Avalonia", StringComparison.Ordinal) == true
                ? synchronizationContext
                : null;
        _apply = new(_dispatcherContext, () =>
        {
            if (!_disposed)
            {
                // #572: the header tick is already once a second; it doubles as the Loot page's
                // own clock rather than a second timer.
                TickLootAutoReturn();
                Refresh(announceBackgroundChange: true);
            }
        });

        _legacyApply = new(_dispatcherContext, _apply.Request);

        Router.Navigated += RouterNavigated;
        _runtime.Changed += RuntimeChanged;
        if (_stashScan is not null)
        {
            _stashScan.ScanRequested += StashScanRequested;
        }

        if (_plan is not null)
        {
            _plan.ShowOnMapRequested += PlanShowOnMapRequested;
            // V2 rough package 17: the Plan page's Hideout card opens the Hideout tab.
            _plan.OpenHideoutRequested += PlanOpenHideoutRequested;
        }

        WireLegacyContext();
        WireRaidClock();
        WireLootAutoReturn();
        WireProfileChip();
        Restore(requestedAddress);
        RebuildSectionItems();
        LoadCurrentWorkspace();
        Refresh(announceBackgroundChange: false);
        if (_dispatcherContext is not null)
        {
            _headerTimer = _clock.CreateTimer(
                _ => _apply.Request(),
                state: null,
                dueTime: TimeSpan.FromSeconds(1),
                period: TimeSpan.FromSeconds(1));
        }
    }

    private static V2ShellMode RequirePreview(V2ShellMode mode) => mode.IsPreview()
        ? mode
        : throw new ArgumentException("A V2 shell view model requires a preview launch mode.", nameof(mode));

    public event EventHandler<V2FocusRequest>? FocusRequested;

    public event EventHandler<V2CaptureArmRequest>? CaptureArmRequested;

    public event EventHandler<V2CaptureResolutionRequest>? CaptureResolutionRequested;

    /// <summary>Assigned by the attached view because a clipboard belongs to a top-level window.</summary>
    public Func<string, Task> Clipboard { get; set; } = _ =>
        Task.FromException(new InvalidOperationException("The shell is not attached to a clipboard."));

    public MainWindowViewModel? Legacy { get; }
    public AmmoWorkspaceViewModel? AmmoWorkspace { get; }
    public KeysWorkspaceViewModel? KeysWorkspace { get; }
    public FleaWorkspaceViewModel? FleaWorkspace { get; }
    public CraftsBartersWorkspaceViewModel? CraftsBartersWorkspace { get; }
    public AcquisitionChainViewModel IntelAcquisitionChain { get; }
    // V2 Raid cockpit (package 2): a sibling of Legacy, not part of it — see the constructor.
    public object? RaidCockpit { get; }
    public LootScanViewModel? LootScanResult => Volatile.Read(ref _lootScanResult);
    public V2RouteRegistry Registry { get; }
    public V2ShellVariantDefinition Variant { get; }
    public V2ShellRouter Router { get; }
    public ObservableCollection<V2ShellDestinationViewModel> PrimaryDestinations { get; }
    public V2ShellDestinationViewModel SetupDestination { get; }
    public V2SetupWorkspaceViewModel? SetupWorkspace { get; }

    /// <summary>The Plan workspace, so the view can hand it a clipboard (#288's export).</summary>
    public PlanWorkspaceViewModel? PlanWorkspace => _plan;
    public IReadOnlyList<V2ShellSectionViewModel> SectionItems { get; private set; } = [];
    public IReadOnlyList<V2ShellCommand> Commands { get; }
    public ObservableCollection<V2ShellCommandViewModel> CommandItems { get; }
    public IReadOnlyList<V2ReadinessCheckViewModel> ReadinessItems { get; private set; } = [];
    public IReadOnlyList<V2RecoveryActionViewModel> RecoveryActions { get; private set; } = [];
    public IReadOnlyList<V2SavedAddressViewModel> RecentItems { get; private set; } = [];
    public IReadOnlyList<V2SavedAddressViewModel> ContinueItems { get; private set; } = [];
    public IReadOnlyList<V2SavedAddressViewModel> PinItems { get; private set; } = [];
    public IReadOnlyList<V2CaptureIntentViewModel> CaptureIntents { get; }
    public IReadOnlyList<V2CaptureProgressViewModel> CaptureProgressItems { get; private set; } = [];
    public IReadOnlyList<V2CaptureActionViewModel> CaptureAttentionActions { get; private set; } = [];
    public IReadOnlyList<V2CaptureActionViewModel> CaptureReviewActions { get; private set; } = [];
    public IReadOnlyList<V2ShellSuggestionViewModel> SuggestionItems { get; private set; } = [];
    public IReadOnlyList<V2ShellSuggestionFilterViewModel> SuggestionFilters { get; }
    public IReadOnlyList<V2BrowseCategoryViewModel> BrowseCategories { get; }
    public string AppName => V2ShellText.Get("V2.Shell.AppName");
    public bool ShowsQuestScreenshotOffer => _questSync?.ShowsPassiveOffer == true;
    public string QuestScreenshotOfferText => _questSync?.PassiveOfferText ?? string.Empty;
    public ICommand? ReviewQuestScreenshotOfferCommand => _questSync?.ReviewPassiveOfferCommand;
    public ICommand? DismissQuestScreenshotOfferCommand => _questSync?.DismissPassiveOfferCommand;
    public string AppTag => V2ShellText.Get("V2.Shell.Tag");
    public string ProvisionalLabel => V2ShellText.Get("V2.Shell.Provisional");
    public string VariantName => V2ShellText.Get(Variant.NameKey);
    /// <summary>Whether the #265 A/B scaffold chrome (variant labels, Back/Forward, the address
    /// bar, Pin/Copy, the Commands button) draws. False for every normal launch; true only under
    /// --developer-mode, which the Windows page gallery script now requests for the scenarios
    /// that still exercise this chrome.</summary>
    public bool IsDeveloperMode => _developerMode;
    public bool ShowsScaffoldChrome => IsDeveloperMode;
    public string NavigationRegionName => V2ShellText.Get("V2.Shell.Region.Navigation");
    public string ContextRegionName => V2ShellText.Get("V2.Shell.Region.Context");
    public string SectionRegionName => V2ShellText.Get("V2.Shell.Region.Sections");
    public string MainRegionName => V2ShellText.Get("V2.Shell.Region.Main");
    public string SetupSectionLabel => V2ShellText.Get("V2.Shell.Region.SetupSection");
    public string BackLabel => V2ShellText.Get("V2.Shell.Command.Back");
    public string ForwardLabel => V2ShellText.Get("V2.Shell.Command.Forward");
    public string CaptureLabel => CaptureState.Attention is not null
        ? V2ShellText.Get("V2.Shell.Capture.NeedsDecision")
        : CaptureState.IntentRevision.Value > 0
            ? V2ShellText.Format(
                "V2.Shell.Capture.ArmedHeader",
                CultureInfo.CurrentCulture,
                IntentLabel(CaptureState.ArmedIntent),
                CaptureState.IntentRevision.Value)
            : V2ShellText.Get("V2.Shell.Command.Capture");
    public string HealthLabel => HealthSummary;
    public string PaletteLabel => V2ShellText.Get("V2.Shell.Command.Palette");

    /// <summary>#292 task 4: the real chrome's one entry point to the palette, replacing the
    /// scaffold's own Commands button (still reachable under developer mode, never in a normal
    /// launch). The gesture is spelled out here rather than left to a tooltip convention, since
    /// this is the only place a player who has never opened Setup would see it.</summary>
    public string PaletteTooltip => V2ShellText.Get("V2.Shell.Command.PaletteTooltip");
    public string CopyAddressLabel => V2ShellText.Get("V2.Shell.Command.CopyAddress");
    public string PinLabel => V2ShellText.Get("V2.Shell.Command.Pin");
    public string AddressLabel => V2ShellText.Get("V2.Shell.Address.Label");
    public string OpenAddressLabel => V2ShellText.Get("V2.Shell.Address.Open");
    public string SearchLabel => V2ShellText.Get("V2.Shell.Search.Label");
    public string SearchPlaceholder => V2ShellText.Get("V2.Shell.Search.Placeholder");
    public string SearchOpenLabel => V2ShellText.Get("V2.Shell.Search.Open");
    public string CloseLabel => V2ShellText.Get("V2.Shell.Close");
    public string ReadinessHeading => V2ShellText.Get("V2.Shell.Readiness.Heading");
    public string ContinueHeading => V2ShellText.Get("V2.Shell.Continue.Heading");
    public string ContinueEmpty => V2ShellText.Get("V2.Shell.Continue.Empty");
    public string PaletteHeading => V2ShellText.Get("V2.Shell.Palette.Heading");
    public string PaletteQueryLabel => V2ShellText.Get("V2.Shell.Palette.Query");
    public string PalettePinsHeading => V2ShellText.Get("V2.Shell.Palette.Pins");
    public string PaletteRecentsHeading => V2ShellText.Get("V2.Shell.Palette.Recents");
    public string PaletteEmpty => V2ShellText.Get("V2.Shell.Palette.Empty");
    public string HealthHeading => V2ShellText.Get("V2.Shell.Health.Heading");
    public string CaptureHeading => V2ShellText.Get("V2.Shell.Capture.Heading");
    public string CaptureGuidance => V2ShellText.Get("V2.Shell.Capture.Guidance");
    public string CaptureIntentHeading => V2ShellText.Get("V2.Shell.Capture.IntentHeading");
    public string CaptureArmLabel => V2ShellText.Get("V2.Shell.Capture.Arm");
    public string CaptureProgressHeading => V2ShellText.Get("V2.Shell.Capture.ProgressHeading");
    public string CapturePriorHeading => V2ShellText.Get("V2.Shell.Capture.PriorHeading");
    public string CapturePrior => Router.Context.PriorScan is { } prior
        ? V2ShellText.Format("V2.Shell.Capture.Prior", CultureInfo.CurrentCulture, prior)
        : V2ShellText.Get("V2.Shell.Capture.NoPrior");
    public string CaptureReference => CaptureState.CorrelationId is { } correlation
        ? V2ShellText.Format("V2.Shell.Capture.Reference", CultureInfo.CurrentCulture, correlation)
        : V2ShellText.Get("V2.Shell.Capture.NoReference");
    public string CaptureWatchingStatus => _runtime.Current.Observation.IsWatchingScreenshots
        ? V2ShellText.Get("V2.Shell.Capture.Watching")
        : V2ShellText.Get("V2.Shell.Capture.NotWatching");
    public string CaptureArmedStatus => CaptureState.IntentRevision.Value == 0
        ? V2ShellText.Get("V2.Shell.Capture.NotArmed")
        : V2ShellText.Format(
            "V2.Shell.Capture.Armed",
            CultureInfo.CurrentCulture,
            IntentLabel(CaptureState.ArmedIntent),
            CaptureState.IntentRevision.Value,
            CaptureState.SettingDevice);
    public string CaptureAttentionHeading => CaptureState.Attention is { } attention
        ? V2ShellText.Format(
            $"V2.Shell.Capture.Attention.{attention.Kind}",
            CultureInfo.CurrentCulture,
            (attention.CaptureOrdinal ?? 0) + 1,
            attention.DetectedContext is { } context ? ContextLabel(context) : IntentLabel(attention.BoundIntent))
        : string.Empty;
    public string CaptureAttentionDetail => CaptureState.Attention is { } attention
        ? attention.Detail ?? V2ShellText.Get($"V2.Shell.Capture.AttentionDetail.{attention.Kind}")
        : string.Empty;
    public string CaptureReviewHeading => V2ShellText.Get("V2.Shell.Capture.Review");
    public string CaptureReviewSummary => CaptureState.Review?.Summary ?? string.Empty;
    public string CaptureReviewEvidence => CaptureState.Review is { } review
        ? V2ShellText.Format(
            "V2.Shell.Capture.Evidence",
            CultureInfo.CurrentCulture,
            review.Provenance,
            LocalTime.Moment(review.CapturedUtc))
        : string.Empty;
    public string CaptureShortcutStatus => V2ShellText.Get(
        CaptureShortcutEnabled ? "V2.Shell.Capture.ShortcutOn" : "V2.Shell.Capture.ShortcutOff");
    public string ProfileContextLabel => Router.Context.ProfileName is { } profile
        ? V2ShellText.Format(
            "V2.Shell.Context.ProfileMode",
            CultureInfo.CurrentCulture,
            profile,
            Router.Context.ProfileMode ?? V2ShellText.Get("V2.Shell.Context.UnknownMode"))
        : V2ShellText.Get("V2.Shell.Context.NoProfile");
    public string LocalTimeLabel => V2ShellText.Format(
        "V2.Shell.Context.LocalTime",
        CultureInfo.CurrentCulture,
        LocalTime.ShortTime(_clock.GetUtcNow()));
    public string RaidContextLabel => FormatRaidContext(_runtime.Current.Raid, _clock.GetUtcNow());
    /// <summary>The top bar's compact raid clock chip, e.g. "In raid · 12:34 left".</summary>
    public string RaidClockLabel => FormatRaidClock(_runtime.Current.Raid, _clock.GetUtcNow());
    /// <summary>The top bar's compact mode chip, without the "Profile:" prefix a diagnostic
    /// reader needs but a glanceable header does not.</summary>
    public string TopBarModeLabel => Router.Context.ProfileName is { } profile
        ? WithWipeAndLanguage(V2ShellText.Format(
            "V2.Shell.Context.ModeCompact",
            CultureInfo.CurrentCulture,
            profile,
            Router.Context.ProfileMode ?? V2ShellText.Get("V2.Shell.Context.UnknownMode")))
        : V2ShellText.Get("V2.Shell.Context.NoProfileCompact");
    /// <summary>"Data updated 12 min ago", from the same freshness signal the readiness/health
    /// surface already reasons about.</summary>
    public string DataFreshnessLabel => FormatDataFreshness(_runtime.Current.Data.UpdatedUtc, _clock.GetUtcNow());
    /// <summary>The Raid workspace, typed for the top bar's map selector. The chrome otherwise
    /// treats <see cref="RaidCockpit"/> as opaque content so the view carries no Raid-specific
    /// type dependency; the map selector is the one place the header needs to reach into it.</summary>
    public RaidCockpitViewModel? RaidCockpitWorkspace => RaidCockpit as RaidCockpitViewModel;
    public bool ShowsMapSelector => RaidCockpitWorkspace is { MapPicker.Count: > 0 };
    public string PlanContextLabel => Router.Context.PlanId is { } plan
        ? V2ShellText.Format(
            "V2.Shell.Context.Plan",
            CultureInfo.CurrentCulture,
            plan,
            Router.Context.ObjectiveId ?? V2ShellText.Get("V2.Shell.Context.NoObjective"))
        : V2ShellText.Get("V2.Shell.Context.NoPlan");
    public string TeamContextLabel => V2ShellText.Format(
        "V2.Shell.Context.Team",
        CultureInfo.CurrentCulture,
        Router.Context.TeamMemberKeys.Count);
    public string DeviceContextLabel => V2ShellText.Format(
        "V2.Shell.Context.Device",
        CultureInfo.CurrentCulture,
        Router.Context.InitiatingDevice);
    public string SelectionContextLabel => Router.Context.SelectedEntity is { } selected
        ? V2ShellText.Format("V2.Shell.Context.Selection", CultureInfo.CurrentCulture, selected)
        : V2ShellText.Get("V2.Shell.Context.NoSelection");
    public string PersistenceFailure => _persistenceFailure;
    public string PersistenceRetryLabel => V2ShellText.Get(_persistenceRetryPending
        ? "V2.Shell.Persistence.Retrying"
        : _persistenceFailureKind == V2ShellPersistenceOperationKind.Reset
            ? "V2.Shell.Persistence.RetryReset"
            : "V2.Shell.Persistence.RetrySave");
    public string SuggestionsHeading => V2ShellText.Get("V2.Shell.Suggestions.Heading");
    public string BrowseHeading => V2ShellText.Get("V2.Shell.Suggestions.Browse");
    public string SuggestionsEmpty => V2ShellText.Get("V2.Shell.Suggestions.Empty");
    public string IntelHeading => V2ShellText.Get("V2.Shell.Intel.Heading");
    public string IntelDescription => _intelResult is { Kind: not V2IntelKind.Unknown } result
        ? result.Name
        : V2ShellText.Format("V2.Shell.Intel.Item", CultureInfo.CurrentCulture, IntelItem);
    public bool IntelIsLoading => _intelLoading;
    public bool IntelIsNotFound => !_intelLoading && _intelResult is { Kind: V2IntelKind.Unknown };
    public string IntelStatusLabel => IntelIsLoading
        ? V2ShellText.Get("V2.Shell.Intel.Loading")
        : IntelIsNotFound ? V2ShellText.Get("V2.Shell.Intel.NotFound") : string.Empty;
    public string IntelKindLabel => _intelResult?.Kind switch
    {
        V2IntelKind.Key => V2ShellText.Get("V2.Shell.Intel.KindKey"),
        V2IntelKind.Ammo => V2ShellText.Get("V2.Shell.Intel.KindAmmo"),
        V2IntelKind.Item => V2ShellText.Get("V2.Shell.Intel.KindItem"),
        _ => string.Empty,
    };
    public IReadOnlyList<V2ShellIntelFactViewModel> IntelFacts => BuildIntelFacts();
    public string IntelWikiLabel => V2ShellText.Get("V2.Shell.Intel.Wiki");
    public bool IntelHasWikiLink => WikiLinkPolicy.IsAllowed(_intelResult?.WikiUri);
    public ICommand OpenIntelWikiCommand { get; }
    public string ReadinessSummary => V2ShellText.Format(
        "V2.Shell.Readiness.Summary",
        CultureInfo.CurrentCulture,
        Readiness.ReadyCount,
        Readiness.RequiredCount,
        Readiness.NeedsActionCount,
        Readiness.UnconfirmedCount);
    public string HealthSummary => Readiness.NeedsActionCount switch
    {
        1 => V2ShellText.Get("V2.Shell.Health.NeedsActionOne"),
        > 1 => V2ShellText.Format("V2.Shell.Health.NeedsActionMany", CultureInfo.CurrentCulture, Readiness.NeedsActionCount),
        _ when Readiness.UnconfirmedCount > 0 => V2ShellText.Format(
            "V2.Shell.Health.Unconfirmed",
            CultureInfo.CurrentCulture,
            Readiness.UnconfirmedCount),
        _ => V2ShellText.Get("V2.Shell.Health.Clear"),
    };
    public string Title => IsDeveloperMode
        ? V2ShellText.Format("V2.Shell.WindowTitle", CultureInfo.CurrentCulture, CurrentHeading, ProvisionalLabel)
        : V2ShellText.Format("V2.Shell.WindowTitleClean", CultureInfo.CurrentCulture, CurrentHeading);
    public string DialogAutomationName => ActiveDialog switch
    {
        V2ShellDialogKind.Capture => CaptureHeading,
        V2ShellDialogKind.Commands => PaletteHeading,
        V2ShellDialogKind.Health => HealthHeading,
        _ => string.Empty,
    };
    public string CurrentHeading
    {
        get
        {
            var route = Router.Current.Location.Route;
            var root = Registry.RootOf(route);
            var destinationKey = Variant.DestinationLabelKey(root);
            if (route == root && destinationKey is not null)
            {
                return V2ShellText.Get(destinationKey);
            }

            if (route == V2Routes.Items && Variant.SearchPlacement == V2SearchPlacement.Header)
            {
                return V2ShellText.Get("V2.Shell.Search.Heading");
            }

            return V2ShellText.Get(Registry[route].HeadingKey);
        }
    }

    public string CurrentAddress { get => _address; set => SetProperty(ref _address, value); }
    public string PoliteAnnouncement { get => _politeAnnouncement; private set => SetProperty(ref _politeAnnouncement, value); }
    public string AssertiveAnnouncement { get => _assertiveAnnouncement; private set => SetProperty(ref _assertiveAnnouncement, value); }
    public string Announcement => string.IsNullOrEmpty(AssertiveAnnouncement) ? PoliteAnnouncement : AssertiveAnnouncement;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(HasSearchText));
                RaiseSuggestionsChanged();
            }
        }
    }
    public string PaletteQuery
    {
        get => _paletteQuery;
        set
        {
            if (SetProperty(ref _paletteQuery, value))
            {
                OnPropertyChanged(nameof(FilteredCommandItems));
                OnPropertyChanged(nameof(HasFilteredCommandItems));
                OnPropertyChanged(nameof(HasNoFilteredCommandItems));
            }
        }
    }

    public IReadOnlyList<V2ShellCommandViewModel> FilteredCommandItems => string.IsNullOrWhiteSpace(PaletteQuery)
        ? CommandItems
        : CommandItems.Where(command =>
            command.Label.Contains(PaletteQuery, StringComparison.CurrentCultureIgnoreCase) ||
            command.Gesture.Contains(PaletteQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
    public bool HasFilteredCommandItems => FilteredCommandItems.Count > 0;
    public bool HasNoFilteredCommandItems => !HasFilteredCommandItems;

    public V2ShellDialogKind ActiveDialog
    {
        get => _activeDialog;
        private set
        {
            if (SetProperty(ref _activeDialog, value))
            {
                OnPropertyChanged(nameof(HasOpenDialog));
                OnPropertyChanged(nameof(HasNoDialog));
                OnPropertyChanged(nameof(IsCaptureOpen));
                OnPropertyChanged(nameof(IsPaletteOpen));
                OnPropertyChanged(nameof(IsHealthOpen));
                OnPropertyChanged(nameof(DialogAutomationName));
            }
        }
    }

    public bool HasOpenDialog => ActiveDialog != V2ShellDialogKind.None;
    public bool HasNoDialog => !HasOpenDialog;
    public bool IsCaptureOpen => ActiveDialog == V2ShellDialogKind.Capture;
    public bool IsPaletteOpen => ActiveDialog == V2ShellDialogKind.Commands;
    public bool IsHealthOpen => ActiveDialog == V2ShellDialogKind.Health;
    public V2CaptureShellState CaptureState => Volatile.Read(ref _captureState);
    public ScanIntent SelectedCaptureIntent
    {
        get => _selectedCaptureIntent;
        private set
        {
            if (SetProperty(ref _selectedCaptureIntent, value))
            {
                foreach (var intent in CaptureIntents)
                {
                    intent.SetSelected(intent.Intent == value);
                }
            }
        }
    }
    public bool HasCaptureProgress => CaptureProgressItems.Count > 0;
    public bool HasNoCaptureProgress => !HasCaptureProgress;
    public bool HasCaptureAttention => CaptureState.Attention is not null;
    public bool HasCaptureReview => CaptureState.Review is not null;
    public bool HasCaptureReference => CaptureState.CorrelationId is not null;
    public bool HasPersistenceFailure => !string.IsNullOrEmpty(PersistenceFailure);
    public bool PersistenceRetryPending => _persistenceRetryPending;
    public bool CanRetryPersistence => HasPersistenceFailure &&
        !PersistenceRetryPending &&
        (_persistenceFailureKind != V2ShellPersistenceOperationKind.Reset || ResetPreviewCommand.CanExecute(null));
    public V2ShellSuggestionKind SuggestionFilter => _suggestionFilter;
    public IReadOnlyList<V2ShellSuggestionViewModel> FilteredSuggestionItems => SuggestionItems
        .Where(item => SuggestionFilter == V2ShellSuggestionKind.All || item.Kind == SuggestionFilter)
        .Where(item => string.IsNullOrWhiteSpace(SearchText) ||
            item.Label.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
            item.Provenance.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase) ||
            item.Category.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase))
        .ToArray();
    public bool ShowsSuggestions => Router.Current.Location.Route == V2Routes.Items;
    public bool HasSuggestions => FilteredSuggestionItems.Count > 0;
    public bool HasNoSuggestions => !HasSuggestions;
    public bool CaptureShortcutEnabled
    {
        get => _captureShortcutEnabled;
        private set
        {
            if (SetProperty(ref _captureShortcutEnabled, value))
            {
                OnPropertyChanged(nameof(CaptureShortcutStatus));
            }
        }
    }
    public V2WidthClass WidthClass { get => _widthClass; private set => SetProperty(ref _widthClass, value); }
    public string WidthClassLabel => WidthClass.ToString();
    public bool UsesCompactDensity => WidthClass is V2WidthClass.Narrow or V2WidthClass.Compact;
    // V2 rough package 15 (shell chrome + Raid workspace): the concept renders (docs/design/v2)
    // always show a left rail, and Clayton flagged the row-navigation "workflow hub" look as the
    // provisional #265 scaffold, not the intended design. #265 still owns which variant's
    // destination *labels* ship; this only fixes the layout at Standard+ width to the rail every
    // variant's data can render, rather than branching on the variant's declared style.
    public bool UsesRailNavigation => WidthClass >= V2WidthClass.Standard;
    public bool UsesRowNavigation => !UsesRailNavigation;

    /// <summary>
    /// How much of the left rail is showing: its labels, only its icons, or nothing at all.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] "The left sidebar should be collapsible ... more map is better."
    /// Three states rather than two, because the middle one is the useful one: 56 pixels of icons
    /// keeps every destination one press away and hands 112 pixels to the map, and hiding it
    /// entirely hands over 168. Nothing becomes unreachable when it is away — a launcher button
    /// floats over the workspace's top-left corner with the same destinations in it, the command
    /// palette still lists every route, and Ctrl+B brings the rail straight back.
    ///
    /// Remembered, because a choice like this is made once. It is chrome, so it is kept in the
    /// same preview state as the window's own placement rather than in a store of its own.
    /// </remarks>
    public V2NavigationRail NavigationRail
    {
        get => _navigationRail;
        private set
        {
            if (_navigationRail == value)
            {
                return;
            }

            _navigationRail = value;
            foreach (var destination in PrimaryDestinations)
            {
                destination.ShowsLabel = value == V2NavigationRail.Labels;
            }

            SetupDestination.ShowsLabel = value == V2NavigationRail.Labels;
            OnPropertyChanged(nameof(NavigationRail));
            OnPropertyChanged(nameof(ShowsNavigationRail));
            OnPropertyChanged(nameof(ShowsNavigationLauncher));
            OnPropertyChanged(nameof(NavigationRailWidth));
            OnPropertyChanged(nameof(NavigationRailStateLabel));
            OnPropertyChanged(nameof(NavigationRailToggleName));
        }
    }

    public bool ShowsNavigationRail => UsesRailNavigation && NavigationRail != V2NavigationRail.Hidden;

    /// <summary>The floating way back to the destinations while the rail is away.</summary>
    public bool ShowsNavigationLauncher => UsesRailNavigation && NavigationRail == V2NavigationRail.Hidden;

    public double NavigationRailWidth => NavigationRail == V2NavigationRail.Labels ? 168 : 60;

    public string NavigationRailStateLabel => V2ShellText.Get(NavigationRail switch
    {
        V2NavigationRail.Labels => "V2.Shell.Nav.RailLabels",
        V2NavigationRail.Icons => "V2.Shell.Nav.RailIcons",
        _ => "V2.Shell.Nav.RailHidden",
    });

    public string NavigationRailToggleName => V2ShellText.Get("V2.Shell.Command.NavigationRail");

    public string ShowNavigationRailLabel => V2ShellText.Get("V2.Shell.Nav.ShowRail");

    public string NavigationLauncherLabel => V2ShellText.Get("V2.Shell.Nav.GoTo");

    /// <summary>Labels to icons to away and round again, the way one control with three states works.</summary>
    public void CycleNavigationRail()
    {
        NavigationRail = NavigationRail switch
        {
            V2NavigationRail.Labels => V2NavigationRail.Icons,
            V2NavigationRail.Icons => V2NavigationRail.Hidden,
            _ => V2NavigationRail.Labels,
        };
        Announce(NavigationRailStateLabel, V2Announcement.Polite);
        QueueSave();
    }

    public void ShowNavigationRail()
    {
        NavigationRail = V2NavigationRail.Labels;
        Announce(NavigationRailStateLabel, V2Announcement.Polite);
        QueueSave();
    }
    /// <summary>
    /// Opens a past raid's trail on the Raid map: moves the map to the raid's own map, starts the
    /// replay V1's History page started, and goes to the Raid workspace where it is drawn.
    /// </summary>
    /// <remarks>
    /// The map is chosen first because a replay carries positions and no map of its own: V1 drew
    /// them on whichever map happened to be showing, so a Customs raid watched from Streets was a
    /// trail across the wrong plan. The replay bar lives on the Raid workspace, where it can be
    /// stepped and closed; while it is open the map does not follow live screenshots.
    /// </remarks>
    private async Task WatchRaidAsync(MainWindowViewModel legacy, DebriefReplayRequest request)
    {
        try
        {
            if (request.MapId is { Length: > 0 } mapId)
            {
                await legacy.Map.FollowRaidAsync(mapId).ConfigureAwait(true);
            }

            legacy.Raid.Replay.Open(request.Title, request.Positions, request.PlannedRoute, request.Comparison);
            GoTo(V2Routes.Raid, V2ShellFocusTargets.Destination(V2Routes.Raid));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _debrief?.ReportReplayFailure(exception.Message);
        }
    }

    public bool ShowsHeaderSetup => Variant.SetupPlacement == V2SetupPlacement.HeaderLink;
    public bool ShowsSeparatedSetup => Variant.SetupPlacement == V2SetupPlacement.LabelledRailSection;
    public bool ShowsHeaderSearch => Variant.SearchPlacement == V2SearchPlacement.Header;
    public bool ShowsWorkspaceSearch => Variant.SearchPlacement == V2SearchPlacement.InsideItemsWorkspace &&
        Router.CurrentDestination == V2Routes.Items;
    public bool ShowsSectionNavigation => SectionItems.Count > 1;
    /// <summary>
    /// Whether the current route hosts a self-contained V2 workspace (stash scan, debrief).
    /// </summary>
    /// <remarks>
    /// A workspace owns its own loading/empty/error presentation the way LootScanView does, so
    /// unlike <see cref="ShowsStatePresenter"/> it does not also key off <see cref="Surface"/> —
    /// that resolver has no fact source for these routes yet (see
    /// <c>V2SurfaceStateResolver.Resolve</c>), so it would otherwise hide a working workspace
    /// behind a permanently stale "empty" badge.
    /// </remarks>
    public bool ShowsWorkspace => Registry[Router.Current.Location.Route].Content == V2RouteContent.Workspace;
    public object? WorkspaceContent => Router.Current.Location.Route switch
    {
        var route when route == V2Routes.Stash => _stashScan,
        var route when route == V2Routes.Debrief => _debrief,
        var route when route == V2Routes.Plan => _plan,
        var route when route == V2Routes.Hideout => _hideout,
        // V2 rough package 28 (parity): the reference workspaces over the V1 pages' view models.
        var route when route == V2Routes.Ammo => AmmoWorkspace,
        var route when route == V2Routes.Keys => KeysWorkspace,
        var route when route == V2Routes.Flea => FleaWorkspace,
        // #287: Crafts & barters, built from the trade catalog service directly.
        var route when route == V2Routes.Crafts => CraftsBartersWorkspace,
        var route when route == V2Routes.Keep => _keep,
        var route when route == V2Routes.Loadout => Legacy?.Loadout,
        var route when route == V2Routes.Events => Legacy?.Events,
        // v2r-team (package 9, wave 2): Group and Tablet are separate addresses/section tabs but
        // render the same Team workspace rather than their own content.
        var route when route == V2Routes.Team || route == V2Routes.Group || route == V2Routes.Tablet => _team,
        _ => null,
    };
    // V2 Raid cockpit (package 2): a full-page workspace like the legacy page it replaced on
    // this route, so it takes the same row span.
    public bool ShowsRaidCockpit => Registry[Router.Current.Location.Route].Content == V2RouteContent.RaidCockpit;
    public bool ShowsLootScan => Registry[Router.Current.Location.Route].Content == V2RouteContent.LootScan;
    public bool ShowsLootScanEmpty => ShowsLootScan && LootScanResult is null;
    public string LootScanEmptyLabel => V2ShellText.Get("V2.Shell.LootScan.Empty");
    public string LootScanEmptyTitle => V2ShellText.Get("V2.Shell.LootScan.EmptyTitle");
    public string LootScanActionLabel => V2ShellText.Get("V2.Shell.LootScan.Scan");

    /// <summary>Opens the capture dialog with Loot decision selected; the player still presses Arm.</summary>
    public ICommand ScanLootCommand { get; }
    public bool ShowsSetupWorkspace => Registry[Router.Current.Location.Route].Content == V2RouteContent.SetupWorkspace;
    public ReleaseExperienceViewModel? ReleaseExperience { get; }
    public int ShellBodyRowSpan =>
        ShowsWorkspace || ShowsRaidCockpit || ShowsLootScan || ShowsSetupWorkspace || ShowsIntelWorkspace ? 1 : 2;
    public bool ShowsReadiness => Registry[Router.Current.Location.Route].ShowsReadiness;
    /// <summary>The plain checklist; Setup draws the same checks as its overview's steps instead.</summary>
    public bool ShowsReadinessChecklist => ShowsReadiness && !ShowsSetupWorkspace;
    public bool ShowsContinue => Registry[Router.Current.Location.Route].ShowsContinue;
    public bool ShowsStatePresenter =>
        Registry[Router.Current.Location.Route].Content == V2RouteContent.StatePresenter ||
        (Surface.Kind != V2SurfaceStateKind.Ready && !ShowsWorkspace);

    // V2 rough package 20: a global problem has exactly one presentation, the single-line banner
    // under the top bar. The page body used to draw the same block a second time, which on Raid
    // pushed the map card down by about 400px on a 1080-high window. The banner can be dismissed
    // for the current problem; the status pill keeps the detail and the recovery actions.
    // Decided from the identity alone. There used to be a flag as well, set a statement before the
    // identity was; a surface rebuild from the runtime's own thread landing between the two saw
    // "no identity recorded", cleared the flag, and the banner the player had just dismissed stayed
    // up. It failed A_global_problem_is_one_dismissible_line... about one run in thirty.
    public bool ShowsSurfaceBanner =>
        ShowsStatePresenter && !string.Equals(_dismissedSurfaceIdentity, Surface.StableIdentity, StringComparison.Ordinal);

    /// <summary>The banner's one line: the specific detail when there is one, else the state word.</summary>
    public string SurfaceBannerText => string.IsNullOrWhiteSpace(Surface.Detail) ? SurfaceTitle : Surface.Detail;

    public string SurfaceDismissLabel => V2ShellText.Get("V2.Shell.Action.DismissBanner");

    /// <summary>Whether the status pill's dialog shows the current problem rather than only the checks.</summary>
    public bool ShowsHealthSurface => !SurfaceIsReady;

    public ICommand DismissSurfaceBannerCommand { get; }
    public bool ShowsIntel => Router.Current.Location.Route == V2Routes.Item || Router.Current.Location.IntelItem is not null;
    public bool ShowsIntelBeside => ShowsIntelCard && Variant.IntelPlacement == V2IntelPlacement.BesideCurrentPage &&
        V2ShellAdaptation.IntelFitsBeside(WidthClass);
    public bool ShowsIntelInsteadOfPage => ShowsIntelCard && Variant.IntelPlacement == V2IntelPlacement.BesideCurrentPage &&
        !V2ShellAdaptation.IntelFitsBeside(WidthClass);
    public bool ShowsPrimaryContent => !ShowsIntelCard || ShowsIntelBeside;
    public int IntelColumn => ShowsIntelBeside ? 1 : 0;
    public int IntelColumnSpan => ShowsIntelBeside ? 1 : 2;
    public string IntelItem => Router.Current.Location.Item ?? Router.Current.Location.IntelItem ?? string.Empty;
    public V2ReadinessSummary Readiness { get; private set; } = new([]);
    public V2SurfaceState Surface { get; private set; } =
        V2SurfaceStateResolver.State(V2SurfaceStateKind.Loading, string.Empty, "V2.Shell.Remainder.Local");
    public string SurfaceTitle => V2ShellText.Get(Surface.Policy.WordingKey);
    public string SurfaceRemainder => V2ShellText.Get(Surface.RemainderKey);
    public string SurfaceGlyph => V2ShellText.Get($"V2.Shell.StateGlyph.{Surface.Kind}");
    public string SurfaceAutomationName => string.Join(
        ". ",
        new[] { SurfaceTitle, Surface.Detail, SurfaceRemainder }.Where(part => !string.IsNullOrWhiteSpace(part)));
    public bool SurfaceIsReady => Surface.Kind == V2SurfaceStateKind.Ready;
    public bool SurfaceIsLoading => Surface.Kind == V2SurfaceStateKind.Loading;
    public bool SurfaceIsUnknown => Surface.Kind == V2SurfaceStateKind.Empty;
    public bool SurfaceIsOffline => Surface.Kind == V2SurfaceStateKind.Offline;
    public bool SurfaceIsStale => Surface.Kind == V2SurfaceStateKind.Stale;
    public bool SurfaceIsPartial => Surface.Kind == V2SurfaceStateKind.Partial;
    public bool SurfaceIsDenied => Surface.Kind == V2SurfaceStateKind.Denied;
    public bool SurfaceIsFailed => Surface.Kind == V2SurfaceStateKind.Failed;
    public bool SurfaceIsDangerTone => SurfaceIsFailed || SurfaceIsOffline || SurfaceIsDenied;
    public bool SurfaceIsWarningTone => !SurfaceIsDangerTone;
    public bool SurfacePatternDashed => Surface.Policy.Border == "dashed";
    public bool SurfacePatternDotted => Surface.Policy.Border == "dotted";
    public bool SurfacePatternDouble => Surface.Policy.Border == "double";
    public IReadOnlyList<string> Recents { get; private set; } = [];
    public IReadOnlyList<string> Pins { get; private set; } = [];
    public bool HasRecents => RecentItems.Count > 0;
    public bool HasContinueItems => ContinueItems.Count > 0;
    public bool HasNoContinueItems => !HasContinueItems;
    public bool HasPins => PinItems.Count > 0;
    public V2ShellWindowPlacement? RestoredWindow => _window;
    public string FocusFallbackTarget => DefaultPageFocusTarget();
    public bool ShowsReadinessTarget => _activeReadinessTarget is not null;
    public string ReadinessTargetAutomationId => _activeReadinessTarget is { } check
        ? V2ShellFocusTargets.ReadinessTarget(check.Id)
        : V2ShellRouter.PageHeadingTarget;
    public string ReadinessTargetHeading => _activeReadinessTarget?.Label ?? string.Empty;
    public string ReadinessTargetDetail => _activeReadinessTarget is { } check
        ? V2ShellText.Get($"V2.Shell.Readiness.Target.{check.Id}")
        : string.Empty;
    public string ReadinessTargetAutomationName => _activeReadinessTarget is { } check
        ? string.Join(". ", check.Label, V2ShellText.Get($"V2.Shell.Status.{check.Status}"), ReadinessTargetDetail)
        : string.Empty;

    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand HealthCommand { get; }
    public ICommand PaletteCommand { get; }

    /// <summary>Labels, icons, away — one control with three states (package 46).</summary>
    public ICommand CycleNavigationRailCommand { get; }

    /// <summary>Brings the rail back from the launcher that floats over the workspace.</summary>
    public ICommand ShowNavigationRailCommand { get; }
    public ICommand AddressCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand PinCommand { get; }
    public ICommand CopyAddressCommand { get; }
    public ICommand CloseTransientCommand { get; }
    public AsyncDelegateCommand ResetPreviewCommand { get; }
    public ICommand RetryPersistenceCommand { get; }
    public ICommand PaletteAddressCommand { get; }
    public ICommand ArmCaptureCommand { get; }

    public void GoTo(V2RouteId route) => GoTo(route, V2ShellFocusTargets.Destination(route));
    public void Back() => Act(Router.Back());
    public void Forward() => Act(Router.Forward());

    private void GoTo(V2RouteId route, string invoker) => Act(Router.Navigate(route, invoker));

    public void CloseTransient()
    {
        if (HasOpenDialog)
        {
            CloseDialog(restoreInvoker: true);
            return;
        }

        if (Router.Current.Location.IntelItem is not null || Router.Current.Location.Route == V2Routes.Item)
        {
            Act(Router.CloseIntel());
        }
    }

    public void OpenAddress()
    {
        Act(Router.NavigateToAddress(CurrentAddress, V2ShellFocusTargets.Address));
    }

    private void OpenPaletteAddress()
    {
        var address = PaletteQuery.Trim();
        var invoker = _dialogInvoker ?? V2ShellFocusTargets.Palette;
        var result = Router.NavigateToAddress(address, invoker);
        if (!result.Succeeded)
        {
            Act(result);
            FocusRequested?.Invoke(this, new(V2ShellFocusTargets.PaletteQuery, V2FocusReason.Invoker));
            return;
        }

        CloseDialog(restoreInvoker: false);
        Act(result);
    }

    public async Task SearchAsync()
    {
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            FocusSearch();
            return;
        }

        if (Router.Current.Location.Route != V2Routes.Items)
        {
            var moved = Router.Navigate(V2Routes.Items, SearchFocusTarget);
            if (!moved.Succeeded)
            {
                Announce(moved.Failure ?? string.Empty, V2Announcement.Assertive);
                return;
            }

            Act(moved);
        }

        if (Legacy is null)
        {
            Announce(V2ShellText.Get("V2.Shell.Announce.SearchUnavailable"), V2Announcement.Assertive);
            return;
        }

        Legacy.Items.SearchQuery = query;
        await Legacy.Items.SearchCommand.ExecuteAsync().ConfigureAwait(true);
        Announce(Legacy.Items.SearchStatus, V2Announcement.Polite);
        RebuildSuggestions();
        SelectFirstIntelResult();
        FocusRequested?.Invoke(this, new(SearchFocusTarget, V2FocusReason.Invoker));
    }

    /// <summary>
    /// V2 rough package 30 (acceptance sweep): a finished search shows its best match, the way
    /// docs/design/v2/v2-intel-workspace-concept.png does.
    /// </summary>
    /// <remarks>
    /// The router is moved directly rather than through <c>Act</c>, so the item's own focus
    /// request is not raised: the player is typing, and the caller returns focus to the search
    /// box on the line after this one. It only ever acts when nothing is selected, so a search
    /// run from an item's page leaves that item showing.
    /// </remarks>
    private void SelectFirstIntelResult()
    {
        if (!ShowsIntelWorkspace || HasIntelSelection || IntelResults.Count == 0)
        {
            return;
        }

        _ = Router.OpenIntel(IntelResults[0].ItemId, invoker: null);
    }

    public void FocusSearch()
    {
        if (Variant.SearchPlacement == V2SearchPlacement.InsideItemsWorkspace &&
            Router.CurrentDestination != V2Routes.Items)
        {
            var moved = Router.Navigate(V2Routes.Items, SearchFocusTarget);
            if (!moved.Succeeded)
            {
                Announce(moved.Failure ?? string.Empty, V2Announcement.Assertive);
                return;
            }
        }

        FocusRequested?.Invoke(this, new(SearchFocusTarget, V2FocusReason.Invoker));
    }

    public void TogglePin()
    {
        var address = Router.CurrentAddress;
        Pins = Pins.Contains(address, StringComparer.Ordinal)
            ? Pins.Where(pin => pin != address).ToArray()
            : [address, .. Pins.Where(pin => pin != address).Take(V2ShellPreviewState.MaxPins - 1)];
        Announce(V2ShellText.Get(
            Pins.Contains(address, StringComparer.Ordinal) ? "V2.Shell.Announce.Pinned" : "V2.Shell.Announce.Unpinned"),
            V2Announcement.Polite);
        RebuildSavedAddresses();
        QueueSave();
        OnPropertyChanged(nameof(Pins));
    }

    public void ToggleCaptureShortcut()
    {
        CaptureShortcutEnabled = !CaptureShortcutEnabled;
        Announce(
            V2ShellText.Get(CaptureShortcutEnabled ? "V2.Shell.Announce.ShortcutOn" : "V2.Shell.Announce.ShortcutOff"),
            V2Announcement.Polite);
        QueueSave();
    }

    /// <summary>Runs a documented, window-local command. No chord is forwarded to EFT.</summary>
    public bool HandleKey(V2KeyChord chord, string? focusedAutomationId = null)
    {
        var command = V2ShellCommands.Match(Commands, chord, CaptureShortcutEnabled);
        if (command is null)
        {
            return false;
        }

        if (command.Kind == V2ShellCommandKind.CloseTransient && !HasOpenDialog && !ShowsIntel)
        {
            // Nothing in chrome owns Escape. Leave it unhandled for the focused V2 control;
            // MainWindow still returns at the preview boundary, so V1 never sees it.
            return false;
        }

        if (HasOpenDialog && !CanExecuteFromOpenDialog(command))
        {
            // Capture and Health are modal decisions, not translucent page chrome. Consuming a
            // background shortcut here prevents it changing the disabled page underneath them.
            Announce(V2ShellText.Get("V2.Shell.Announce.CloseDialogFirst"), V2Announcement.Assertive);
            FocusRequested?.Invoke(this, new(ActiveDialogFocusTarget(), V2FocusReason.Restored));
            return true;
        }

        ExecuteCommand(command, focusedAutomationId);
        return true;
    }

    private bool CanExecuteFromOpenDialog(V2ShellCommand command) => IsPaletteOpen || command.Kind is
        V2ShellCommandKind.CloseTransient or
        V2ShellCommandKind.ToggleCapture or
        V2ShellCommandKind.ToggleHealth or
        V2ShellCommandKind.TogglePalette or
        V2ShellCommandKind.NextRegion or
        V2ShellCommandKind.PreviousRegion;

    public void UpdateEffectiveWidth(double effectiveWidth, string? focusedAutomationId = null)
    {
        var width = V2ShellAdaptation.Classify(effectiveWidth);
        if (WidthClass == width)
        {
            return;
        }

        var usedRail = UsesRailNavigation;
        WidthClass = width;
        OnPropertyChanged(nameof(WidthClassLabel));
        OnPropertyChanged(nameof(UsesCompactDensity));
        OnPropertyChanged(nameof(UsesRailNavigation));
        OnPropertyChanged(nameof(ShowsNavigationRail));
        OnPropertyChanged(nameof(ShowsNavigationLauncher));
        OnPropertyChanged(nameof(UsesRowNavigation));
        OnPropertyChanged(nameof(ShowsIntelBeside));
        OnPropertyChanged(nameof(ShowsIntelInsteadOfPage));
        OnPropertyChanged(nameof(ShowsPrimaryContent));
        OnPropertyChanged(nameof(IntelColumn));
        OnPropertyChanged(nameof(IntelColumnSpan));
        if (usedRail != UsesRailNavigation &&
            focusedAutomationId?.StartsWith("v2-shell-destination-", StringComparison.Ordinal) == true)
        {
            // The rail and row intentionally expose the same stable controls. Once the binding
            // hides one copy, restore focus to the visible copy instead of dropping it to the
            // window when a docked resize crosses the breakpoint.
            FocusRequested?.Invoke(this, new(focusedAutomationId, V2FocusReason.Restored));
        }
    }

    /// <summary>
    /// Accepts continuity from capture, planning, or a paired-device adapter without letting a
    /// later runtime refresh replace it with hard-coded desktop/null placeholders.
    /// </summary>
    public void UpdateContinuity(
        string? planId,
        string? objectiveId,
        string? priorScan,
        string? captureCorrelationId,
        string initiatingDevice)
    {
        var continuity = new V2NavigationContinuity(
            RequireContextValue(planId, nameof(planId), optional: true),
            RequireContextValue(objectiveId, nameof(objectiveId), optional: true),
            RequireContextValue(priorScan, nameof(priorScan), optional: true),
            RequireContextValue(captureCorrelationId, nameof(captureCorrelationId), optional: true),
            RequireContextValue(initiatingDevice, nameof(initiatingDevice), optional: false)!);
        Volatile.Write(ref _continuity, continuity);
        _apply.Request();
    }

    /// <summary>
    /// Projects #271's typed state into shared chrome. It never starts capture work or reads EFT.
    /// </summary>
    public void UpdateCaptureState(V2CaptureShellState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Volatile.Write(ref _captureState, state);
        var continuity = Volatile.Read(ref _continuity);
        Volatile.Write(ref _continuity, continuity with
        {
            CaptureCorrelationId = state.CorrelationId,
            InitiatingDevice = state.SettingDevice,
        });
        _apply.Request();
    }

    /// <summary>
    /// Projects a frozen Loot Scan result into the Loot route and returns focus to it, from
    /// whatever thread the capture session's own handoff runs on. #271/#282 own its source.
    /// </summary>
    /// <remarks>
    /// Setting the result and navigating must land on the UI thread together, the same rule
    /// <see cref="CoalescingDispatch"/> follows for background-driven updates: run inline when
    /// there is no dispatcher or the caller is already on it (tests, headless), otherwise post.
    /// </remarks>
    /// <summary>Opens the Intel page for the item a capture was read as (#287).</summary>
    /// <remarks>
    /// The same dispatcher dance as <see cref="ShowLootScanResult"/> and for the same reason: a
    /// handoff completes on whatever thread finished the analysis, and navigation touches
    /// observable collections the interface is bound to.
    /// </remarks>
    public void ShowScannedItem(string itemId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        void Apply()
        {
            if (HasOpenDialog)
            {
                CloseDialog(restoreInvoker: false);
            }

            Act(Router.OpenIntel(itemId, "v2-shell-capture-identified"));
        }

        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            Apply();
        }
        else
        {
            _dispatcherContext.Post(_ => Apply(), null);
        }
    }

    public void ShowLootScanResult(LootScanViewModel result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ApplyLootScanResult(result, result.Result);
    }

    /// <summary>Shows the correlated live model without starting the result auto-return timer.</summary>
    public void ShowLootScanProgress(LootScanViewModel result)
    {
        ArgumentNullException.ThrowIfNull(result);
        void Apply()
        {
            if (Volatile.Read(ref _lootScanResult) is { } previous)
            {
                previous.ScanAgainRequested -= LootScanAgainRequested;
            }

            result.ScanAgainRequested += LootScanAgainRequested;
            Volatile.Write(ref _lootScanResult, result);
            OnPropertyChanged(nameof(LootScanResult));
            OnPropertyChanged(nameof(ShowsLootScanEmpty));
            ShowLootScanStarting();
        }

        DispatchLootUpdate(Apply);
    }

    /// <summary>Mutates only the live scan named by the event, on the interface thread.</summary>
    public void UpdateLootScanProgress(CaptureCorrelationId correlationId, Action<LootScanViewModel> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        DispatchLootUpdate(() =>
        {
            if (Volatile.Read(ref _lootScanResult) is { } current && current.CorrelationId == correlationId)
            {
                update(current);
            }
        });
    }

    /// <summary>
    /// Reconciles a live model before publishing the final result. The callback runs only when
    /// this result was not superseded, after row reuse and navigation have completed.
    /// </summary>
    public void ApplyLootScanResult(
        LootScanViewModel result,
        LootScanResult final,
        Action<LootScanViewModel>? applied = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(final);
        DispatchLootUpdate(() =>
        {
            if (Volatile.Read(ref _lootScanResult) is { IsProgressive: true } active &&
                active.CorrelationId != final.CorrelationId)
            {
                return;
            }

            if (result.IsProgressive)
            {
                result.Reconcile(final);
            }

            if (Volatile.Read(ref _lootScanResult) is { } previous && !ReferenceEquals(previous, result))
            {
                previous.ScanAgainRequested -= LootScanAgainRequested;
            }

            result.ScanAgainRequested -= LootScanAgainRequested;
            result.ScanAgainRequested += LootScanAgainRequested;
            Volatile.Write(ref _lootScanResult, result);
            OnPropertyChanged(nameof(LootScanResult));
            OnPropertyChanged(nameof(ShowsLootScanEmpty));
            // #572: this is the one path a completed capture result reaches the Loot page by
            // itself; it starts (or restarts) the result's auto-return wait.
            EnterLootAutomatically(result);
            applied?.Invoke(result);
        });
    }

    private void DispatchLootUpdate(Action apply)
    {
        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            apply();
            return;
        }

        _dispatcherContext.Post(_ => apply(), null);
    }

    private void LootScanAgainRequested(object? sender, EventArgs e) => ScanLootCommand.Execute(null);

    /// <summary>Accepts plan-owned suggestions without making the shell own plan persistence.</summary>
    public void UpdatePlannedSuggestions(IReadOnlyList<V2PlannedItemSuggestion> suggestions)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        if (suggestions.Any(suggestion => suggestion is null))
        {
            throw new ArgumentException("A planned suggestion list cannot contain null entries.", nameof(suggestions));
        }

        Volatile.Write(ref _plannedSuggestions, suggestions
            .DistinctBy(suggestion => suggestion.ItemId, StringComparer.Ordinal)
            .Take(20)
            .ToArray());
        _apply.Request();
    }

    private void SelectCaptureIntent(ScanIntent intent)
    {
        SelectedCaptureIntent = intent;
        OnPropertyChanged(nameof(CaptureArmLabel));
    }

    /// <summary>
    /// Opens the shared capture dialog with the workspace's requested intent pre-selected. It
    /// still asks the player to press Arm — this never starts a capture session on its own.
    /// </summary>
    private void StashScanRequested(object? sender, ScanIntent intent)
    {
        SelectCaptureIntent(intent);
        ToggleDialog(V2ShellDialogKind.Capture, V2ShellFocusTargets.Capture, V2ShellFocusTargets.CaptureDialog);
    }

    /// <summary>
    /// The Plan workspace's "Show on map" already moved the shared map; this only sends the
    /// player to the route that renders it.
    /// </summary>
    private void PlanShowOnMapRequested(object? sender, EventArgs e) => GoTo(V2Routes.Raid);

    private void PlanOpenHideoutRequested(object? sender, EventArgs e) => GoTo(V2Routes.Hideout);

    private void ArmSelectedCaptureIntent() => RequestCaptureArm(
        SelectedCaptureIntent,
        V2NavigationContext.ThisDesktop,
        requestedSessionId: null,
        requestedContext: null);

    /// <summary>
    /// Arms a capability-checked paired request through the same event, state projection and
    /// announcements as the capture panel's Arm button.
    /// </summary>
    public void ArmCaptureFromPairedDevice(
        ScanIntent intent,
        string requestingDevice,
        CaptureSessionId requestedSessionId,
        CaptureContextMetadata requestedContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestingDevice);
        ArgumentNullException.ThrowIfNull(requestedContext);

        void Apply()
        {
            SelectCaptureIntent(intent);
            RequestCaptureArm(intent, requestingDevice, requestedSessionId, requestedContext);
        }

        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            Apply();
        }
        else
        {
            _dispatcherContext.Post(_ => Apply(), null);
        }
    }

    private void RequestCaptureArm(
        ScanIntent intent,
        string requestingDevice,
        CaptureSessionId? requestedSessionId,
        CaptureContextMetadata? requestedContext)
    {
        var requested = CaptureArmRequested;
        if (requested is null)
        {
            Announce(V2ShellText.Get("V2.Shell.Announce.CaptureUnavailable"), V2Announcement.Assertive);
            return;
        }

        try
        {
            requested(this, new(
                intent,
                CaptureState.IntentRevision,
                requestingDevice,
                requestedSessionId,
                requestedContext));
        }
        catch (OperationCanceledException)
        {
            Announce(V2ShellText.Get("V2.Shell.Announce.CaptureRequestCancelled"), V2Announcement.Polite);
            return;
        }
        catch (Exception)
        {
            Announce(V2ShellText.Get("V2.Shell.Announce.CaptureRequestFailed"), V2Announcement.Assertive);
            return;
        }

        Announce(
            V2ShellText.Format(
                "V2.Shell.Announce.CaptureRequested",
                CultureInfo.CurrentCulture,
                IntentLabel(intent)),
            V2Announcement.Polite);
        if (IsCaptureOpen)
        {
            CloseDialog(restoreInvoker: true);
        }
    }

    private void RequestCaptureResolution(V2CaptureResolutionKind resolution)
    {
        var requested = CaptureResolutionRequested;
        if (requested is null)
        {
            Announce(V2ShellText.Get("V2.Shell.Announce.CaptureUnavailable"), V2Announcement.Assertive);
            return;
        }

        var attention = CaptureState.Attention;
        var review = CaptureState.Review;
        var intent = resolution switch
        {
            V2CaptureResolutionKind.AnalyzeAsArmed => attention?.BoundIntent,
            V2CaptureResolutionKind.AnalyzeAsDetected => attention?.DetectedContext is { } detected
                ? IntentForContext(detected)
                : null,
            V2CaptureResolutionKind.AnalyzeAsSelected or
                V2CaptureResolutionKind.ArmSelectedIntent or
                V2CaptureResolutionKind.Correct => SelectedCaptureIntent,
            _ => null,
        };
        try
        {
            requested(this, new(
                attention?.SessionId ?? review?.SessionId,
                attention?.ArtifactId ?? review?.ArtifactId,
                attention?.CaptureOrdinal ?? review?.CaptureOrdinal,
                resolution,
                intent,
                CaptureState.IntentRevision,
                V2NavigationContext.ThisDesktop));
        }
        catch (OperationCanceledException)
        {
            Announce(V2ShellText.Get("V2.Shell.Announce.CaptureRequestCancelled"), V2Announcement.Polite);
            return;
        }
        catch (Exception)
        {
            Announce(V2ShellText.Get("V2.Shell.Announce.CaptureRequestFailed"), V2Announcement.Assertive);
            return;
        }

        Announce(V2ShellText.Get("V2.Shell.Announce.CaptureActionRequested"), V2Announcement.Polite);
    }

    public void RecordFocusedTarget(string? automationId)
    {
        if (_disposed || string.IsNullOrEmpty(automationId) ||
            string.Equals(Router.Current.FocusTarget, automationId, StringComparison.Ordinal))
        {
            return;
        }

        Router.RecordFocus(automationId);
        QueueSave();
    }

    public void RequestInitialFocus()
    {
        if (_disposed)
        {
            return;
        }

        var target = PersistentFocusTarget() ?? DefaultPageFocusTarget();
        FocusRequested?.Invoke(this, new(target, V2FocusReason.Restored));
    }

    public V2ShellWindowPlacement? RestoreWindow(IReadOnlyList<ScreenBounds> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (_window is not { } window)
        {
            return null;
        }

        var clamped = window.ClampTo(screens);
        if (clamped != window)
        {
            _window = clamped;
            OnPropertyChanged(nameof(RestoredWindow));
            QueueSave();
        }

        return clamped;
    }

    public void RecordWindow(double width, double height, double left, double top, bool maximized)
    {
        if (_disposed || !double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(left) || !double.IsFinite(top))
        {
            return;
        }

        if (maximized && _window is { } normal)
        {
            _window = normal with { IsMaximized = true };
        }
        else
        {
            _window = new(
                Math.Clamp(width, V2ShellWindowPlacement.MinimumWidth, V2ShellWindowPlacement.MaximumDimension),
                Math.Clamp(height, V2ShellWindowPlacement.MinimumHeight, V2ShellWindowPlacement.MaximumDimension),
                Math.Clamp(left, -V2ShellWindowPlacement.MaximumCoordinateMagnitude, V2ShellWindowPlacement.MaximumCoordinateMagnitude),
                Math.Clamp(top, -V2ShellWindowPlacement.MaximumCoordinateMagnitude, V2ShellWindowPlacement.MaximumCoordinateMagnitude),
                maximized);
        }

        OnPropertyChanged(nameof(RestoredWindow));
        QueueSave();
    }

    private string SearchFocusTarget => Variant.SearchPlacement == V2SearchPlacement.Header
        ? V2ShellFocusTargets.HeaderSearch
        : V2ShellFocusTargets.WorkspaceSearch;

    private void Restore(string? requestedAddress)
    {
        var loaded = _preview.Load();
        CaptureShortcutEnabled = loaded.State.CaptureShortcutEnabled;
        NavigationRail = V2NavigationRailTokens.Parse(loaded.State.NavigationRail);
        Pins = loaded.State.Pins;
        Recents = loaded.State.Recents;
        _window = loaded.State.Window;
        RebuildSavedAddresses();
        if (loaded.Outcome == V2PreviewLoadOutcome.ResetAfterCorruption)
        {
            Announce(V2ShellText.Get("V2.Shell.Announce.PreviewCorrupt"), V2Announcement.Polite);
        }

        var address = requestedAddress ?? loaded.State.Address;
        if (address is null)
        {
            CurrentAddress = Router.CurrentAddress;
            return;
        }

        var parsed = Router.Addresses.Parse(address);
        if (parsed.Location is not { } location)
        {
            if (requestedAddress is not null &&
                V2LegacyPageAddressAliases.TryResolve(Registry, Variant, requestedAddress) is { } aliasedAddress)
            {
                parsed = Router.Addresses.Parse(aliasedAddress);
            }

            if (parsed.Location is not { } aliasedLocation)
            {
                if (requestedAddress is not null)
                {
                    throw new ArgumentException(parsed.Failure);
                }

                CurrentAddress = Router.CurrentAddress;
                return;
            }

            var aliasRestored = Router.Restore(aliasedLocation, null, null);
            if (!aliasRestored.Succeeded)
            {
                throw new ArgumentException(aliasRestored.Failure);
            }

            CurrentAddress = Router.CurrentAddress;
            return;
        }

        var restored = Router.Restore(
            location,
            requestedAddress is null ? loaded.State.SelectedEntity : null,
            requestedAddress is null ? loaded.State.FocusTarget : null);
        if (!restored.Succeeded && requestedAddress is not null)
        {
            throw new ArgumentException(restored.Failure);
        }

        CurrentAddress = Router.CurrentAddress;
    }

    private void RouterNavigated(object? sender, V2NavigationChange change)
    {
        var resetting = Volatile.Read(ref _resetInProgress) != 0;
        _activeReadinessTarget = null;
        CurrentAddress = Router.CurrentAddress;
        // Every navigation funnels through here, which is what makes this the one place worth
        // recording. The crash on 2026-09-19 happened on navigation and left the log silent;
        // a breadcrumb naming the destination is the difference between "it died" and "it died
        // going to Plan".
        CrashBreadcrumbs.Drop("navigate", CurrentAddress);
        UiActivity.Navigated(CurrentAddress);
        if (!resetting)
        {
            Recents = Recents
                .Where(address => !string.Equals(address, CurrentAddress, StringComparison.Ordinal))
                .Prepend(CurrentAddress)
                .Take(V2ShellPreviewState.MaxRecents)
                .ToArray();
        }

        RebuildSavedAddresses();
        RebuildSectionItems();
        _playerActionStateFocus = null;
        LoadCurrentWorkspace();
        Refresh(
            announceBackgroundChange: false,
            playerAction: change.Kind != V2NavigationKind.Restore);
        if (!resetting)
        {
            QueueSave();
        }
    }

    /// <summary>
    /// V2 rough package 17: a launch that restores straight onto Plan or Hideout loads that
    /// workspace before startup has migrated and filled the database, and it then showed
    /// "unavailable" until the player pressed Refresh. Reloading once whenever the game data's
    /// timestamp moves covers that first arrival and every later sync, without reloading on the
    /// many runtime changes (raid clock, observation) that leave the data untouched.
    /// </summary>
    private void ReloadPlanWhenGameDataChanges(DateTimeOffset? dataUpdatedUtc)
    {
        if (dataUpdatedUtc == _planDataUpdatedUtc)
        {
            return;
        }

        _planDataUpdatedUtc = dataUpdatedUtc;
        var route = Router.Current.Location.Route;
        // Setup joins Plan and Hideout here (package 17, home): its overview shows the same plan,
        // and variant A lands on it, so it is the page most likely to be open when data first lands.
        if (route == V2Routes.Plan || route == V2Routes.Hideout || route == V2Routes.Keep || route == V2Routes.Setup)
        {
            LoadCurrentWorkspace();
        }
    }

    /// <summary>Loads the workspace for whichever route is now current, if it needs one.</summary>
    private void LoadCurrentWorkspace()
    {
        var route = Router.Current.Location.Route;
        if (route == V2Routes.Stash && _stashScan is not null)
        {
            Load("stash", _stashScan.LoadAsync);
        }
        else if (route == V2Routes.Debrief && _debrief is not null)
        {
            Load("debrief", _debrief.LoadAsync);
        }
        else if (route == V2Routes.Plan && _plan is not null)
        {
            Load("plan", _plan.LoadAsync);
        }
        else if (route == V2Routes.Hideout && _hideout is not null)
        {
            Load("hideout", _hideout.LoadAsync);
        }
        else if (route == V2Routes.Keep && _keep is not null)
        {
            Load("keep", _keep.LoadAsync);
        }
        else if ((route == V2Routes.Team || route == V2Routes.Group || route == V2Routes.Tablet) && _team is not null)
        {
            Load("team", _team.LoadAsync);
        }
        // #283: a case scan finished on the Stash page changes what these say the player owns.
        else if (route == V2Routes.Ammo && AmmoWorkspace is not null)
        {
            Load("ammo-owned", AmmoWorkspace.LoadOwnedAsync);
        }
        else if (route == V2Routes.Keys && KeysWorkspace is not null)
        {
            Load("keys-owned", KeysWorkspace.LoadOwnedAsync);
        }
        else if (route == V2Routes.Setup)
        {
            _homeOverviewLoaded = false;
            LoadHomeOverview(_runtime.Current);
        }
    }

    /// <summary>
    /// Starts a workspace load and watches it, rather than dropping the task on the floor.
    /// </summary>
    /// <remarks>
    /// Every one of these used to be <c>_ = workspace.LoadAsync();</c>. A load that faulted outside
    /// the workspace's own catch — <c>TeamWorkspaceViewModel</c> had no catch at all — left a blank
    /// pane, no message on it, and nothing anywhere saying why: the discarded task's exception
    /// reached only <see cref="TaskScheduler.UnobservedTaskException"/>, whenever the collector got
    /// round to it, if ever.
    ///
    /// Still not awaited, and deliberately so. Navigation must not wait for a database read, and a
    /// workspace that is slow to fill is a workspace filling in, not a frozen window. What changes
    /// is that the failure is now recorded where a player's log will show it, and that it fails
    /// alone. Recoverable without a restart, and this sentence used to claim more than was true:
    /// Plan, Hideout and Keep now put a notice with a Retry button in the pane
    /// (<see cref="LoadFaultNoticeViewModel"/>); Team has Reload; Debrief, Stash, Ammo, Keys and
    /// Events have their Refresh; and returning to any route loads it again.
    /// </remarks>
    private void Load(string surface, Func<Task> load) => _ = ObserveWorkspaceLoad(surface, load);

    /// <summary>Awaits a workspace load and records whatever it throws.</summary>
    /// <remarks>
    /// Internal so it can be tested on its own. Building a whole shell to prove that a discarded
    /// task's exception reaches the log would need a dozen real services, and the claim is about
    /// this method and nothing else.
    /// </remarks>
    internal static async Task ObserveWorkspaceLoad(string surface, Func<Task> load)
    {
        UiActivity.LoadStarted(surface);
        try
        {
            await load().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            CrashLog.Write($"workspace-fault/{surface}", $"load: {exception}");
        }
        finally
        {
            UiActivity.LoadFinished(surface);
        }
    }

    /// <summary>
    /// Package 17 (home): the Setup overview's plan and recent raids come from the Plan and Debrief
    /// workspaces. Setup is variant A's landing page, so this waits for the database: loading
    /// before its migrations ran showed a raw "no such table" error in the plan card.
    /// </summary>
    private void LoadHomeOverview(ApplicationRuntimeSnapshot snapshot)
    {
        if (!snapshot.DatabaseReady || Router.Current.Location.Route != V2Routes.Setup)
        {
            return;
        }

        if (!_homeOverviewLoaded)
        {
            _homeOverviewLoaded = true;
            _homeOverviewLoadedUtc = _clock.GetUtcNow();
            _homeOverviewRaid = snapshot.Raid.State;
            LoadForOverview();
        }
        else if (_homeOverviewRaid != snapshot.Raid.State ||
            _clock.GetUtcNow() - _homeOverviewLoadedUtc >= HomeOverviewRefresh)
        {
            // Raids are written while the overview is showing (the landing page is where a
            // session starts), so its recent raids follow the raid state and, failing that,
            // re-read on a slow beat; otherwise a raid that opened a second after the first
            // load would leave "No raids recorded yet" on screen for the rest of the session.
            _homeOverviewLoadedUtc = _clock.GetUtcNow();
            _homeOverviewRaid = snapshot.Raid.State;
            LoadForOverview(planToo: false);
        }
    }

    /// <summary>The Setup overview's two sources, loaded the observed way like every other workspace.</summary>
    private void LoadForOverview(bool planToo = true)
    {
        if (planToo && _plan is { } plan)
        {
            Load("plan", plan.LoadAsync);
        }

        if (_debrief is { } debrief)
        {
            Load("debrief", debrief.LoadAsync);
        }
    }

    private void WireLegacyContext()
    {
        if (Legacy is null)
        {
            return;
        }

        _legacyContextSources.AddRange(
        [
            Legacy,
            Legacy.Map,
            Legacy.Items,
            Legacy.Quests,
            Legacy.Ammo,
            Legacy.Keys,
            Legacy.Flea,
            Legacy.Hideout,
            Legacy.Events,
        ]);
        foreach (var source in _legacyContextSources)
        {
            source.PropertyChanged += LegacyContextChanged;
        }

        WireStartupFaults();
    }

    private void LegacyContextChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }

        var isLegacyRoot = ReferenceEquals(sender, Legacy);
        if (!ShouldRefreshLegacyContext(isLegacyRoot, eventArgs.PropertyName))
        {
            return;
        }

        _legacyApply.Request();
    }

    private void QuestSyncPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(QuestScreenshotSyncViewModel.ShowsPassiveOffer)
            or nameof(QuestScreenshotSyncViewModel.PassiveOfferText))
        {
            OnPropertyChanged(nameof(ShowsQuestScreenshotOffer));
            OnPropertyChanged(nameof(QuestScreenshotOfferText));
        }
    }

    /// <summary>Rejects the preview-to-window-title notification from its own context feed.</summary>
    /// <remarks>
    /// The preview raises Title, its legacy host forwards that as WindowTitle, and this shell also
    /// listens to the host for page changes. Treating every host property as page context fed the
    /// forwarded title straight back into Refresh on the UI thread until the process exhausted its
    /// stack. Child page models remain broad feeds; only CurrentPage is context on the host itself.
    /// </remarks>
    internal static bool ShouldRefreshLegacyContext(bool isLegacyRoot, string? propertyName) =>
        !isLegacyRoot || propertyName is null or nameof(MainWindowViewModel.CurrentPage);

    /// <summary>
    /// Marks a destination for as long as a build is waiting behind it.
    /// </summary>
    /// <remarks>
    /// [#294] Static, and taking the two things it needs rather than reading them off the shell,
    /// because that is the only way this wiring can be tested: nothing in the unit suite builds a
    /// whole <see cref="V2ShellViewModel"/>, which needs the entire composition. A fake
    /// <see cref="IUpdateWaitingSource"/> and a real destination prove the behaviour; a source
    /// ratchet proves the constructor still calls this.
    ///
    /// Reads the current value before subscribing, because the event only fires on a change and
    /// the updater may have found a build before this shell existed.
    /// </remarks>
    internal static IDisposable MarkWhileUpdateWaits(IUpdateWaitingSource source, V2ShellDestinationViewModel destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        destination.HasNotice = source.IsUpdateWaiting;
        void Changed(object? sender, bool waiting) => destination.HasNotice = waiting;
        source.UpdateWaitingChanged += Changed;
        return new UpdateNoticeSubscription(() => source.UpdateWaitingChanged -= Changed);
    }

    private sealed class UpdateNoticeSubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }

    private void SynchronizeLegacySelection()
    {
        if (Legacy is null)
        {
            return;
        }

        var selected = Router.Current.Location.Route switch
        {
            var route when route == V2Routes.Raid => Legacy.Map.SelectedLocation?.Id,
            var route when route == V2Routes.Ammo => Legacy.Ammo.SelectedRound?.ItemId,
            var route when route == V2Routes.Keys => Legacy.Keys.Selected?.ItemId,
            var route when route == V2Routes.Flea => Legacy.Flea.Selected?.ItemId,
            var route when route == V2Routes.Plan => Legacy.Quests.SelectedTask?.TaskId,
            var route when route == V2Routes.Hideout => Legacy.Hideout.Selected?.StationId,
            var route when route == V2Routes.Events => Legacy.Events.Selected?.EventId,
            _ => Router.Current.SelectedEntity,
        };
        if (selected is not null && !V2AddressCodec.IsValidItem(selected))
        {
            selected = null;
        }

        if (!string.Equals(selected, Router.Current.SelectedEntity, StringComparison.Ordinal))
        {
            Router.Select(selected);
            QueueSave();
        }
    }

    private void RuntimeChanged(object? sender, EventArgs eventArgs) => _apply.Request();

    private void Refresh(bool announceBackgroundChange, bool playerAction = false)
    {
        ApplyPersistenceResults();
        var snapshot = _runtime.Current;
        var continuity = Volatile.Read(ref _continuity);
        SynchronizeLegacySelection();
        LoadHomeOverview(snapshot);
        ReloadPlanWhenGameDataChanges(snapshot.Data.UpdatedUtc);
        // v2r-team (package 9, wave 2): kept live on every refresh, like Legacy.Group/Legacy.Squad
        // already are, rather than only while the Team route is current — presence should not go
        // stale between visits.
        _team?.Apply(snapshot);
        // [V2 rough package 60 — Team] #289: the pairing code's countdown rides the shell's own
        // one-second pass rather than starting a second timer that would need its own shutdown.
        _companionPairing?.TickExpiry();
        _team?.SetActiveSection(Router.Current.Location.Route == V2Routes.Group
            ? TeamWorkspaceSection.Group
            : Router.Current.Location.Route == V2Routes.Tablet
                ? TeamWorkspaceSection.Devices
                : TeamWorkspaceSection.Overview);
        var selectedTask = Legacy?.Quests.SelectedTask;
        var selectedObjective = selectedTask?.Objectives.FirstOrDefault(objective => objective.Model.IsPinned)?.ObjectiveId;
        var priorScan = snapshot.Scan.Succeeded
            ? snapshot.Scan.CanonicalItemId
            : continuity.PriorScan ?? Router.Context.PriorScan;
        Router.UpdateContext(new(
            snapshot.Profile?.Name,
            snapshot.Raid.MapId ?? Legacy?.Map.SelectedLocation?.Id,
            continuity.PlanId ?? selectedTask?.TaskId ?? Router.Context.PlanId,
            priorScan,
            CaptureState.IntentRevision.Value > 0 || CaptureState.CorrelationId is not null
                ? CaptureState.SettingDevice
                : continuity.InitiatingDevice)
        {
            ProfileId = snapshot.Profile?.Id.ToString("D", CultureInfo.InvariantCulture),
            ProfileMode = snapshot.Profile?.GameMode.ToString(),
            RaidId = snapshot.Raid.RaidId?.ToString("D", CultureInfo.InvariantCulture),
            RaidState = snapshot.Raid.State.ToString(),
            ObjectiveId = continuity.ObjectiveId ?? selectedObjective ?? Router.Context.ObjectiveId,
            TeamMemberKeys = snapshot.Squad.Members.Select(member => member.Key).Take(5).ToArray(),
            CaptureCorrelationId = CaptureState.CorrelationId ?? continuity.CaptureCorrelationId ?? Router.Context.CaptureCorrelationId,
            WorkspaceId = (Router.CurrentDestination ?? Registry.RootOf(Router.Current.Location.Route)).Value,
            SelectedEntity = Router.Current.SelectedEntity,
        });
        var nextReadiness = V2Readiness.Evaluate(snapshot, _clock.GetUtcNow(), CultureInfo.CurrentCulture);
        var readinessChanged = !Readiness.Checks.SequenceEqual(nextReadiness.Checks);
        Readiness = nextReadiness;
        if (readinessChanged)
        {
            ReadinessItems = Readiness.Checks
                .Select(check => new V2ReadinessCheckViewModel(check, () => OpenReadiness(check)))
                .ToArray();
            SetupWorkspace?.Overview.ApplyReadiness(Readiness, ReadinessSummary, OpenReadiness);
        }

        SetupWorkspace?.Overview.ApplyDataFreshness(DataFreshnessLabel);

        var previousSurface = Surface.Kind;
        var nextSurface = V2SurfaceStateResolver.Resolve(
            Registry[Router.Current.Location.Route],
            snapshot,
            _clock.GetUtcNow(),
            CultureInfo.CurrentCulture);
        var recoveryChanged = !Surface.Recovery.SequenceEqual(nextSurface.Recovery);
        Surface = nextSurface;
        if (recoveryChanged)
        {
            RecoveryActions = Surface.Recovery
                .Select(action => new V2RecoveryActionViewModel(action, () => ExecuteRecovery(action)))
                .ToArray();
        }

        if (_surfaceInitialized && announceBackgroundChange && previousSurface != Surface.Kind &&
            Surface.Policy.Background != V2Announcement.None)
        {
            Announce(SurfaceAutomationName, Surface.Policy.Background);
        }

        if (playerAction && Surface.Policy.PlayerAction != V2Announcement.None)
        {
            Announce(SurfaceAutomationName, Surface.Policy.PlayerAction);
            if (Surface.Policy.FocusesOnPlayerAction && ShowsStatePresenter && ShowsPrimaryContent &&
                RecoveryActions.FirstOrDefault() is { } recovery)
            {
                _playerActionStateFocus = new(recovery.AutomationId, V2FocusReason.Invoker);
            }
        }

        // A new problem (or a recovered one) un-dismisses the banner; the same problem stays hidden.
        if (!string.Equals(_dismissedSurfaceIdentity, Surface.StableIdentity, StringComparison.Ordinal))
        {
            _dismissedSurfaceIdentity = string.Empty;
        }

        _surfaceInitialized = true;
        RebuildCapturePresentation();
        RebuildSuggestions();
        foreach (var destination in PrimaryDestinations.Append(SetupDestination))
        {
            destination.IsCurrent = Router.CurrentDestination == destination.Route;
        }
        foreach (var section in SectionItems)
        {
            section.IsCurrent = Router.Current.Location.Route == section.Route ||
                (Router.Current.Location.Route == V2Routes.Item && section.Route == V2Routes.Items);
        }

        RefreshIntelIfNeeded();
        RefreshIntelLandingIfNeeded();
        RefreshIntelTradeIfNeeded();
        RaisePresentationChanged();
        if (announceBackgroundChange && (readinessChanged || recoveryChanged) &&
            Router.Current.FocusTarget is { } focusedTarget && HasRenderedFocusTarget(focusedTarget))
        {
            // A runtime update may replace a checklist row with a new immutable row. Restore the
            // same control after the binding applies; this preserves focus instead of choosing it.
            FocusRequested?.Invoke(this, new(focusedTarget, V2FocusReason.Restored));
        }
    }

    /// <summary>
    /// Resolves the Intel workspace's result card off the router's current item, once per
    /// distinct item id. <see cref="Refresh"/> runs on every navigation and every background
    /// runtime tick, so this must be a no-op whenever the address has not actually changed.
    /// </summary>
    private void RefreshIntelIfNeeded()
    {
        var itemId = IntelItem;
        if (string.IsNullOrEmpty(itemId))
        {
            _intelLoadCts?.Cancel();
            _loadedIntelItemId = null;
            _intelResult = null;
            _intelLoading = false;
            IntelAcquisitionChain.Clear();
            return;
        }

        if (string.Equals(_loadedIntelItemId, itemId, StringComparison.Ordinal))
        {
            return;
        }

        _intelLoadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _intelLoadCts = cts;
        _loadedIntelItemId = itemId;
        _intelResult = null;
        _intelLoading = true;
        LoadIntelAsync(itemId, cts.Token).Observe("intel", "load an item");
    }

    private async Task LoadIntelAsync(string itemId, CancellationToken cancellationToken)
    {
        V2ItemIntelResult result;
        try
        {
            result = await _intel.GetAsync(itemId, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            result = V2ItemIntelResult.NotFound(itemId);
        }

        if (cancellationToken.IsCancellationRequested || _disposed ||
            !string.Equals(_loadedIntelItemId, itemId, StringComparison.Ordinal))
        {
            return;
        }

        _intelResult = result;
        _intelLoading = false;
        IntelAcquisitionChain.ShowAsync(itemId).Observe("intel", "plan the cheapest acquisition chain");
        RaiseIntelChanged();
    }

    private void RaiseIntelChanged()
    {
        foreach (var property in new[]
        {
            nameof(IntelDescription), nameof(IntelIsLoading), nameof(IntelIsNotFound), nameof(IntelStatusLabel),
            nameof(IntelKindLabel), nameof(IntelFacts), nameof(IntelHasWikiLink),
        })
        {
            OnPropertyChanged(property);
        }

        RaiseIntelWorkspaceChanged();
    }

    private IReadOnlyList<V2ShellIntelFactViewModel> BuildIntelFacts()
    {
        if (_intelResult is not { Kind: not V2IntelKind.Unknown } result)
        {
            return [];
        }

        var facts = new List<V2ShellIntelFactViewModel>
        {
            new(V2ShellText.Get("V2.Shell.Intel.ShortName"), result.ShortName),
            new(V2ShellText.Get("V2.Shell.Intel.Category"), result.Category.ToString()),
            new(
                V2ShellText.Get("V2.Shell.Intel.Size"),
                V2ShellText.Format(
                    "V2.Shell.Intel.SizeValue",
                    CultureInfo.CurrentCulture,
                    result.Width,
                    result.Height,
                    result.Width * result.Height)),
            new(V2ShellText.Get("V2.Shell.Intel.Price"), PriceValueLabel(result.Value)),
            new(
                V2ShellText.Get("V2.Shell.Intel.Flea"),
                V2ShellText.Get(result.FleaEligible ? "V2.Shell.Intel.FleaAllowed" : "V2.Shell.Intel.FleaNotAllowed")),
            new(V2ShellText.Get("V2.Shell.Intel.Need"), NeedValueLabel(result)),
        };

        if (result.Kind == V2IntelKind.Key)
        {
            facts.Add(new(V2ShellText.Get("V2.Shell.Intel.Opens"), OpensValueLabel(result.Key)));
            if (result.Key?.MaximumUses is { } uses)
            {
                facts.Add(new(V2ShellText.Get("V2.Shell.Intel.Uses"), uses.ToString("N0", CultureInfo.CurrentCulture)));
            }

            if (result.Key?.AcquisitionCostRoubles is { } cost)
            {
                facts.Add(new(V2ShellText.Get("V2.Shell.Intel.KeyCost"), Roubles(cost)));
            }
        }

        if (result.Kind == V2IntelKind.Ammo)
        {
            if (result.Ammo is { } ammo)
            {
                facts.Add(new(V2ShellText.Get("V2.Shell.Intel.Damage"), ammo.Damage.ToString(CultureInfo.CurrentCulture)));
                facts.Add(new(V2ShellText.Get("V2.Shell.Intel.Penetration"), ammo.Penetration.ToString(CultureInfo.CurrentCulture)));
                if (ammo.ArmorDamagePercent is { } armorDamage)
                {
                    facts.Add(new(V2ShellText.Get("V2.Shell.Intel.ArmorDamage"), $"{armorDamage.ToString("N0", CultureInfo.CurrentCulture)}%"));
                }

                if (ammo.FragmentationChance is { } fragmentation)
                {
                    facts.Add(new(V2ShellText.Get("V2.Shell.Intel.Fragmentation"), fragmentation.ToString("P0", CultureInfo.CurrentCulture)));
                }

                facts.Add(new(V2ShellText.Get("V2.Shell.Intel.Tier"), ammo.Tier));
                facts.Add(new(V2ShellText.Get("V2.Shell.Intel.Advice"), ammo.PracticalAdvice));
            }
            else
            {
                facts.Add(new(V2ShellText.Get("V2.Shell.Intel.Damage"), V2ShellText.Get("V2.Shell.Intel.AmmoUnknown")));
            }
        }

        return facts;
    }

    private static string PriceValueLabel(V2IntelValueFacts? value) =>
        value is { ValueRoubles: { } roubles, SaleChannelLabel: { } channel }
            ? V2ShellText.Format("V2.Shell.Intel.PriceValue", CultureInfo.CurrentCulture, roubles, channel)
            : V2ShellText.Get("V2.Shell.Intel.PriceUnknown");

    // Package 33 (#287): the tracked-quest count reads result.Keep, not result.Value, for the
    // same reason V2ShellViewModel.IntelWorkspace.cs's need summary does (see the remark there) —
    // result.Value.TrackedQuestsNeedingIt/QuestsNeedingIt read zero for every item this workspace
    // tried against the seed database, including one a direct SQL check confirmed a real quest
    // needs, while the hideout count from that same source was correct.
    private static string NeedValueLabel(V2ItemIntelResult result)
    {
        var tracked = result.Keep?.Quests.Count ?? 0;
        var hideout = result.Value?.HideoutCount ?? 0;
        return tracked == 0 && hideout == 0
            ? V2ShellText.Get("V2.Shell.Intel.NeedNone")
            : V2ShellText.Format("V2.Shell.Intel.NeedValue", CultureInfo.CurrentCulture, tracked, 0, hideout);
    }

    private static string OpensValueLabel(V2IntelKeyFacts? key) => key switch
    {
        { MapId: { } map, Locks.Count: > 0 } withLocks =>
            V2ShellText.Format("V2.Shell.Intel.OpensMapAndLocks", CultureInfo.CurrentCulture, withLocks.MapName ?? map, string.Join(", ", withLocks.Locks)),
        { MapId: { } map } named => named.MapName ?? map,
        { Locks.Count: > 0 } locksOnly => string.Join(", ", locksOnly.Locks),
        _ => V2ShellText.Get("V2.Shell.Intel.OpensUnknown"),
    };

    private bool HasRenderedFocusTarget(string automationId) =>
        ReadinessItems.Any(item =>
            string.Equals(item.AutomationId, automationId, StringComparison.Ordinal) ||
            string.Equals(item.DialogAutomationId, automationId, StringComparison.Ordinal)) ||
        RecoveryActions.Any(item => string.Equals(item.AutomationId, automationId, StringComparison.Ordinal)) ||
        _activeReadinessTarget is not null &&
        string.Equals(ReadinessTargetAutomationId, automationId, StringComparison.Ordinal);

    /// <summary>Hides the global-problem banner until the problem itself changes.</summary>
    private void DismissSurfaceBanner()
    {
        _dismissedSurfaceIdentity = Surface.StableIdentity;
        OnPropertyChanged(nameof(ShowsSurfaceBanner));
        Announce(V2ShellText.Get("V2.Shell.Banner.Dismissed"), V2Announcement.Polite);
    }

    private void ExecuteRecovery(V2RecoveryAction action)
    {
        if (action.Route is { } route)
        {
            Act(Router.Navigate(route, $"v2-shell-recovery-{action.Id}"));
            return;
        }

        switch (action.Id)
        {
            case "open-capture":
                ToggleDialog(V2ShellDialogKind.Capture, $"v2-shell-recovery-{action.Id}", V2ShellFocusTargets.CaptureDialog);
                break;
            case "manage-pairing":
                // [V2 rough package 48] Pairing is a section of the Team workspace now, not a
                // popout window, so recovery navigates to it like every other recovery action.
                Act(Router.Navigate(V2Routes.Tablet, $"v2-shell-recovery-{action.Id}"));
                break;
            case "sync":
                if (Legacy is not null)
                {
                    Legacy.Settings.SyncCommand.Execute(null);
                    Announce(V2ShellText.Get("V2.Shell.Announce.SyncStarted"), V2Announcement.Polite);
                }
                else
                {
                    Announce(V2ShellText.Get("V2.Shell.Announce.ActionUnavailable"), V2Announcement.Assertive);
                }
                break;
            default:
                Announce(V2ShellText.Get("V2.Shell.Announce.ActionUnavailable"), V2Announcement.Assertive);
                break;
        }
    }


    private void OpenReadiness(V2ReadinessCheck check)
    {
        var invoker = IsHealthOpen
            ? _dialogInvoker ?? V2ShellFocusTargets.Health
            : $"v2-shell-readiness-{check.Id}";
        if (IsHealthOpen)
        {
            CloseDialog(restoreInvoker: false);
        }

        var result = Router.Navigate(check.ActionRoute, invoker);
        if (!result.Succeeded)
        {
            Act(result);
            return;
        }

        // #292: several readiness rows share Setup; open the section that actually fixes each one
        // instead of leaving the workspace wherever it last was.
        if (check.ActionRoute == V2Routes.Setup && V2SetupWorkspaceViewModel.TryMapReadinessCheck(check.Id, out var section))
        {
            SetupWorkspace?.Select(section);
        }

        // A readiness row names a specific thing to inspect. Several rows share Setup, and a
        // same-page Navigate result only focuses the generic page heading, so preserve the row's
        // identity in a visible action target and focus that target in every case.
        _activeReadinessTarget = check;
        RaiseReadinessTargetChanged();
        FocusRequested?.Invoke(this, new(
            V2ShellFocusTargets.ReadinessTarget(check.Id),
            V2FocusReason.PageHeading));
    }

    private void RebuildCapturePresentation()
    {
        var state = CaptureState;
        if (ReferenceEquals(state, _renderedCaptureState))
        {
            return;
        }

        var armedIntentChanged = _renderedCaptureState is null ||
            _renderedCaptureState.IntentRevision != state.IntentRevision ||
            _renderedCaptureState.ArmedIntent != state.ArmedIntent;
        _renderedCaptureState = state;
        if (armedIntentChanged)
        {
            // Progress updates replace the immutable projection too. Preserve a radio choice the
            // player has not armed yet unless the authoritative intent revision actually moved.
            SelectedCaptureIntent = state.ArmedIntent;
        }
        CaptureProgressItems = state.Session?.Progress
            .Select(progress => new V2CaptureProgressViewModel(
                progress.Sequence,
                V2ShellText.Get($"V2.Shell.Capture.Stage.{progress.Stage}"),
                progress.Detail ?? V2ShellText.Get($"V2.Shell.Capture.StageDetail.{progress.Stage}"),
                progress.CaptureOrdinal is { } ordinal
                    ? V2ShellText.Format(
                        "V2.Shell.Capture.Artifact",
                        CultureInfo.CurrentCulture,
                        ordinal + 1,
                        progress.ArtifactId)
                    : V2ShellText.Get("V2.Shell.Capture.Session"),
                progress.Percent is { } percent
                    ? V2ShellText.Format("V2.Shell.Capture.Percent", CultureInfo.CurrentCulture, percent)
                    : string.Empty,
                state.CorrelationId ?? string.Empty))
            .ToArray() ?? [];
        CaptureAttentionActions = state.Attention is { } attention
            ? AttentionActions(attention)
            : [];
        CaptureReviewActions = state.Review is { } review
            ? ReviewActions(review)
            : [];

        foreach (var property in new[]
        {
            nameof(CaptureState), nameof(CaptureLabel), nameof(CaptureProgressItems),
            nameof(HasCaptureProgress), nameof(HasNoCaptureProgress), nameof(HasCaptureAttention),
            nameof(HasCaptureReview), nameof(HasCaptureReference), nameof(CaptureReference),
            nameof(CaptureArmedStatus), nameof(CaptureAttentionHeading), nameof(CaptureAttentionDetail),
            nameof(CaptureAttentionActions), nameof(CaptureReviewSummary), nameof(CaptureReviewEvidence),
            nameof(CaptureReviewActions),
        })
        {
            OnPropertyChanged(property);
        }
    }

    private IReadOnlyList<V2CaptureActionViewModel> AttentionActions(V2CaptureAttention attention)
    {
        var resolutions = attention.Kind switch
        {
            V2CaptureAttentionKind.IntentMismatch => new[]
            {
                V2CaptureResolutionKind.Skip,
                V2CaptureResolutionKind.AnalyzeAsArmed,
                V2CaptureResolutionKind.AnalyzeAsDetected,
            },
            // [V2 rough package 60 — Intel scan] #287: "Analyse as armed", not "as selected".
            // The intent radio above can be changed while a decode waits, but the frame was taken
            // under the intent that was armed when the shutter fired, and #271's coordinator has
            // no action that re-analyses one artifact as a different intent. Offering a button
            // that quietly does something else is worse than offering the honest one, and Retry
            // is the way to ask the other question.
            V2CaptureAttentionKind.UnknownContext =>
            [V2CaptureResolutionKind.Skip, V2CaptureResolutionKind.AnalyzeAsArmed, V2CaptureResolutionKind.Retry],
            V2CaptureAttentionKind.StillWriting =>
            [V2CaptureResolutionKind.Skip, V2CaptureResolutionKind.Retry],
            V2CaptureAttentionKind.Duplicate =>
            [V2CaptureResolutionKind.AnalyzeAgain],
            V2CaptureAttentionKind.DeviceRace =>
            [V2CaptureResolutionKind.KeepCurrentIntent, V2CaptureResolutionKind.ArmSelectedIntent],
            V2CaptureAttentionKind.SourceUnavailable =>
            [V2CaptureResolutionKind.Skip, V2CaptureResolutionKind.Retry],
            _ => [],
        };
        return resolutions.Select(ActionFor).ToArray();
    }

    private IReadOnlyList<V2CaptureActionViewModel> ReviewActions(V2CaptureReview review)
    {
        var actions = new List<V2CaptureActionViewModel> { ActionFor(V2CaptureResolutionKind.Review) };
        if (review.CanCorrect)
        {
            actions.Add(ActionFor(V2CaptureResolutionKind.Correct));
        }

        return actions;
    }

    private V2CaptureActionViewModel ActionFor(V2CaptureResolutionKind resolution) => new(
        resolution,
        V2ShellText.Get($"V2.Shell.Capture.Action.{resolution}"),
        $"v2-shell-capture-action-{resolution.ToString().ToLowerInvariant()}",
        RequestCaptureResolution);

    private void RebuildSuggestions()
    {
        var suggestions = new List<V2ShellSuggestionViewModel>();
        AddAddressSuggestions(Pins, V2ShellSuggestionKind.Pinned, "V2.Shell.Suggestions.Source.Pinned", suggestions);
        AddAddressSuggestions(Recents, V2ShellSuggestionKind.Recent, "V2.Shell.Suggestions.Source.Recent", suggestions);

        foreach (var planned in Volatile.Read(ref _plannedSuggestions))
        {
            var itemId = planned.ItemId;
            var automationId = $"v2-shell-suggestion-planned-{itemId}";
            suggestions.Add(new(
                itemId,
                planned.DisplayName,
                V2ShellText.Format(
                    "V2.Shell.Suggestions.Source.Planned",
                    CultureInfo.CurrentCulture,
                    planned.PlanLabel,
                    planned.ObjectiveLabel),
                V2ShellText.Get("V2.Shell.Suggestions.Category.Planned"),
                V2ShellSuggestionKind.Planned,
                () => OpenSuggestedItem(itemId, automationId)));
        }

        if (Legacy is not null)
        {
            var selected = Legacy.Quests.SelectedTask;
            var tasks = selected is not null
                ? new[] { selected }
                : Legacy.Quests.Tasks.Where(task => task.Model.IsPinned).Take(3).ToArray();
            var seenItems = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in tasks)
            {
                foreach (var objective in task.Objectives
                    .Where(objective => objective.Model.RecordedState != RecordedObjectiveState.Completed)
                    .SelectMany(objective => objective.Model.ItemTargets.Select(target => (objective, target)))
                    .Where(pair => seenItems.Add(pair.target.ItemId))
                    .Take(8))
                {
                    var itemId = objective.target.ItemId;
                    var automationId = $"v2-shell-suggestion-planned-{itemId}";
                    suggestions.Add(new(
                        itemId,
                        Legacy.Quests.NameOfItem(itemId),
                        V2ShellText.Format(
                            "V2.Shell.Suggestions.Source.Planned",
                            CultureInfo.CurrentCulture,
                            task.Name,
                            objective.objective.Description),
                        V2ShellText.Get("V2.Shell.Suggestions.Category.Planned"),
                        V2ShellSuggestionKind.Planned,
                        () => OpenSuggestedItem(itemId, automationId)));
                }
            }
        }

        SuggestionItems = suggestions
            .GroupBy(item => (item.Kind, item.Key))
            .Select(group => group.First())
            .Take(30)
            .ToArray();
        RaiseSuggestionsChanged();
    }

    private void AddAddressSuggestions(
        IReadOnlyList<string> addresses,
        V2ShellSuggestionKind kind,
        string provenanceKey,
        ICollection<V2ShellSuggestionViewModel> suggestions)
    {
        foreach (var address in addresses.Take(8))
        {
            var parsed = Router.Addresses.Parse(address);
            if (parsed.Location is not { } location)
            {
                continue;
            }

            var route = Registry[location.Route];
            var selectedItem = location.Item ?? location.IntelItem;
            var label = selectedItem is { } item
                ? V2ShellText.Format(
                    "V2.Shell.Suggestions.Item",
                    CultureInfo.CurrentCulture,
                    item,
                    V2ShellText.Get(route.HeadingKey))
                : V2ShellText.Get(route.HeadingKey);
            var automationId = V2ShellFocusTargets.SavedAddress(kind.ToString().ToLowerInvariant(), suggestions.Count);
            suggestions.Add(new(
                address,
                label,
                V2ShellText.Get(provenanceKey),
                V2ShellText.Get(route.HeadingKey),
                kind,
                () => OpenSavedAddress(address, automationId)));
        }
    }

    private void OpenSuggestedItem(string itemId, string automationId)
    {
        if (HasOpenDialog)
        {
            CloseDialog(restoreInvoker: false);
        }

        Act(Router.OpenIntel(itemId, automationId));
    }

    private void SelectSuggestionFilter(V2ShellSuggestionKind kind)
    {
        _suggestionFilter = kind;
        foreach (var filter in SuggestionFilters)
        {
            filter.IsSelected = filter.Kind == kind;
        }

        RaiseSuggestionsChanged();
    }

    private void RaiseSuggestionsChanged()
    {
        OnPropertyChanged(nameof(SuggestionItems));
        OnPropertyChanged(nameof(FilteredSuggestionItems));
        OnPropertyChanged(nameof(ShowsSuggestions));
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(HasNoSuggestions));
        OnPropertyChanged(nameof(SuggestionFilter));
    }

    private void PersistenceCompleted(V2ShellPersistenceResult result)
    {
        if (_disposed || result.Kind == V2ShellPersistenceOperationKind.Reset)
        {
            // Reset is awaited by ResetPreviewAsync so the durable result and the visible state
            // change stay one operation. Publishing its queue event first could expose Retry
            // while the original command was still unwinding and make that click a no-op.
            return;
        }

        _persistenceResults.Enqueue(result);
        _apply.Request();
    }

    private void ApplyPersistenceResults()
    {
        while (_persistenceResults.TryDequeue(out var result))
        {
            if (result.Succeeded)
            {
                if (_persistenceFailureKind == result.Kind && (HasPersistenceFailure || _persistenceRetryPending))
                {
                    _persistenceFailure = string.Empty;
                    _persistenceFailureKind = null;
                    _persistenceRetryPending = false;
                    Announce(V2ShellText.Get("V2.Shell.Announce.PersistenceRestored"), V2Announcement.Polite);
                }

                continue;
            }

            if (_persistenceFailureKind == V2ShellPersistenceOperationKind.Reset &&
                result.Kind == V2ShellPersistenceOperationKind.Save)
            {
                // A later navigation save cannot make an earlier reset true. Keep Reset as the
                // retry target until that delete itself succeeds or the player retries it.
                continue;
            }

            _persistenceRetryPending = false;
            _persistenceFailureKind = result.Kind;
            _persistenceFailure = V2ShellText.Format(
                result.Kind == V2ShellPersistenceOperationKind.Reset
                    ? "V2.Shell.Persistence.ResetFailed"
                    : "V2.Shell.Persistence.SaveFailed",
                CultureInfo.CurrentCulture,
                StorageFailureDetail(result.Error));
            Announce(_persistenceFailure, V2Announcement.Assertive);
        }

        OnPropertyChanged(nameof(PersistenceFailure));
        OnPropertyChanged(nameof(HasPersistenceFailure));
        OnPropertyChanged(nameof(PersistenceRetryPending));
        OnPropertyChanged(nameof(CanRetryPersistence));
        OnPropertyChanged(nameof(PersistenceRetryLabel));
    }

    private void RetryPersistence()
    {
        if (_disposed || !CanRetryPersistence)
        {
            return;
        }

        _persistenceRetryPending = true;
        OnPropertyChanged(nameof(PersistenceRetryPending));
        OnPropertyChanged(nameof(CanRetryPersistence));
        OnPropertyChanged(nameof(PersistenceRetryLabel));
        // Publish the in-progress state before starting work. A fast durable operation may finish
        // synchronously enough to publish its final result before Execute returns; announcing the
        // retry afterwards would then overwrite the truthful "reset"/"restored" outcome.
        Announce(V2ShellText.Get("V2.Shell.Announce.PersistenceRetrying"), V2Announcement.Polite);
        if (_persistenceFailureKind == V2ShellPersistenceOperationKind.Reset)
        {
            ResetPreviewCommand.Execute(null);
        }
        else
        {
            _persistence.QueueSave(Snapshot());
        }
    }

    private void ResetPreviewCanExecuteChanged(object? sender, EventArgs eventArgs) =>
        OnPropertyChanged(nameof(CanRetryPersistence));

    private string FormatRaidContext(RaidSnapshot raid, DateTimeOffset nowUtc)
    {
        var map = raid.MapId ?? Router.Context.MapId ?? V2ShellText.Get("V2.Shell.Context.NoMap");
        var state = V2ShellText.Get($"V2.Shell.Context.RaidState.{raid.State}");
        if (SharedRaidClock(raid) is { } sharedContextClock)
        {
            return $"{map} · {state} · {sharedContextClock}";
        }

        if (raid.State == RaidLifecycleState.InRaid &&
            raid.RaidClock is { } observedRemaining &&
            raid.RaidClockReadUtc is { } readUtc)
        {
            // A clock read from a screenshot is a reading at that instant. Showing the original
            // value forever is the full-raid-time defect #267's context strip must not repeat.
            var age = nowUtc - readUtc;
            var remaining = observedRemaining - (age < TimeSpan.Zero ? TimeSpan.Zero : age);
            return V2ShellText.Format(
                "V2.Shell.Context.RaidRemaining",
                CultureInfo.CurrentCulture,
                map,
                state,
                FormatDuration(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining));
        }

        if (raid.StartedUtc is { } started && raid.State == RaidLifecycleState.InRaid)
        {
            var elapsed = nowUtc - started;
            return V2ShellText.Format(
                "V2.Shell.Context.RaidElapsed",
                CultureInfo.CurrentCulture,
                map,
                state,
                FormatDuration(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed));
        }

        return V2ShellText.Format("V2.Shell.Context.RaidOnMap", CultureInfo.CurrentCulture, map, state);
    }

    /// <summary>The top bar's clock chip: state and remaining/elapsed time, without the map (the
    /// map selector names it separately).</summary>
    private string FormatRaidClock(RaidSnapshot raid, DateTimeOffset nowUtc)
    {
        var state = V2ShellText.Get($"V2.Shell.Context.RaidState.{raid.State}");
        if (SharedRaidClock(raid) is { } sharedClock)
        {
            return V2ShellText.Format("V2.Shell.Context.RaidOnMap", CultureInfo.CurrentCulture, state, sharedClock);
        }

        if (raid.State == RaidLifecycleState.InRaid &&
            raid.RaidClock is { } observedRemaining &&
            raid.RaidClockReadUtc is { } readUtc)
        {
            var age = nowUtc - readUtc;
            var remaining = observedRemaining - (age < TimeSpan.Zero ? TimeSpan.Zero : age);
            return V2ShellText.Format(
                "V2.Shell.Context.ClockRemaining",
                CultureInfo.CurrentCulture,
                state,
                FormatDuration(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining));
        }

        if (raid.StartedUtc is { } started && raid.State == RaidLifecycleState.InRaid)
        {
            var elapsed = nowUtc - started;
            return V2ShellText.Format(
                "V2.Shell.Context.ClockElapsed",
                CultureInfo.CurrentCulture,
                state,
                FormatDuration(elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed));
        }

        return state;
    }

    /// <summary>"Data updated 12 min ago", from the same freshness the Health dialog already
    /// reasons about; never a live per-second clock, so it does not itself invalidate.</summary>
    private string FormatDataFreshness(DateTimeOffset? updatedUtc, DateTimeOffset nowUtc)
    {
        if (updatedUtc is not { } updated)
        {
            return V2ShellText.Get("V2.Shell.DataFreshness.Unknown");
        }

        var age = nowUtc - updated;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age < TimeSpan.FromMinutes(1)
            ? V2ShellText.Get("V2.Shell.DataFreshness.JustNow")
            : V2ShellText.Format("V2.Shell.DataFreshness.Ago", CultureInfo.CurrentCulture, FormatApproximateAge(age));
    }

    private static string FormatApproximateAge(TimeSpan age) =>
        age < TimeSpan.FromHours(1)
            ? V2ShellText.Format("V2.Shell.DataFreshness.Minutes", CultureInfo.CurrentCulture, (int)age.TotalMinutes)
            : V2ShellText.Format("V2.Shell.DataFreshness.Hours", CultureInfo.CurrentCulture, (int)age.TotalHours);

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = Math.Max(0, (int)duration.TotalHours);
        return totalHours > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{totalHours}:{duration.Minutes:00}:{duration.Seconds:00}")
            : string.Create(CultureInfo.CurrentCulture, $"{duration.Minutes:00}:{duration.Seconds:00}");
    }

    private static string StorageFailureDetail(Exception? exception)
    {
        if (string.IsNullOrWhiteSpace(exception?.Message))
        {
            return V2ShellText.Get("V2.Shell.Persistence.UnknownFailure");
        }

        const int maximumLength = 200;
        var printable = new string(exception.Message
            .Select(character => char.IsControl(character) ? ' ' : character)
            .Take(maximumLength + 1)
            .ToArray())
            .Trim();
        if (printable.Length == 0)
        {
            return V2ShellText.Get("V2.Shell.Persistence.UnknownFailure");
        }

        return printable.Length <= maximumLength ? printable : $"{printable[..maximumLength]}…";
    }

    private static string IntentLabel(ScanIntent intent) => V2ShellText.Get($"V2.Shell.Intent.{intent}");

    private static string ContextLabel(RecognizedContext context) =>
        V2ShellText.Get($"V2.Shell.Capture.Context.{context}");

    private static ScanIntent IntentForContext(RecognizedContext context) => context switch
    {
        RecognizedContext.Loot => ScanIntent.Loot,
        RecognizedContext.Stash => ScanIntent.Stash,
        RecognizedContext.Ammo => ScanIntent.Ammo,
        RecognizedContext.Keys => ScanIntent.Keys,
        RecognizedContext.QuestItems => ScanIntent.QuestItems,
        RecognizedContext.ExtractsAndMap => ScanIntent.ExtractsAndMap,
        RecognizedContext.HealthAndCharacter => ScanIntent.HealthAndCharacter,
        RecognizedContext.Flea => ScanIntent.Flea,
        _ => ScanIntent.Auto,
    };

    private void RaisePresentationChanged()
    {
        foreach (var property in new[]
        {
            nameof(Readiness), nameof(ReadinessItems), nameof(Surface), nameof(SurfaceTitle), nameof(SurfaceRemainder),
            nameof(SurfaceGlyph), nameof(SurfaceAutomationName), nameof(RecoveryActions), nameof(CurrentHeading), nameof(Title),
            nameof(ShowsSurfaceBanner), nameof(SurfaceBannerText), nameof(ShowsHealthSurface),
            nameof(ReadinessSummary), nameof(HealthSummary), nameof(HealthLabel),
            nameof(ShowsWorkspaceSearch), nameof(ShowsWorkspace), nameof(WorkspaceContent),
            nameof(ShowsRaidCockpit),
            nameof(ShowsLootScan), nameof(LootScanResult), nameof(ShowsLootScanEmpty),
            nameof(ShowsSetupWorkspace), nameof(ShellBodyRowSpan),
            nameof(ShowsReadiness), nameof(ShowsReadinessChecklist), nameof(ShowsContinue),
            nameof(ShowsStatePresenter), nameof(ShowsIntel), nameof(ShowsIntelBeside), nameof(ShowsIntelInsteadOfPage),
            nameof(ShowsPrimaryContent), nameof(IntelItem), nameof(IntelDescription), nameof(IntelColumn), nameof(IntelColumnSpan),
            nameof(SurfaceIsReady), nameof(SurfaceIsLoading), nameof(SurfaceIsUnknown), nameof(SurfaceIsOffline),
            nameof(SurfaceIsStale), nameof(SurfaceIsPartial), nameof(SurfaceIsDenied), nameof(SurfaceIsFailed),
            nameof(SurfacePatternDashed), nameof(SurfacePatternDotted), nameof(SurfacePatternDouble),
            nameof(SectionItems), nameof(ShowsSectionNavigation),
            nameof(ShowsReadinessTarget), nameof(ReadinessTargetAutomationId), nameof(ReadinessTargetHeading),
            nameof(ReadinessTargetDetail), nameof(ReadinessTargetAutomationName),
            nameof(ProfileContextLabel), nameof(LocalTimeLabel), nameof(RaidContextLabel), nameof(PlanContextLabel),
            nameof(TeamContextLabel), nameof(DeviceContextLabel), nameof(SelectionContextLabel),
            nameof(RaidClockLabel), nameof(TopBarModeLabel), nameof(DataFreshnessLabel),
            nameof(RaidCockpitWorkspace), nameof(ShowsMapSelector),
            nameof(CaptureWatchingStatus), nameof(CapturePrior), nameof(CaptureReference), nameof(CaptureLabel),
            nameof(ShowsSuggestions), nameof(FilteredSuggestionItems), nameof(HasSuggestions), nameof(HasNoSuggestions),
            nameof(PersistenceFailure), nameof(HasPersistenceFailure), nameof(PersistenceRetryPending),
            nameof(CanRetryPersistence), nameof(PersistenceRetryLabel),
        })
        {
            OnPropertyChanged(property);
        }

        RaiseIntelWorkspaceChanged();
    }

    private void Act(V2NavigationResult result)
    {
        if (!result.Succeeded)
        {
            Announce(result.Failure ?? string.Empty, V2Announcement.Assertive);
            return;
        }

        var requestedFocus = _playerActionStateFocus ?? result.Focus;
        _playerActionStateFocus = null;
        if (requestedFocus is { } focus)
        {
            FocusRequested?.Invoke(this, focus);
        }
    }

    private ICommand CreateCommand(V2ShellCommand command) =>
        new DelegateCommand(() => ExecuteCommand(command, V2ShellFocusTargets.Command(command.Id)));

    private void ExecuteCommand(V2ShellCommand command, string? invoker)
    {
        var paletteWasOpen = IsPaletteOpen;
        var effectiveInvoker = IsPaletteOpen
            ? _dialogInvoker ?? V2ShellFocusTargets.Palette
            : invoker;
        if (IsPaletteOpen && command.Kind is not (V2ShellCommandKind.TogglePalette or V2ShellCommandKind.CloseTransient))
        {
            CloseDialog(restoreInvoker: false);
        }

        switch (command.Kind)
        {
            case V2ShellCommandKind.Navigate: Act(Router.Navigate(command.Route!.Value, effectiveInvoker)); break;
            case V2ShellCommandKind.Back: Back(); break;
            case V2ShellCommandKind.Forward: Forward(); break;
            case V2ShellCommandKind.ToggleCapture:
                ToggleDialog(V2ShellDialogKind.Capture, effectiveInvoker, V2ShellFocusTargets.CaptureDialog);
                break;
            case V2ShellCommandKind.ToggleHealth:
                ToggleDialog(V2ShellDialogKind.Health, effectiveInvoker, V2ShellFocusTargets.HealthDialog);
                break;
            case V2ShellCommandKind.TogglePalette:
                ToggleDialog(V2ShellDialogKind.Commands, effectiveInvoker, V2ShellFocusTargets.PaletteDialog);
                break;
            case V2ShellCommandKind.FocusSearch: FocusSearch(); break;
            case V2ShellCommandKind.CopyAddress:
                CopyAddressCommand.Execute(null);
                RestorePaletteInvoker(paletteWasOpen, effectiveInvoker);
                break;
            case V2ShellCommandKind.CloseTransient: CloseTransient(); break;
            case V2ShellCommandKind.TogglePin:
                TogglePin();
                RestorePaletteInvoker(paletteWasOpen, effectiveInvoker);
                break;
            case V2ShellCommandKind.ToggleCaptureShortcut:
                ToggleCaptureShortcut();
                RestorePaletteInvoker(paletteWasOpen, effectiveInvoker);
                break;
            case V2ShellCommandKind.ResetPreview: ResetPreviewCommand.Execute(null); break;
            case V2ShellCommandKind.CycleNavigationRail:
                CycleNavigationRail();
                RestorePaletteInvoker(paletteWasOpen, effectiveInvoker);
                break;
            case V2ShellCommandKind.NextRegion: MoveRegion(reverse: false); break;
            case V2ShellCommandKind.PreviousRegion: MoveRegion(reverse: true); break;
            case V2ShellCommandKind.ShowKeyboardShortcuts:
            {
                var opened = Router.Navigate(V2Routes.Setup, effectiveInvoker);
                Act(opened);
                if (opened.Succeeded)
                {
                    SetupWorkspace?.Select(V2SetupSection.Accessibility);
                }

                break;
            }
        }
    }

    private void RestorePaletteInvoker(bool paletteWasOpen, string? invoker)
    {
        if (paletteWasOpen)
        {
            FocusRequested?.Invoke(this, new(
                invoker ?? V2ShellRouter.PageHeadingTarget,
                invoker is null ? V2FocusReason.PageHeading : V2FocusReason.Invoker));
        }
    }

    private void MoveRegion(bool reverse)
    {
        var targets = HasOpenDialog
            ? new[] { ActiveDialogFocusTarget() }
            : new[]
            {
                V2ShellFocusTargets.Capture,
                CurrentDestinationFocusTarget(),
                ShowsPrimaryContent ? V2ShellRouter.PageHeadingTarget : null,
                ShowsIntel ? V2ShellRouter.IntelHeadingTarget : null,
            }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (targets.Length == 0)
        {
            return;
        }

        if (_regionIndex < 0 && reverse)
        {
            _regionIndex = 0;
        }

        _regionIndex = (_regionIndex + (reverse ? targets.Length - 1 : 1)) % targets.Length;
        FocusRequested?.Invoke(this, new(targets[_regionIndex], V2FocusReason.Invoker));
    }

    private string CurrentDestinationFocusTarget()
    {
        var route = Router.CurrentDestination ?? Variant.Destinations[0].Route;
        return V2ShellFocusTargets.Destination(route);
    }

    private void ToggleDialog(V2ShellDialogKind dialog, string? invoker, string focusTarget)
    {
        if (ActiveDialog == dialog)
        {
            CloseDialog(restoreInvoker: true);
            return;
        }

        if (HasOpenDialog)
        {
            // Switching modal content keeps the control that opened the modal layer. The title
            // in the outgoing body disappears and cannot be a valid restoration target.
            invoker = _dialogInvoker ?? invoker;
        }

        ActiveDialog = dialog;
        _dialogInvoker = string.IsNullOrWhiteSpace(invoker) ? V2ShellRouter.PageHeadingTarget : invoker;
        FocusRequested?.Invoke(this, new(focusTarget, V2FocusReason.PageHeading));
    }

    private void CloseDialog(bool restoreInvoker)
    {
        var invoker = _dialogInvoker;
        ActiveDialog = V2ShellDialogKind.None;
        _dialogInvoker = null;
        if (restoreInvoker)
        {
            FocusRequested?.Invoke(this, new(
                invoker ?? V2ShellRouter.PageHeadingTarget,
                invoker is null ? V2FocusReason.PageHeading : V2FocusReason.Invoker));
        }
    }

    private string ActiveDialogFocusTarget() => ActiveDialog switch
    {
        V2ShellDialogKind.Capture => V2ShellFocusTargets.CaptureDialog,
        V2ShellDialogKind.Commands => V2ShellFocusTargets.PaletteDialog,
        V2ShellDialogKind.Health => V2ShellFocusTargets.HealthDialog,
        _ => V2ShellRouter.PageHeadingTarget,
    };

    private async Task CopyAddressAsync()
    {
        try
        {
            await Clipboard(Router.CurrentAddress).ConfigureAwait(true);
            Announce(V2ShellText.Get("V2.Shell.Announce.Copied"), V2Announcement.Polite);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Announce(V2ShellText.Get("V2.Shell.Announce.CopyFailed"), V2Announcement.Assertive);
        }
    }

    private async Task ResetPreviewAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _resetInProgress, 1) != 0)
        {
            return;
        }

        try
        {
            await _persistence.ResetAsync().ConfigureAwait(false);
            await InvokeOnDispatcherAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                _persistenceFailure = string.Empty;
                _persistenceFailureKind = null;
                _persistenceRetryPending = false;
                Pins = [];
                Recents = [];
                var restored = Router.Restore(new(Variant.Landing), selectedEntity: null, focusTarget: null);
                Announce(V2ShellText.Get("V2.Shell.Announce.PreviewReset"), V2Announcement.Polite);
                Act(restored);
                OnPropertyChanged(nameof(PersistenceFailure));
                OnPropertyChanged(nameof(HasPersistenceFailure));
                OnPropertyChanged(nameof(PersistenceRetryPending));
                OnPropertyChanged(nameof(CanRetryPersistence));
                OnPropertyChanged(nameof(PersistenceRetryLabel));
            }, _lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                await InvokeOnDispatcherAsync(() =>
                {
                    _persistenceFailure = V2ShellText.Format(
                        "V2.Shell.Persistence.ResetFailed",
                        CultureInfo.CurrentCulture,
                        StorageFailureDetail(exception));
                    _persistenceFailureKind = V2ShellPersistenceOperationKind.Reset;
                    _persistenceRetryPending = false;
                    OnPropertyChanged(nameof(PersistenceFailure));
                    OnPropertyChanged(nameof(HasPersistenceFailure));
                    OnPropertyChanged(nameof(PersistenceRetryPending));
                    OnPropertyChanged(nameof(CanRetryPersistence));
                    OnPropertyChanged(nameof(PersistenceRetryLabel));
                    Announce(_persistenceFailure, V2Announcement.Assertive);
                }, _lifetimeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
            }
        }
        finally
        {
            Interlocked.Exchange(ref _resetInProgress, 0);
        }
    }

    private void OpenSavedAddress(string address, string automationId)
    {
        var invoker = IsPaletteOpen
            ? _dialogInvoker ?? V2ShellFocusTargets.Palette
            : automationId;
        CloseDialog(restoreInvoker: false);
        Act(Router.NavigateToAddress(address, invoker));
    }

    private void RebuildSavedAddresses()
    {
        RecentItems = Recents.Select((address, index) => new V2SavedAddressViewModel(
            address,
            V2ShellFocusTargets.SavedAddress("recent", index),
            () => OpenSavedAddress(address, V2ShellFocusTargets.SavedAddress("recent", index)))).ToArray();
        ContinueItems = RecentItems
            .Where(item => !string.Equals(item.Address, Router.CurrentAddress, StringComparison.Ordinal))
            .ToArray();
        PinItems = Pins.Select((address, index) => new V2SavedAddressViewModel(
            address,
            V2ShellFocusTargets.SavedAddress("pin", index),
            () => OpenSavedAddress(address, V2ShellFocusTargets.SavedAddress("pin", index)))).ToArray();
        OnPropertyChanged(nameof(RecentItems));
        OnPropertyChanged(nameof(ContinueItems));
        OnPropertyChanged(nameof(PinItems));
        OnPropertyChanged(nameof(HasRecents));
        OnPropertyChanged(nameof(HasContinueItems));
        OnPropertyChanged(nameof(HasNoContinueItems));
        OnPropertyChanged(nameof(HasPins));
        RebuildSuggestions();
    }

    private void RebuildSectionItems()
    {
        var current = Router.Current.Location.Route;
        SectionItems = Registry.VisibleSections(Variant, current)
            .Select(definition => new V2ShellSectionViewModel(
                definition,
                route => GoTo(route, V2ShellFocusTargets.Section(route)))
            {
                // V2 rough package 17: an item open in the Intel workspace is still the Items tab.
                IsCurrent = definition.Id == current || (current == V2Routes.Item && definition.Id == V2Routes.Items),
            })
            .ToArray();
        OnPropertyChanged(nameof(SectionItems));
        OnPropertyChanged(nameof(ShowsSectionNavigation));
    }

    private void RaiseReadinessTargetChanged()
    {
        OnPropertyChanged(nameof(ShowsReadinessTarget));
        OnPropertyChanged(nameof(ReadinessTargetAutomationId));
        OnPropertyChanged(nameof(ReadinessTargetHeading));
        OnPropertyChanged(nameof(ReadinessTargetDetail));
        OnPropertyChanged(nameof(ReadinessTargetAutomationName));
    }

    private void Announce(string text, V2Announcement announcement)
    {
        if (announcement == V2Announcement.Assertive)
        {
            PoliteAnnouncement = string.Empty;
            AssertiveAnnouncement = text;
        }
        else
        {
            AssertiveAnnouncement = string.Empty;
            PoliteAnnouncement = text;
        }

        OnPropertyChanged(nameof(Announcement));
    }

    private V2ShellPreviewState Snapshot() => new()
    {
        Variant = Variant.Token,
        Address = Router.CurrentAddress,
        SelectedEntity = Router.Current.SelectedEntity,
        FocusTarget = PersistentFocusTarget(),
        Recents = Recents,
        Pins = Pins,
        Window = _window,
        CaptureShortcutEnabled = CaptureShortcutEnabled,
        NavigationRail = NavigationRail.ToToken(),
    };

    private string DefaultPageFocusTarget() =>
        Router.Current.Location.Item is null && Router.Current.Location.IntelItem is null
            ? V2ShellRouter.PageHeadingTarget
            : V2ShellRouter.IntelHeadingTarget;

    private string? PersistentFocusTarget()
    {
        if (HasOpenDialog)
        {
            return _dialogInvoker ?? DefaultPageFocusTarget();
        }

        var target = Router.Current.FocusTarget;
        return target is not null &&
            (target.Contains("-dialog", StringComparison.Ordinal) ||
             target.StartsWith("v2-shell-palette-", StringComparison.Ordinal) ||
             target.EndsWith("-palette", StringComparison.Ordinal) && target != V2ShellFocusTargets.Palette ||
             target.StartsWith("v2-shell-command-", StringComparison.Ordinal))
                ? DefaultPageFocusTarget()
                : target;
    }

    /// <summary>Coalesces window-move bursts without coupling persistence to the UI dispatcher.</summary>
    private void QueueSave()
    {
        if (!_disposed)
        {
            _persistence.QueueSave(Snapshot());
        }
    }

    private Task InvokeOnDispatcherAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcherContext is null || ReferenceEquals(SynchronizationContext.Current, _dispatcherContext))
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatcherContext.Post(_ =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
                completion.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, null);
        return completion.Task.WaitAsync(cancellationToken);
    }

    private static string? RequireContextValue(string? value, string parameterName, bool optional)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (optional)
            {
                return null;
            }

            throw new ArgumentException("A device identity is required.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > V2AddressCodec.MaxItemLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("A continuity value must be bounded text without control characters.", parameterName);
        }

        return normalized;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            return new(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        var finalState = Snapshot();
        var resetWasInProgress = Volatile.Read(ref _resetInProgress) != 0;
        _disposed = true;
        _headerTimer?.Dispose();
        _headerTimer = null;
        _runtime.Changed -= RuntimeChanged;
        if (_questSync is not null)
        {
            _questSync.PropertyChanged -= QuestSyncPropertyChanged;
        }
        UnwireRaidClock();
        UnwireLootAutoReturn();
        Router.Navigated -= RouterNavigated;
        if (_stashScan is not null)
        {
            _stashScan.ScanRequested -= StashScanRequested;
        }

        if (_plan is not null)
        {
            _plan.ShowOnMapRequested -= PlanShowOnMapRequested;
            _plan.OpenHideoutRequested -= PlanOpenHideoutRequested;
        }

        _updateNotice?.Dispose();
        ResetPreviewCommand.CanExecuteChanged -= ResetPreviewCanExecuteChanged;
        _persistence.Completed -= PersistenceCompleted;
        foreach (var source in _legacyContextSources)
        {
            source.PropertyChanged -= LegacyContextChanged;
        }

        _legacyContextSources.Clear();
        _lifetime.Cancel();
        await _persistence.DisposeAsync(finalState, suppressFinalSave: resetWasInProgress).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}

public sealed class V2ShellDestinationViewModel : BindableViewModel
{
    private readonly V2DestinationDefinition _definition;
    private bool _isCurrent;
    private bool _hasNotice;
    private bool _showsLabel = true;

    public V2ShellDestinationViewModel(V2DestinationDefinition definition, Action<V2RouteId> navigate)
    {
        _definition = definition;
        NavigateCommand = new DelegateCommand(() => navigate(definition.Route));
    }

    public V2RouteId Route => _definition.Route;
    public string Label => V2ShellText.Get(_definition.LabelKey);
    public string DisplayLabel => IsCurrent ? $"› {Label}" : Label;

    /// <summary>
    /// Whether the rail is wide enough to be writing this destination's name beside its icon.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] Set by the shell when the rail's width changes, rather than read out
    /// of the shell by a binding that walks up out of the item template. An item that knows its
    /// own presentation is also what lets the rail's tooltip carry the name while it is collapsed:
    /// the label is hidden, never removed from the accessible name.
    /// </remarks>
    public bool ShowsLabel
    {
        get => _showsLabel;
        set => SetProperty(ref _showsLabel, value);
    }
    /// <summary>A decorative rail icon. Purely visual — the automation name is <see cref="Label"/>.</summary>
    // Decorative rail icons are small vector shapes built directly in the view (Rectangle/
    // Ellipse, not a font glyph — a Unicode dingbat from an uncovered font block rendered as
    // tofu the first time this shipped). Each of these selects exactly one shape; the
    // accessible name is always Label, never the icon.
    public bool IsRaid => Route == V2Routes.Raid;
    public bool IsIntel => Route == V2Routes.Items;
    public bool IsPlan => Route == V2Routes.Plan;
    public bool IsTeam => Route == V2Routes.Team;
    public bool IsDebrief => Route == V2Routes.Debrief;
    public bool IsSetup => Route == V2Routes.Setup;
    public string AutomationId => V2ShellFocusTargets.Destination(Route);

    /// <summary>
    /// Whether this destination has something waiting behind it.
    /// </summary>
    /// <remarks>
    /// [#294] Today this is only Setup, and only an update. V1 marked its rail the same way and
    /// V2 marked nothing, so a build arrived and said nothing to anybody who did not open
    /// Setup › Updates of their own accord.
    ///
    /// A dot rather than a count or a colour: what is waiting is a yes-or-no, the rail is small,
    /// and the accessible name carries the words for anybody not looking at a dot.
    /// </remarks>
    public bool HasNotice
    {
        get => _hasNotice;
        set
        {
            if (SetProperty(ref _hasNotice, value))
            {
                OnPropertyChanged(nameof(NoticeDescription));
                OnPropertyChanged(nameof(StatusDescription));
            }
        }
    }

    /// <summary>What a screen reader says about the dot, or nothing when there is none.</summary>
    public string NoticeDescription => HasNotice ? V2ShellText.Get("V2.Shell.Nav.UpdateWaiting") : string.Empty;

    /// <summary>
    /// Whether this is the current page, and what is waiting behind it.
    /// </summary>
    /// <remarks>
    /// [#294] One string because a control has one ItemStatus. Announcing the mark instead of the
    /// selection would trade one thing a screen reader was told for another; this adds.
    /// </remarks>
    public string StatusDescription => HasNotice
        ? $"{SelectionDescription}. {NoticeDescription}"
        : SelectionDescription;

    public string SelectionDescription => IsCurrent
        ? V2ShellText.Get("V2.Shell.Nav.Current")
        : V2ShellText.Get("V2.Shell.Nav.NotCurrent");
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(SelectionDescription));
                OnPropertyChanged(nameof(StatusDescription));
                OnPropertyChanged(nameof(DisplayLabel));
            }
        }
    }

    public ICommand NavigateCommand { get; }
}

public sealed class V2ShellSectionViewModel : BindableViewModel
{
    private readonly V2RouteDefinition _definition;
    private bool _isCurrent;

    public V2ShellSectionViewModel(V2RouteDefinition definition, Action<V2RouteId> navigate)
    {
        _definition = definition;
        NavigateCommand = new DelegateCommand(() => navigate(definition.Id));
    }

    public V2RouteId Route => _definition.Id;
    public string Label => V2ShellText.Get(_definition.HeadingKey);
    public string DisplayLabel => IsCurrent ? $"› {Label}" : Label;
    public string AutomationId => V2ShellFocusTargets.Section(Route);
    public string SelectionDescription => IsCurrent
        ? V2ShellText.Get("V2.Shell.Nav.Current")
        : V2ShellText.Get("V2.Shell.Nav.NotCurrent");
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
                OnPropertyChanged(nameof(SelectionDescription));
            }
        }
    }

    public ICommand NavigateCommand { get; }
}

public sealed class V2ShellCommandViewModel(V2ShellCommand definition, ICommand invoke)
{
    public string Label => V2ShellText.Get(definition.LabelKey);
    public string Gesture => definition.Gesture ?? string.Empty;
    public string AutomationId => V2ShellFocusTargets.Command(definition.Id);
    public ICommand InvokeCommand { get; } = invoke;
}

public sealed class V2SavedAddressViewModel(string address, string automationId, Action open)
{
    public string Address { get; } = address;
    public string AutomationId { get; } = automationId;
    public string PaletteAutomationId => $"{AutomationId}-palette";
    public ICommand OpenCommand { get; } = new DelegateCommand(open);
}

public sealed class V2ReadinessCheckViewModel(V2ReadinessCheck check, Action open)
{
    public string Label => check.Label;
    public string Detail => check.Detail;
    public string Status => V2ShellText.Get($"V2.Shell.Status.{check.Status}");
    public string Glyph => check.Status switch
    {
        V2CheckStatus.Ready => "✓",
        V2CheckStatus.NeedsAction => "!",
        V2CheckStatus.Optional => "○",
        V2CheckStatus.Unconfirmed => "?",
        V2CheckStatus.Failed => "×",
        _ => "…",
    };
    public string AutomationId => $"v2-shell-readiness-{check.Id}";
    public string DialogAutomationId => $"v2-shell-readiness-dialog-{check.Id}";
    public string ActionLabel => V2ShellText.Format("V2.Shell.Action.OpenReadiness", CultureInfo.CurrentCulture, Label);
    public string AutomationName => string.Join(". ", ActionLabel, Status, Detail);
    public ICommand OpenCommand { get; } = new DelegateCommand(open);
}

public sealed class V2RecoveryActionViewModel(V2RecoveryAction action, Action invoke)
{
    public string Label => V2ShellText.Get(action.LabelKey);
    public string AutomationId => $"v2-shell-recovery-{action.Id}";
    public ICommand InvokeCommand { get; } = new DelegateCommand(invoke);
}

/// <summary>The fallback used in tests that build the shell without composing the intel service.</summary>
internal sealed class NullItemIntelService : IItemIntelService
{
    public static readonly NullItemIntelService Instance = new();

    public Task<V2ItemIntelResult> GetAsync(string itemId, CancellationToken cancellationToken) =>
        Task.FromResult(V2ItemIntelResult.NotFound(itemId));
}

/// <summary>The fallback used in tests that build the shell without composing the wiki opener.</summary>
internal sealed class NullWikiLinkOpener : IWikiLinkOpener
{
    public static readonly NullWikiLinkOpener Instance = new();

    public bool TryOpen(string? wikiUrl) => false;
}
