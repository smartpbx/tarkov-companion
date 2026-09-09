namespace TarkovCompanion.App.ViewModels;

public sealed record NavigationItem(string Name, string Glyph, bool IsSelected = false);

public sealed record StatusChip(string Label, string Value, string Color);

public sealed class MainWindowViewModel
{
    private MainWindowViewModel(bool demoMode)
    {
        IsDemoMode = demoMode;
        Navigation =
        [
            new("Raid", "⌖", true),
            new("Scanner", "⌁"),
            new("Items", "◇"),
            new("Ammo", "◉"),
            new("Keys", "⌑"),
            new("Flea", "₽"),
            new("Quests", "✓"),
            new("Hideout", "⌂"),
            new("Events", "⚑"),
            new("Loadout", "▦"),
            new("History", "◷"),
            new("Settings", "⚙"),
        ];
        Status =
        [
            new("EFT", demoMode ? "Simulator" : "Not detected", demoMode ? "#77B895" : "#8F9BA6"),
            new("Map", demoMode ? "Customs" : "Unknown", "#56B8C6"),
            new("Raid", demoMode ? "18:34" : "Waiting", "#C6A15B"),
            new("Position", demoMode ? "12s old" : "No evidence", "#8F9BA6"),
            new("Data", demoMode ? "Fixture cache" : "Cache not loaded", "#8F9BA6"),
        ];
    }

    public bool IsDemoMode { get; }

    public IReadOnlyList<NavigationItem> Navigation { get; }

    public IReadOnlyList<StatusChip> Status { get; }

    public string ModeLabel => IsDemoMode ? "Fixture replay" : "External read-only companion";

    public string LastScanName => IsDemoMode ? "Graphics Card" : "No item scanned";

    public string LastScanValue => IsDemoMode ? "1,234,567 ₽  ·  617,284 ₽/slot" : "Press the configured scan hotkey while an item is visible.";

    public string LastScanAdvice => IsDemoMode ? "KEEP — outstanding quest need and high value per slot" : "No recommendation without observed evidence.";

    public static MainWindowViewModel CreateFoundationDemo(bool demoMode) => new(demoMode);
}
