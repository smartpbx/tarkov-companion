using TarkovCompanion.App.Localization;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>One quest the Raid map lists for its current map, as the brief is handed it.</summary>
/// <param name="MapId">The map the list was built for, so a list from another map is never shown.</param>
public sealed record PreRaidBriefQuest(string MapId, string Task, string Objectives, string Bring, bool IsPinned);

/// <summary>One exit the Raid map lists for the raid's side (#873), with its #737/#743 requirement words.</summary>
public sealed record PreRaidBriefExtract(string Name, string Requirement, bool IsTransit);

/// <summary>Everything the brief is built from; each part is optional except the situation.</summary>
/// <param name="ListsMapId">The map the extract and quest lists were built for.</param>
/// <param name="LootSpots">High-value spots on the loot layer for this map, where it has data.</param>
public sealed record PreRaidBriefInputs(Situation Situation)
{
    public string? MapName { get; init; }

    public TimeSpan? RaidLength { get; init; }

    public IReadOnlyList<MapBossChance> Bosses { get; init; } = [];

    /// <summary>False until the catalog has answered, so a slow read is not shown as "no bosses".</summary>
    public bool BossesRead { get; init; } = true;

    public string? ListsMapId { get; init; }

    public IReadOnlyList<PreRaidBriefQuest> Quests { get; init; } = [];

    public IReadOnlyList<PreRaidBriefExtract> Extracts { get; init; } = [];

    public int? LootSpots { get; init; }

    public bool LootSpotsCapped { get; init; }
}

public sealed record PreRaidBriefQuestRow(string Task, string Objectives, string Needs)
{
    public bool HasObjectives => Objectives.Length > 0;

    public bool HasNeeds => Needs.Length > 0;
}

/// <summary>The brief as the Raid panel shows it (#712 T4): short, one screen, every model labelled.</summary>
public sealed record PreRaidBrief(
    bool IsShown,
    string Kicker,
    string Title,
    string Bosses,
    IReadOnlyList<PreRaidBriefQuestRow> Quests,
    string QuestsNote,
    string Route,
    bool CanStillLeave,
    string ExtractsHeading,
    string Extracts,
    IReadOnlyList<string> ExtractRequirements,
    IReadOnlyList<string> Squad,
    string Loot,
    string Because)
{
    public static PreRaidBrief Hidden { get; } = new(false, "", "", "", [], "", "", false, "", "", [], [], "", "");

    public bool HasBosses => Bosses.Length > 0;

    public bool HasQuestsNote => QuestsNote.Length > 0;

    public bool HasRoute => Route.Length > 0;

    public bool HasSquad => Squad.Count > 0;

    public bool HasLoot => Loot.Length > 0;

    public string SquadLine => string.Join(" · ", Squad);
}

/// <summary>Builds the brief from the situation and the Raid map's own lists (#712 0-9).</summary>
/// <remarks>
/// The brief is shown only while the situation is Matching or Loading and names a map: the game
/// log writes the map in the queue, about a minute before anybody can move, and that minute is the
/// last one in which a missing key can still be fetched. The Now panel takes over at GameStarted.
/// Bosses are catalog spawn chances and say so; nothing here claims a boss is in the raid.
/// </remarks>
public static class PreRaidBriefBuilder
{
    public const int MaximumQuests = 3;
    public const int MaximumExtracts = 6;
    public const int MaximumRequirements = 3;
    public const int MaximumBosses = 3;
    public const int MaximumSquad = 5;

    public static bool ShowsFor(Situation situation) =>
        situation.Phase.Value is SituationPhase.Matching or SituationPhase.Loading &&
        situation.Map?.Value is { Length: > 0 };

    public static PreRaidBrief Build(PreRaidBriefInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var situation = inputs.Situation;
        if (!ShowsFor(situation))
        {
            return PreRaidBrief.Hidden;
        }

        var mapId = situation.Map!.Value;
        var side = situation.Side?.Value ?? SituationSide.Unknown;
        var sameMap = string.Equals(inputs.ListsMapId, mapId, StringComparison.OrdinalIgnoreCase);

        var title = string.Join(" · ", new[]
        {
            inputs.MapName is { Length: > 0 } name ? name : mapId,
            RaidText.BriefSide(side),
            inputs.RaidLength is { TotalMinutes: > 0 } length ? RaidText.BriefMinutes(length.TotalMinutes) : null,
        }.Where(part => part is not null));

        var bosses = !inputs.BossesRead
            ? string.Empty
            : inputs.Bosses.Count == 0
            ? RaidText.BriefNoBosses
            : RaidText.BriefBossesFromCatalog(string.Join(" · ", inputs.Bosses
                .Take(MaximumBosses)
                .Select(boss => RaidText.BriefBossChance(boss.Name, boss.Chance, boss.IsTriggered))));

        var quests = sameMap
            ? inputs.Quests.Where(quest => string.Equals(quest.MapId, mapId, StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
        var questRows = quests
            .OrderByDescending(quest => quest.IsPinned)
            .Take(MaximumQuests)
            .Select(quest => new PreRaidBriefQuestRow(
                quest.Task,
                quest.Objectives,
                quest.Bring.Length > 0 ? RaidText.BriefNeeds(quest.Bring) : string.Empty))
            .ToArray();
        var questsNote = quests.Length == 0
            ? RaidText.BriefNoQuests
            : quests.Length > MaximumQuests ? RaidText.BriefMoreQuests(quests.Length - MaximumQuests) : string.Empty;

        var route = situation.Next is { } next
            ? RaidText.BriefRoute(situation.Then is { } then
                ? $"{RaidText.BriefRouteStep("A", next.Label)} → {RaidText.BriefRouteStep("B", then.Label)}"
                : RaidText.BriefRouteStep("A", next.Label))
            : string.Empty;

        var exits = sameMap ? inputs.Extracts.Where(extract => !extract.IsTransit).ToArray() : [];
        var extracts = exits.Length == 0
            ? RaidText.BriefNoExtracts
            : string.Join(" · ", exits.Take(MaximumExtracts).Select(extract => extract.Name)) +
                (exits.Length > MaximumExtracts ? $" · {RaidText.BriefMoreExtracts(exits.Length - MaximumExtracts)}" : string.Empty);
        var requirements = exits
            .Where(extract => extract.Requirement.Length > 0)
            .Take(MaximumRequirements)
            .Select(extract => $"{extract.Name} · {extract.Requirement}")
            .ToArray();

        var squad = situation.Squad
            .Take(MaximumSquad)
            .Select(member => RaidText.BriefSquadMember(member.Name, RaidText.BriefSquadState(member.State)))
            .ToArray();

        var loot = inputs.LootSpots is > 0 and var spots ? RaidText.BriefLoot(spots, inputs.LootSpotsCapped) : string.Empty;

        return new PreRaidBrief(
            true,
            (situation.Phase.Value == SituationPhase.Matching ? RaidText.BriefWhileMatching : RaidText.BriefWhileLoading)
                .ToUpper(System.Globalization.CultureInfo.CurrentCulture),
            title,
            bosses,
            questRows,
            questsNote,
            route,
            situation.Phase.Value == SituationPhase.Matching && questRows.Any(row => row.HasNeeds),
            side is SituationSide.Pmc or SituationSide.Scav
                ? RaidText.BriefExtractsFor(RaidText.BriefSide(side))
                : RaidText.BriefExtractsBothSides,
            extracts,
            requirements,
            squad,
            loot,
            situation.Phase.Because);
    }
}
