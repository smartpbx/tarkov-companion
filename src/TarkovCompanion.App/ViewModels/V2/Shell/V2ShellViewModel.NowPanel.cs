using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.App.ViewModels.V2.Shell;

/// <summary>[#712 0-4] What the shell does for the Raid page's Now panel.</summary>
/// <remarks>
/// Three things. The Loot page's verdict is handed to LAST SCAN as it arrives. In a raid, on the
/// Raid page, a loot screenshot no longer jumps to Loot (decision 7 of #712: the verdict stays on
/// the Now panel, and one tap on it opens Loot). And while the Now panel shows, its NOW block is
/// the only clock: the top bar keeps the raid state words and drops the countdown.
/// </remarks>
public sealed partial class V2ShellViewModel
{
    /// <summary>The Now panel is on screen with the page's clock in it.</summary>
    private bool NowPanelOwnsClock =>
        ShowsRaidCockpit && RaidCockpitWorkspace?.NowHost.ShowsNowPanel == true;

    /// <summary>A loot verdict mid-raid stays in LAST SCAN rather than taking the player to Loot.</summary>
    internal bool LootStaysOnNowPanel => IsInRaid && NowPanelOwnsClock;

    private void WireNowPanel()
    {
        if (RaidCockpitWorkspace is not { } cockpit)
        {
            return;
        }

        cockpit.NowHost.LayoutChanged += (_, _) => OnPropertyChanged(nameof(RaidClockLabel));
        if (cockpit.NowHost.Panel is { } panel)
        {
            panel.OpenLootRequested += (_, _) => GoTo(V2Routes.Loot);
        }

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LootScanResult) && LootScanResult is { IsProgressive: false } result)
            {
                cockpit.NowHost.Panel?.ShowLoot(result);
            }
        };
    }
}
