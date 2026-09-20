namespace TarkovCompanion.Core.Domain.Planning;

/// <summary>A station level that must already be built before another one can be started.</summary>
public sealed record HideoutStationPrerequisite(
    string StationId,
    int TargetLevel,
    string RequiredStationId,
    int RequiredLevel);

/// <summary>A trader loyalty or skill a station level asks for, which no item buys.</summary>
public sealed record HideoutOtherPrerequisite(string StationId, int TargetLevel, string Label);

/// <summary>Everything a station level asks for besides items.</summary>
public sealed record HideoutPrerequisites(
    IReadOnlyList<HideoutStationPrerequisite> Stations,
    IReadOnlyList<HideoutOtherPrerequisite> Others)
{
    public static HideoutPrerequisites None { get; } = new([], []);
}

/// <summary>One level of one station, in the order it has to be built.</summary>
/// <param name="AlsoNeeds">Trader loyalty and skill levels this step asks for.</param>
public sealed record HideoutUpgradeStep(
    string StationId,
    string Name,
    int Level,
    IReadOnlyList<HideoutLevelNeed> Needs,
    IReadOnlyList<string> AlsoNeeds)
{
    public int MissingItemCount => Needs.Count(need => need.Owned is not null && !need.IsSatisfied);

    public int UnknownItemCount => Needs.Count(need => need.Owned is null);
}

/// <summary>One item across several upgrade steps, counted once against one holding.</summary>
/// <param name="Have">The recorded holding, or null where none is recorded (see <see cref="HeldCount"/>).</param>
/// <param name="Steps">How many of the steps ask for it.</param>
public sealed record HideoutShoppingLine(string ItemId, int Need, int? Have, int Steps)
{
    public int Remaining => HeldCount.Remaining(Need, Have);
}
