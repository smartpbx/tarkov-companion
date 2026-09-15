using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

public enum V2ShellDialogKind
{
    None = 0,
    Capture,
    Commands,
    Health,
}

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
public sealed class V2ShellViewModel : BindableViewModel, IAsyncDisposable
{
    private readonly IRuntimeStateStore _runtime;
    private readonly V2ShellPreviewStore _preview;
    private readonly V2ShellPersistenceQueue _persistence;
    private readonly TimeProvider _clock;
    private readonly CoalescingDispatch _apply;
    private readonly SynchronizationContext? _dispatcherContext;
    private readonly CancellationTokenSource _lifetime = new();
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
    private bool _disposed;
    private V2WidthClass _widthClass = V2WidthClass.Expanded;
    private V2ShellWindowPlacement? _window;
    private V2NavigationContinuity _continuity = V2NavigationContinuity.Desktop;
    private int _regionIndex = -1;
    private int _resetInProgress;
    private Task? _disposeTask;
    private V2ReadinessCheck? _activeReadinessTarget;
    private V2FocusRequest? _playerActionStateFocus;

    public V2ShellViewModel(
        AppCommandLine options,
        AppDataPaths paths,
        IRuntimeStateStore runtime,
        MainWindowViewModel legacy,
        TimeProvider? clock = null)
    {
        if (!options.UiShell.IsPreview())
        {
            throw new ArgumentException("A V2 shell view model requires a preview launch mode.", nameof(options));
        }

        _runtime = runtime;
        _clock = clock ?? TimeProvider.System;
        Legacy = legacy;
        Registry = V2RouteRegistry.Default;
        Variant = V2ShellVariants.For(options.UiShell);
        Router = new V2ShellRouter(Variant, Registry);
        _preview = new V2ShellPreviewStore(paths.Config, options.UiShell, _clock);
        _persistence = new(_preview.SaveAsync, _preview.ResetAsync);
        PrimaryDestinations = new ObservableCollection<V2ShellDestinationViewModel>(
            Variant.Destinations.Select(destination => new V2ShellDestinationViewModel(
                destination,
                route => GoTo(route, V2ShellFocusTargets.Destination(route)))));
        SetupDestination = new(
            Variant.Setup,
            route => GoTo(route, V2ShellFocusTargets.Destination(route)));

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
        PaletteCommand = new DelegateCommand(() => ToggleDialog(
            V2ShellDialogKind.Commands,
            V2ShellFocusTargets.Palette,
            V2ShellFocusTargets.PaletteDialog));
        AddressCommand = new DelegateCommand(OpenAddress);
        SearchCommand = new AsyncDelegateCommand(SearchAsync);
        PinCommand = new DelegateCommand(TogglePin);
        CopyAddressCommand = new AsyncDelegateCommand(CopyAddressAsync);
        CloseTransientCommand = new DelegateCommand(CloseTransient);
        ResetPreviewCommand = new AsyncDelegateCommand(ResetPreviewAsync);
        PaletteAddressCommand = new DelegateCommand(OpenPaletteAddress);
        Commands = V2ShellCommands.For(Variant, Registry);
        CommandItems = new ObservableCollection<V2ShellCommandViewModel>(
            Commands.Select(command => new V2ShellCommandViewModel(command, CreateCommand(command))));

        var synchronizationContext = SynchronizationContext.Current;
        _dispatcherContext = synchronizationContext?.GetType().Namespace?
            .StartsWith("Avalonia", StringComparison.Ordinal) == true
                ? synchronizationContext
                : null;
        _apply = new(_dispatcherContext, () =>
        {
            if (!_disposed)
            {
                Refresh(announceBackgroundChange: true);
            }
        });

        Router.Navigated += RouterNavigated;
        _runtime.Changed += RuntimeChanged;
        Restore(options.StartPage);
        SynchronizeLegacyRoute();
        RebuildSectionItems();
        Refresh(announceBackgroundChange: false);
    }

    public event EventHandler<V2FocusRequest>? FocusRequested;

    /// <summary>Assigned by the attached view because a clipboard belongs to a top-level window.</summary>
    public Func<string, Task> Clipboard { get; set; } = _ =>
        Task.FromException(new InvalidOperationException("The shell is not attached to a clipboard."));

