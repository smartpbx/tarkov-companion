using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Infrastructure.Profile;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.Profiles;

/// <summary>
/// #269: two profiles must never see each other's progress, whichever one is open. These run the real
/// file services against a real folder, because "separate files" is the whole claim.
/// </summary>
public sealed class ProfileScopedPlayerProfileServiceTests : IDisposable
{
    private static readonly Guid LegacyId = Id(1);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tarkov-scoped-{Guid.NewGuid():N}");
    private readonly MemoryProfileStore _store = new();
    private readonly ProfileContextService _profiles;
    private readonly ProfileRuntimeContextService _runtime;
    private readonly JsonFilePlayerProfileService _legacy;
    private readonly ProfileScopedPlayerProfileService _service;

    public ProfileScopedPlayerProfileServiceTests()
    {
        _profiles = new ProfileContextService(_store, new ProfileClock(Now));
        _runtime = new ProfileRuntimeContextService(_profiles);
        _legacy = new JsonFilePlayerProfileService(new(Path.Combine(_directory, "profile.json")));
        _service = new ProfileScopedPlayerProfileService(
            _legacy,
            _runtime,
            Path.Combine(_directory, "profiles"),
            path => new JsonFilePlayerProfileService(new(path)));
    }

    [Fact]
    public async Task EachProfileReadsAndWritesOnlyItsOwnProgress()
    {
        await SeedLegacyProfileAsync(level: 30, completedTask: "task-legacy");
        var pve = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
        await CreateProfileAsync(pve, "PvE alt", ProfileGameMode.Pve);

        var alt = await _service.GetActiveAsync(default);
        Assert.Equal(pve, alt.Id);
        Assert.Equal("PvE alt", alt.Name);
        Assert.Equal(GameMode.Pve, alt.GameMode);
        Assert.Equal(1, alt.Level);
        Assert.Empty(alt.CompletedTaskIds);

        var legacyBefore = await File.ReadAllTextAsync(Path.Combine(_directory, "profile.json"));
        await _service.SaveAsync(alt with { Level = 5, CompletedTaskIds = new HashSet<string> { "task-alt" } }, default);
        Assert.Equal(legacyBefore, await File.ReadAllTextAsync(Path.Combine(_directory, "profile.json")));

        await _runtime.SwitchAsync(LegacyId, default);
        var original = await _service.GetActiveAsync(default);
        Assert.Equal(LegacyId, original.Id);
        Assert.Equal(30, original.Level);
        Assert.Equal(["task-legacy"], original.CompletedTaskIds);

        await _runtime.SwitchAsync(pve, default);
        var again = await _service.GetActiveAsync(default);
        Assert.Equal(5, again.Level);
        Assert.Equal(["task-alt"], again.CompletedTaskIds);
    }

    [Fact]
    public async Task ASaveThatBeganUnderAnotherProfileIsRefusedNotAppliedToTheOneNowOpen()
    {
        await SeedLegacyProfileAsync(level: 30, completedTask: "task-legacy");
        var pve = Guid.Parse("00000000-0000-0000-0000-0000000000a3");
        await CreateProfileAsync(pve, "PvE alt", ProfileGameMode.Pve);
        var staleLegacy = await _legacy.GetActiveAsync(default);

        // The player switched to the alt while a write for the first profile was still in flight.
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SaveAsync(staleLegacy with { Level = 99 }, default));

        Assert.Contains("active profile changed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, (await _service.GetActiveAsync(default)).Level);
        Assert.Equal(30, (await _legacy.GetActiveAsync(default)).Level);
    }

    [Fact]
    public async Task WithNoActiveProfileEverythingStaysInTheOriginalFile()
    {
        await SeedLegacyProfileAsync(level: 42, completedTask: "task-legacy");

        var profile = await _service.GetActiveAsync(default);
        await _service.SaveAsync(profile with { Level = 43 }, default);

        Assert.Equal(43, (await _legacy.GetActiveAsync(default)).Level);
        Assert.False(Directory.Exists(Path.Combine(_directory, "profiles")));
    }

    [Fact]
    public async Task ACompletedSavePublishesTheNewProfile()
    {
        await SeedLegacyProfileAsync(level: 30, completedTask: "task-legacy");
        PlayerProfile? changed = null;
        _service.Changed += profile => changed = profile;
        var profile = await _service.GetActiveAsync(default);

        await _service.SaveAsync(profile with { Level = 42 }, default);

        Assert.NotNull(changed);
        Assert.Equal(42, changed!.Level);
    }

    [Fact]
    public async Task AnUnreadableWorkspaceFallsBackForNowAndIsAskedAgainNextCall()
    {
        await SeedLegacyProfileAsync(level: 30, completedTask: "task-legacy");
        var pve = Guid.Parse("00000000-0000-0000-0000-0000000000a4");
        await CreateProfileAsync(pve, "PvE alt", ProfileGameMode.Pve);
        using var freshRuntime = new ProfileRuntimeContextService(_profiles);
        var failing = true;
        _store.OnRead = () =>
        {
            if (failing)
            {
                throw new IOException("no such table: profile_workspaces");
            }
        };
        using var service = new ProfileScopedPlayerProfileService(
            _legacy,
            freshRuntime,
            Path.Combine(_directory, "profiles"),
            path => new JsonFilePlayerProfileService(new(path)));

        Assert.Equal(LegacyId, (await service.GetActiveAsync(default)).Id);

        failing = false;
        Assert.Equal(pve, (await service.GetActiveAsync(default)).Id);
    }

    public void Dispose()
    {
        _service.Dispose();
        _legacy.Dispose();
        _runtime.Dispose();
        _profiles.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>What a V2 launch does once: the legacy profile becomes the workspace's first profile.</summary>
    private async Task SeedLegacyProfileAsync(int level, string completedTask)
    {
        var legacy = new PlayerProfile(
            LegacyId,
            "Local profile",
            GameMode.Regular,
            level,
            Faction.Bear,
            null,
            new Dictionary<string, int>(),
            new HashSet<string> { completedTask },
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, EventItemState>(),
            new Dictionary<string, string>(),
            Now,
            "legacy-generation");
        await _legacy.SaveAsync(legacy, default);
        var record = LegacyProfileMigration.Migrate(
            legacy,
            Context(LegacyId, "legacy-generation", ProfileGameMode.Pvp, "legacy"));
        await _profiles.CreateAsync(new CreateProfileRequest(record.Context, record.Name, record.Progress), default);
        await _runtime.InitializeAsync(default);
    }

    private async Task CreateProfileAsync(Guid id, string name, ProfileGameMode mode)
    {
        var context = Context(id, $"generation-{id:N}", mode, "wipe-2");
        await _profiles.CreateAsync(new CreateProfileRequest(context, name, new ProfileProgress(1)), default);
        await _runtime.RefreshAsync(default);
    }
}
