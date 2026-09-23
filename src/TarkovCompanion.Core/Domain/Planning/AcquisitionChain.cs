namespace TarkovCompanion.Core.Domain.Planning;

/// <summary>How one step in an item's cheapest acquisition chain obtains its output.</summary>
public enum AcquisitionChainMethod
{
    Buy,
    Craft,
    Barter,
}

/// <summary>One item the planner can price directly, before recipes are considered.</summary>
/// <param name="BuyPriceRoubles">The cheapest flea or currently-obtainable trader cash price.</param>
/// <param name="OpportunityValueRoubles">
/// What consuming one gives up: the better of trader sale and flea sale after its listing fee.
/// </param>
public sealed record AcquisitionChainItem(
    string ItemId,
    string Name,
    long? BuyPriceRoubles,
    string? BuySource,
    long? OpportunityValueRoubles,
    DateTimeOffset? PriceUpdatedUtc);

/// <summary>One input to a craft or barter.</summary>
/// <param name="Reusable">True for a craft tool that is needed but not consumed.</param>
public sealed record AcquisitionChainIngredient(string ItemId, int Count, bool Reusable = false);

/// <summary>One profile-obtainable craft or barter the planner may recurse through.</summary>
public sealed record AcquisitionChainRecipe(
    string RecipeId,
    AcquisitionChainMethod Method,
    string OutputItemId,
    int OutputCount,
    IReadOnlyList<AcquisitionChainIngredient> Inputs,
    string SourceName,
    TimeSpan? Duration,
    long FuelCostRoubles,
    long StationTimeCostRoubles);

/// <summary>One chosen step, with its recursively chosen inputs.</summary>
public sealed record AcquisitionChainStep(
    string ItemId,
    string ItemName,
    int Quantity,
    AcquisitionChainMethod Method,
    string SourceName,
    long TotalRoubles,
    long FuelCostRoubles,
    long StationTimeCostRoubles,
    long InputOpportunityCostRoubles,
    TimeSpan? Duration,
    IReadOnlyList<AcquisitionChainStep> Inputs)
{
    public bool HasOverhead => FuelCostRoubles > 0 || StationTimeCostRoubles > 0 || InputOpportunityCostRoubles > 0;
}

/// <summary>The cheapest known chain and the limits that affected the search.</summary>
public sealed record AcquisitionChainPlan(
    string ItemId,
    string ItemName,
    int Quantity,
    AcquisitionChainStep? Cheapest,
    bool CycleSkipped,
    bool DepthLimitReached,
    bool SearchLimitReached,
    DateTimeOffset? OldestPriceUtc)
{
    public bool HasRoute => Cheapest is not null;
}
