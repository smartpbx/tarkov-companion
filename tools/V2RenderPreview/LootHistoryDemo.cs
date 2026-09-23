using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Loot;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// #274: saved loot scans for a render. The live demo result is saved through the real
/// <see cref="LootScanHistoryViewModel"/> and store; two earlier scans of the same raid are saved
/// beside it so "Last scans" and Debrief have a history to list. Dev tool only; never shipped.
/// </summary>
internal static class LootHistoryDemo
{
    /// <summary>--loot-history-demo [--loot-history-open N]: after --loot-demo.</summary>
    public static void RunLoot(
        IServiceProvider services,
        V2ShellViewModel shell,
        Action<Task> drain,
        Action<int> pump,
        string? openIndex)
    {
        var history = services.GetRequiredService<LootScanHistoryViewModel>();
        shell.LootHistory = history;
        var raid = services.GetRequiredService<IRuntimeStateStore>().Current.Raid;
        var live = shell.LootScanResult ?? new LootScanViewModel(ScanDemo.LootResult(Scope(services)));
        live.History = history;
        var store = services.GetRequiredService<ILootScanHistoryStore>();
        foreach (var earlier in Earlier(LootScanHistorySnapshot.From(live, raid)))
        {
            drain(store.SaveAsync(earlier, CancellationToken.None));
        }

        drain(history.RecordAsync(live));
        if (openIndex is not null && int.TryParse(openIndex, out var index) && index < history.Rows.Count)
        {
            history.Open(history.Rows[index].Scan);
        }

        pump(20);
    }

    /// <summary>--debrief-loot-demo [--debrief-loot-wrong]: three saved scans on the seeded raid.</summary>
    public static void SeedDebrief(IServiceProvider services, Guid raidId, Action<Task> drain, bool markOneWrong)
    {
        var shown = new LootScanViewModel(ScanDemo.LootResult(Scope(services)));
        var latest = LootScanHistorySnapshot.From(shown, null) with { RaidId = raidId, MapId = "customs" };
        var store = services.GetRequiredService<ILootScanHistoryStore>();
        var scans = Earlier(latest).Append(latest).ToArray();
        foreach (var scan in scans)
        {
            drain(store.SaveAsync(scan, CancellationToken.None));
        }

        if (markOneWrong)
        {
            var history = services.GetRequiredService<TarkovCompanion.Infrastructure.Persistence.Repositories.SqliteRaidHistoryService>();
            var correctedUtc = DateTimeOffset.UtcNow;
            drain(history.RecordEventAsync(
                raidId,
                TarkovCompanion.Application.Services.Raids.RaidScanCorrection.EventType,
                correctedUtc,
                new TarkovCompanion.Application.Services.Raids.RaidScanCorrection(scans[0].CorrectionId, true, correctedUtc).ToPayload(),
                CancellationToken.None));
        }
    }

    private static IEnumerable<SavedLootScan> Earlier(SavedLootScan latest)
    {
        yield return latest with
        {
            ScanId = latest.ScanId + "-earliest",
            EvaluatedUtc = latest.EvaluatedUtc.AddMinutes(-31),
            Items = latest.Items.Where(item => item.Verdict != LootScanVerdict.Swap).Take(3).ToArray(),
        };
        yield return latest with
        {
            ScanId = latest.ScanId + "-earlier",
            EvaluatedUtc = latest.EvaluatedUtc.AddMinutes(-14),
            Items = latest.Items
                .Select(item => item.Verdict == LootScanVerdict.Swap ? item with { Verdict = LootScanVerdict.Leave, Placement = string.Empty } : item)
                .ToArray(),
        };
    }

    private static InventoryProfileScope Scope(IServiceProvider services)
    {
        var profile = services.GetRequiredService<IRuntimeStateStore>().Current.Profile
            ?? throw new InvalidOperationException("The demo composition has no profile.");
        return new InventoryProfileScope(profile.Id, profile.ProfileGeneration, profile.GameMode.ToString());
    }
}
