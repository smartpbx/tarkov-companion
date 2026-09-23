namespace TarkovCompanion.Core.Domain.Maps;

/// <summary>One fixed switch in the public map catalog.</summary>
/// <remarks>
/// A switch is reference data, not an observation that somebody used it this raid. Its graph
/// says which control enables another control; it never says the exit is currently open.
/// </remarks>
public sealed record MapSwitch(
    string Id,
    string Name,
    string? SwitchType,
    WorldPosition Position,
    string? ActivatedById,
    IReadOnlyList<MapSwitchActivation> Activates);

/// <summary>One catalog edge from a switch to the switch it changes.</summary>
public sealed record MapSwitchActivation(string Operation, string TargetSwitchId);

/// <summary>An item or payment an extract asks the player to hand over.</summary>
public sealed record MapExtractTransfer(
    string ItemId,
    string? ItemName,
    long Count,
    string? CurrencySymbol)
{
    public bool IsPayment => CurrencySymbol is not null;
}

/// <summary>A reviewed condition the primary map catalog does not publish.</summary>
public enum MapExtractConditionKind
{
    NoBackpack,
    NoArmor,
    Items,
    TimedWindow,
}

/// <summary>A raid-clock window in which an extract arrives and remains available.</summary>
/// <remarks>
/// Both arrival bounds are expressed as time left in the raid, because that is the clock the
/// player sees. A larger value happens first as the clock counts down.
/// </remarks>
public sealed record MapExtractTimedWindow(
    TimeSpan ArrivalStartsAtTimeLeft,
    TimeSpan ArrivalEndsAtTimeLeft,
    TimeSpan Duration);

/// <summary>One checked non-catalog condition attached to an extract.</summary>
public sealed record MapExtractCondition(
    MapExtractConditionKind Kind,
    IReadOnlyList<string> Items,
    MapExtractTimedWindow? TimedWindow);

/// <summary>The catalog-backed and checked supplemental things needed to use one extract.</summary>
/// <remarks>
/// Switches are already in activation order. Empty and false values mean the catalog did not
/// state that requirement; they are not a promise that the game has no other condition.
/// </remarks>
public sealed record MapExtractRequirements(
    IReadOnlyList<MapSwitch> SwitchChain,
    MapExtractTransfer? Transfer,
    bool RequiresCoOp,
    bool IsOneTime)
{
    public IReadOnlyList<MapExtractCondition> Conditions { get; init; } = [];

    public bool HasAny => SwitchChain.Count > 0 || Transfer is not null || RequiresCoOp || IsOneTime || Conditions.Count > 0;

    public bool RequiresSwitch => SwitchChain.Count > 0;

    public bool RequiresKey => Transfer is { IsPayment: false };

    public bool RequiresPayment => Transfer is { IsPayment: true };

    public bool RequiresNoBackpack => Conditions.Any(item => item.Kind == MapExtractConditionKind.NoBackpack);

    public bool RequiresNoArmor => Conditions.Any(item => item.Kind == MapExtractConditionKind.NoArmor);

    public bool RequiresItems => Conditions.Any(item => item.Kind == MapExtractConditionKind.Items);

    public bool HasTimedWindow => Conditions.Any(item => item.Kind == MapExtractConditionKind.TimedWindow);
}
