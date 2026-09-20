namespace TarkovCompanion.Core.Domain.Planning;

/// <summary>One item the next level of a station asks for, against what the player holds.</summary>
/// <param name="Owned">The recorded holding, or null where none is recorded. Null is not zero: see <see cref="HeldCount"/>.</param>
public sealed record HideoutLevelNeed(string ItemId, int Required, int? Owned)
{
    public int Remaining => HeldCount.Remaining(Required, Owned);

    public bool IsSatisfied => HeldCount.Meets(Required, Owned);
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

    /// <summary>How many of the next level's items the player is known not to hold enough of.</summary>
    public int MissingItemCount => NextLevelNeeds.Count(need => need.Owned is not null && !need.IsSatisfied);

    /// <summary>
    /// How many of the next level's items have no holding recorded. They are not counted as
    /// missing, because nothing says they are; they still stop <see cref="CanBuildNow"/>, because
    /// nothing says they are held either.
    /// </summary>
    public int UnknownItemCount => NextLevelNeeds.Count(need => need.Owned is null);

    /// <summary>Whether the next level could be started with what the player holds. A maxed station cannot.</summary>
    public bool CanBuildNow => HasNextLevel && MissingItemCount == 0 && UnknownItemCount == 0;
}

/// <summary>One item still short across the next level of every station, counted once.</summary>
public sealed record HideoutShortfall(string ItemId, int Need, int? Have)
{
    public int Remaining => HeldCount.Remaining(Need, Have);
}
