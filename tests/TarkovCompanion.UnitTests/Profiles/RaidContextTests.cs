using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Raids;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.Profiles;

/// <summary>[#269] Which raids belong to the active profile's mode and wipe, and what carries that out.</summary>
public sealed class RaidContextTests
{
    private static readonly DateTimeOffset Changed = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    private static ProfileRecord Relabelled(ProfileRecord profile, string wipe, DateTimeOffset at) => new(
        new ProfileContext(profile.Context.Identity, profile.Context.Mode, new WipeSeason(wipe), profile.Context.Locale, profile.Context.DataSnapshot),
        profile.Name,
        profile.Progress,
        profile.Lifecycle,
        at,
        ProfileWipeHistory.Record(profile.ExtensionJson, profile.Context.WipeSeason.Value, wipe, at));

    private static RaidHistoryEntry Raid(ProfileRecord owner, string mode, DateTimeOffset? started, string? outcome = "Survived") =>
        new(Guid.NewGuid(), owner.Context.Identity.ProfileId, "customs", mode, started, started?.AddMinutes(30), outcome, null);

    [Fact]
    public void A_profile_whose_label_never_changed_has_every_raid_in_its_one_wipe()
    {
        var profile = Profile(1, "g", ProfileGameMode.Pvp, "x", wipe: "Wipe 3");

        Assert.Equal("Wipe 3", ProfileWipeHistory.WipeAt(profile, Changed.AddDays(-400)));
        Assert.Equal("Wipe 3", ProfileWipeHistory.WipeAt(profile, null));
    }

    [Fact]
    public void A_raid_before_the_label_changed_is_in_the_old_wipe_and_one_after_in_the_new()
    {
        var profile = Relabelled(Profile(1, "g", ProfileGameMode.Pvp, "x", wipe: "Wipe 3"), "Wipe 4", Changed);

        Assert.Equal("Wipe 3", ProfileWipeHistory.WipeAt(profile, Changed.AddHours(-1)));
        Assert.Equal("Wipe 4", ProfileWipeHistory.WipeAt(profile, Changed.AddHours(1)));
        Assert.Null(ProfileWipeHistory.WipeAt(profile, null));
    }

    [Fact]
    public void Two_changes_place_a_raid_between_them_in_the_middle_wipe()
    {
        var once = Relabelled(Profile(1, "g", ProfileGameMode.Pvp, "x", wipe: "Wipe 3"), "Wipe 4", Changed);
        var twice = Relabelled(once, "Wipe 5", Changed.AddDays(90));

        Assert.Equal("Wipe 3", ProfileWipeHistory.WipeAt(twice, Changed.AddDays(-1)));
        Assert.Equal("Wipe 4", ProfileWipeHistory.WipeAt(twice, Changed.AddDays(30)));
        Assert.Equal("Wipe 5", ProfileWipeHistory.WipeAt(twice, Changed.AddDays(91)));
    }

    [Fact]
    public void A_history_that_no_longer_ends_in_the_profiles_label_is_not_used()
    {
        var relabelled = Relabelled(Profile(1, "g", ProfileGameMode.Pvp, "x", wipe: "Wipe 3"), "Wipe 4", Changed);
        // Some other writer set the label without a mark.
        var overtaken = new ProfileRecord(
            new ProfileContext(relabelled.Context.Identity, relabelled.Context.Mode, new WipeSeason("Wipe 9"), relabelled.Context.Locale, relabelled.Context.DataSnapshot),
            relabelled.Name, relabelled.Progress, relabelled.Lifecycle, Changed, relabelled.ExtensionJson);

        Assert.Equal("Wipe 9", ProfileWipeHistory.WipeAt(overtaken, Changed.AddDays(-5)));
    }

