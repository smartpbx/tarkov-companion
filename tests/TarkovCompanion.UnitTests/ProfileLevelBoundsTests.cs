using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Infrastructure.Profile;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The two profile fields that now have a control, and the writer that guards them.
/// </summary>
/// <remarks>
/// TraderLevels and HideoutStationLevels have been on the profile since it was written, are
/// validated on save, and were written by nothing anywhere in the application. So Hideout
/// printed every station as "level 0 of N" under a line saying the levels came from the
/// player's profile — true, and useless — and every ammo rule requiring any trader loyalty
/// could never fire, because ObtainableForProfile compared against a dictionary nothing filled.
///
/// These pin the range the writer enforces, because the steppers clamp to the same numbers and
/// a save it refuses would lose the whole profile edit rather than one field.
/// </remarks>
public sealed class ProfileLevelBoundsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task A_trader_level_the_game_allows_is_stored(int level)
    {
        await using var store = new ProfileFile();

        await store.SaveAsync(Profile() with { TraderLevels = new Dictionary<string, int> { ["prapor"] = level } });

        Assert.Equal(level, (await store.ReadAsync()).TraderLevels["prapor"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public async Task A_trader_level_the_game_does_not_have_is_refused(int level)
    {
        // Which is why the stepper clamps as well as bounding its own range: a refused save
        // loses the whole profile edit, not one number.
        await using var store = new ProfileFile();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            store.SaveAsync(Profile() with { TraderLevels = new Dictionary<string, int> { ["prapor"] = level } }));
    }

    [Fact]
    public async Task A_hideout_station_level_survives_a_reload()
    {
        // The point of the stepper. Hideout measures which level is next, what it needs and how
        // much is outstanding against this number, so a number that did not survive a restart
        // would be worse than no control at all.
        await using var store = new ProfileFile();

        await store.SaveAsync(Profile() with
        {
            HideoutStationLevels = new Dictionary<string, int> { ["generator"] = 3 },
        });

        Assert.Equal(3, (await store.ReadAsync()).HideoutStationLevels["generator"]);
    }

    [Fact]
    public async Task Zero_is_a_level_somebody_can_set_rather_than_an_absence()
    {
        // A station you have not built and a station you have not told us about are both 0, and
        // nothing can tell them apart without reading a profile the logs never write. So zero
        // stays reachable on the stepper and stays stored when it is chosen.
        await using var store = new ProfileFile();

        await store.SaveAsync(Profile() with
        {
            HideoutStationLevels = new Dictionary<string, int> { ["generator"] = 0 },
        });

        Assert.Equal(0, (await store.ReadAsync()).HideoutStationLevels["generator"]);
    }

    private static PlayerProfile Profile() => new(
        Guid.Parse("2c2f2a0d-6f1e-4a7f-9c5a-1f4f0c6ad2b1"),
        "Level setter",
        GameMode.Regular,
        25,
        Faction.Usec,
        null,
        new Dictionary<string, int>(),
        new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, int>(),
        new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, EventItemState>(),
        new Dictionary<string, string>(),
        DateTimeOffset.Parse("2026-09-13T12:00:00Z"),
        "generation-a");

    private sealed class ProfileFile : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"tarkov-profile-levels-{Guid.NewGuid():N}");

        private readonly JsonFilePlayerProfileService _service;

        public ProfileFile()
        {
            Directory.CreateDirectory(_directory);
            _service = new(new(Path.Combine(_directory, "profile.json")));
        }

        public Task SaveAsync(PlayerProfile profile) => _service.SaveAsync(profile, CancellationToken.None);

        public Task<PlayerProfile> ReadAsync() => _service.GetActiveAsync(CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            _service.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
