namespace TarkovCompanion.Core.Domain.Planning;

/// <summary>How a quest asks for an item, which decides when the player has to act on it.</summary>
public enum RequirementHandling
{
    /// <summary>Carried or worn into the raid (a key, a weapon, a marker) and not given up.</summary>
    Bring,

    /// <summary>Handed to the trader, found in raid or not.</summary>
    HandIn,

    /// <summary>Handed to the trader, and only counts when it was found in a raid.</summary>
    FindInRaid,
}

/// <summary>
/// One thing a set of quest objectives asks the player to have, and how much of it they hold.
/// </summary>
/// <param name="ItemIds">
/// The items that satisfy it. More than one means "this or that": the requirement is met by any
/// of them, and the first names it.
/// </param>
/// <param name="Need">How many are still needed. One for something carried in, however many objectives ask.</param>
/// <param name="Have">How many the player holds, summed over the alternatives.</param>
public sealed record PlannedRequirement(
    IReadOnlyList<string> ItemIds,
    RequirementHandling Handling,
    int Need,
    int Have)
{
    /// <summary>The item that names the requirement.</summary>
    public string PrimaryItemId => ItemIds[0];

    /// <summary>How many further items would do instead of the primary one.</summary>
    public int AlternativeCount => ItemIds.Count - 1;

    public bool IsSatisfied => Have >= Need;

    public int Remaining => Math.Max(0, Need - Have);
}

/// <summary>Which quest requirement fields name something to carry, and which are not items to have at all.</summary>
public static class QuestItemTargetFields
{
    /// <remarks>
    /// The split is the whole point of having two lines in a pre-raid brief. A key you forgot is a
    /// raid you cannot finish; an item you meant to hand in is a raid you finish and then repeat.
    /// Those are different mistakes and they are made at different moments, so they are not one list.
    ///
    /// A marker is carried in and left behind rather than handed over, so it belongs here.
    /// "Not wearing" belongs here too: it is a decision made at the same screen, in the same
    /// minute, about the same rig.
    /// </remarks>
    private static readonly string[] CarriedIn =
    [
        "requiredKeys",
        "usingWeapon",
        "usingWeaponMods",
        "wearing",
        "notWearing",
        "markerItem",
    ];

    /// <summary>Whether a requirement field names something carried or worn rather than handed over.</summary>
    public static bool IsCarriedIn(string sourceField) => CarriedIn.Contains(sourceField, StringComparer.Ordinal);

    /// <summary>
    /// Whether a requirement field is a condition on the raid rather than an item to hold: "not
    /// wearing" and container-content checks name nothing the player could go and get.
    /// </summary>
    public static bool IsCondition(string sourceField) =>
        sourceField is "notWearing" or "attributes" or "containsAll" or "containsOne";
}
