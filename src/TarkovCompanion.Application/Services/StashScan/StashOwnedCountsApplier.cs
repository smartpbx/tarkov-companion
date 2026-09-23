using System.Globalization;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>What a finished scan did to the profile's owned counts.</summary>
public sealed record StashOwnedCountsChange(int Raised, int Lowered, int Unchanged, bool WasExact)
{
    public static StashOwnedCountsChange None { get; } = new(0, 0, 0, false);

    /// <summary>
    /// How many needed items an exact scan did not see and therefore recorded as none held.
    /// Always 0 for a scan that was not exact.
    /// </summary>
    public int RecordedNone { get; init; }

    public string Summary => (Raised + Lowered) switch
    {
        0 when Unchanged == 0 && RecordedNone == 0 => "No named items, so owned counts are unchanged.",
        0 when RecordedNone == 0 => "Owned counts already matched.",
        var changed => string.Create(
            CultureInfo.CurrentCulture,
            $"Owned counts updated for {changed + RecordedNone} item{(changed + RecordedNone == 1 ? string.Empty : "s")}.") +
            (RecordedNone > 0
                ? string.Create(CultureInfo.CurrentCulture, $" {RecordedNone} needed and not seen, so none held.")
                : string.Empty),
    };
}

/// <summary>
/// Writes what a finished stash scan saw into the profile's owned counts, which is where the
/// hideout requirements, the quest item needs and the Keep verdict all read "how many do I have".
/// </summary>
/// <remarks>
/// <para>
/// Until this, a scan was a picture and nothing more: <c>OwnedItemCounts</c> was only ever typed in
/// by hand, and the observed-inventory projection that could have fed it had no caller.
/// </para>
/// <para>
/// A scan's counts are lower bounds whenever any tile is unknown or any screenshot is unplaced -
/// the missing items may be exactly there - so such a scan only ever raises a count. Only a scan
/// with every tile named and every screenshot placed may lower one.
/// </para>
/// <para>
/// An exact scan also answers for what it did not see: an item a quest or a hideout level asks
/// for, which no count has ever been recorded for, is recorded as 0. Until then the planning
/// pages read "?" for it, and "?" after a complete scan was the scan failing to say what it knew.
/// Only needed items, and only ones with no record: the owned counts are not a list of everything
/// the player lacks, and a count somebody typed in (three in a case, say) is theirs to change.
/// Anything short of exact leaves "?" alone, because the item may be exactly where the scan
/// could not read.
/// </para>
/// </remarks>
public sealed class StashOwnedCountsApplier(IPlayerProfileService profiles, IRequirementCatalog? requirements = null)
{
    public async Task<StashOwnedCountsChange> ApplyAsync(StashReconstruction reconstruction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reconstruction);
        if (reconstruction.OwnedCounts.Count == 0)
        {
            return StashOwnedCountsChange.None;
        }

        var exact = reconstruction is { UnknownTiles: 0, UnplacedRegions: 0 };
        var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var owned = new Dictionary<string, int>(profile.OwnedItemCounts, StringComparer.Ordinal);
        var raised = 0;
        var lowered = 0;
        var unchanged = 0;
        foreach (var (itemId, seen) in reconstruction.OwnedCounts)
        {
            var before = owned.GetValueOrDefault(itemId);
            var after = exact ? seen : Math.Max(before, seen);
            if (after > before)
            {
                raised++;
            }
            else if (after < before)
            {
                lowered++;
            }
            else
            {
                unchanged++;
            }

            owned[itemId] = after;
        }

        var recordedNone = 0;
        if (exact && requirements is not null)
        {
            var quest = await requirements.GetQuestRequirementsAsync(cancellationToken).ConfigureAwait(false);
            var hideout = await requirements.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var itemId in quest.Select(need => need.ItemId)
                         .Concat(hideout.Select(need => need.ItemId))
                         .Distinct(StringComparer.Ordinal))
            {
                if (owned.TryAdd(itemId, 0))
                {
                    recordedNone++;
                }
            }
        }

        if (raised + lowered + recordedNone > 0)
        {
            await profiles.SaveAsync(profile with { OwnedItemCounts = owned }, cancellationToken).ConfigureAwait(false);
        }

        return new(raised, lowered, unchanged, exact) { RecordedNone = recordedNone };
    }

    /// <summary>
    /// A case sub-scan's counts: only items in <paramref name="only"/> (every named item when it
    /// is null), and only ever raised, since one case is not the whole stash.
    /// </summary>
    public async Task<StashOwnedCountsChange> ApplyRaisingAsync(
        StashReconstruction reconstruction,
        IReadOnlySet<string>? only,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reconstruction);
        var seenCounts = reconstruction.OwnedCounts
            .Where(pair => only is null || only.Contains(pair.Key))
            .ToArray();
        if (seenCounts.Length == 0)
        {
            return StashOwnedCountsChange.None;
        }

        var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var owned = new Dictionary<string, int>(profile.OwnedItemCounts, StringComparer.Ordinal);
        var raised = 0;
        foreach (var (itemId, seen) in seenCounts)
        {
            if (seen > owned.GetValueOrDefault(itemId))
            {
                owned[itemId] = seen;
                raised++;
            }
        }

        if (raised > 0)
        {
            await profiles.SaveAsync(profile with { OwnedItemCounts = owned }, cancellationToken).ConfigureAwait(false);
        }

        return new(raised, 0, seenCounts.Length - raised, WasExact: false);
    }
}
