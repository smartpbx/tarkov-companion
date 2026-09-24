using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Profiles;

/// <summary>Reads any profile's progress without making it the active one.</summary>
/// <remarks>
/// The workspace record's own progress is what the profile was created with and nothing writes it
/// after; the progress the app shows lives in each profile's own file. Comparing records would
/// have shown every profile at level 1.
/// </remarks>
public interface IProfileProgressReader
{
    Task<PlayerProfile> ReadAsync(ProfileRecord profile, CancellationToken cancellationToken);
}

/// <summary>One profile's side of Setup › Game &amp; Profile's compare (#269).</summary>
/// <param name="HideoutLevels">Every station's level added up.</param>
/// <param name="KeysOwned">Distinct keys with a count above zero.</param>
/// <param name="AmmoRounds">Rounds owned, loose and in packs.</param>
/// <param name="Survived">Raids recorded as survived or run through.</param>
public sealed record ProfileCompareSide(
    Guid ProfileId,
    string Name,
    ProfileGameMode Mode,
    string Wipe,
    int Level,
    int QuestsCompleted,
    int HideoutLevels,
    int KeysOwned,
    int AmmoRounds,
    int Raids,
    int Survived,
    int Died);

/// <summary>What a compare counts, from the same stores every page reads. Read-only.</summary>
public sealed class ProfileCompareService(
    IProfileProgressReader progress,
    IQuestProgressStore quests,
    IRaidHistoryService raids,
    IItemFactCatalog catalog)
{
    public async Task<(ProfileCompareSide Left, ProfileCompareSide Right)> CompareAsync(
        ProfileRecord left,
        ProfileRecord right,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var keys = (await catalog.GetKeyFactsAsync(cancellationToken).ConfigureAwait(false))
            .Select(key => key.ItemId).ToHashSet(StringComparer.Ordinal);
        var ammo = (await catalog.GetAmmoAsync(cancellationToken).ConfigureAwait(false))
            .Select(round => round.ItemId).ToHashSet(StringComparer.Ordinal);
        var packs = await catalog.GetAmmoPacksAsync(cancellationToken).ConfigureAwait(false);
        var history = await raids.ListAsync(cancellationToken).ConfigureAwait(false);
        return (
            await SideAsync(left, keys, ammo, packs, history, cancellationToken).ConfigureAwait(false),
            await SideAsync(right, keys, ammo, packs, history, cancellationToken).ConfigureAwait(false));
    }

    private async Task<ProfileCompareSide> SideAsync(
        ProfileRecord record,
        IReadOnlySet<string> keys,
        IReadOnlySet<string> ammo,
        IReadOnlyList<AmmoPackContents> packs,
        IReadOnlyList<RaidHistoryEntry> history,
        CancellationToken cancellationToken)
    {
        var player = await progress.ReadAsync(record, cancellationToken).ConfigureAwait(false);
        var recorded = await quests.GetAsync(new QuestProfileScope(player.Id, player.GameMode, player.ProfileGeneration), cancellationToken)
            .ConfigureAwait(false);
        return Summarize(record, player, recorded, history, keys, ammo, packs);
    }

    /// <summary>The counts for one profile. Pure, so the arithmetic is tested without stores.</summary>
    public static ProfileCompareSide Summarize(
        ProfileRecord record,
        PlayerProfile player,
        QuestProgressSnapshot? recorded,
        IEnumerable<RaidHistoryEntry> history,
        IReadOnlySet<string> keyIds,
        IReadOnlySet<string> ammoIds,
        IReadOnlyList<AmmoPackContents> packs)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(player);

        // A quest is done if either record says so: the progress file (typed in, or V1) or the
        // quest store (the game's log and imports). Counted once when both do.
        var completed = new HashSet<string>(player.CompletedTaskIds, StringComparer.Ordinal);
        foreach (var task in recorded?.Tasks.Values ?? [])
        {
            if (task.State == RecordedTaskState.Completed)
            {
                completed.Add(task.TaskId);
            }
        }

        var packSizes = packs
            .Where(pack => ammoIds.Contains(pack.AmmoItemId))
            .GroupBy(pack => pack.PackItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(pack => Math.Max(0, pack.Quantity)), StringComparer.Ordinal);
        long rounds = 0;
        var keysOwned = 0;
        foreach (var (itemId, count) in player.OwnedItemCounts)
        {
            if (count <= 0)
            {
                continue;
            }

            if (keyIds.Contains(itemId))
            {
                keysOwned++;
            }
            else if (ammoIds.Contains(itemId))
            {
                rounds += count;
            }
            else if (packSizes.TryGetValue(itemId, out var size))
            {
                rounds += (long)count * size;
            }
        }

        var id = record.Context.Identity.ProfileId;
        var own = history.Where(raid => raid.ProfileId == id).ToArray();
        var outcomes = own.Select(raid => RaidCoverage.Classify(raid.Outcome)).ToArray();
        return new(
            id,
            record.Name,
            record.Context.Mode,
            record.Context.WipeSeason.Value,
            player.Level,
            completed.Count,
            player.HideoutStationLevels.Values.Where(level => level > 0).Sum(),
            keysOwned,
            (int)Math.Min(int.MaxValue, rounds),
            own.Length,
            outcomes.Count(RaidCoverage.IsExtracted),
            outcomes.Count(bucket => bucket == RaidOutcomeBucket.Died));
    }
}
