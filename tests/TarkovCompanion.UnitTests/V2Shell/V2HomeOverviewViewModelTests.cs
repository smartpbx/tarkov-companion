using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels.V2.Setup;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// Package 17 (home): what the Setup overview shows is whatever readiness, Plan, Debrief and the
/// Settings page gave it — never the concept's placeholder numbers.
/// </summary>
public sealed class V2HomeOverviewViewModelTests
{
    private static V2ReadinessCheck Check(string id, V2CheckStatus status, bool required = true) =>
        new(id, "V2.Shell.Check.GameLog", status, $"{id} detail", V2Routes.Setup, required);

    private static V2HomeOverviewViewModel Overview(
        Action<V2RouteId>? navigate = null,
        Action<V2SetupSection>? select = null) =>
        new(navigate ?? (_ => { }), select ?? (_ => { }));

    [Fact]
    public void EachCheckBecomesAStepAndOnlyTheLastOneDropsItsConnector()
    {
        var overview = Overview();

        overview.ApplyReadiness(
            new([Check("game-log", V2CheckStatus.Ready), Check("screenshots", V2CheckStatus.NeedsAction), Check("group-sharing", V2CheckStatus.Optional, required: false)]),
            "1 of 2 checks ready",
            _ => { });

        Assert.Equal(3, overview.Steps.Count);
        Assert.Equal([true, true, false], overview.Steps.Select(step => step.HasConnector));
        Assert.Equal(V2HomeTone.Done, overview.Steps[0].Tone);
        Assert.Equal(V2HomeTone.Attention, overview.Steps[1].Tone);
        Assert.Equal(V2HomeTone.Muted, overview.Steps[2].Tone);
        Assert.Equal("1 of 2 checks ready", overview.StepsSummary);
    }

    [Fact]
    public void AStepKeepsTheShellsOwnReadinessAutomationIdentity()
    {
        var overview = Overview();

        overview.ApplyReadiness(new([Check("game-log", V2CheckStatus.Unconfirmed)]), string.Empty, _ => { });

        var step = Assert.Single(overview.Steps);
        Assert.Equal("v2-shell-readiness-game-log", step.AutomationId);
        Assert.StartsWith("Open Game log folder.", step.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheRequiredChecksAreCountedAsSystemHealth()
    {
        var overview = Overview();

        overview.ApplyReadiness(
            new([Check("game-log", V2CheckStatus.Ready), Check("screenshots", V2CheckStatus.NeedsAction), Check("group-sharing", V2CheckStatus.Optional, required: false)]),
            string.Empty,
            _ => { });

        Assert.Equal(2, overview.HealthRows.Count);
        Assert.Equal("1 ready", overview.HealthReadyLabel);
        Assert.Equal("1 need attention", overview.HealthAttentionLabel);
        Assert.True(overview.HealthNeedsAttention);
        Assert.False(overview.IsSetUp);
    }

    [Fact]
    public void FinishSetupOpensTheFirstCheckThatWantsSomethingDone()
    {
        V2ReadinessCheck? opened = null;
        var overview = Overview();
        overview.ApplyReadiness(
            new([Check("game-log", V2CheckStatus.Ready), Check("screenshots", V2CheckStatus.Unconfirmed), Check("text-recognition", V2CheckStatus.NeedsAction)]),
            string.Empty,
            check => opened = check);

        overview.PrimaryCommand.Execute(null);

        Assert.Equal("text-recognition", opened?.Id);
    }

    [Fact]
    public void WithEveryRequiredCheckReadyThePrimaryActionOpensTheRaid()
    {
        V2RouteId? navigated = null;
        var overview = Overview(navigate: route => navigated = route);
        overview.ApplyReadiness(new([Check("game-log", V2CheckStatus.Ready)]), string.Empty, _ => { });

        Assert.True(overview.IsSetUp);
        overview.PrimaryCommand.Execute(null);

        Assert.Equal(V2Routes.Raid, navigated);
    }

    [Fact]
    public void ThePlanCardFollowsTheCurrentMapWhenThePlanHasOne()
    {
        var overview = Overview();
        overview.ApplyPlan(
            [
                ("Factory", [new("Find the wrench", "Gunsmith")]),
                ("Customs", [new("Check Big Red", "Debut"), new("Find Dorms intel", "Shortage")]),
            ],
            "Clay · Regular",
            "3 objective(s) across 2 map(s)");

        overview.ApplyMap("Customs");

        Assert.True(overview.HasPlan);
        Assert.Equal("Customs · 2 objectives", overview.PlanTitle);
        Assert.Equal(["Check Big Red", "Find Dorms intel"], overview.PlanObjectives.Select(line => line.Text));
    }

    [Fact]
    public void AFailedPlanIsWordedForThePlayerInsteadOfShowingTheDatabaseError()
    {
        var overview = Overview();

        overview.ApplyPlan([], "Clay · Regular", "Unavailable · SQLite Error 1: 'no such table: quest_progress_profiles'.");

        Assert.True(overview.HasNoPlan);
        Assert.Equal("Your plan couldn't be loaded. Open Plan to retry.", overview.PlanEmpty);
    }

    [Fact]
    public void RecentRaidsAreCappedAndTheEmptyStateIsHonest()
    {
        var overview = Overview();
        Assert.True(overview.HasNoRecentRaids);
        Assert.Equal("No raids recorded yet.", overview.RecentEmpty);

        overview.ApplyRaids(
            [new("Customs · 24m", "Today"), new("Woods · 31m", "Yesterday"), new("Factory · 6m", "Monday"), new("Lighthouse · 44m", "Sunday")],
            string.Empty);

        Assert.Equal(3, overview.RecentRaids.Count);
        Assert.True(overview.HasRecentRaids);
    }

    [Fact]
    public void PrivacySaysWhichWayScreenshotCleanupIsSet()
    {
        var overview = Overview();

        overview.ApplyPrivacy(false, "7 days");
        Assert.Equal("Screenshot cleanup: Off", overview.CleanupTitle);
        Assert.Equal("Screenshots stay on your PC.", overview.CleanupDetail);

        overview.ApplyPrivacy(true, "7 days");
        Assert.Equal("Screenshot cleanup: On", overview.CleanupTitle);
        Assert.Equal("Screenshots are recycled after 7 days.", overview.CleanupDetail);
    }

    [Fact]
    public void ReviewPrivacyOpensThePrivacySectionRatherThanAnotherPage()
    {
        V2SetupSection? selected = null;
        var overview = Overview(select: section => selected = section);

        overview.ReviewPrivacyCommand.Execute(null);

        Assert.Equal(V2SetupSection.Privacy, selected);
    }

    [Fact]
    public void WithNoMapSelectedTheCardSaysSoInsteadOfNamingOne()
    {
        var overview = Overview();

        overview.ApplyMap(null);

        Assert.Equal("No map selected", overview.MapTitle);
        Assert.Equal("Open Raid", overview.ExploreMapLabel);
    }
}