    [Fact]
    public void Recording_keeps_other_extension_fields_and_ignores_a_damaged_list()
    {
        var json = ProfileWipeHistory.Record("""{"other":1,"wipes":"not a list"}""", "A", "B", Changed);

        Assert.Contains("\"other\":1", json, StringComparison.Ordinal);
        var marks = ProfileWipeHistory.Read(json);
        Assert.Equal([new WipeMark("A", null), new WipeMark("B", Changed)], marks);
    }

    [Fact]
    public void Match_names_why_a_raid_is_left_out()
    {
        var pvp = Relabelled(Profile(1, "g", ProfileGameMode.Pvp, "x", wipe: "Wipe 3"), "Wipe 4", Changed);
        var other = Profile(2, "h", ProfileGameMode.Pvp, "x", wipe: "Wipe 4");

        Assert.Equal(RaidContextMatch.Same, RaidContextRules.Match(Raid(pvp, "Regular", Changed.AddDays(1)), pvp));
        Assert.Equal(RaidContextMatch.OtherMode, RaidContextRules.Match(Raid(pvp, "Pve", Changed.AddDays(1)), pvp));
        Assert.Equal(RaidContextMatch.OtherWipe, RaidContextRules.Match(Raid(pvp, "Regular", Changed.AddDays(-1)), pvp));
        Assert.Equal(RaidContextMatch.OtherProfile, RaidContextRules.Match(Raid(other, "Regular", Changed.AddDays(1)), pvp));
    }

    [Fact]
    public void An_unreadable_mode_or_unplaceable_wipe_is_not_evidence_of_another_context()
    {
        var pvp = Relabelled(Profile(1, "g", ProfileGameMode.Pvp, "x", wipe: "Wipe 3"), "Wipe 4", Changed);

        Assert.True(RaidContextRules.InContext(Raid(pvp, "Pmc", Changed.AddDays(1)), pvp));
        Assert.True(RaidContextRules.InContext(Raid(pvp, "Regular", started: null), pvp));
    }

    [Fact]
    public async Task Changing_a_profiles_wipe_label_records_when_it_changed()
    {
        var store = new MemoryProfileStore();
        using var service = new ProfileContextService(store, new ProfileClock(Changed));
        var profile = Profile(3, "g", ProfileGameMode.Pvp, "x", wipe: "Wipe 3");
        await service.CreateAsync(Request(profile), CancellationToken.None);

        var updated = await service.UpdateModeAndWipeAsync(profile.Context.Identity.ProfileId, ProfileGameMode.Pvp, new WipeSeason("Wipe 4"), CancellationToken.None);

        var record = updated.Profiles.Single();
        Assert.Equal([new WipeMark("Wipe 3", null), new WipeMark("Wipe 4", Changed)], ProfileWipeHistory.Read(record.ExtensionJson));
        Assert.Equal(RaidContextMatch.OtherWipe, RaidContextRules.Match(Raid(record, "Regular", Changed.AddDays(-2)), record));
    }

    [Fact]
    public void Compare_counts_only_raids_in_the_profiles_mode_and_current_wipe()
    {
        var record = Relabelled(Profile(1, "g", ProfileGameMode.Pvp, "x", wipe: "Wipe 3"), "Wipe 4", Changed);
        var id = record.Context.Identity.ProfileId;
        var player = new PlayerProfile(
            id, "p", Core.Common.GameMode.Regular, 10, Faction.Usec, null,
            new Dictionary<string, int>(), new HashSet<string>(), new Dictionary<string, int>(), new Dictionary<string, int>(),
            new HashSet<string>(), new Dictionary<string, int>(), new Dictionary<string, Core.Domain.Events.EventItemState>(),
            new Dictionary<string, string>(), Changed);
        RaidHistoryEntry[] raids =
        [
            Raid(record, "Regular", Changed.AddDays(1), "Survived"),
            Raid(record, "Regular", Changed.AddDays(2), "Killed"),
            Raid(record, "Pve", Changed.AddDays(2), "Survived"),
            Raid(record, "Regular", Changed.AddDays(-20), "Survived"),
        ];

        var side = ProfileCompareService.Summarize(record, player, null, raids, new HashSet<string>(), new HashSet<string>(), []);

        Assert.Equal(2, side.Raids);
        Assert.Equal(1, side.Survived);
    }

