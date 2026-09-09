using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TarkovCompanion.App.ViewModels.Maps;

namespace TarkovCompanion.App.ViewModels;

public abstract class BindableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class DelegateCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}

public sealed class NavigationItem : BindableViewModel
{
    private bool _isSelected;

    internal NavigationItem(string name, string glyph, PageViewModel page, Action<NavigationItem> select)
    {
        Name = name;
        Glyph = glyph;
        Page = page;
        SelectCommand = new DelegateCommand(() => select(this));
    }

    public string Name { get; }

    public string Glyph { get; }

    public PageViewModel Page { get; }

    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

public sealed record StatusChip(string Label, string Value, string Evidence, string Color);

public abstract record PageViewModel(string Title, string Description, string Evidence);

public sealed record RaidPageViewModel(MapViewModel Map)
    : PageViewModel("Customs raid", "Map-first route planning from last-known evidence", "Position · screenshot · 12 seconds old");

public sealed record ScannerPageViewModel()
    : PageViewModel("Scanner", "Review a user-triggered capture before acting on it", "Confidence · 96% · observed 8 seconds ago");

public sealed record ItemsPageViewModel()
    : PageViewModel("Items", "Compare value, progression needs, and storage cost", "Prices · fixture cache · synced 14 minutes ago");

public sealed record AmmoPageViewModel()
    : PageViewModel("Ammo", "Caliber comparisons tuned to your current trader access", "Profile · level 28 · traders confirmed");

public sealed record KeysPageViewModel()
    : PageViewModel("Keys", "Uses, routes, and current progression value", "Reference · structured game data + field notes");

public sealed record FleaPageViewModel()
    : PageViewModel("Flea", "Price context for deliberate decisions outside raid", "Market data · 14 minutes old");

public sealed record QuestsPageViewModel()
    : PageViewModel("Quests", "Track only the objectives that change item decisions", "Profile · manual progress · updated today");

public sealed record HideoutPageViewModel()
    : PageViewModel("Hideout", "See what each upgrade consumes before selling", "Profile · 3 stations ready to build");

public sealed record EventsPageViewModel()
    : PageViewModel("Events", "Explicit rules, progress, and collection states", "Event reference · ends in 2 days 7 hours");

public sealed record LoadoutPageViewModel()
    : PageViewModel("Loadout", "Check armor, ammunition, and cost as one plan", "Evaluation · fixture profile · no live inventory access");

public sealed record HistoryPageViewModel()
    : PageViewModel("History", "A local timeline of observed raid evidence", "Local database · last entry 22 minutes ago");

public sealed record SettingsPageViewModel()
    : PageViewModel("Settings & diagnostics", "Configure explicit access and verify every integration", "Diagnostics · 5 passing · 1 needs attention");

public sealed class MainWindowViewModel : BindableViewModel
{
    private PageViewModel _currentPage;

    private MainWindowViewModel(bool demoMode)
    {
        Map = MapViewModel.CreateDefault();
        IsDemoMode = demoMode;

        Navigation =
        [
            CreateNavigation("Raid", "⌖", new RaidPageViewModel(Map)),
            CreateNavigation("Scanner", "⌁", new ScannerPageViewModel()),
            CreateNavigation("Items", "◇", new ItemsPageViewModel()),
            CreateNavigation("Ammo", "◉", new AmmoPageViewModel()),
            CreateNavigation("Keys", "⌑", new KeysPageViewModel()),
            CreateNavigation("Flea", "₽", new FleaPageViewModel()),
            CreateNavigation("Quests", "✓", new QuestsPageViewModel()),
            CreateNavigation("Hideout", "⌂", new HideoutPageViewModel()),
            CreateNavigation("Events", "⚑", new EventsPageViewModel()),
            CreateNavigation("Loadout", "▦", new LoadoutPageViewModel()),
            CreateNavigation("History", "◷", new HistoryPageViewModel()),
            CreateNavigation("Settings", "⚙", new SettingsPageViewModel()),
        ];

        _currentPage = Navigation[0].Page;
        Navigation[0].IsSelected = true;
        Status = CreateStatus(demoMode);
    }

    public bool IsDemoMode { get; }

    public IReadOnlyList<NavigationItem> Navigation { get; }

    public IReadOnlyList<StatusChip> Status { get; }

    public PageViewModel CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public MapViewModel Map { get; }

    public string ModeLabel => IsDemoMode ? "Fixture replay · Linux-safe demo" : "External read-only companion";

    public string LastScanName => IsDemoMode ? "Graphics Card" : "No item scanned";

    public string LastScanValue => IsDemoMode ? "1,234,567 ₽ · 617,284 ₽ per slot" : "Press the configured hotkey while an item is visible.";

    public string LastScanAdvice => IsDemoMode ? "Keep · outstanding quest need and exceptional slot value" : "No recommendation without observed evidence.";

    public string LastScanEvidence => IsDemoMode ? "96% confidence · OCR + icon agreement · 8s old" : "Waiting for a user-triggered capture";

    public static MainWindowViewModel CreateFoundationDemo(bool demoMode) => new(demoMode);

    public bool Navigate(string pageName)
    {
        var target = Navigation.FirstOrDefault(item => string.Equals(item.Name, pageName, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return false;
        }

        Select(target);
        return true;
    }

    private NavigationItem CreateNavigation(string name, string glyph, PageViewModel page) =>
        new(name, glyph, page, Select);

    private void Select(NavigationItem selected)
    {
        foreach (var item in Navigation)
        {
            item.IsSelected = ReferenceEquals(item, selected);
        }

        CurrentPage = selected.Page;
    }

    private static IReadOnlyList<StatusChip> CreateStatus(bool demoMode) =>
    [
        new("EFT", demoMode ? "Simulator" : "Not detected", demoMode ? "Window evidence" : "No window evidence", demoMode ? "#77B895" : "#8F9BA6"),
        new("Map", demoMode ? "Customs" : "Unknown", demoMode ? "Log evidence · 18s old" : "Waiting for raid evidence", "#56B8C6"),
        new("Raid", demoMode ? "18:34" : "Waiting", demoMode ? "Elapsed · estimated" : "No active session", "#C6A15B"),
        new("Time", demoMode ? "21:42:16" : "—", demoMode ? "Local display · 1s old" : "Starts with raid", "#C6D0D8"),
        new("Position", demoMode ? "Train Yard" : "No evidence", demoMode ? "Screenshot · 12s old" : "Create a screenshot to update", "#56B8C6"),
        new("Data", demoMode ? "Synced" : "Cache not loaded", demoMode ? "18,462 records · 14m old" : "Run data sync", "#77B895"),
        new("Scan", demoMode ? "Ready" : "Unavailable", demoMode ? "Ctrl+Shift+S" : "Configure a hotkey", "#C6A15B"),
    ];
}
