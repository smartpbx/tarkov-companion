namespace TarkovCompanion.Core.Domain.Planning;

/// <summary>One item the next level of a station asks for, against what the player holds.</summary>
public sealed record HideoutLevelNeed(string ItemId, int Required, int Owned)
{
    public int Remaining => Math.Max(0, Required - Owned);

    public bool IsSatisfied => Owned >= Required;
}

/// <summary>
/// A station at whatever level the profile says is built, and what raising it one level asks for.
/// </summary>
/// <param name="NextLevel">The lowest level above the built one, or 0 where the station is fully built.</param>
public sealed record HideoutStationPlan(
    string StationId,
    string Name,
    int BuiltLevel,
    int MaximumLevel,
    int NextLevel,
    IReadOnlyList<HideoutLevelNeed> NextLevelNeeds)
{
    public bool HasNextLevel => NextLevel > 0;

    /// <summary>How many of the next level's items the player does not yet hold enough of.</summary>
    public int MissingItemCount => NextLevelNeeds.Count(need => !need.IsSatisfied);

    /// <summary>Whether the next level could be started with what the player holds. A maxed station cannot.</summary>
    public bool CanBuildNow => HasNextLevel && MissingItemCount == 0;
}

/// <summary>One item still short across the next level of every station, counted once.</summary>
public sealed record HideoutShortfall(string ItemId, int Need, int Have)
{
    public int Remaining => Math.Max(0, Need - Have);
}
