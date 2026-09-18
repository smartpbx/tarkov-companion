using System.Globalization;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.App.ViewModels;

/// <summary>
/// Which barter, if any, is the cheap way to get an item the hideout wants. Shared by the V1
/// Hideout page and the V2 Hideout workspace so both say the same thing about the same barter.
/// </summary>
internal static class HideoutBarterRoutes
{
    /// <summary>
    /// The cheapest barter for each wanted item, where one beats buying it.
    /// </summary>
    /// <remarks>
    /// 789 barters are rewritten on every sync and no query had ever read one. This is the half
    /// of that the trader-level stepper unblocked: every barter's loyalty requirement was
    /// unanswerable while TraderLevels was written by nothing, so a route line would have had
    /// to either ignore the requirement — offering routes the player cannot take — or assume
    /// level 1 and hide most of them.
    ///
    /// Silent on every failure. No barter catalog, no prices, an unpriced input, a route that
    /// is not actually cheaper, or loyalty the player does not have: all of those are "no
    /// answer", and the row simply does not carry a route line. A line saying "buy it" when the
    /// truth is "nothing here could work it out" would be making something up.
    /// </remarks>
    public static async Task<Dictionary<string, string>> ComputeAsync(
        IBarterCatalog? barters,
        ITraderCatalog? traders,
        IItemRepository itemRepository,
        IReadOnlyList<HideoutItemRequirement> wanted,
        IReadOnlyDictionary<string, int> traderLevels,
        CancellationToken cancellationToken)
    {
        var routes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (barters is null)
        {
            return routes;
        }

        try
        {
            var catalog = await barters.GetAsync(cancellationToken).ConfigureAwait(true);
            if (catalog.Count == 0)
            {
                return routes;
            }

            var traderNames = traders is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : await traders.GetNamesAsync(cancellationToken).ConfigureAwait(true);

            // Prices are read once per item and remembered, because a barter's inputs are
            // frequently the inputs of other barters and the wanted list repeats items across
            // levels.
            var prices = new Dictionary<string, long?>(StringComparer.Ordinal);
            async Task<long?> PriceAsync(string itemId)
            {
                if (!prices.TryGetValue(itemId, out var known))
                {
                    var snapshot = await itemRepository.GetPriceAsync(itemId, cancellationToken).ConfigureAwait(true);
                    prices[itemId] = known = snapshot?.FleaPriceRoubles;
                }

                return known;
            }

            // Warmed before routing, because the routing itself is synchronous: it has to
            // compare every barter against every other and cannot await inside that.
            foreach (var itemId in catalog
                .SelectMany(barter => barter.Wants.Select(want => want.ItemId))
                .Concat(wanted.Select(requirement => requirement.ItemId))
                .Distinct(StringComparer.Ordinal))
            {
                await PriceAsync(itemId).ConfigureAwait(true);
            }

            foreach (var requirement in wanted.DistinctBy(item => item.ItemId, StringComparer.Ordinal))
            {
                var route = BarterRouting.Cheapest(
                    requirement.ItemId,
                    catalog,
                    traderLevels,
                    itemId => prices.GetValueOrDefault(itemId));
                if (route is null)
                {
                    continue;
                }

                // Only where it actually beats buying one. A barter that costs more than the
                // flea price is a route, not a cheaper route, and putting it on the row would
                // be advice to spend more.
                var flea = prices.GetValueOrDefault(requirement.ItemId);
                if (flea is not { } buying || route.PerItem >= buying)
                {
                    continue;
                }

                var trader = route.TraderId is { Length: > 0 } id
                    ? traderNames.GetValueOrDefault(id, id)
                    : "a trader";
                routes[requirement.ItemId] = string.Create(
                    CultureInfo.CurrentCulture,
                    $"Barter from {trader} costs about {route.PerItem:N0} ₽ each · flea {buying:N0} ₽");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The rest of the page is unaffected; the rows simply lose their route line.
            routes.Clear();
        }

        return routes;
    }
}
