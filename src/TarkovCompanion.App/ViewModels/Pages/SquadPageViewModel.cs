using System.Globalization;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels;

/// <summary>One party member, rendered as finished text.</summary>
/// <param name="Nickname">The member's PMC nickname.</param>
/// <param name="Role">Leader or member, and their side when the game stated one.</param>
/// <param name="Level">Their PMC level, or a statement that it was not given.</param>
/// <param name="Readiness">Ready, not ready, or not stated.</param>
/// <param name="Scav">When their scav is next available, when the game said so.</param>
/// <param name="Gear">The gear slots the game listed, by name where the item is known.</param>
/// <param name="IsReady">Drives the readiness colour; null when the game never said.</param>
/// <summary>One line of a member's kit, so the list binds without a converter.</summary>
public sealed record SquadGearViewModel(string Text);

public sealed record SquadMemberViewModel(
    string Nickname,
    string Role,
    string Level,
    string Readiness,
    string Scav,
    IReadOnlyList<SquadGearViewModel> Gear,
    bool? IsReady)
{
    public string ReadinessColor => IsReady switch
    {
        true => "#77B895",
        false => "#C6A15B",
        null => "#8F9BA6",
    };

    public bool HasGear => Gear.Count > 0;

    public bool HasNoGear => Gear.Count == 0;
}

/// <summary>One dogtag the player is carrying.</summary>
/// <param name="Victim">Who the tag names, with their side and level where stated.</param>
/// <param name="Killer">Who killed them.</param>
/// <param name="When">When the kill happened, in the player's own time zone.</param>
public sealed record DogtagViewModel(string Victim, string Killer, string When);

/// <summary>
/// The player's party, and the dogtags they are carrying.
/// </summary>
/// <remarks>
/// These are other real people, so the boundary in docs/SAFETY.md decides what appears here.
/// The party qualifies because the game already shows the player every one of them, by name,
/// on its own party screen; this is the same list, on a second monitor, so it does not have to
/// be alt-tabbed to. Numeric account and profile ids never reach this layer at all.
///
/// Nothing here is ever transmitted, exported or written to a file. A squadmate's own looted
/// dogtags are not read, because those name players this player never met.
/// </remarks>
public sealed class SquadPageViewModel : PageViewModel
{
    /// <summary>
    /// The gear slots worth listing, in the order a player reads their own kit.
    /// </summary>
    /// <remarks>
    /// A loadout arrives flat and complete, down to every screw in a weapon, and listing all
    /// of it would be both unreadable and a wider read of somebody else's data than the
    /// product needs. These are the slots that answer "what is my squadmate bringing".
    /// </remarks>
    private static readonly string[] GearSlots =
    [
        "FirstPrimaryWeapon",
        "SecondPrimaryWeapon",
        "Holster",
        "Headwear",
        "ArmorVest",
        "TacticalVest",
        "Backpack",
    ];

    private readonly IItemRepository _items;
    private readonly Dictionary<string, string> _itemNames = new(StringComparer.Ordinal);
    private IReadOnlyList<SquadMemberViewModel> _members = [];
    private IReadOnlyList<DogtagViewModel> _dogtags = [];
    private string _status = "No party observed. Group up in game and members appear here.";
    private string _queue = "No match has been queued from this party yet.";
    private DateTimeOffset _rendered = DateTimeOffset.MinValue;

    public SquadPageViewModel(IItemRepository items)
        : base(
            "Squad",
            "Who you are running with, and what they are bringing",
            "Read from the game's own party notifications")
    {
        _items = items;
    }

    public IReadOnlyList<SquadMemberViewModel> Members
    {
        get => _members;
        private set => SetProperty(ref _members, value);
    }

