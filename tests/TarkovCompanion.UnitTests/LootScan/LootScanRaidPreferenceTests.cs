using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.UnitTests.V2Capture;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.LootScan;

/// <summary>[#902 P8] Risk is a preference kept between runs; a chosen phase lasts one raid.</summary>
public sealed class LootScanRaidPreferenceTests
{
    [Fact]
    public void Risk_survives_a_restart_and_the_phase_is_never_stored()
    {
        using var file = new LayoutFile();
        var before = new LootScanRaidPreference(file.Restart())
        {
            Risk = RecommendationRaidRisk.High,
            Phase = RecommendationRaidPhase.Extracting,
        };

        var after = new LootScanRaidPreference(file.Restart());

        Assert.Equal(RecommendationRaidRisk.High, before.Risk);
        Assert.Equal(RecommendationRaidRisk.High, after.Risk);
        Assert.Null(after.Phase);
    }

    [Fact]
    public async Task A_phase_chosen_in_one_raid_is_back_to_counted_in_the_next()
    {
        var raid = new SwitchableRaid();
        raid.Current = raid.Current with { RaidId = Guid.NewGuid() };
        var preference = new LootScanRaidPreference();
        var source = new LootScanRaidContextSource(raid, new LootScanFactFixtures.NoMaps(), preference);
        preference.Phase = RecommendationRaidPhase.Extracting;

        // Decided again in the same raid: the player's call stands.
        var same = await source.ReadAsync(DateTimeOffset.UnixEpoch.AddMinutes(5), CancellationToken.None);
        Assert.Equal(RecommendationRaidPhase.Extracting, same.Phase.Value);
        await source.ReadAsync(DateTimeOffset.UnixEpoch.AddMinutes(6), CancellationToken.None);
        Assert.Equal(RecommendationRaidPhase.Extracting, preference.Phase);

        raid.Current = raid.Current with { RaidId = Guid.NewGuid() };
        var next = await source.ReadAsync(DateTimeOffset.UnixEpoch.AddHours(1).AddMinutes(1), CancellationToken.None);

        Assert.Null(preference.Phase);
        Assert.NotEqual(RecommendationRaidPhase.Extracting, next.Phase.Value);
        Assert.Equal("raid.phase", next.Phase.FieldId);
    }

    /// <summary>The raid in progress, changed by hand: a new id is a new raid.</summary>
    private sealed class SwitchableRaid : IRaidStateService
    {
        public RaidSnapshot Current { get; set; } = new RaidStateService().Current;

        public RaidSnapshot Apply(RaidEvidence evidence) => throw new NotSupportedException();

        public RaidSnapshot ApplyPosition(ScreenshotPosition position) => throw new NotSupportedException();

        public RaidSnapshot ApplyExtracts(
            IReadOnlyList<ActiveExtract> extracts,
            DateTimeOffset observedUtc,
            TimeSpan? raidClock = null,
            IReadOnlyList<string>? linesNotMatched = null,
            IReadOnlyList<string>? transits = null) => throw new NotSupportedException();

        public RaidSnapshot Adopt(Guid raidId, DateTimeOffset? startedUtc, IReadOnlyList<ScreenshotPosition> trail) =>
            throw new NotSupportedException();
    }
}
