using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#712 0-4] The Now panel's states for a render, each reached through the real services.
/// </summary>
/// <remarks>
/// <c>--now-outcome died|survived</c> after <c>--raid-left</c>: the one-tap answer reported to the
/// situation. <c>--now-menu</c>: the raid state back in the menu. <c>--now-loot-demo</c>: the
/// Loot Scan demo result handed to the shell the way a capture hands it, which in a raid now
/// stays on the Now panel (decision 7). <c>--now-more</c>: the More drawer open. Dev tool only.
/// </remarks>
internal static class NowPanelDemo
{
    public static void Apply(IServiceProvider services, V2ShellViewModel? shell, string[] args, Action<int> pump)
    {
        var store = services.GetRequiredService<IRuntimeStateStore>();
        if (args.Contains("--now-menu"))
        {
            store.Update(snapshot => snapshot with { Raid = snapshot.Raid with { State = RaidLifecycleState.Menu, UpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-30) } });
            pump(20);
        }

        if (Option(args, "--now-outcome") is { } outcome && services.GetService<SituationService>() is { } situation &&
            store.Current.Raid.RaidId is { } raidId)
        {
            var value = outcome.Equals("died", StringComparison.OrdinalIgnoreCase) ? SituationOutcome.Died : SituationOutcome.Survived;
            situation.ReportOutcome(raidId, new(value, Confidence.Certain, SituationSource.Player, DateTimeOffset.UtcNow, "You answered the question."));
            pump(20);
        }

        if (args.Contains("--now-loot-demo") && shell is not null)
        {
            var profile = store.Current.Profile ?? throw new InvalidOperationException("The demo composition has no profile.");
            var scope = new InventoryProfileScope(profile.Id, profile.ProfileGeneration, profile.GameMode.ToString());
            shell.ShowLootScanResult(new LootScanViewModel(ScanDemo.LootResult(scope)));
            pump(40);
        }

        if (args.Contains("--now-more") && shell?.RaidCockpit is RaidCockpitViewModel cockpit)
        {
            cockpit.NowHost.OpenMore();
            pump(20);
        }

        // [#712 0-5] --now-squad-ping: a squadmate's ping arrives through the runtime store, the
        // way the relay's snapshot lands, so the row flash and the map edge come from the real path.
        if (args.Contains("--now-squad-ping") &&
            store.Current.Group.Members.FirstOrDefault(member => member.Position is not null && member.MapId is not null) is { } member)
        {
            var at = member.Position!.Value;
            var ping = new TarkovCompanion.Application.Services.Group.GroupPingView(
                990_001, member.Name, member.MapId!, at.X + 15, at.Y, at.Z - 15, null, DateTimeOffset.UtcNow);
            store.Update(snapshot => snapshot with { Group = snapshot.Group with { Pings = [.. snapshot.Group.Pings, ping] } });
            pump(20);
            Console.WriteLine($"Squad ping from {member.Name}: edge={(shell?.RaidCockpit as RaidCockpitViewModel)?.SquadEdge.Edge}");
        }

        if (shell?.RaidCockpit is RaidCockpitViewModel shown)
        {
            Console.WriteLine($"Now panel: shown={shown.NowHost.ShowsNowPanel} phase={shown.NowHost.Panel?.Situation.Phase.Value} " +
                $"clock='{shown.NowHost.Panel?.State.NowHeadline}' you='{shown.NowHost.Panel?.State.YouWhere}' route={shell.Router.Current.Location.Route}");
        }
    }

    /// <summary>
    /// [#712 0-7] <c>--now-probe</c>: the room the Raid page gives the Now panel's blocks, in
    /// DIPs, at this window size and text scale. The glance ratchet (NowPanelGlanceTests) lays the
    /// panel out in exactly this room; re-measure with this when the shell's chrome changes.
    /// </summary>
    public static void Probe(Avalonia.Controls.Window window, string[] args)
    {
        if (!args.Contains("--now-probe"))
        {
            return;
        }

        foreach (var view in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TarkovCompanion.App.Views.V2.Now.NowPanelView>())
        {
            var blocks = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.StackPanel>(view, "Blocks");
            Console.WriteLine($"Now probe: panel {view.Bounds.Width:0}x{view.Bounds.Height:0}, blocks {blocks?.Bounds.Width:0}x{blocks?.Bounds.Height:0} " +
                $"(desired {blocks?.DesiredSize.Height:0}), visible={view.IsEffectivelyVisible}");
        }
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
