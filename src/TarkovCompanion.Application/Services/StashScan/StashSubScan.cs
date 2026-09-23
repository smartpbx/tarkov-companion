using System.Globalization;
using TarkovCompanion.Application.Services.Catalogs;

namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>What a guided scan covers: the whole stash, or the cases of one kind.</summary>
public enum StashScanKind
{
    Full,
    Ammo,
    Keys,
}

/// <summary>
/// The Ammo and Keys sub-scans: one screenshot per open case (an ammo case, a key tool), each read
/// as its own container, whose counts only ever raise the owned counts of that kind.
/// </summary>
/// <remarks>
/// <para>
/// A full scan scrolls the stash, and ammo and keys live in cases a full scan sees closed: the
/// assembler reports a closed case as guidance, never as contents, so the Ammo and Keys pages had
/// no "you own" to show. A case window is read by <c>CaseWindowLocator</c>, not by the stash
/// panel finder.
/// </para>
/// <para>
/// Raise only, because a case is not the whole stash: rounds loose in the stash or in another
/// case the player did not open are still there. Only items of the scan's kind are touched, so a
/// case holding a stray item, or a misread tile, cannot move an unrelated count.
/// </para>
/// </remarks>
public static class StashSubScan
{
    /// <summary>The container a sub-scan's screenshot is saved under: one per case.</summary>
    public static string CasePath(StashScanKind kind, int ordinal) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(kind == StashScanKind.Keys ? "key-case" : "ammo-case")}-{ordinal + 1}");

    /// <summary>The catalog items a sub-scan may count: rounds and ammo packs, or keys.</summary>
    public static async Task<IReadOnlySet<string>?> ItemsOfAsync(
        StashScanKind kind,
        IItemFactCatalog? catalog,
        CancellationToken cancellationToken)
    {
        if (catalog is null || kind == StashScanKind.Full)
        {
            return null;
        }

        if (kind == StashScanKind.Keys)
        {
            return (await catalog.GetKeyFactsAsync(cancellationToken).ConfigureAwait(false))
                .Select(key => key.ItemId)
                .ToHashSet(StringComparer.Ordinal);
        }

        var rounds = await catalog.GetAmmoAsync(cancellationToken).ConfigureAwait(false);
        var packs = await catalog.GetAmmoPacksAsync(cancellationToken).ConfigureAwait(false);
        return rounds.Select(round => round.ItemId)
            .Concat(packs.Select(pack => pack.PackItemId))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static string Headline(StashScanKind kind, int cases, StashReconstruction reconstruction) => cases == 0
        ? kind == StashScanKind.Keys ? "Keys scan started" : "Ammo scan started"
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{cases} case{(cases == 1 ? string.Empty : "s")} · {reconstruction.KnownTiles} named");

    public static string NextStep(StashScanKind kind, GuidedStashFrameOutcome outcome, int cases)
    {
        var what = kind == StashScanKind.Keys ? "key tool or keycard case" : "ammo case";
        var article = kind == StashScanKind.Keys ? "a" : "an";
        return outcome switch
        {
            GuidedStashFrameOutcome.NoGrid => $"No open case in that one. Open your {what} and take it again.",
            GuidedStashFrameOutcome.Duplicate => "Same screenshot as before — skipped.",
            _ when cases == 0 => $"Open {article} {what} in your stash and take a screenshot.",
            _ => $"Open the next {what} and take a screenshot, or press Finish. One screenshot per case.",
        };
    }

    public static string SavedHeadline(StashScanKind kind) => kind switch
    {
        StashScanKind.Ammo => "Ammo scan saved",
        StashScanKind.Keys => "Keys scan saved",
        _ => "Scan saved",
    };
}
