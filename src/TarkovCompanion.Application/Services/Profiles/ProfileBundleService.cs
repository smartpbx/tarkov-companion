using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Profiles;

/// <summary>Where an import lands: the profile in use, or a new one made from the file.</summary>
public enum ProfileBundleTarget
{
    ActiveProfile,
    NewProfile,
}

/// <summary>A file read and compared against the target, waiting for the player to confirm it.</summary>
/// <param name="CanImportIntoActive">
/// False when the file tracks another game mode than the profile in use: PvE progress written
/// into a PvP profile is exactly the cross-mode mix #269 exists to prevent.
/// </param>
public sealed record ProfileBundlePreview(
    ProfileBundle Bundle,
    ProfileBundleTarget Target,
    string TargetName,
    IReadOnlyList<ProfileBundleChange> Changes,
    bool CanImportIntoActive,
    ProfileBundleRefusal? Refusal);

/// <summary>
/// [#314] Why a file cannot go into the active profile, for the App to word: there is no active
/// profile (<paramref name="TargetName"/> null), or the modes differ.
/// </summary>
public sealed record ProfileBundleRefusal(
    Core.Domain.Profiles.ProfileGameMode FileMode,
    string? TargetName,
    Core.Domain.Profiles.ProfileGameMode? TargetMode);

