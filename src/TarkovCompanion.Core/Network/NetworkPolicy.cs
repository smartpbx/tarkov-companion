namespace TarkovCompanion.Core.Network;

/// <summary>[#292] Everything this application sends off the PC, one value per kind of traffic.</summary>
/// <remarks>
/// Game data has no switch of its own: the catalog, the maps and the loot spawns are what the app
/// is for, so only Local only stops them. The others are extras a player may not want.
/// </remarks>
public enum NetworkService
{
    /// <summary>tarkov.dev's catalog, prices, maps and loot spawns (downloads only).</summary>
    GameData,

    /// <summary>The group relay: position, marks, quests, and the paired tablet.</summary>
    SquadSharing,

    /// <summary>The update feed on the relay.</summary>
    UpdateChecks,

    /// <summary>A problem report the player chose to send.</summary>
    ProblemReports,

    /// <summary>TarkovTracker's API, with the player's token.</summary>
    TarkovTracker,
}

/// <summary>What the policy says about one kind of traffic right now.</summary>
public enum NetworkVerdict
{
    Allowed,

    /// <summary>Local only is on (the switch, or TARKOV_COMPANION_OFFLINE): nothing leaves the PC.</summary>
    LocalOnly,

    /// <summary>This one service is switched off in Setup › Data &amp; Privacy.</summary>
    SwitchedOff,
}

/// <summary>The player's choices in Setup › Data &amp; Privacy, as saved in Config/network.json.</summary>
/// <remarks>Every service defaults on and Local only defaults off, which is how the app behaved before the switches existed.</remarks>
public sealed record NetworkControls
{
    public static NetworkControls Default { get; } = new();

    public bool LocalOnly { get; init; }

    public bool SquadSharing { get; init; } = true;

    public bool UpdateChecks { get; init; } = true;

    public bool ProblemReports { get; init; } = true;

    public bool TarkovTracker { get; init; } = true;

    /// <summary>Whether this service's own switch is on, ignoring Local only.</summary>
    public bool IsOn(NetworkService service) => service switch
    {
        NetworkService.GameData => true,
        NetworkService.SquadSharing => SquadSharing,
        NetworkService.UpdateChecks => UpdateChecks,
        NetworkService.ProblemReports => ProblemReports,
        NetworkService.TarkovTracker => TarkovTracker,
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, null),
    };

    public NetworkControls With(NetworkService service, bool on) => service switch
    {
        NetworkService.SquadSharing => this with { SquadSharing = on },
        NetworkService.UpdateChecks => this with { UpdateChecks = on },
        NetworkService.ProblemReports => this with { ProblemReports = on },
        NetworkService.TarkovTracker => this with { TarkovTracker = on },
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, "Game data has no switch of its own."),
    };

    /// <summary>Local only wins over every service switch; a service switch only ever turns traffic off.</summary>
    public NetworkVerdict Check(NetworkService service, bool localOnlyForced = false) =>
        localOnlyForced || LocalOnly
            ? NetworkVerdict.LocalOnly
            : IsOn(service) ? NetworkVerdict.Allowed : NetworkVerdict.SwitchedOff;
}

/// <summary>Asked by every outbound client before it connects.</summary>
public interface INetworkPolicy
{
    NetworkControls Controls { get; }

    /// <summary>True when TARKOV_COMPANION_OFFLINE holds Local only on, whatever the switch says.</summary>
    bool LocalOnlyForced { get; }

    NetworkVerdict Check(NetworkService service);

    /// <summary>Raised after the controls change, on the thread that changed them.</summary>
    event EventHandler? Changed;
}

/// <summary>Where the controls are kept. Synchronous: a few bytes, read before any view exists.</summary>
public interface INetworkControlsStore
{
    NetworkControls Read();

    void Save(NetworkControls controls);
}