    [Fact]
    public void An_import_preview_flags_raids_the_file_places_in_another_wipe()
    {
        var identity = new ProfileBundleIdentity("Main", ProfileGameMode.Pvp, "Wipe 4");
        var incoming = Bundle(identity,
        [
            BundleRaid("Regular", "Wipe 4"),
            BundleRaid("Regular", "Wipe 3"),
            BundleRaid("Regular", null),
            BundleRaid("Pve", "Wipe 4"),
        ]);

        var changes = ProfileBundleChanges.Compare(Bundle(identity, []), incoming);

        Assert.Contains(changes, change => change.Area == ProfileBundleArea.Raids && ProfileWords.After(change) == "2");
        Assert.Contains(changes, change => change.Area == ProfileBundleArea.RaidsFromAnotherMode && change.Now == "1");
        Assert.Contains(changes, change => change.Area == ProfileBundleArea.RaidsFromAnotherWipe && change.Now == "1");
    }

    [Fact]
    public void A_bundle_raids_wipe_survives_the_file_and_an_older_file_reads_as_unknown()
    {
        var identity = new ProfileBundleIdentity("Main", ProfileGameMode.Pvp, "Wipe 4");
        var json = ProfileBundleCodec.Write(Bundle(identity, [BundleRaid("Regular", "Wipe 3")]));

        Assert.Equal("Wipe 3", Assert.Single(ProfileBundleCodec.Read(json).Raids).Wipe);
        var older = ProfileBundleCodec.Read(json.Replace("\"wipe\": \"Wipe 3\"", "\"other\": 1", StringComparison.Ordinal));
        Assert.Null(Assert.Single(older.Raids).Wipe);
    }

    [Fact]
    public async Task The_raid_export_writes_each_raids_wipe_beside_its_mode()
    {
        var record = new RaidExportRecord(
            new RaidHistoryEntry(Guid.NewGuid(), Id(1), "customs", "Regular", Changed, Changed.AddMinutes(20), null, null),
            new RaidFactSources(RaidFactKind.Observed, RaidFactKind.Inferred, RaidFactKind.Observed, RaidFactKind.Observed, RaidFactKind.Unknown, RaidFactKind.Unknown),
            [])
        {
            Wipe = "Wipe 4",
        };
        await using var stream = new MemoryStream();

        await RaidHistoryExport.WriteCsvAsync(stream, [record], CancellationToken.None);

        var lines = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].TrimEnd('\r').Split(',');
        var row = lines[1].TrimEnd('\r').Split(',');
        Assert.Equal("Regular", row[Array.IndexOf(header, "mode")]);
        Assert.Equal("Wipe 4", row[Array.IndexOf(header, "wipe")]);
    }

    private static ProfileBundle Bundle(ProfileBundleIdentity identity, IReadOnlyList<ProfileBundleRaid> raids) => new(
        ProfileBundleCodec.FormatId,
        ProfileBundleCodec.CurrentVersion,
        Changed,
        identity,
        new ProfileBundleProgress(
            1, Faction.Unknown, null,
            new Dictionary<string, int>(), [], new Dictionary<string, int>(), new Dictionary<string, int>(), [],
            new Dictionary<string, int>(), new Dictionary<string, Core.Domain.Events.EventItemState>(), new Dictionary<string, string>()),
        ProfileBundleQuests.Empty,
        raids);

    private static int _minutes;

    private static ProfileBundleRaid BundleRaid(string mode, string? wipe)
    {
        var started = Changed.AddMinutes(Interlocked.Increment(ref _minutes) * 40);
        return new(Guid.NewGuid(), "customs", mode, started, started.AddMinutes(30), "Survived", null, null, wipe);
    }
}
