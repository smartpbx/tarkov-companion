using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>
/// [#266] The strip under the map and the top bar say the same thing about the raid.
/// </summary>
/// <remarks>
/// Before any log evidence the top bar read "Raid unknown" while the strip asserted "Not in raid"
/// about the same moment: one of them was claiming something nothing had observed.
/// </remarks>
public sealed class RaidPhaseLabelTests
{
    [Theory]
    [InlineData(RaidLifecycleState.Unknown)]
    [InlineData(RaidLifecycleState.LauncherOrGameDetected)]
    [InlineData(RaidLifecycleState.Menu)]
    [InlineData(RaidLifecycleState.LoadingRaid)]
    [InlineData(RaidLifecycleState.InRaid)]
    [InlineData(RaidLifecycleState.PostRaid)]
    public void Without_a_clock_the_strip_uses_the_top_bars_words(RaidLifecycleState state)
    {
        Assert.Equal(
            V2ShellText.Get($"V2.Shell.Context.RaidState.{state}"),
            RaidCockpitViewModel.PhaseLabel(string.Empty, state));
    }

    [Fact]
    public void No_evidence_is_not_reported_as_out_of_raid()
    {
        Assert.NotEqual("Not in raid", RaidCockpitViewModel.PhaseLabel(string.Empty, RaidLifecycleState.Unknown));
    }

    [Fact]
    public void A_running_clock_is_shown_as_it_is()
    {
        Assert.Equal("20:56 left", RaidCockpitViewModel.PhaseLabel("20:56 left", RaidLifecycleState.InRaid));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("00:09 elapsed", true)]
    [InlineData("20:56 left", true)]
    public void The_strip_shows_the_phase_only_while_a_clock_runs(string clock, bool shown)
    {
        Assert.Equal(shown, RaidCockpitViewModel.ShowsPhaseInStrip(clock));
    }
}
