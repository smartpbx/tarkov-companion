namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>
/// What #292 adds to Setup, handed over as one object: the data detail, the About and Data &amp; Privacy
/// pages, and the displays. One hand-off instead of four keeps the shell's constructor to a single new
/// parameter, and keeps them together, which is how the page uses them.
/// </summary>
public sealed class SetupAdminViewModel(
    SetupDataDetailViewModel data,
    SetupInfoPageViewModel about,
    SetupInfoPageViewModel dataPrivacy,
    SetupDisplaysViewModel displays,
    // The report review and send, optional so a shell built without a Settings page still builds.
    SetupReportViewModel? report = null,
    // [#572] The loot auto-return countdown, the last scan's timing and the Loot page's progress line.
    SetupLootScanViewModel? lootScan = null,
    // [#292] Data & Privacy's Local only switch and per-service rows.
    SetupNetworkControlsViewModel? network = null,
    // [#712 0-10] Notifications › Sound.
    SetupSoundViewModel? sound = null)
{
    public SetupDataDetailViewModel Data { get; } = data ?? throw new ArgumentNullException(nameof(data));

    public SetupInfoPageViewModel About { get; } = about ?? throw new ArgumentNullException(nameof(about));

    public SetupInfoPageViewModel DataPrivacy { get; } = dataPrivacy ?? throw new ArgumentNullException(nameof(dataPrivacy));

    public SetupDisplaysViewModel Displays { get; } = displays ?? throw new ArgumentNullException(nameof(displays));

    /// <summary>Diagnostics' read-then-send problem report, or null where there is no Settings page to build it from.</summary>
    public SetupReportViewModel? Report { get; } = report;

    public bool HasReport => Report is not null;

    public SetupLootScanViewModel? LootScan { get; } = lootScan;

    public bool HasLootScan => LootScan is not null;

    public SetupNetworkControlsViewModel? Network { get; } = network;

    public bool HasNetwork => Network is not null;

    public SetupSoundViewModel? Sound { get; } = sound;

    public bool HasSound => Sound is not null;
}
