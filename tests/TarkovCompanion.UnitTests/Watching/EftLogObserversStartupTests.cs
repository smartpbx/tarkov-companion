using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.UnitTests.Watching;

/// <summary>
/// [#971] The Windows log watcher builds this observer while the app starts. Asking for the
/// profile follower there pulled the profile services into that construction and the app hung
/// before its database existed, so the follower is asked for only when the game names its mode.
/// </summary>
public sealed class EftLogObserversStartupTests
{
    [Fact]
    public void Building_the_observer_does_not_ask_for_the_profile_follower()
    {
        var asked = 0;
        _ = new EftLogObservers(new SquadStateService(), new FleaSaleStateService(), profileMode: () =>
        {
            asked++;
            return null;
        });

        Assert.Equal(0, asked);
    }

    [Fact]
    public void A_session_mode_line_asks_for_the_follower()
    {
        var asked = 0;
        var observers = new EftLogObservers(new SquadStateService(), new FleaSaleStateService(), profileMode: () =>
        {
            asked++;
            return null;
        });

        ((IEftLogObserver)observers).Observe(new TarkovCompanion.Core.Domain.Profiles.GameSessionMode(
            "pve", TarkovCompanion.Core.Domain.Profiles.ProfileGameMode.Pve, DateTimeOffset.UnixEpoch));

        Assert.Equal(1, asked);
    }
}