/// <summary>
/// Gathers the active profile's progress into a <see cref="ProfileBundle"/> and writes one back,
/// through the same stores every page reads, so an import is visible everywhere at once.
/// </summary>
/// <remarks>
/// The profile workspace is reached through two delegates rather than the management service
/// itself: the one fact needed from it is "which profile is active, called what, in which mode",
/// and the one action is "create this profile and make it active".
/// </remarks>
public sealed class ProfileBundleService(
    IPlayerProfileService players,
    IQuestProgressStore quests,
    IRaidHistoryService raids,
    Func<CancellationToken, Task<ProfileBundleIdentity?>> activeIdentity,
    Func<ProfileBundleIdentity, CancellationToken, Task> createAndActivate,
    TimeProvider? clock = null,
    // [#269] Places each exported raid in a wipe; absent, every raid is the profile's one wipe.
    Core.Domain.Profiles.IRaidContextSource? raidContext = null)
{
    private const string ImportSource = "ProfileImport";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The active profile as a file.</summary>
    public async Task<ProfileBundle> ExportAsync(CancellationToken cancellationToken)
    {
        var identity = await activeIdentity(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("There is no active profile to export.");
        var player = await players.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        return await GatherAsync(identity, player, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a file and says what importing it into <paramref name="target"/> would change. Writes nothing.</summary>
    public async Task<ProfileBundlePreview> PreviewAsync(string json, ProfileBundleTarget target, CancellationToken cancellationToken)
    {
        var bundle = ProfileBundleCodec.Read(json);
        var identity = await activeIdentity(cancellationToken).ConfigureAwait(false);
        var sameMode = identity is not null && identity.Mode == bundle.Profile.Mode;
        var refusal = target == ProfileBundleTarget.ActiveProfile && !sameMode
            ? new ProfileBundleRefusal(bundle.Profile.Mode, identity?.Name, identity?.Mode)
            : null;

        ProfileBundle current;
        if (target == ProfileBundleTarget.NewProfile || identity is null)
        {
            current = Empty(bundle.Profile);
        }
        else
        {
            var player = await players.GetActiveAsync(cancellationToken).ConfigureAwait(false);
            current = await GatherAsync(identity, player, cancellationToken).ConfigureAwait(false);
        }

        return new(
            bundle,
            target,
            target == ProfileBundleTarget.NewProfile ? bundle.Profile.Name : identity?.Name ?? string.Empty,
            ProfileBundleChanges.Compare(current, bundle),
            sameMode,
            refusal);
    }

    /// <summary>Writes a previewed file into its target and returns the name of the profile it landed in.</summary>
    public async Task<string> ImportAsync(ProfileBundlePreview preview, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Refusal is { } refusal)
        {
            throw new InvalidOperationException(refusal.TargetName is null
                ? "There is no active profile; import it as a new one."
                : $"This file is {Mode(refusal.FileMode)} and {refusal.TargetName} is {Mode(refusal.TargetMode ?? Core.Domain.Profiles.ProfileGameMode.Unknown)}. Import it as a new profile.");
        }

        var bundle = preview.Bundle;
        if (preview.Target == ProfileBundleTarget.NewProfile)
        {
            await createAndActivate(bundle.Profile, cancellationToken).ConfigureAwait(false);
        }

        var identity = await activeIdentity(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("There is no active profile to import into.");
        if (identity.Mode != bundle.Profile.Mode)
        {
            throw new InvalidOperationException($"This file is {Mode(bundle.Profile.Mode)} and {identity.Name} is {Mode(identity.Mode)}.");
        }

        var now = _clock.GetUtcNow();
        var player = await players.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var before = await GatherAsync(identity, player, cancellationToken).ConfigureAwait(false);
        var progress = bundle.Progress;
        player = player with
        {
            Level = progress.Level,
            Faction = progress.Faction,
            Edition = progress.Edition,
            TraderLevels = new Dictionary<string, int>(progress.TraderLevels, StringComparer.Ordinal),
            CompletedTaskIds = progress.CompletedTaskIds.ToHashSet(StringComparer.Ordinal),
            ObjectiveProgress = new Dictionary<string, int>(progress.ObjectiveProgress, StringComparer.Ordinal),
            HideoutStationLevels = new Dictionary<string, int>(progress.HideoutStationLevels, StringComparer.Ordinal),
            WishlistItemIds = progress.WishlistItemIds.ToHashSet(StringComparer.Ordinal),
            OwnedItemCounts = new Dictionary<string, int>(progress.OwnedItemCounts, StringComparer.Ordinal),
            EventItemStates = new Dictionary<string, Core.Domain.Events.EventItemState>(progress.EventItemStates, StringComparer.Ordinal),
            ItemOverrides = new Dictionary<string, string>(progress.ItemOverrides, StringComparer.Ordinal),
            UpdatedUtc = now,
        };
        await players.SaveAsync(player, cancellationToken).ConfigureAwait(false);

        await ImportQuestsAsync(identity, player, before.Quests, bundle.Quests, now, cancellationToken).ConfigureAwait(false);
        // [#269] Only the raids played in this profile's mode and wipe; the preview said how many are left out.
        await ImportRaidsAsync(
            player,
            before.Raids,
            ProfileBundleChanges.SameWipe(ProfileBundleChanges.SameMode(bundle.Raids, identity.Mode), identity.Wipe),
            cancellationToken).ConfigureAwait(false);
        return identity.Name;
    }

    private async Task ImportQuestsAsync(
        ProfileBundleIdentity identity,
        PlayerProfile player,
        ProfileBundleQuests current,
        ProfileBundleQuests incoming,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var scope = Scope(player);
        var name = identity.Name;
        var tasks = current.Tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
        foreach (var task in incoming.Tasks.Where(task => !tasks.TryGetValue(task.TaskId, out var local) || local != task))
        {
            await quests.ApplyAsync(
                new SetTaskStateMutation(scope, name, task.TaskId, task.State, QuestProgressActor.Import, ImportSource, Guid.NewGuid(), now),
                cancellationToken).ConfigureAwait(false);
        }

        var objectives = current.Objectives.ToDictionary(objective => objective.ObjectiveId, StringComparer.Ordinal);
        foreach (var objective in incoming.Objectives.Where(objective =>
            !objectives.TryGetValue(objective.ObjectiveId, out var local) || local != objective))
        {
            await quests.ApplyAsync(
                new SetObjectiveProgressMutation(
                    scope, name, objective.ObjectiveId, objective.State, objective.Count,
                    QuestProgressActor.Import, ImportSource, Guid.NewGuid(), now),
                cancellationToken).ConfigureAwait(false);
        }

        var holdings = current.Holdings.ToHashSet();
        foreach (var holding in incoming.Holdings.Where(holding => !holdings.Contains(holding)))
        {
            await quests.ApplyAsync(
                new SetItemHoldingMutation(
                    scope, name, holding.ItemId, holding.FoundInRaid, holding.Count,
                    QuestProgressActor.Import, ImportSource, Guid.NewGuid(), now),
                cancellationToken).ConfigureAwait(false);
        }

        var pins = current.Pins.ToHashSet();
        foreach (var pin in incoming.Pins.Where(pin => !pins.Contains(pin)))
        {
            await quests.ApplyAsync(
                new SetQuestPinMutation(
                    scope, name, pin.TargetKind, pin.TargetId, IsPinned: true, pin.SortOrder, pin.Note,
                    QuestProgressActor.Import, ImportSource, Guid.NewGuid(), now),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ImportRaidsAsync(
        PlayerProfile player,
        IReadOnlyList<ProfileBundleRaid> current,
        IReadOnlyList<ProfileBundleRaid> incoming,
        CancellationToken cancellationToken)
    {
        var added = ProfileBundleChanges.NewRaids(current, incoming);
        if (added.Count == 0)
        {
            return;
        }

        // A raid id is global, not per profile: the same file imported into a second profile on
        // this machine would collide with the first, so an id already in use gets a new one.
        var taken = (await raids.ListAsync(cancellationToken).ConfigureAwait(false)).Select(raid => raid.Id).ToHashSet();
        foreach (var raid in added)
        {
            var id = raid.Id == Guid.Empty || taken.Contains(raid.Id) ? Guid.NewGuid() : raid.Id;
            taken.Add(id);
            await raids.StartAsync(
                new RaidHistoryEntry(id, player.Id, raid.MapId, raid.Mode, raid.StartedUtc, raid.EndedUtc, raid.Outcome, raid.Notes),
                cancellationToken).ConfigureAwait(false);
            if (raid.Manual is { IsEmpty: false } manual)
            {
                await raids.SetManualMetadataAsync(id, manual, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ProfileBundle> GatherAsync(ProfileBundleIdentity identity, PlayerProfile player, CancellationToken cancellationToken)
    {
        var snapshot = await quests.GetAsync(Scope(player), cancellationToken).ConfigureAwait(false);
        var bundleRaids = new List<ProfileBundleRaid>();
        var context = raidContext?.Current();
        foreach (var raid in (await raids.ListAsync(cancellationToken).ConfigureAwait(false)).Where(raid => raid.ProfileId == player.Id))
        {
            var manual = await raids.GetManualMetadataAsync(raid.Id, cancellationToken).ConfigureAwait(false);
            var wipe = context is null ? identity.Wipe : context.WipeOf(raid);
            bundleRaids.Add(new(raid.Id, raid.MapId, raid.Mode, raid.StartedUtc, raid.EndedUtc, raid.Outcome, raid.Notes, manual, wipe));
        }

        return new(
            ProfileBundleCodec.FormatId,
            ProfileBundleCodec.CurrentVersion,
            _clock.GetUtcNow(),
            identity,
            new ProfileBundleProgress(
                player.Level,
                player.Faction,
                player.Edition,
                Sorted(player.TraderLevels),
                [.. player.CompletedTaskIds.Order(StringComparer.Ordinal)],
                Sorted(player.ObjectiveProgress),
                Sorted(player.HideoutStationLevels),
                [.. player.WishlistItemIds.Order(StringComparer.Ordinal)],
                Sorted(player.OwnedItemCounts),
                Sorted(player.EventItemStates),
                Sorted(player.ItemOverrides)),
            new ProfileBundleQuests(
                [.. snapshot.Tasks.Values.OrderBy(task => task.TaskId, StringComparer.Ordinal)
                    .Select(task => new ProfileBundleTask(task.TaskId, task.State))],
                [.. snapshot.Objectives.Values.OrderBy(objective => objective.ObjectiveId, StringComparer.Ordinal)
                    .Select(objective => new ProfileBundleObjective(objective.ObjectiveId, objective.State, objective.Count))],
                [.. snapshot.ItemHoldings.Select(holding => new ProfileBundleHolding(holding.ItemId, holding.FoundInRaid, holding.Count))],
                [.. snapshot.Pins.Select(pin => new ProfileBundlePin(pin.TargetKind, pin.TargetId, pin.SortOrder, pin.Note))]),
            bundleRaids);
    }

    private static ProfileBundle Empty(ProfileBundleIdentity identity) => new(
        ProfileBundleCodec.FormatId,
        ProfileBundleCodec.CurrentVersion,
        DateTimeOffset.UnixEpoch,
        identity,
        new ProfileBundleProgress(
            1, Faction.Unknown, null,
            new Dictionary<string, int>(), [], new Dictionary<string, int>(), new Dictionary<string, int>(), [],
            new Dictionary<string, int>(), new Dictionary<string, Core.Domain.Events.EventItemState>(), new Dictionary<string, string>()),
        ProfileBundleQuests.Empty,
        []);

    private static string Mode(Core.Domain.Profiles.ProfileGameMode mode) => mode switch
    {
        Core.Domain.Profiles.ProfileGameMode.Pvp => "PvP",
        Core.Domain.Profiles.ProfileGameMode.Pve => "PvE",
        Core.Domain.Profiles.ProfileGameMode.Seasonal => "Seasonal",
        _ => "an unknown mode",
    };

    private static QuestProfileScope Scope(PlayerProfile player) => new(player.Id, player.GameMode, player.ProfileGeneration);

    private static SortedDictionary<string, T> Sorted<T>(IReadOnlyDictionary<string, T> values) =>
        new(values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
}
