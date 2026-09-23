using TarkovCompanion.App.ViewModels.V2.LootScan;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

public sealed partial class V2ShellViewModel
{
    private LootScanHistoryViewModel? _lootHistory;

    /// <summary>#274: saved loot scans, for the Loot page's "Last scans" and a reopened scan.</summary>
    public LootScanHistoryViewModel? LootHistory
    {
        get => _lootHistory;
        set
        {
            if (!ReferenceEquals(_lootHistory, value))
            {
                _lootHistory = value;
                OnPropertyChanged();
            }
        }
    }
}
