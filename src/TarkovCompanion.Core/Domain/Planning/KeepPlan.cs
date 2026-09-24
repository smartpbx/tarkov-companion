namespace TarkovCompanion.Core.Domain.Planning;

/// <summary>Which group of the Keep list an item sorts under: the first reason that applies, in this order.</summary>
public enum KeepGroupKind
{
    /// <summary>A quest the player is on (active or pinned) still asks for it.</summary>
    ActiveQuest,

    /// <summary>A quest not yet started still asks for it.</summary>
    Quest,

    /// <summary>The next level of a hideout station asks for more than the player holds.</summary>
    Hideout,

    /// <summary>A key worth keeping for its price or for what needs it.</summary>
    Key,

    /// <summary>Nothing needs it, but it is worth a lot per slot.</summary>
    HighValue,
}

/// <summary>What one quest still asks for of an item, after the progress recorded against it.</summary>
/// <param name="FoundInRaid">
/// Of <paramref name="Remaining"/>, how many only count if found in raid. The split decides whether
/// the item can be bought (flea, trader) or has to be picked up in a raid, so it is kept apart.
/// </param>
public sealed record KeepQuestNeed(string TaskId, string TaskName, int Remaining, int FoundInRaid)
{
    /// <summary>The part that a purchase or a stash copy satisfies.</summary>
    public int NotFoundInRaid => Remaining - FoundInRaid;

    /// <summary>
    /// How many different items would each satisfy this need, this one included. One means only
    /// this item does. Above one, <see cref="Remaining"/> is wanted of any of them together, not of
    /// each: five kinds of water and "3 for Operation Aquarius" is three bottles, not fifteen.
    /// </summary>
    public int AnyOf { get; init; } = 1;

    /// <summary>Whether the player is on this quest (active or pinned), rather than it lying ahead.</summary>
    public bool IsTracked { get; init; }

    /// <summary>
    /// Whether the item is carried in and brought back out (a key), so that one serves this quest
    /// and every other that asks the same. Such needs are not added together.
    /// </summary>
    public bool IsReusable { get; init; }
}

/// <summary>What one hideout station's next build asks for of an item.</summary>
public sealed record KeepHideoutNeed(string StationId, string StationName, int Required);

/// <summary>What the catalog and the market say about an item: what to call it, and where its value sits.</summary>
/// <param name="Tier">S, A, B, C or D by value per slot, or "—" where the item or its price is unknown.</param>
public sealed record KeepItemFacts(string Name, string Tier)
{
    /// <summary>S and A read as "high value"; nothing below that is worth a reason on its own.</summary>
    public bool IsHighValue => Tier is "S" or "A";
}

/// <summary>One item worth keeping, with every source that says so.</summary>
/// <param name="HideoutTotalBuild">
/// How many the whole hideout build asks for across every level of every station, including levels
/// already built. <see cref="HideoutRemaining"/> is the part above the profile's current levels.
/// </param>
public sealed record KeepEntry(
    string ItemId,
    KeepGroupKind Group,
    KeepItemFacts Item,
    IReadOnlyList<KeepQuestNeed> QuestNeeds,
    IReadOnlyList<KeepHideoutNeed> HideoutNeeds,
    KeyReasonCode? KeyReason,
    int HideoutTotalBuild = 0,
    int QuestTotal = 0)
{
    public string Name => Item.Name;

    public bool IsHighValue => Item.IsHighValue;

    /// <summary>How many the quests still open ask for, across all of them.</summary>
    public int QuestRemaining => Total(QuestNeeds);

    /// <summary>Of <see cref="QuestRemaining"/>, how many the quests the player is on ask for.</summary>
    public int QuestRemainingTracked => Total(QuestNeeds.Where(need => need.IsTracked));

    /// <summary>
    /// What is used up adds up; what is carried back out is one, however many quests ask. The one
    /// is on top of what is used up, because a key handed in for one quest is gone for the next.
    /// </summary>
    private static int Total(IEnumerable<KeepQuestNeed> needs)
    {
        var all = needs.ToArray();
        return all.Where(need => !need.IsReusable).Sum(need => need.Remaining) + (all.Any(need => need.IsReusable) ? 1 : 0);
    }

    /// <summary>Of <see cref="QuestRemaining"/>, how many must be found in raid.</summary>
    public int QuestFoundInRaid => QuestNeeds.Sum(need => need.FoundInRaid);

    /// <summary>How many the hideout levels above the ones built ask for, across all stations, before the player's stock is taken off.</summary>
    public int HideoutRemaining => HideoutNeeds.Sum(need => need.Required);

    /// <summary>
    /// How many the player is recorded as holding, or null where nothing is recorded. Null is not
    /// zero: a holding nobody has entered is unknown, and "0 held" would be a claim.
    /// </summary>
    public int? Held { get; init; }
}

/// <summary>The computed Keep list: every item some source says to keep, and whether there was any data to compute from.</summary>
public sealed record KeepPlan(bool HasData, IReadOnlyList<KeepEntry> Entries)
{
    /// <summary>Nothing has synced yet, which is different from having synced and finding nothing to keep.</summary>
    public static KeepPlan NoData { get; } = new(false, []);
}
