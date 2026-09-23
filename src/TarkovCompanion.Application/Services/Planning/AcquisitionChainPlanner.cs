using TarkovCompanion.Core.Domain.Planning;

namespace TarkovCompanion.Application.Services.Planning;

/// <summary>
/// Finds the cheapest known way to acquire an item through buying, crafting or bartering.
/// </summary>
/// <remarks>
/// Recipe inputs recurse through the same choices, but a path may never revisit an item and the
/// whole search is bounded by depth and expanded-node limits. Consuming an input is charged at
/// least its net resale value: a cheap craft ingredient is not free when using it gives up a flea
/// sale (after fee) or a better trader sale. Unknown prices never become zero; a route with an
/// unpriceable input is not a priced route.
/// </remarks>
public static class AcquisitionChainPlanner
{
    public const int DefaultMaximumDepth = 6;
    public const int DefaultMaximumExpandedNodes = 10_000;

    public static AcquisitionChainPlan Plan(
        string itemId,
        int quantity,
        IReadOnlyDictionary<string, AcquisitionChainItem> items,
        IReadOnlyList<AcquisitionChainRecipe> recipes,
        int maximumDepth = DefaultMaximumDepth,
        int maximumExpandedNodes = DefaultMaximumExpandedNodes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumExpandedNodes, 1);

        var byOutput = recipes
            .Where(recipe => recipe.OutputCount > 0 && recipe.OutputItemId.Length > 0)
            .GroupBy(recipe => recipe.OutputItemId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(recipe => recipe.RecipeId, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var state = new SearchState(items, byOutput, maximumDepth, maximumExpandedNodes);
        var step = state.Find(itemId, quantity, depth: 0, new HashSet<string>(StringComparer.Ordinal));
        var item = items.GetValueOrDefault(itemId);
        return new(
            itemId,
            item?.Name ?? itemId,
            quantity,
            step,
            state.CycleSkipped,
            state.DepthLimitReached,
            state.SearchLimitReached,
            OldestPrice(step, items));
    }

    private static DateTimeOffset? OldestPrice(
        AcquisitionChainStep? step,
        IReadOnlyDictionary<string, AcquisitionChainItem> items)
    {
        if (step is null)
        {
            return null;
        }

        DateTimeOffset? oldest = items.GetValueOrDefault(step.ItemId)?.PriceUpdatedUtc;
        foreach (var input in step.Inputs)
        {
            var child = OldestPrice(input, items);
            if (child is not null && (oldest is null || child < oldest))
            {
                oldest = child;
            }
        }

        return oldest;
    }

    private sealed class SearchState(
        IReadOnlyDictionary<string, AcquisitionChainItem> items,
        IReadOnlyDictionary<string, AcquisitionChainRecipe[]> recipes,
        int maximumDepth,
        int maximumExpandedNodes)
    {
        private int _expanded;

        public bool CycleSkipped { get; private set; }
        public bool DepthLimitReached { get; private set; }
        public bool SearchLimitReached { get; private set; }

        public AcquisitionChainStep? Find(string itemId, int quantity, int depth, HashSet<string> path)
        {
            if (++_expanded > maximumExpandedNodes)
            {
                SearchLimitReached = true;
                return Buy(itemId, quantity);
            }

            var candidates = new List<AcquisitionChainStep>();
            if (Buy(itemId, quantity) is { } buy)
            {
                candidates.Add(buy);
            }

            if (!recipes.TryGetValue(itemId, out var itemRecipes))
            {
                return Cheapest(candidates);
            }

            if (depth >= maximumDepth)
            {
                DepthLimitReached = true;
                return Cheapest(candidates);
            }

            path.Add(itemId);
            try
            {
                foreach (var recipe in itemRecipes)
                {
                    var runs = DivideRoundUp(quantity, recipe.OutputCount);
                    var children = new List<AcquisitionChainStep>(recipe.Inputs.Count);
                    long inputCost = 0;
                    long opportunityCost = 0;
                    var complete = true;
                    foreach (var input in recipe.Inputs)
                    {
                        if (path.Contains(input.ItemId))
                        {
                            CycleSkipped = true;
                            complete = false;
                            break;
                        }

                        var inputQuantity = checked(Math.Max(1, input.Count) * (input.Reusable ? 1 : runs));
                        var child = Find(input.ItemId, inputQuantity, depth + 1, path);
                        if (child is null)
                        {
                            complete = false;
                            break;
                        }

                        children.Add(child);
                        inputCost = Add(inputCost, child.TotalRoubles);
                        if (items.GetValueOrDefault(input.ItemId)?.OpportunityValueRoubles is { } each)
                        {
                            var forgone = Multiply(each, inputQuantity);
                            opportunityCost = Add(opportunityCost, Math.Max(0, forgone - child.TotalRoubles));
                        }
                    }

                    if (!complete)
                    {
                        continue;
                    }

                    var total = Add(inputCost, opportunityCost, recipe.FuelCostRoubles, recipe.StationTimeCostRoubles);
                    candidates.Add(new(
                        itemId,
                        items.GetValueOrDefault(itemId)?.Name ?? itemId,
                        quantity,
                        recipe.Method,
                        recipe.SourceName,
                        total,
                        recipe.FuelCostRoubles,
                        recipe.StationTimeCostRoubles,
                        opportunityCost,
                        recipe.Duration,
                        children));
                }
            }
            finally
            {
                path.Remove(itemId);
            }

            return Cheapest(candidates);
        }

        private AcquisitionChainStep? Buy(string itemId, int quantity)
        {
            var item = items.GetValueOrDefault(itemId);
            return item?.BuyPriceRoubles is > 0 and var each
                ? new(
                    itemId,
                    item.Name,
                    quantity,
                    AcquisitionChainMethod.Buy,
                    item.BuySource ?? "Buy",
                    Multiply(each, quantity),
                    0,
                    0,
                    0,
                    null,
                    [])
                : null;
        }

        private static AcquisitionChainStep? Cheapest(IReadOnlyList<AcquisitionChainStep> candidates) => candidates
            .OrderBy(candidate => candidate.TotalRoubles)
            .ThenBy(candidate => candidate.Method)
            .ThenBy(candidate => candidate.SourceName, StringComparer.Ordinal)
            .FirstOrDefault();

        private static int DivideRoundUp(int value, int divisor) => checked((value + divisor - 1) / divisor);

        private static long Multiply(long left, int right) => left > long.MaxValue / right ? long.MaxValue : left * right;

        private static long Add(params long[] values)
        {
            long total = 0;
            foreach (var value in values)
            {
                if (value > long.MaxValue - total)
                {
                    return long.MaxValue;
                }

                total += Math.Max(0, value);
            }

            return total;
        }
    }
}
