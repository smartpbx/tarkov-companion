namespace TarkovCompanion.Core.Domain.Planning;

/// <summary>
/// How many of an item the player is recorded as holding, where that may not be recorded at all.
/// </summary>
/// <remarks>
/// The profile's holdings are a dictionary the player fills in, and most items are not in it.
/// Every Plan page read it with <c>GetValueOrDefault</c>, so an item nobody had counted came back
/// as 0 and was shown as "0 / 5" and "0 held": a claim that the stash is empty of it, made from
/// having no information. Null is "not recorded" and stays null to the screen. A recorded 0 is a
/// real answer and stays 0.
///
/// Nothing is taken off an unknown. What is left to get is the whole need, not because the player
/// holds none but because nothing is known that would make it less.
/// </remarks>
public static class HeldCount
{
    /// <summary>The recorded holding of one item, or null where none is recorded.</summary>
    public static int? Of(IReadOnlyDictionary<string, int> held, string itemId)
    {
        ArgumentNullException.ThrowIfNull(held);
        return held.TryGetValue(itemId, out var count) ? count : null;
    }

    /// <summary>
    /// The recorded holding across items that would each do. Null only where none of them is
    /// recorded; otherwise the sum of those that are, since an unrecorded alternative adds nothing known.
    /// </summary>
    public static int? OfAny(IReadOnlyDictionary<string, int> held, IEnumerable<string> itemIds)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(itemIds);
        int? total = null;
        foreach (var itemId in itemIds)
        {
            if (held.TryGetValue(itemId, out var count))
            {
                total = (total ?? 0) + count;
            }
        }

        return total;
    }

    /// <summary>What is still to get: the need less a known holding, and the whole need against an unknown one.</summary>
    public static int Remaining(int need, int? held) => Math.Max(0, need - (held ?? 0));

    /// <summary>Whether the need is known to be met. An unknown holding is not known to meet anything.</summary>
    public static bool Meets(int need, int? held) => held >= need;
}
