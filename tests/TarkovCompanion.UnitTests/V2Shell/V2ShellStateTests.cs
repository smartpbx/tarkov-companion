using System.Globalization;
using System.Text.Json;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// The eight workspace states are distinct, honest about what they know, and announced in proportion.
/// </summary>
public sealed class V2ShellStateTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static ApplicationRuntimeSnapshot GameData(
        DataAvailability availability,
        int items,
        double? hoursOld,
        bool offline,
        bool databaseReady,
        bool demo) =>
        V2ShellTestData.Snapshot(demo, offline).WithData(
            availability,
            items,
            hoursOld is { } hours ? V2ShellTestData.Now.AddHours(-hours) : null,
            databaseReady);

    [Fact]
    public void Every_state_has_one_policy_and_no_two_states_read_or_look_alike()
    {
        var policies = V2SurfaceStatePolicies.All;

        Assert.Equal(Enum.GetValues<V2SurfaceStateKind>().Order(), policies.Select(policy => policy.Kind).Order());
        Assert.Equal(policies.Count, policies.Select(policy => V2ShellText.Get(policy.WordingKey)).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(policies.Count, policies.Select(policy => policy.Glyph).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Shared_state_words_match_the_design_system_strings()
    {
        using var strings = JsonDocument.Parse(File.ReadAllText(
            V2ShellTestData.RepositoryPath("src", "TarkovCompanion.App", "Assets", "V2", "strings.en.json")));
        var designSystem = strings.RootElement.GetProperty("strings");

        foreach (var policy in V2SurfaceStatePolicies.All.Where(policy => policy.DesignSystemKey is not null))
        {
            Assert.Equal(designSystem.GetProperty(policy.DesignSystemKey!).GetString(), V2ShellText.Get(policy.WordingKey));
        }
    }

    [Fact]
    public void A_background_change_is_never_assertive_and_only_a_players_own_failure_takes_focus()
    {
        Assert.All(V2SurfaceStatePolicies.All, policy => Assert.NotEqual(V2Announcement.Assertive, policy.Background));
        // State matrix rule 5: only a failure of the player's own action moves focus; a refusal
        // keeps focus on the control that was refused, and nothing in the background moves it.
        Assert.Equal(
            V2SurfaceStateKind.Failed,
            Assert.Single(V2SurfaceStatePolicies.All, policy => policy.FocusesOnPlayerAction).Kind);
        Assert.Equal(V2Announcement.Assertive, V2SurfaceStatePolicies.For(V2SurfaceStateKind.Denied).PlayerAction);
    }

    [Theory]
    [InlineData(DataAvailability.Error, 0, null, false, true, false, V2SurfaceStateKind.Failed)]
    [InlineData(DataAvailability.Refreshing, 0, null, false, true, false, V2SurfaceStateKind.Loading)]
    [InlineData(DataAvailability.Unavailable, 0, null, false, false, false, V2SurfaceStateKind.Loading)]
    [InlineData(DataAvailability.Unavailable, 0, null, true, true, false, V2SurfaceStateKind.Offline)]
    [InlineData(DataAvailability.Unavailable, 0, null, false, true, false, V2SurfaceStateKind.Empty)]
    [InlineData(DataAvailability.Cached, 5000, 2d, true, true, false, V2SurfaceStateKind.Offline)]
    [InlineData(DataAvailability.Cached, 5000, null, false, true, false, V2SurfaceStateKind.Partial)]
    [InlineData(DataAvailability.Current, 5000, 72d, false, true, false, V2SurfaceStateKind.Stale)]
    [InlineData(DataAvailability.Current, 5000, 0.1d, false, true, false, V2SurfaceStateKind.Ready)]
    [InlineData(DataAvailability.DemoFixture, 12, 0d, false, true, true, V2SurfaceStateKind.Ready)]
    public void Game_data_resolves_to_the_state_it_actually_is(
        DataAvailability availability,
        int items,
        double? hoursOld,
        bool offline,
        bool databaseReady,
        bool demo,
        V2SurfaceStateKind expected)
    {
        var snapshot = GameData(availability, items, hoursOld, offline, databaseReady, demo);

        var state = V2SurfaceStateResolver.ForGameData(snapshot, V2ShellTestData.Now, Culture);

        Assert.Equal(expected, state.Kind);
        Assert.False(string.IsNullOrWhiteSpace(V2ShellText.Get(state.RemainderKey)));
        Assert.NotEqual(V2SurfaceStateKind.Denied, state.Kind);
        if (state.Kind is not V2SurfaceStateKind.Ready and not V2SurfaceStateKind.Loading)
        {
            Assert.NotEmpty(state.Recovery);
            Assert.False(string.IsNullOrWhiteSpace(state.Detail));
        }
    }

    [Fact]
    public void Routes_without_a_source_yet_say_so_and_offer_what_still_works()
    {
        var snapshot = V2ShellTestData.Snapshot().WithData(DataAvailability.Current, 5000, V2ShellTestData.Now);
        var stash = V2SurfaceStateResolver.Resolve(V2RouteRegistry.Default[V2Routes.Stash], snapshot, V2ShellTestData.Now, Culture);
        var tablet = V2SurfaceStateResolver.Resolve(V2RouteRegistry.Default[V2Routes.Tablet], snapshot, V2ShellTestData.Now, Culture);
        var debrief = V2SurfaceStateResolver.Resolve(V2RouteRegistry.Default[V2Routes.Debrief], snapshot, V2ShellTestData.Now, Culture);

        Assert.Equal(V2SurfaceStateKind.Empty, stash.Kind);
        Assert.Contains(stash.Recovery, action => action.Id == "open-capture");
        Assert.Equal(V2SurfaceStateKind.Empty, tablet.Kind);
        Assert.Equal(V2Routes.Team, Assert.Single(tablet.Recovery).Route);
        Assert.Equal(V2SurfaceStateKind.Ready, debrief.Kind);
    }

    [Fact]
    public void A_demo_readiness_check_is_unconfirmed_rather_than_ready()
    {
        var summary = V2Readiness.Evaluate(
            V2ShellTestData.Snapshot(demo: true).WithData(DataAvailability.DemoFixture, 12, V2ShellTestData.Now),
            V2ShellTestData.Now,
            Culture);

        Assert.Equal(0, summary.ReadyCount);
        Assert.Equal(0, summary.NeedsActionCount);
        Assert.Equal(summary.RequiredCount, summary.UnconfirmedCount);
        Assert.Equal(V2SurfaceStateKind.Partial, summary.Kind);
    }

    [Fact]
    public void An_observing_synced_installation_is_ready_and_optional_sharing_never_counts()
    {
        var snapshot = V2ShellTestData.Snapshot()
            .WithData(DataAvailability.Current, 5000, V2ShellTestData.Now.AddMinutes(-3))
            .Observing() with
        {
            Profile = null,
        };

        var summary = V2Readiness.Evaluate(snapshot, V2ShellTestData.Now, Culture);

        Assert.Equal(["game-log", "screenshots", "text-recognition", "game-data", "profile", "group-sharing"], summary.Checks.Select(check => check.Id));
        Assert.Equal(4, summary.ReadyCount);
        Assert.Equal(5, summary.RequiredCount);
        Assert.Equal(1, summary.UnconfirmedCount);
        Assert.Equal(summary.RequiredCount, summary.ReadyCount + summary.NeedsActionCount + summary.UnconfirmedCount);
        Assert.Equal(V2CheckStatus.Optional, summary.Checks.Single(check => check.Id == "group-sharing").Status);
        Assert.False(summary.Checks.Single(check => check.Id == "group-sharing").Required);
    }

    [Fact]
    public void A_fully_ready_required_checklist_reports_five_of_five_not_five_of_six()
    {
        var checks = Enumerable.Range(1, 5)
            .Select(index => new V2ReadinessCheck(
                $"required-{index}",
                "V2.Shell.Check.Profile",
                V2CheckStatus.Ready,
                "Ready fixture",
                V2Routes.Setup,
                Required: true))
            .Append(new(
                "optional",
                "V2.Shell.Check.GroupSharing",
                V2CheckStatus.Optional,
                "Optional fixture",
                V2Routes.Group,
                Required: false))
            .ToArray();

        var summary = new V2ReadinessSummary(checks);

        Assert.Equal(5, summary.RequiredCount);
        Assert.Equal(5, summary.ReadyCount);
        Assert.Equal(0, summary.NeedsActionCount);
        Assert.Equal(0, summary.UnconfirmedCount);
        Assert.Equal(V2SurfaceStateKind.Ready, summary.Kind);
    }

    [Fact]
    public void A_failed_sync_makes_readiness_failed_and_counts_as_needing_action()
    {
        var summary = V2Readiness.Evaluate(
            V2ShellTestData.Snapshot().WithData(DataAvailability.Error, 0, null).Observing(),
            V2ShellTestData.Now,
            Culture);

        Assert.Equal(V2SurfaceStateKind.Failed, summary.Kind);
        Assert.Equal(V2CheckStatus.Failed, summary.Checks.Single(check => check.Id == "game-data").Status);
        Assert.True(summary.NeedsActionCount >= 1);
    }

    [Fact]
    public void Ages_are_computed_from_the_observation_time()
    {
        Assert.Equal("45s ago", V2ShellText.Age(V2ShellTestData.Now.AddSeconds(-45), V2ShellTestData.Now, Culture));
        Assert.Equal("12m ago", V2ShellText.Age(V2ShellTestData.Now.AddMinutes(-12), V2ShellTestData.Now, Culture));
        Assert.Equal("5h ago", V2ShellText.Age(V2ShellTestData.Now.AddHours(-5), V2ShellTestData.Now, Culture));
        Assert.Equal("3d ago", V2ShellText.Age(V2ShellTestData.Now.AddDays(-3), V2ShellTestData.Now, Culture));
        Assert.Equal("at a future time", V2ShellText.Age(V2ShellTestData.Now.AddMinutes(1), V2ShellTestData.Now, Culture));
    }
}
