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
public sealed record KeepQuestNeed(string TaskId, string TaskName, int Remaining);

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
public sealed record KeepEntry(
    string ItemId,
    KeepGroupKind Group,
    KeepItemFacts Item,
    IReadOnlyList<KeepQuestNeed> QuestNeeds,
    IReadOnlyList<KeepHideoutNeed> HideoutNeeds,
    string? KeyReason)
{
    public string Name => Item.Name;

    public bool IsHighValue => Item.IsHighValue;
}

/// <summary>The computed Keep list: every item some source says to keep, and whether there was any data to compute from.</summary>
public sealed record KeepPlan(bool HasData, IReadOnlyList<KeepEntry> Entries)
{
    /// <summary>Nothing has synced yet, which is different from having synced and finding nothing to keep.</summary>
    public static KeepPlan NoData { get; } = new(false, []);
}
