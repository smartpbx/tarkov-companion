using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Strategy.Data;
using TarkovCompanion.Infrastructure.Strategy.Datasets;
using TarkovCompanion.UnitTests.Profiles;

namespace TarkovCompanion.UnitTests.StrategyRuntime;

/// <summary>
/// The join between the installed snapshot and the Raid cockpit's traffic line. Every case uses a
/// real <see cref="TrafficSnapshotStore"/> on disk with a really signed package, so the store,
/// the verifier, the runtime and the scope rules are all exercised together.
/// </summary>
public sealed class HistoricalTrafficSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = TrafficPackageFixture.GeneratedUtc.AddDays(1);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tarkov-traffic-{Guid.NewGuid():N}");
    private readonly ECDsa _key = TrafficPackageFixture.NewKey();

    public void Dispose()
    {
        _key.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task No_installed_snapshot_says_so_and_shows_no_rows()
    {
        var source = await SourceAsync(installed: []);
        var view = await source.EvaluateAsync("customs", InRaid(), CancellationToken.None);

        Assert.Equal(HistoricalTrafficRuntimeStatus.NoInstalledModel, view.Status);
        Assert.Equal(HistoricalTrafficSource.NoModelNotice, view.Notice);
        Assert.False(view.HasRows);
        Assert.Null(view.Receipt);
    }

    [Fact]
    public async Task An_installed_snapshot_is_evaluated_for_the_exact_scope_and_raid_phase()
    {
        var source = await SourceAsync(installed: [TrafficPackageFixture.Scope()]);
        var view = await source.EvaluateAsync("customs", InRaid(), CancellationToken.None);

        Assert.Equal(HistoricalTrafficRuntimeStatus.Partial, view.Status);
        Assert.Contains("estimate", view.Notice, StringComparison.Ordinal);
        Assert.Contains("data through 2026-09-12", view.Notice, StringComparison.Ordinal);
        var row = Assert.Single(view.Rows);
        Assert.Equal("Old gas station", row.Label);
        Assert.Equal("customs", view.Receipt!.Scope.MapId);
        Assert.Equal(RaidPhase.Mid, view.Receipt.Phase);
    }

    [Theory]
    [InlineData("woods", TrafficPackageFixture.GameVersion, ProfileGameMode.Pvp, TrafficPackageFixture.Wipe)]
    [InlineData("customs", "1.1.6.0.50000", ProfileGameMode.Pvp, TrafficPackageFixture.Wipe)]
    [InlineData("customs", TrafficPackageFixture.GameVersion, ProfileGameMode.Pve, TrafficPackageFixture.Wipe)]
    [InlineData("customs", TrafficPackageFixture.GameVersion, ProfileGameMode.Pvp, "wipe-2026-3")]
    public async Task Every_part_of_the_scope_must_match_and_none_is_generalized(
        string map,
        string gameVersion,
        ProfileGameMode mode,
        string wipe)
    {
        var source = await SourceAsync(
            installed: [TrafficPackageFixture.Scope()],
            gameVersion: gameVersion,
            mode: mode,
            wipe: wipe);

        var view = await source.EvaluateAsync(map, InRaid(map), CancellationToken.None);

        Assert.Equal(HistoricalTrafficRuntimeStatus.IncompatibleModel, view.Status);
        Assert.False(view.HasRows);
        Assert.Contains("no model for this", view.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_game_version_is_reported_rather_than_guessed()
    {
        var source = await SourceAsync(installed: [TrafficPackageFixture.Scope()], gameVersion: null);

        var view = await source.EvaluateAsync("customs", InRaid(), CancellationToken.None);

        Assert.Equal(HistoricalTrafficRuntimeStatus.IncompatibleModel, view.Status);
        Assert.Contains("game version not known", view.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_raid_clock_the_phase_stays_unknown()
    {
        var source = await SourceAsync(installed: [TrafficPackageFixture.Scope()]);

        var notInRaid = await source.EvaluateAsync("customs", raid: null, CancellationToken.None);
        var otherMap = await source.EvaluateAsync("customs", InRaid("woods"), CancellationToken.None);

        Assert.Equal(HistoricalTrafficRuntimeStatus.RaidPhaseUnknown, notInRaid.Status);
        Assert.Equal(HistoricalTrafficRuntimeStatus.RaidPhaseUnknown, otherMap.Status);
        Assert.False(notInRaid.HasRows);
    }

    [Fact]
    public async Task Several_cohorts_use_all_players_and_never_pick_another_by_accident()
    {
        var withDefault = await SourceAsync(installed:
        [
            TrafficPackageFixture.Scope(cohort: "scav-runs"),
            TrafficPackageFixture.Scope(cohort: HistoricalTrafficSource.DefaultCohortId),
        ]);
        var view = await withDefault.EvaluateAsync("customs", InRaid(), CancellationToken.None);
        Assert.Equal(HistoricalTrafficSource.DefaultCohortId, view.Receipt!.Scope.CohortId);

        using var otherRoot = new TemporaryDirectory();
        var withoutDefault = await SourceAsync(
            installed:
            [
                TrafficPackageFixture.Scope(cohort: "scav-runs"),
                TrafficPackageFixture.Scope(cohort: "night-raids"),
            ],
            root: otherRoot.Path);
        var ambiguous = await withoutDefault.EvaluateAsync("customs", InRaid(), CancellationToken.None);
        Assert.Equal(HistoricalTrafficRuntimeStatus.IncompatibleModel, ambiguous.Status);
    }

    [Fact]
    public async Task A_package_signed_by_a_key_nobody_trusts_installs_nothing()
    {
        var inbox = Path.Combine(_root, "Inbox");
        TrafficPackageFixture.WritePackage(Path.Combine(inbox, "package-1"), _key, TrafficPackageFixture.Scope());
        var store = new TrafficSnapshotStore(
            new TrafficSnapshotStoreOptions(Path.Combine(_root, "Snapshots")),
            new TrafficModelPackageImporter(new UntrustedTrafficSignatureVerifier()));
        var source = new InstalledTrafficPublicationSource(store, inbox);

        Assert.Null(await source.GetAsync(CancellationToken.None));
    }

    [Fact]
    public void Trusted_keys_file_that_is_missing_or_damaged_trusts_nothing()
    {
        Directory.CreateDirectory(_root);
        var damaged = Path.Combine(_root, "damaged.json");
        File.WriteAllText(damaged, "{ not json");
        var wrongShape = Path.Combine(_root, "wrong.json");
        File.WriteAllText(wrongShape, """{ "fixture-key": "%%%not-base64%%%" }""");

        Assert.IsType<UntrustedTrafficSignatureVerifier>(TrafficTrustedKeys.Load(Path.Combine(_root, "absent.json")));
        Assert.IsType<UntrustedTrafficSignatureVerifier>(TrafficTrustedKeys.Load(damaged));
        Assert.IsType<UntrustedTrafficSignatureVerifier>(TrafficTrustedKeys.Load(wrongShape));
    }

    [Fact]
    public async Task The_application_composition_installs_from_the_inbox_and_the_cockpit_reads_it()
    {
        // Fails if the store, the trusted-keys file, the inbox or the source is not composed:
        // the store was once complete, tested and never constructed.
        var trustedKeys = Path.Combine(_root, "Config", "traffic-trusted-keys.json");
        Directory.CreateDirectory(Path.GetDirectoryName(trustedKeys)!);
        File.WriteAllText(trustedKeys, TrafficPackageFixture.TrustedKeysJson(_key));
        TrafficPackageFixture.WritePackage(
            Path.Combine(_root, "Traffic", "Inbox", "package-1"),
            _key,
            TrafficPackageFixture.Scope());

        await using var services = AppComposition.Build(
            new AppCommandLine(false, false, false, false, null, null, null),
            new(DataRoot: _root, Offline: true));

        var publication = await services.GetRequiredService<ITrafficPublicationSource>().GetAsync(CancellationToken.None);
        Assert.NotNull(publication);
        Assert.Equal("traffic-model-1", publication.Manifest.ModelVersion);
        Assert.NotNull(services.GetRequiredService<HistoricalTrafficSource>());
        Assert.NotNull(services.GetRequiredService<TarkovCompanion.App.ViewModels.V2.Raid.RaidCockpitViewModel>());
    }

    private async Task<HistoricalTrafficSource> SourceAsync(
        IReadOnlyList<TrafficCompatibilityScope> installed,
        string? gameVersion = TrafficPackageFixture.GameVersion,
        ProfileGameMode mode = ProfileGameMode.Pvp,
        string wipe = TrafficPackageFixture.Wipe,
        string? root = null)
    {
        root ??= _root;
        var inbox = Path.Combine(root, "Inbox");
        if (installed.Count > 0)
        {
            TrafficPackageFixture.WritePackage(Path.Combine(inbox, "package-1"), _key, [.. installed]);
        }

        var store = new TrafficSnapshotStore(
            new TrafficSnapshotStoreOptions(Path.Combine(root, "Snapshots")),
            new TrafficModelPackageImporter(new EcdsaTrafficArtifactSignatureVerifier(
                new Dictionary<string, byte[]> { [TrafficPackageFixture.KeyId] = _key.ExportSubjectPublicKeyInfo() })));
        var publications = new InstalledTrafficPublicationSource(store, inbox);

        var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(ProfileV2Fixtures.Now));
        await profiles.CreateAsync(
            ProfileV2Fixtures.Request(ProfileV2Fixtures.Profile(701, "generation-traffic", mode, "item", wipe)),
            CancellationToken.None);
        var runtimeProfile = new ProfileRuntimeContextService(profiles);
        await runtimeProfile.InitializeAsync(CancellationToken.None);

        return new HistoricalTrafficSource(
            publications,
            new HistoricalTrafficRuntimeService(),
            new FixedGameVersion(gameVersion),
            runtimeProfile,
            maps: null,
            new FixedClock(Now));
    }

    /// <summary>In a raid on <paramref name="map"/> with twenty minutes left of a forty-minute raid.</summary>
    private static RaidSnapshot InRaid(string map = "customs") =>
        new(
            Guid.NewGuid(),
            RaidLifecycleState.InRaid,
            map,
            Now.AddMinutes(-20),
            Now,
            Confidence.Unknown,
            null,
            [],
            false)
        {
            RaidClock = TimeSpan.FromMinutes(20),
            RaidClockReadUtc = Now,
        };

    private sealed class FixedGameVersion(string? version) : IGameVersionSource
    {
        public Task<string?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(version);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tarkov-traffic-{Guid.NewGuid():N}");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