    public IReadOnlyList<DogtagViewModel> Dogtags
    {
        get => _dogtags;
        private set => SetProperty(ref _dogtags, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Queue
    {
        get => _queue;
        private set => SetProperty(ref _queue, value);
    }

    public bool HasMembers => Members.Count > 0;

    public bool HasNoMembers => Members.Count == 0;

    public bool HasDogtags => Dogtags.Count > 0;

    public bool HasNoDogtags => Dogtags.Count == 0;

    /// <summary>
    /// Renders the party from the latest runtime snapshot.
    /// </summary>
    /// <remarks>
    /// Runs on every snapshot, so it returns immediately when the party has not changed. The
    /// squad's own timestamp is the comparison rather than the collections, which are rebuilt
    /// on every readiness toggle and would never compare equal.
    /// </remarks>
    public void Apply(ApplicationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var squad = snapshot.Squad;
        if (squad.UpdatedUtc == _rendered)
        {
            return;
        }

        _rendered = squad.UpdatedUtc;
        Members = squad.Members.Select(Describe).ToArray();
        Dogtags = squad.Dogtags.Select(Describe).ToArray();
        OnPropertyChanged(nameof(HasMembers));
        OnPropertyChanged(nameof(HasNoMembers));
        OnPropertyChanged(nameof(HasDogtags));
        OnPropertyChanged(nameof(HasNoDogtags));
        Status = squad.Members.Count switch
        {
            0 => "No party observed. Group up in game and members appear here.",
            1 => "1 member in your party.",
            var count => string.Create(CultureInfo.CurrentCulture, $"{count} members in your party."),
        };
        Queue = squad.MatchStartedUtc is { } queued
            ? squad.QueueEstimate is { } estimate
                ? string.Create(
                    CultureInfo.CurrentCulture,
                    $"Queued together at {queued.ToLocalTime():T}; the game estimated {estimate.TotalSeconds:F0}s.")
                : string.Create(CultureInfo.CurrentCulture, $"Queued together at {queued.ToLocalTime():T}.")
            : "No match has been queued from this party yet.";
        Evidence = $"Party read from the game's group notifications · updated {squad.UpdatedUtc.ToLocalTime():T}";
        _ = ResolveGearNamesAsync(squad);
    }

    private static DogtagViewModel Describe(DogtagObservation dogtag) => new(
        dogtag.VictimSide is { } side
            ? dogtag.VictimLevel is { } level
                ? string.Create(CultureInfo.CurrentCulture, $"{dogtag.VictimNickname} · {side} · level {level}")
                : $"{dogtag.VictimNickname} · {side}"
            : dogtag.VictimNickname,
        dogtag.KillerNickname is { } killer ? $"Killed by {killer}" : "Killer not stated",
        dogtag.KilledAt is { } killedAt
            ? string.Create(CultureInfo.CurrentCulture, $"{killedAt.ToLocalTime():g}")
            : "Time of death not stated");

    private SquadMemberViewModel Describe(GroupMember member) => new(
        member.Nickname ?? "Unnamed member",
        member.IsLeader == true
            ? member.Side is { } leaderSide ? $"Party leader · {leaderSide}" : "Party leader"
            : member.Side ?? "Party member",
        member.Level is { } level
            ? string.Create(CultureInfo.CurrentCulture, $"Level {level}")
            : "Level not stated",
        member.IsReady switch
        {
            true => "Ready",
            false => "Not ready",
            null => "Readiness not stated",
        },
        member.ScavLockedUntil is { } until
            ? string.Create(CultureInfo.CurrentCulture, $"Scav available {until.ToLocalTime():t}")
            : "Scav timer not stated",
        DescribeGear(member),
        member.IsReady);

    private IReadOnlyList<SquadGearViewModel> DescribeGear(GroupMember member) => member.Equipment
        .Where(item => item.SlotId is not null && GearSlots.Contains(item.SlotId, StringComparer.Ordinal))
        .OrderBy(item => Array.IndexOf(GearSlots, item.SlotId!))
        .Select(item => new SquadGearViewModel($"{Humanise(item.SlotId!)}: {Name(item.TemplateId)}"))
        .ToArray();

    /// <summary>
    /// Names an item, or says plainly that it has not been resolved.
    /// </summary>
    /// <remarks>
    /// The game writes template ids and never names, so a name only exists once the item
    /// catalog has been synced and the id looked up. An unresolved id is shown as unresolved
    /// rather than as a raw id, which would read like a bug.
    /// </remarks>
    private string Name(string templateId) =>
        _itemNames.TryGetValue(templateId, out var name) ? name : "not in the synced catalog";

    private static string Humanise(string slotId) => slotId switch
    {
        "FirstPrimaryWeapon" => "Primary",
        "SecondPrimaryWeapon" => "Secondary",
        "Holster" => "Sidearm",
        "ArmorVest" => "Armour",
        "TacticalVest" => "Rig",
        _ => slotId,
    };

    /// <summary>
    /// Looks up the gear names the party's loadouts refer to, once each.
    /// </summary>
    /// <remarks>
    /// Deliberately fire-and-forget and deliberately cached: the same members are restated
    /// constantly with the same gear, and a database round trip per restatement would be
    /// hundreds of queries for an answer that never changes. A failed lookup is left out of
    /// the cache so a later sync can still fill it in.
    /// </remarks>
    private async Task ResolveGearNamesAsync(SquadSnapshot squad)
    {
        var unknown = squad.Members
            .SelectMany(member => member.Equipment)
            .Where(item => item.SlotId is not null && GearSlots.Contains(item.SlotId, StringComparer.Ordinal))
            .Select(item => item.TemplateId)
            .Distinct(StringComparer.Ordinal)
            .Where(templateId => !_itemNames.ContainsKey(templateId))
            .ToArray();
        if (unknown.Length == 0)
        {
            return;
        }

        var resolved = false;
        foreach (var templateId in unknown)
        {
            try
            {
                if (await _items.GetAsync(templateId, CancellationToken.None).ConfigureAwait(true) is { } item)
                {
                    _itemNames[templateId] = string.IsNullOrWhiteSpace(item.ShortName) ? item.Name : item.ShortName;
                    resolved = true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
            {
                return;
            }
        }

        if (resolved)
        {
            Members = squad.Members.Select(Describe).ToArray();
        }
    }
}