    public MainWindowViewModel Legacy { get; }
    public V2RouteRegistry Registry { get; }
    public V2ShellVariantDefinition Variant { get; }
    public V2ShellRouter Router { get; }
    public ObservableCollection<V2ShellDestinationViewModel> PrimaryDestinations { get; }
    public V2ShellDestinationViewModel SetupDestination { get; }
    public IReadOnlyList<V2ShellSectionViewModel> SectionItems { get; private set; } = [];
    public IReadOnlyList<V2ShellCommand> Commands { get; }
    public ObservableCollection<V2ShellCommandViewModel> CommandItems { get; }
    public IReadOnlyList<V2ReadinessCheckViewModel> ReadinessItems { get; private set; } = [];
    public IReadOnlyList<V2RecoveryActionViewModel> RecoveryActions { get; private set; } = [];
    public IReadOnlyList<V2SavedAddressViewModel> RecentItems { get; private set; } = [];
    public IReadOnlyList<V2SavedAddressViewModel> ContinueItems { get; private set; } = [];
    public IReadOnlyList<V2SavedAddressViewModel> PinItems { get; private set; } = [];
    public string AppName => V2ShellText.Get("V2.Shell.AppName");
    public string ProvisionalLabel => V2ShellText.Get("V2.Shell.Provisional");
    public string VariantName => V2ShellText.Get(Variant.NameKey);
    public string NavigationRegionName => V2ShellText.Get("V2.Shell.Region.Navigation");
    public string SectionRegionName => V2ShellText.Get("V2.Shell.Region.Sections");
    public string MainRegionName => V2ShellText.Get("V2.Shell.Region.Main");
    public string SetupSectionLabel => V2ShellText.Get("V2.Shell.Region.SetupSection");
    public string BackLabel => V2ShellText.Get("V2.Shell.Command.Back");
    public string ForwardLabel => V2ShellText.Get("V2.Shell.Command.Forward");
    public string CaptureLabel => V2ShellText.Get("V2.Shell.Command.Capture");
    public string HealthLabel => HealthSummary;
    public string PaletteLabel => V2ShellText.Get("V2.Shell.Command.Palette");
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
    public string CaptureShortcutStatus => V2ShellText.Get(
        CaptureShortcutEnabled ? "V2.Shell.Capture.ShortcutOn" : "V2.Shell.Capture.ShortcutOff");
    public string IntelHeading => V2ShellText.Get("V2.Shell.Intel.Heading");
    public string IntelDescription => V2ShellText.Format("V2.Shell.Intel.Item", CultureInfo.CurrentCulture, IntelItem);
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
    public string Title => V2ShellText.Format("V2.Shell.WindowTitle", CultureInfo.CurrentCulture, CurrentHeading, ProvisionalLabel);
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
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
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
    public bool UsesRailNavigation => V2ShellAdaptation.UsesRail(Variant, WidthClass);
    public bool UsesRowNavigation => !UsesRailNavigation;
    public bool ShowsHeaderSetup => Variant.SetupPlacement == V2SetupPlacement.HeaderLink;
    public bool ShowsSeparatedSetup => Variant.SetupPlacement == V2SetupPlacement.LabelledRailSection;
    public bool ShowsHeaderSearch => Variant.SearchPlacement == V2SearchPlacement.Header;
    public bool ShowsWorkspaceSearch => Variant.SearchPlacement == V2SearchPlacement.InsideItemsWorkspace &&
        Router.CurrentDestination == V2Routes.Items;
    public bool ShowsSectionNavigation => SectionItems.Count > 1;
    public bool ShowsLegacyPage => Registry[Router.Current.Location.Route].Content == V2RouteContent.LegacyPage;
    public int ShellBodyRowSpan => ShowsLegacyPage ? 1 : 2;
    public bool ShowsReadiness => Registry[Router.Current.Location.Route].ShowsReadiness;
    public bool ShowsContinue => Registry[Router.Current.Location.Route].ShowsContinue;
    public bool ShowsStatePresenter =>
        Registry[Router.Current.Location.Route].Content == V2RouteContent.StatePresenter ||
        Surface.Kind != V2SurfaceStateKind.Ready;
    public bool ShowsIntel => Router.Current.Location.Route == V2Routes.Item || Router.Current.Location.IntelItem is not null;
    public bool ShowsIntelBeside => ShowsIntel && Variant.IntelPlacement == V2IntelPlacement.BesideCurrentPage &&
        V2ShellAdaptation.IntelFitsBeside(WidthClass);
    public bool ShowsIntelInsteadOfPage => ShowsIntel && Variant.IntelPlacement == V2IntelPlacement.BesideCurrentPage &&
        !V2ShellAdaptation.IntelFitsBeside(WidthClass);
    public bool ShowsPrimaryContent => !ShowsIntel || ShowsIntelBeside;
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
    public ICommand AddressCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand PinCommand { get; }
    public ICommand CopyAddressCommand { get; }
    public ICommand CloseTransientCommand { get; }
    public AsyncDelegateCommand ResetPreviewCommand { get; }
    public ICommand PaletteAddressCommand { get; }

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

