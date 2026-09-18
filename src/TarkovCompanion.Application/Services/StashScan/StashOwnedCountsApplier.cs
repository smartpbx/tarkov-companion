using System.Globalization;
using TarkovCompanion.Core.Abstractions;

namespace TarkovCompanion.Application.Services.StashScan;

/// <summary>What a finished scan did to the profile's owned counts.</summary>
public sealed record StashOwnedCountsChange(int Raised, int Lowered, int Unchanged, bool WasExact)
{
    public static StashOwnedCountsChange None { get; } = new(0, 0, 0, false);

    public string Summary => (Raised + Lowered) switch
    {
        0 when Unchanged == 0 => "No named items, so owned counts are unchanged.",
        0 => "Owned counts already matched.",
        var changed => string.Create(
            CultureInfo.CurrentCulture,
            $"Owned counts updated for {changed} item{(changed == 1 ? string.Empty : "s")}."),
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
/// with every tile named and every screenshot placed may lower one. Items the scan did not see
/// are never touched either way: they may be in a case, on the character, or below the last
/// screenshot.
/// </para>
/// </remarks>
public sealed class StashOwnedCountsApplier(IPlayerProfileService profiles)
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

        if (raised + lowered > 0)
        {
            await profiles.SaveAsync(profile with { OwnedItemCounts = owned }, cancellationToken).ConfigureAwait(false);
        }

        return new(raised, lowered, unchanged, exact);
    }
}
