using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.App.ViewModels;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>The one provisional presentation over the already-composed V1 runtime.</summary>
/// <remarks>
/// This owns only preview navigation and chrome.  It deliberately receives the existing V1 view
/// model instead of composing services again: starting a preview must not create a second watcher,
/// database connection, refresh loop, or capture path.
/// </remarks>
public sealed class V2ShellViewModel : BindableViewModel, IDisposable
{
    private readonly IRuntimeStateStore _runtime;
    private readonly V2ShellPreviewStore _preview;
    private readonly TimeProvider _clock;
    private string _address = string.Empty;
    private string _announcement = string.Empty;
    private string _searchText = string.Empty;
    private bool _isPaletteOpen;
    private bool _isCaptureOpen;
    private bool _isHealthOpen;
    private bool _captureShortcutEnabled = true;
    private bool _disposed;

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
        Destinations = new ObservableCollection<V2ShellDestinationViewModel>(
            Variant.Destinations.Append(Variant.Setup).Select(destination =>
                new V2ShellDestinationViewModel(destination, GoTo)));
        Commands = new ObservableCollection<V2ShellCommand>(V2ShellCommands.For(Variant));

        Router.Navigated += RouterNavigated;
        _runtime.Changed += RuntimeChanged;
        Restore(options.StartPage);
        Refresh();
    }

    public event EventHandler<V2FocusRequest>? FocusRequested;

    public MainWindowViewModel Legacy { get; }
    public V2RouteRegistry Registry { get; }
    public V2ShellVariantDefinition Variant { get; }
    public V2ShellRouter Router { get; }
    public ObservableCollection<V2ShellDestinationViewModel> Destinations { get; }
    public ObservableCollection<V2ShellCommand> Commands { get; }
    public string ProvisionalLabel => V2ShellText.Get("V2.Shell.Provisional");
    public string VariantName => V2ShellText.Get(Variant.NameKey);
    public string Title => V2ShellText.Format("V2.Shell.WindowTitle", CultureInfo.CurrentCulture, CurrentHeading, ProvisionalLabel);
    public string CurrentHeading => V2ShellText.Get(Registry[Router.Current.Location.Route].HeadingKey);
    public string CurrentAddress { get => _address; set => SetProperty(ref _address, value); }
    public string Announcement { get => _announcement; private set => SetProperty(ref _announcement, value); }
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public bool IsPaletteOpen { get => _isPaletteOpen; private set => SetProperty(ref _isPaletteOpen, value); }
    public bool IsCaptureOpen { get => _isCaptureOpen; private set => SetProperty(ref _isCaptureOpen, value); }
    public bool IsHealthOpen { get => _isHealthOpen; private set => SetProperty(ref _isHealthOpen, value); }
    public bool CaptureShortcutEnabled { get => _captureShortcutEnabled; private set => SetProperty(ref _captureShortcutEnabled, value); }
    public bool ShowsLegacyPage => Registry[Router.Current.Location.Route].Content == V2RouteContent.LegacyPage;
    public bool ShowsReadiness => Registry[Router.Current.Location.Route].ShowsReadiness;
    public bool ShowsStatePresenter => Registry[Router.Current.Location.Route].Content == V2RouteContent.StatePresenter;
    public bool ShowsIntel => Router.Current.Location.Route == V2Routes.Item || Router.Current.Location.IntelItem is not null;
    public V2ReadinessSummary Readiness { get; private set; } = new([]);
    public V2SurfaceState Surface { get; private set; } = V2SurfaceStateResolver.State(V2SurfaceStateKind.Loading, string.Empty, "V2.Shell.Remainder.Local");
    public string SurfaceTitle => V2ShellText.Get(Surface.Policy.WordingKey);
    public string SurfaceRemainder => V2ShellText.Get(Surface.RemainderKey);
    public IReadOnlyList<string> Recents { get; private set; } = [];
    public IReadOnlyList<string> Pins { get; private set; } = [];
    public ICommand BackCommand => new DelegateCommand(Back);
    public ICommand ForwardCommand => new DelegateCommand(Forward);
    public ICommand CaptureCommand => new DelegateCommand(ToggleCapture);
    public ICommand HealthCommand => new DelegateCommand(ToggleHealth);
    public ICommand PaletteCommand => new DelegateCommand(TogglePalette);
    public ICommand AddressCommand => new DelegateCommand(OpenAddress);
    public ICommand SearchCommand => new DelegateCommand(Search);
    public ICommand PinCommand => new DelegateCommand(TogglePin);
    public ICommand CloseTransientCommand => new DelegateCommand(CloseTransient);

    public void GoTo(V2RouteId route) => Act(Router.Navigate(route, $"v2-nav-{route}"));
    public void Back() => Act(Router.Back());
    public void Forward() => Act(Router.Forward());
    public void ToggleCapture() => IsCaptureOpen = !IsCaptureOpen;
    public void ToggleHealth() => IsHealthOpen = !IsHealthOpen;
    public void TogglePalette() => IsPaletteOpen = !IsPaletteOpen;
    public void CloseTransient()
    {
        if (IsCaptureOpen) { IsCaptureOpen = false; return; }
        if (IsHealthOpen) { IsHealthOpen = false; return; }
        if (IsPaletteOpen) { IsPaletteOpen = false; return; }
        if (Router.Current.Location.IntelItem is not null || Router.Current.Location.Route == V2Routes.Item) { Act(Router.CloseIntel()); }
    }

    public void OpenAddress()
    {
        Act(Router.NavigateToAddress(CurrentAddress, "v2-shell-address"));
        IsPaletteOpen = false;
    }

    public void Search()
    {
        if (V2AddressCodec.IsValidItem(SearchText))
        {
            Act(Router.OpenIntel(SearchText, "v2-shell-search"));
            return;
        }

        GoTo(V2Routes.Items);
    }

    public void TogglePin()
    {
        var address = Router.CurrentAddress;
        Pins = Pins.Contains(address, StringComparer.Ordinal) ? Pins.Where(pin => pin != address).ToArray() : [address, .. Pins];
        Announcement = V2ShellText.Get(Pins.Contains(address, StringComparer.Ordinal) ? "V2.Shell.Announce.Pinned" : "V2.Shell.Announce.Unpinned");
        Save();
        OnPropertyChanged(nameof(Pins));
    }

    public void ToggleCaptureShortcut()
    {
        CaptureShortcutEnabled = !CaptureShortcutEnabled;
        Announcement = V2ShellText.Get(CaptureShortcutEnabled ? "V2.Shell.Announce.ShortcutOn" : "V2.Shell.Announce.ShortcutOff");
        Save();
    }

    /// <summary>Runs a documented, window-local command. No chord is ever forwarded to EFT.</summary>
    public bool HandleKey(V2KeyChord chord)
    {
        var command = V2ShellCommands.Match(Commands, chord, CaptureShortcutEnabled);
        if (command is null) return false;
        switch (command.Kind)
        {
            case V2ShellCommandKind.Navigate: GoTo(command.Route!.Value); break;
            case V2ShellCommandKind.Back: Back(); break;
            case V2ShellCommandKind.Forward: Forward(); break;
            case V2ShellCommandKind.ToggleCapture: ToggleCapture(); break;
            case V2ShellCommandKind.ToggleHealth: ToggleHealth(); break;
            case V2ShellCommandKind.TogglePalette: TogglePalette(); break;
            case V2ShellCommandKind.FocusSearch: GoTo(V2Routes.Items); break;
            case V2ShellCommandKind.CopyAddress: Announcement = V2ShellText.Get("V2.Shell.Announce.Copied"); break;
            case V2ShellCommandKind.CloseTransient: CloseTransient(); break;
            case V2ShellCommandKind.TogglePin: TogglePin(); break;
            case V2ShellCommandKind.ToggleCaptureShortcut: ToggleCaptureShortcut(); break;
            default: return false;
        }
        return true;
    }

    public void RecordWindow(double width, double height, double left, double top, bool maximized)
    {
        var state = Snapshot() with { Window = new(width, height, left, top, maximized) };
        _ = _preview.SaveAsync(state, CancellationToken.None);
    }

    public V2ShellWindowPlacement? RestoredWindow { get; private set; }

    private void Restore(string? requestedAddress)
    {
        var loaded = _preview.Load();
        CaptureShortcutEnabled = loaded.State.CaptureShortcutEnabled;
        Pins = loaded.State.Pins;
        Recents = loaded.State.Recents;
        RestoredWindow = loaded.State.Window;
        if (loaded.Outcome == V2PreviewLoadOutcome.ResetAfterCorruption)
        {
            Announcement = V2ShellText.Get("V2.Shell.Announce.PreviewCorrupt");
        }

        if (requestedAddress is not null)
        {
            var result = Router.NavigateToAddress(requestedAddress, "process-start");
            if (!result.Succeeded) throw new ArgumentException(result.Failure);
        }
        else if (loaded.State.Address is { } address)
        {
            Router.Restore(Router.Addresses.Parse(address).Location ?? Router.Current.Location, loaded.State.SelectedEntity, loaded.State.FocusTarget);
        }
    }

    private void RouterNavigated(object? sender, V2NavigationChange change)
    {
        var route = Registry[change.Current.Location.Route];
        if (route.LegacyPage is { } page) Legacy.Navigate(page);
        CurrentAddress = Router.CurrentAddress;
        Recents = [CurrentAddress, .. Recents.Where(address => !string.Equals(address, CurrentAddress, StringComparison.Ordinal))].Take(V2ShellPreviewState.MaxRecents).ToArray();
        Refresh();
        Save();
        FocusRequested?.Invoke(this, change.Focus);
    }

    private void RuntimeChanged(object? sender, EventArgs eventArgs) => Refresh();

    private void Refresh()
    {
        var snapshot = _runtime.Current;
        Router.UpdateContext(new(snapshot.Profile?.Name, snapshot.Raid.MapId, null, null, V2NavigationContext.ThisDesktop));
        Readiness = V2Readiness.Evaluate(snapshot, _clock.GetUtcNow(), CultureInfo.CurrentCulture);
        Surface = V2SurfaceStateResolver.Resolve(Registry[Router.Current.Location.Route], snapshot, _clock.GetUtcNow(), CultureInfo.CurrentCulture);
        CurrentAddress = Router.CurrentAddress;
        foreach (var destination in Destinations) destination.IsCurrent = Router.CurrentDestination == destination.Route;
        OnPropertyChanged(nameof(Readiness)); OnPropertyChanged(nameof(Surface)); OnPropertyChanged(nameof(SurfaceTitle)); OnPropertyChanged(nameof(SurfaceRemainder)); OnPropertyChanged(nameof(CurrentHeading));
        OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(ShowsLegacyPage)); OnPropertyChanged(nameof(ShowsReadiness));
        OnPropertyChanged(nameof(ShowsStatePresenter)); OnPropertyChanged(nameof(ShowsIntel));
    }

    private void Act(V2NavigationResult result)
    {
        if (!result.Succeeded) { Announcement = result.Failure ?? string.Empty; return; }
        if (result.Focus is { } focus) FocusRequested?.Invoke(this, focus);
    }

    private V2ShellPreviewState Snapshot() => new()
    {
        Variant = Variant.Token, Address = Router.CurrentAddress, SelectedEntity = Router.Current.SelectedEntity,
        FocusTarget = Router.Current.FocusTarget, Recents = Recents, Pins = Pins, CaptureShortcutEnabled = CaptureShortcutEnabled,
    };

    private void Save() => _ = _preview.SaveAsync(Snapshot(), CancellationToken.None);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.Changed -= RuntimeChanged;
        Router.Navigated -= RouterNavigated;
        Save();
    }
}

public sealed class V2ShellDestinationViewModel(V2DestinationDefinition definition, Action<V2RouteId> navigate) : BindableViewModel
{
    private bool _isCurrent;
    public V2RouteId Route => definition.Route;
    public string Label => V2ShellText.Get(definition.LabelKey);
    public bool IsCurrent { get => _isCurrent; set => SetProperty(ref _isCurrent, value); }
    public ICommand NavigateCommand { get; } = new DelegateCommand(() => navigate(definition.Route));
}