        Legacy.Items.SearchQuery = query;
        await Legacy.Items.SearchCommand.ExecuteAsync().ConfigureAwait(true);
        Announce(Legacy.Items.SearchStatus, V2Announcement.Polite);
        FocusRequested?.Invoke(this, new(SearchFocusTarget, V2FocusReason.Invoker));
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

        ExecuteCommand(command, focusedAutomationId);
        return true;
    }

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
            if (requestedAddress is not null)
            {
                throw new ArgumentException(parsed.Failure);
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
        _activeReadinessTarget = null;
        SynchronizeLegacyRoute();
        CurrentAddress = Router.CurrentAddress;
        Recents = Recents
            .Where(address => !string.Equals(address, CurrentAddress, StringComparison.Ordinal))
            .Prepend(CurrentAddress)
            .Take(V2ShellPreviewState.MaxRecents)
            .ToArray();
        RebuildSavedAddresses();
        RebuildSectionItems();
        _playerActionStateFocus = null;
        Refresh(
            announceBackgroundChange: false,
            playerAction: change.Kind != V2NavigationKind.Restore);
        QueueSave();
    }

    private void SynchronizeLegacyRoute()
    {
        if (Registry[Router.Current.Location.Route].LegacyPage is { } page)
        {
            Legacy.Navigate(page);
        }
    }

    private void RuntimeChanged(object? sender, EventArgs eventArgs) => _apply.Request();

    private void Refresh(bool announceBackgroundChange, bool playerAction = false)
    {
        var snapshot = _runtime.Current;
        var continuity = Volatile.Read(ref _continuity);
        var selectedTask = Legacy.Quests.SelectedTask;
        var selectedObjective = selectedTask?.Objectives.FirstOrDefault(objective => objective.Model.IsPinned)?.ObjectiveId;
        var priorScan = snapshot.Scan.Succeeded
            ? snapshot.Scan.CanonicalItemId
            : continuity.PriorScan ?? Router.Context.PriorScan;
        Router.UpdateContext(new(
            snapshot.Profile?.Name,
            snapshot.Raid.MapId,
            continuity.PlanId ?? selectedTask?.TaskId ?? Router.Context.PlanId,
            priorScan,
            continuity.InitiatingDevice)
        {
            ProfileId = snapshot.Profile?.Id.ToString("D", CultureInfo.InvariantCulture),
            ProfileMode = snapshot.Profile?.GameMode.ToString(),
            RaidId = snapshot.Raid.RaidId?.ToString("D", CultureInfo.InvariantCulture),
            RaidState = snapshot.Raid.State.ToString(),
            ObjectiveId = continuity.ObjectiveId ?? selectedObjective ?? Router.Context.ObjectiveId,
            TeamMemberKeys = snapshot.Squad.Members.Select(member => member.Key).Take(5).ToArray(),
            CaptureCorrelationId = continuity.CaptureCorrelationId ?? Router.Context.CaptureCorrelationId,
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
        }

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

        _surfaceInitialized = true;
        foreach (var destination in PrimaryDestinations.Append(SetupDestination))
        {
            destination.IsCurrent = Router.CurrentDestination == destination.Route;
        }
        foreach (var section in SectionItems)
        {
            section.IsCurrent = Router.Current.Location.Route == section.Route;
        }

        RaisePresentationChanged();
        if (announceBackgroundChange && (readinessChanged || recoveryChanged) &&
            Router.Current.FocusTarget is { } focusedTarget && HasRenderedFocusTarget(focusedTarget))
        {
            // A runtime update may replace a checklist row with a new immutable row. Restore the
            // same control after the binding applies; this preserves focus instead of choosing it.
            FocusRequested?.Invoke(this, new(focusedTarget, V2FocusReason.Restored));
        }
    }

    private bool HasRenderedFocusTarget(string automationId) =>
        ReadinessItems.Any(item =>
            string.Equals(item.AutomationId, automationId, StringComparison.Ordinal) ||
            string.Equals(item.DialogAutomationId, automationId, StringComparison.Ordinal)) ||
        RecoveryActions.Any(item => string.Equals(item.AutomationId, automationId, StringComparison.Ordinal)) ||
        _activeReadinessTarget is not null &&
        string.Equals(ReadinessTargetAutomationId, automationId, StringComparison.Ordinal);

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
            case "sync":
                Legacy.Settings.SyncCommand.Execute(null);
                Announce(V2ShellText.Get("V2.Shell.Announce.SyncStarted"), V2Announcement.Polite);
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

        // A readiness row names a specific thing to inspect. Several rows share Setup, and a
        // same-page Navigate result only focuses the generic page heading, so preserve the row's
        // identity in a visible action target and focus that target in every case.
        _activeReadinessTarget = check;
        RaiseReadinessTargetChanged();
        FocusRequested?.Invoke(this, new(
            V2ShellFocusTargets.ReadinessTarget(check.Id),
            V2FocusReason.PageHeading));
    }

    private void RaisePresentationChanged()
    {
        foreach (var property in new[]
        {
            nameof(Readiness), nameof(ReadinessItems), nameof(Surface), nameof(SurfaceTitle), nameof(SurfaceRemainder),
            nameof(SurfaceGlyph), nameof(SurfaceAutomationName), nameof(RecoveryActions), nameof(CurrentHeading), nameof(Title),
            nameof(ReadinessSummary), nameof(HealthSummary), nameof(HealthLabel),
            nameof(ShowsWorkspaceSearch), nameof(ShowsLegacyPage), nameof(ShellBodyRowSpan),
            nameof(ShowsReadiness), nameof(ShowsContinue),
            nameof(ShowsStatePresenter), nameof(ShowsIntel), nameof(ShowsIntelBeside), nameof(ShowsIntelInsteadOfPage),
            nameof(ShowsPrimaryContent), nameof(IntelItem), nameof(IntelDescription), nameof(IntelColumn), nameof(IntelColumnSpan),
            nameof(SurfaceIsReady), nameof(SurfaceIsLoading), nameof(SurfaceIsUnknown), nameof(SurfaceIsOffline),
            nameof(SurfaceIsStale), nameof(SurfaceIsPartial), nameof(SurfaceIsDenied), nameof(SurfaceIsFailed),
            nameof(SurfacePatternDashed), nameof(SurfacePatternDotted), nameof(SurfacePatternDouble),
            nameof(SectionItems), nameof(ShowsSectionNavigation),
            nameof(ShowsReadinessTarget), nameof(ReadinessTargetAutomationId), nameof(ReadinessTargetHeading),
            nameof(ReadinessTargetDetail), nameof(ReadinessTargetAutomationName),
        })
        {
            OnPropertyChanged(property);
        }
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
            case V2ShellCommandKind.NextRegion: MoveRegion(reverse: false); break;
            case V2ShellCommandKind.PreviousRegion: MoveRegion(reverse: true); break;
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
        if (_disposed || Interlocked.Exchange(ref _resetInProgress, 1) != 0)
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

                Pins = [];
                Recents = [];
                var restored = Router.Restore(new(Variant.Landing), selectedEntity: null, focusTarget: null);
                Announce(V2ShellText.Get("V2.Shell.Announce.PreviewReset"), V2Announcement.Polite);
                Act(restored);
            }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
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
    }

    private void RebuildSectionItems()
    {
        var current = Router.Current.Location.Route;
        SectionItems = Registry.VisibleSections(Variant, current)
            .Select(definition => new V2ShellSectionViewModel(
                definition,
                route => GoTo(route, V2ShellFocusTargets.Section(route)))
            {
                IsCurrent = definition.Id == current,
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
        _runtime.Changed -= RuntimeChanged;
        Router.Navigated -= RouterNavigated;
        _lifetime.Cancel();
        await _persistence.DisposeAsync(finalState, suppressFinalSave: resetWasInProgress).ConfigureAwait(false);
    }
}

public sealed class V2ShellDestinationViewModel : BindableViewModel
{
    private readonly V2DestinationDefinition _definition;
    private bool _isCurrent;

    public V2ShellDestinationViewModel(V2DestinationDefinition definition, Action<V2RouteId> navigate)
    {
        _definition = definition;
        NavigateCommand = new DelegateCommand(() => navigate(definition.Route));
    }

    public V2RouteId Route => _definition.Route;
    public string Label => V2ShellText.Get(_definition.LabelKey);
    public string DisplayLabel => IsCurrent ? $"› {Label}" : Label;
    public string AutomationId => V2ShellFocusTargets.Destination(Route);
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
