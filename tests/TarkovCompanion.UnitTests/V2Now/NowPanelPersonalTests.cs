using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.Application.Services.Personal;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Situations;
using static TarkovCompanion.UnitTests.V2Now.NowPanelStateTests;

namespace TarkovCompanion.UnitTests.V2Now;

/// <summary>[#712 2-4] YOU's exit and every walk minute on the Now panel, from the player's own history.</summary>
public sealed class NowPanelPersonalTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 14, 0, 0, TimeSpan.Zero);

    /// <summary>2 m/s, 1.6 to 2.5 either side: quicker than the careful pace, so the minutes differ.</summary>
    private static readonly WalkPace Quick = new(0.5, 0.4, 0.6, 20, 4);

    private static NowPersonal Used(string exit, int times, WalkPace? pace = null) =>
        new(pace, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [exit] = times });

    [Fact]
    public void An_exit_you_used_is_named_over_a_slightly_nearer_one_and_says_why()
    {
        using var scope = English();
        NowExit[] exits = [Exit("Crossroads", 200, "N", offered: true), Exit("ZB-1011", 240, "W", offered: true)];

        var plain = NowPanelState.Project(InRaid(19, 40), Now, exits);
        var yours = NowPanelState.Project(InRaid(19, 40), Now, exits, personal: Used("ZB-1011", 5));

        Assert.StartsWith("Nearest offered exit: Crossroads", plain.YouExit, StringComparison.Ordinal);
        Assert.Equal(string.Empty, plain.YouExitNote);
        Assert.StartsWith("Nearest offered exit: ZB-1011", yours.YouExit, StringComparison.Ordinal);
        Assert.Equal("you used it 5 times", yours.YouExitNote);
    }

    [Fact]
    public void Use_never_beats_an_exit_a_third_nearer_nor_an_offered_one_and_one_use_says_once()
    {
        using var scope = English();

        // Nine uses still count only a quarter: 300 m weighs 225 m, further than 200 m.
        var far = NowPanelState.Project(
            InRaid(19, 40),
            Now,
            [Exit("Crossroads", 200, "N", offered: true), Exit("ZB-1011", 300, "W", offered: true)],
            personal: Used("ZB-1011", 9));
        Assert.StartsWith("Nearest offered exit: Crossroads", far.YouExit, StringComparison.Ordinal);

        // Used but not seen offered: the offered exit still wins, however often the other was used.
        var offered = NowPanelState.Project(
            InRaid(19, 40),
            Now,
            [Exit("Crossroads", 400, "N", offered: true), Exit("ZB-1011", 100, "W", offered: false)],
            personal: Used("ZB-1011", 9));
        Assert.StartsWith("Nearest offered exit: Crossroads", offered.YouExit, StringComparison.Ordinal);

        var once = NowPanelState.Project(
            InRaid(19, 40),
            Now,
            [Exit("Crossroads", 200, "N", offered: false), Exit("ZB-1011", 205, "W", offered: false)],
            personal: Used("ZB-1011", 1));
        Assert.StartsWith("Nearest exit: ZB-1011", once.YouExit, StringComparison.Ordinal);
        Assert.Equal("not seen on your extract list · you used it once", once.YouExitNote);
    }

    [Fact]
    public void Walk_minutes_use_your_pace_when_measured_and_the_label_says_which()
    {
        using var scope = English();
        using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
        NowExit[] exits = [Exit("ZB-1011", 450, "W", offered: true)];

        // 450 m: careful pace 450 × 1.25 / 1.8 = 313 s, 6 min; your 0.5 s/m is 225 s, 4 min. Ten minutes left.
        var careful = NowPanelState.Project(InRaid(30, 40), Now, exits);
        var yours = NowPanelState.Project(InRaid(30, 40), Now, exits, personal: new NowPersonal(Quick, NowPersonal.None.ExitUses));

        Assert.EndsWith("~6 min", careful.YouExit, StringComparison.Ordinal);
        Assert.Equal(NowText.LeaveBy("ZB-1011", LocalTime.ShortTime(Now.AddMinutes(10 - 6 - 2)), 6, 2), careful.NowNote);
        Assert.Equal("estimate from your last screenshot · careful walking pace", careful.LeaveEstimate);
        Assert.Equal(string.Empty, careful.YouExitNote);

        Assert.EndsWith("~4 min", yours.YouExit, StringComparison.Ordinal);
        Assert.Equal(NowText.LeaveBy("ZB-1011", LocalTime.ShortTime(Now.AddMinutes(10 - 4 - 2)), 4, 2), yours.NowNote);
        Assert.Equal("estimate from your last screenshot · your pace", yours.LeaveEstimate);
        Assert.Equal("your pace", yours.YouExitNote);
    }

    [Fact]
    public void The_panel_reads_your_history_once_per_raid_map_and_side()
    {
        var calls = new List<(string Map, SituationSide Side)>();
        using var panel = new NowPanelViewModel(null, tick: false)
        {
            LoadPersonal = (map, side, _) =>
            {
                calls.Add((map, side));
                return Task.FromResult(Used("ZB-1011", 5, Quick));
            },
        };
        panel.SetExits([Exit("Crossroads", 200, "N", offered: true), Exit("ZB-1011", 240, "W", offered: true)]);

        panel.Show(Out(SituationPhase.Menu));
        Assert.Empty(calls);

        var raid = InRaid(19, 40) with { Side = new(SituationSide.Pmc, Confidence.Certain, SituationSource.GameLog, Now, "log") };
        panel.Show(raid);
        panel.Refresh();
        panel.Show(raid with { Version = raid.Version + 1 });

        Assert.Equal([("customs", SituationSide.Pmc)], calls);
        Assert.True(panel.Personal.IsYourPace);
        Assert.Contains("ZB-1011", panel.State.YouExit, StringComparison.Ordinal);

        panel.Show(raid with { Map = raid.Map! with { Value = "woods" } });
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task History_counts_exits_from_finished_unarchived_raids_on_this_map_and_side()
    {
        var history = new History();
        history.Raid("customs", ended: true, used: "ZB-1011", side: "PMC");
        history.Raid("customs", ended: true, used: "ZB-1011", side: "Pmc");
        history.Raid("customs", ended: true, used: "ZB-1011", side: "scav");
        history.Raid("customs", ended: true, used: "Crossroads", side: null);
        history.Raid("customs", ended: false, used: "Crossroads", side: "PMC");
        history.Raid("customs", ended: true, used: "Crossroads", side: "PMC", archived: true);
        history.Raid("woods", ended: true, used: "Outskirts", side: "PMC");

        var uses = await new PersonalHistory(history).ExitUsesAsync("customs", "Pmc", CancellationToken.None);

        Assert.Equal(2, uses["ZB-1011"]);
        Assert.Equal(1, uses["Crossroads"]);
        Assert.False(uses.ContainsKey("Outskirts"));
    }

    [Fact]
    public async Task History_measures_pace_over_every_maps_trails_and_none_with_too_little()
    {
        var empty = await new PersonalHistory(new History()).PaceAsync(CancellationToken.None);
        Assert.Null(empty);

        var history = new History();
        history.Trail("customs", Walk(5));
        history.Trail("woods", Walk(5));
        var pace = await new PersonalHistory(history).PaceAsync(CancellationToken.None);

        Assert.NotNull(pace);
        Assert.Equal(2, pace.Raids);
        Assert.Equal(10, pace.Legs);
        Assert.Equal(1.5, pace.MetresPerSecond, 2);
    }

    /// <summary>Screenshots 90 m and 60 s apart: moving legs at 1.5 m/s.</summary>
    private static IReadOnlyList<ScreenshotPosition> Walk(int legs) =>
        [.. Enumerable.Range(0, legs + 1).Select(step => new ScreenshotPosition(
            Now.AddMinutes(step),
            new WorldPosition(step * 90, 0, 0),
            new QuaternionOrientation(0, 0, 0, 1),
            0,
            null,
            null,
            "shot.png"))];

    private sealed class History : IRaidHistoryService
    {
        private readonly List<RaidHistoryEntry> _raids = [];
        private readonly Dictionary<(Guid, string), List<string>> _events = [];
        private readonly List<(string Map, RaidTrail Trail)> _trails = [];

        public void Raid(string map, bool ended, string used, string? side, bool archived = false)
        {
            var id = Guid.NewGuid();
            _raids.Add(new(id, Guid.Empty, map, "Regular", Now.AddDays(-1), ended ? Now.AddDays(-1).AddMinutes(30) : null, "Survived", null));
            _events[(id, RaidExtractUsed.EventType)] = [new RaidExtractUsed(used, Now).ToPayload()];
            _events[(id, "state")] = side is null ? [] : [$$"""{"Side":"{{side}}"}"""];
            _events[(id, RaidArchive.EventType)] = archived ? [new RaidArchive(true, Now).ToPayload()] : [];
        }

        public void Trail(string map, IReadOnlyList<ScreenshotPosition> positions)
        {
            var id = Guid.NewGuid();
            _raids.Add(new(id, Guid.Empty, map, "Regular", Now.AddDays(-1), Now.AddDays(-1).AddMinutes(30), "Survived", null));
            _trails.Add((map, new RaidTrail(id, Now.AddDays(-1), positions)));
        }

        public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidHistoryEntry>>(_raids);

        public Task<IReadOnlyList<string>> ListEventPayloadsAsync(Guid raidId, string type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(_events.TryGetValue((raidId, type), out var payloads) ? payloads : []);

        public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(string mapId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RaidTrail>>([.. _trails.Where(trail => trail.Map == mapId).Select(trail => trail.Trail).Take(limit)]);

        public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RecordEventAsync(Guid raidId, string type, DateTimeOffset timestampUtc, string payloadJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EndAsync(Guid raidId, DateTimeOffset endUtc, string? outcome, string? notes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CorrectAsync(Guid raidId, string? outcome, string? notes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SoftDeleteAsync(IReadOnlyCollection<Guid> raidIds, DateTimeOffset deletedUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RestoreDeletedAsync(IReadOnlyCollection<Guid> raidIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PurgeDeletedAsync(IReadOnlyCollection<Guid> exceptRaidIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RaidManualMetadata?> GetManualMetadataAsync(Guid raidId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetManualMetadataAsync(Guid raidId, RaidManualMetadata metadata, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(Guid raidId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
