using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Profiles;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.Profiles;

/// <summary>
/// [#712 decision 4, #403] The active profile follows the game's <c>Session mode:</c> line, says so
/// in one line, and Undo puts it back.
/// </summary>
public sealed class ProfileModeFollowerTests
{
    /// <summary>The shape the game writes; synthetic, like the format pack's.</summary>
    private const string SeasonLine = "2026-01-01 20:00:00.000|1.1.5.1.47510|Info|application|Session mode: PvpSeason";

    [Theory]
    [InlineData(SeasonLine, "PvpSeason", ProfileGameMode.Seasonal)]
    [InlineData("2026-01-01 20:00:00.000|1.1.5.1.47510|Info|application|Session mode: Pve", "Pve", ProfileGameMode.Pve)]
    [InlineData("2026-01-01 20:00:00.000|1.1.5.1.47510|Info|application|Session mode: Regular", "Regular", ProfileGameMode.Pvp)]
    public void ReadsTheGamesSessionMode(string line, string value, ProfileGameMode mode)
    {
        var session = SessionModeParser.ParseLine(line, Now);

        Assert.NotNull(session);
        Assert.Equal(value, session.GameValue);
        Assert.Equal(mode, session.Mode);
    }

    /// <summary>A value never observed is kept, but maps to no mode, so nothing switches on a guess.</summary>
    [Fact]
    public void AnUnknownSessionModeMapsToNothing()
    {
        var session = SessionModeParser.ParseLine("2026-01-01 20:00:00.000|1.1.5.1.47510|Info|application|Session mode: Arena", Now);

        Assert.NotNull(session);
        Assert.Null(session.Mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-01-01 20:00:00.000|1.1.5.1.47510|Info|application|Session mode: ")]
    [InlineData("2026-01-01 20:00:00.000|1.1.5.1.47510|Info|application|Session mode: Pvp {\"x\":1}")]
    [InlineData("2026-01-01 20:00:00.000|1.1.5.1.47510|Info|application|Matching with group id: 1")]
    public void IgnoresLinesThatAreNotASessionMode(string? line) => Assert.Null(SessionModeParser.ParseLine(line, Now));

    [Fact]
    public async Task SwitchesToTheProfileInTheGamesModeAndUndoGoesBack()
    {
        var (management, follower) = await CreateAsync((1, ProfileGameMode.Pvp), (2, ProfileGameMode.Seasonal));

        await follower.ObserveAsync(SessionModeParser.ParseLine(SeasonLine, Now)!, default);

        Assert.Equal(Id(2), management.Current.ActiveProfile!.Context.Identity.ProfileId);
        var notice = Assert.IsType<ProfileFollowNotice>(follower.Notice);
        Assert.Equal(ProfileFollowKind.Switched, notice.Kind);
        Assert.Equal(ProfileGameMode.Seasonal, notice.GameMode);
        Assert.Equal("profile-generation-2", notice.ProfileName);
        Assert.Equal(Id(1), notice.PreviousProfileId);

        await follower.UndoAsync(default);

        Assert.Equal(Id(1), management.Current.ActiveProfile!.Context.Identity.ProfileId);
        Assert.Null(follower.Notice);
    }

    /// <summary>
    /// The line is in application and mirrored into output, and the startup replay reads it again:
    /// after Undo the player's choice holds until the game names a different mode.
    /// </summary>
    [Fact]
    public async Task TheSameModeAgainDoesNotOverrideAnUndo()
    {
        var (management, follower) = await CreateAsync((1, ProfileGameMode.Pvp), (2, ProfileGameMode.Seasonal));
        await follower.ObserveAsync(SessionModeParser.ParseLine(SeasonLine, Now)!, default);
        await follower.UndoAsync(default);

        await follower.ObserveAsync(SessionModeParser.ParseLine(SeasonLine, Now.AddSeconds(1))!, default);

        Assert.Equal(Id(1), management.Current.ActiveProfile!.Context.Identity.ProfileId);
        Assert.Null(follower.Notice);
    }

    [Fact]
    public async Task AlreadyInTheGamesModeChangesNothingAndSaysNothing()
    {
        var (management, follower) = await CreateAsync((2, ProfileGameMode.Seasonal), (1, ProfileGameMode.Pvp));

        await follower.ObserveAsync(SessionModeParser.ParseLine(SeasonLine, Now)!, default);

        Assert.Equal(Id(2), management.Current.ActiveProfile!.Context.Identity.ProfileId);
        Assert.Null(follower.Notice);
    }

    /// <summary>
    /// No profile in the game's mode: nothing switches (an empty profile replacing a full one would
    /// look like lost progress), and the line offers to create one.
    /// </summary>
    [Fact]
    public async Task WithNoProfileInTheGamesModeItOffersToCreateOne()
    {
        var (management, follower) = await CreateAsync((1, ProfileGameMode.Pvp));

        await follower.ObserveAsync(SessionModeParser.ParseLine(SeasonLine, Now)!, default);

        Assert.Equal(Id(1), management.Current.ActiveProfile!.Context.Identity.ProfileId);
        Assert.Equal(ProfileFollowKind.NoProfile, follower.Notice?.Kind);

        await follower.CreateForGameModeAsync("Seasonal", default);

        var active = management.Current.ActiveProfile!;
        Assert.Equal(ProfileGameMode.Seasonal, active.Context.Mode);
        Assert.Equal("Seasonal", active.Name);
        Assert.Equal(2, management.Current.Workspace.Profiles.Count);
    }

    [Fact]
    public async Task AnUnknownModeNeverSwitches()
    {
        var (management, follower) = await CreateAsync((1, ProfileGameMode.Pvp), (2, ProfileGameMode.Seasonal));

        await follower.ObserveAsync(new GameSessionMode("Arena", null, Now), default);

        Assert.Equal(Id(1), management.Current.ActiveProfile!.Context.Identity.ProfileId);
        Assert.Null(follower.Notice);
    }

    /// <summary>The line the shell shows, and its button: Undo after a switch, Create with no profile.</summary>
    [Fact]
    public async Task TheLineNamesTheModeAndTheProfileAndOffersUndo()
    {
        var (management, follower) = await CreateAsync((1, ProfileGameMode.Pvp), (2, ProfileGameMode.Seasonal));
        using var view = new ProfileFollowViewModel(follower);
        Assert.False(view.IsVisible);

        await follower.ObserveAsync(SessionModeParser.ParseLine(SeasonLine, Now)!, default);

        Assert.True(view.IsVisible);
        Assert.Equal("The game is in Seasonal. Switched to profile-generation-2.", view.Line);
        Assert.Equal("Undo", view.ActionLabel);
        Assert.True(view.HasAction);

        view.ActionCommand.Execute(null);
        await UntilAsync(() => !view.IsVisible);
        Assert.Equal(Id(1), management.Current.ActiveProfile!.Context.Identity.ProfileId);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static async Task<(ProfileManagementService Management, ProfileModeFollower Follower)> CreateAsync(
        params (int Id, ProfileGameMode Mode)[] specs)
    {
        var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        foreach (var (id, mode) in specs)
        {
            await profiles.CreateAsync(
                Request(Profile(Context(Id(id), $"generation-{id}", mode, language: "en"), $"item-{id}"), makeActive: id == specs[0].Id),
                default);
        }

        var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(default);
        var management = new ProfileManagementService(profiles, runtime, new ProfileClock(Now));
        return (management, new ProfileModeFollower(management));
    }
}
