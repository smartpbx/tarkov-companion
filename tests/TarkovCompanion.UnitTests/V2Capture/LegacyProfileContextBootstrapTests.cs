using TarkovCompanion.App.Services.V2.Profile;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.UnitTests.Profiles;

namespace TarkovCompanion.UnitTests.V2Capture;

public sealed class LegacyProfileContextBootstrapTests
{
    private static readonly RuntimeOptions Options = new(false, false, GameMode.Regular, "en", TimeSpan.Zero, TimeSpan.FromMinutes(1));

    [Theory]
    [InlineData(GameMode.Regular, ProfileGameMode.Pvp)]
    [InlineData(GameMode.Pve, ProfileGameMode.Pve)]
    [InlineData(GameMode.PvpSeason, ProfileGameMode.Seasonal)]
    public async Task SeedsOneV2ProfileFromTheLegacyProfileWhenNoneExists(GameMode legacyMode, ProfileGameMode expectedMode)
    {
        var legacyId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var legacy = new StubLegacyProfiles(new PlayerProfile(
            legacyId,
            "Local raider",
            legacyMode,
            12,
            Faction.Bear,
            null,
            new Dictionary<string, int>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, EventItemState>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            DateTimeOffset.Parse("2026-09-01T00:00:00Z")));
        using var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(DateTimeOffset.Parse("2026-09-16T00:00:00Z")));
        var bootstrap = new LegacyProfileContextBootstrap(legacy, profiles, Options);

        await bootstrap.EnsureSeededAsync(CancellationToken.None);

        var workspace = await profiles.GetAsync(CancellationToken.None);
        var seeded = Assert.Single(workspace.Profiles);
        Assert.Equal(legacyId, seeded.Context.Identity.ProfileId);
        Assert.Equal(expectedMode, seeded.Context.Mode);
        Assert.Equal("Local raider", seeded.Name);
        Assert.Equal(legacyId, workspace.ActiveProfileId);
    }

    [Fact]
    public async Task DoesNothingWhenAV2ProfileAlreadyExists()
    {
        var legacy = new StubLegacyProfiles(new PlayerProfile(
            Guid.NewGuid(),
            "Should not be used",
            GameMode.Regular,
            1,
            Faction.Unknown,
            null,
            new Dictionary<string, int>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal),
            new Dictionary<string, EventItemState>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            DateTimeOffset.UnixEpoch));
        using var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(DateTimeOffset.Parse("2026-09-16T00:00:00Z")));
        await profiles.CreateAsync(
            ProfileV2Fixtures.Request(ProfileV2Fixtures.Profile(1, "generation-a", ProfileGameMode.Pve, "item-a")),
            CancellationToken.None);
        var bootstrap = new LegacyProfileContextBootstrap(legacy, profiles, Options);

        await bootstrap.EnsureSeededAsync(CancellationToken.None);

        var workspace = await profiles.GetAsync(CancellationToken.None);
        Assert.Single(workspace.Profiles);
        Assert.False(legacy.WasRead);
    }

    private sealed class StubLegacyProfiles(PlayerProfile profile) : IPlayerProfileService
    {
        public bool WasRead { get; private set; }

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken)
        {
            WasRead = true;
            return Task.FromResult(profile);
        }

        public Task SaveAsync(PlayerProfile updated, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
