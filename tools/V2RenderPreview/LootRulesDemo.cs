using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#902 P9] --loot-rules-demo: the choices a few Loot Scan results would have written (a pin, a
/// wishlist entry, Always take, Always leave), through the Loot Scan's own controls, with
/// Plan › Keep's Loot rules open so the list that undoes them can be looked at.
/// </summary>
internal static class LootRulesDemo
{
    internal static void Run(IServiceProvider services, Action<Task> drain, Action<int> pump)
    {
        var items = services.GetRequiredService<IItemRepository>();
        var controls = services.GetRequiredService<ILootScanWorkspaceControls>();
        string? Find(string name)
        {
            var search = items.SearchAsync(name, 8, CancellationToken.None);
            drain(search);
            return (search.Result.FirstOrDefault(hit => string.Equals(hit.Item.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? search.Result.FirstOrDefault())?.Item.Id;
        }

        if (Find("Soap") is { } soap)
        {
            drain(controls.SetPinnedAsync(soap, true));
        }

        if (Find("LEDX Skin Transilluminator") is { } ledx)
        {
            drain(controls.SetWishlistedAsync(ledx, true));
        }

        if (Find("Graphics card") is { } gpu)
        {
            drain(controls.SetRuleAsync(gpu, LootScanItemRule.AlwaysTake));
        }

        foreach (var name in new[] { "Bolts", "Screw nut" })
        {
            if (Find(name) is { } id)
            {
                drain(controls.SetRuleAsync(id, LootScanItemRule.AlwaysLeave));
            }
        }

        var keep = services.GetRequiredService<KeepListWorkspaceViewModel>();
        keep.LootRules?.Open();
        if (keep.LootRules is { } rules)
        {
            drain(rules.RefreshAsync());
        }

        pump(20);
    }
}
