using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// The shared router: history, deep links, selection, context and focus, identical in both variants
/// apart from where Intel opens.
/// </summary>
public sealed class V2ShellRouterTests
{
    private const string Drill = "electric-drill";
    private const string DetailsButton = "loot-details-electric-drill";

    [Theory]
    [InlineData(V2ShellMode.VariantA, "#/setup")]
    [InlineData(V2ShellMode.VariantB, "#/home")]
    public void A_first_launch_opens_on_readiness_rather_than_a_map(V2ShellMode mode, string landing)
    {
        var router = Router(mode);

        Assert.Equal(landing, router.CurrentAddress);
        Assert.False(router.CanGoBack);
        Assert.False(router.CanGoForward);
    }

    [Theory]
    [InlineData(V2ShellMode.VariantA)]
    [InlineData(V2ShellMode.VariantB)]
    public void Back_and_forward_return_to_the_place_and_the_control_that_left_it(V2ShellMode mode)
    {
        var router = Router(mode);
        router.Navigate(V2Routes.Raid);

        var moved = router.Navigate(V2Routes.Plan, invoker: "nav-plan");
        var back = router.Back();

        Assert.True(moved.Succeeded);
        Assert.Equal(new V2FocusRequest(V2ShellRouter.PageHeadingTarget, V2FocusReason.PageHeading), moved.Focus);
        Assert.Equal(V2Routes.Raid, router.Current.Location.Route);
        Assert.Equal(new V2FocusRequest("nav-plan", V2FocusReason.Restored), back.Focus);
        Assert.True(router.CanGoForward);

        var forward = router.Forward();

        Assert.True(forward.Succeeded);
        Assert.Equal(V2Routes.Plan, router.Current.Location.Route);
        Assert.False(router.CanGoForward);
    }

    [Fact]
    public void Choosing_the_current_page_focuses_its_heading_without_a_history_entry()
    {
        var router = Router(V2ShellMode.VariantA);
        router.Navigate(V2Routes.Raid);
        var navigations = 0;
        router.Navigated += (_, _) => navigations++;

        var again = router.Navigate(V2Routes.Raid);

        Assert.True(again.Succeeded);
        Assert.Equal(V2FocusReason.PageHeading, again.Focus?.Reason);
        Assert.Single(router.BackEntries);
        Assert.Equal(0, navigations);
    }

    [Fact]
    public void A_navigation_that_starts_a_new_branch_discards_forward_history()
    {
        var router = Router(V2ShellMode.VariantB);
        router.Navigate(V2Routes.Raid);
        router.Navigate(V2Routes.Team);
        router.Back();

        router.Navigate(V2Routes.Debrief);

        Assert.False(router.CanGoForward);
        Assert.Equal("#/history", router.CurrentAddress);
    }

    [Fact]
    public void Variant_a_opens_intel_as_a_workspace_and_back_returns_to_the_loot_decision()
    {
        var router = Router(V2ShellMode.VariantA);
        router.Navigate(V2Routes.Loot);

        var opened = router.OpenIntel(Drill, DetailsButton);

        Assert.True(opened.Succeeded);
        Assert.Equal("#/intel/item/electric-drill", router.CurrentAddress);
        Assert.Equal(V2Routes.Items, router.CurrentDestination);
        Assert.Equal(Drill, router.Current.SelectedEntity);
        Assert.Equal(V2FocusReason.IntelHeading, opened.Focus?.Reason);

        var closed = router.CloseIntel();

        Assert.True(closed.Succeeded);
        Assert.Equal("#/raid/loot", router.CurrentAddress);
        Assert.Equal(new V2FocusRequest(DetailsButton, V2FocusReason.Restored), closed.Focus);
    }

    [Fact]
    public void Variant_b_opens_intel_beside_the_page_at_its_own_address()
    {
        var router = Router(V2ShellMode.VariantB);
        router.Navigate(V2Routes.Loot);

        var opened = router.OpenIntel(Drill, DetailsButton);

        Assert.True(opened.Succeeded);
        Assert.Equal("#/raid/loot/intel/electric-drill", router.CurrentAddress);
        Assert.Equal(V2Routes.Loot, router.Current.Location.Route);
        Assert.Equal(V2Routes.Raid, router.CurrentDestination);
        Assert.Equal(new V2FocusRequest(V2ShellRouter.IntelHeadingTarget, V2FocusReason.IntelHeading), opened.Focus);

        var closed = router.CloseIntel();

        Assert.Equal("#/raid/loot", router.CurrentAddress);
        Assert.Equal(new V2FocusRequest(DetailsButton, V2FocusReason.Invoker), closed.Focus);

        router.Back();

        Assert.Equal("#/raid/loot/intel/electric-drill", router.CurrentAddress);
    }

    [Theory]
    [InlineData(V2ShellMode.VariantA)]
    [InlineData(V2ShellMode.VariantB)]
    public void Both_variants_reach_the_same_state_from_the_same_steps(V2ShellMode mode)
    {
        var router = Router(mode);
        var context = new V2NavigationContext("Moth", "customs", "plan-7", "scan-20260915T115900000Z", V2NavigationContext.ThisDesktop);
        router.UpdateContext(context);
        router.Navigate(V2Routes.Loot);

        router.OpenIntel(Drill, DetailsButton);

        Assert.Equal(Drill, router.Current.SelectedEntity);
        Assert.Same(context, router.Context);

        router.CloseIntel();

        Assert.Equal(V2Routes.Loot, router.Current.Location.Route);
        Assert.Null(router.Current.Location.IntelItem);
        Assert.Same(context, router.Context);
    }

    [Theory]
    [InlineData(V2ShellMode.VariantA)]
    [InlineData(V2ShellMode.VariantB)]
    public void Moving_everywhere_never_changes_the_players_context(V2ShellMode mode)
    {
        var router = Router(mode);
        var context = new V2NavigationContext("Moth", "customs", "plan-7", "scan-1", V2NavigationContext.ThisDesktop);
        router.UpdateContext(context);

        foreach (var route in router.Variant.Addresses.Keys.Where(route => route != V2Routes.Item))
        {
            Assert.True(router.Navigate(route).Succeeded, $"{route} was refused.");
            router.OpenIntel(Drill, invoker: null);
        }

        while (router.CanGoBack)
        {
            router.Back();
        }

        Assert.Same(context, router.Context);
    }

    [Fact]
    public void A_context_update_is_never_a_move_and_never_moves_focus()
    {
        var router = Router(V2ShellMode.VariantB);
        router.Navigate(V2Routes.Raid);
        var navigations = 0;
        router.Navigated += (_, _) => navigations++;

        router.UpdateContext(new("Moth", "woods", null, null, V2NavigationContext.ThisDesktop));

        Assert.Equal(0, navigations);
        Assert.Single(router.BackEntries);
        Assert.Equal("woods", router.Context.MapId);
    }

    [Fact]
    public void Context_carries_profile_raid_team_objective_capture_and_workspace_identity()
    {
        var router = Router(V2ShellMode.VariantB);
        var context = new V2NavigationContext("Moth", "woods", "plan-7", "scan-4", "paired-tablet-2")
        {
            ProfileId = "profile-1",
            ProfileMode = "Pve",
            RaidId = "raid-9",
            RaidState = "InRaid",
            ObjectiveId = "objective-3",
            TeamMemberKeys = ["self", "wingmate"],
            CaptureCorrelationId = "capture-12",
            WorkspaceId = "raid",
            SelectedEntity = "electric-drill",
        };

        router.UpdateContext(context);
        router.Navigate(V2Routes.Team);

        Assert.Same(context, router.Context);
        Assert.Equal("profile-1", router.Context.ProfileId);
        Assert.Equal("Pve", router.Context.ProfileMode);
        Assert.Equal("raid-9", router.Context.RaidId);
        Assert.Equal("InRaid", router.Context.RaidState);
        Assert.Equal("objective-3", router.Context.ObjectiveId);
        Assert.Equal(["self", "wingmate"], router.Context.TeamMemberKeys);
        Assert.Equal("capture-12", router.Context.CaptureCorrelationId);
        Assert.Equal("raid", router.Context.WorkspaceId);
        Assert.Equal("electric-drill", router.Context.SelectedEntity);
        Assert.Equal("paired-tablet-2", router.Context.InitiatingDevice);
    }

    [Fact]
    public void A_deep_link_opens_exactly_what_it_names()
    {
        var router = Router(V2ShellMode.VariantB);

        var opened = router.NavigateToAddress("#/prepare/stash/intel/5c05308086f7746b2101e90b");

        Assert.True(opened.Succeeded);
        Assert.Equal(V2Routes.Stash, router.Current.Location.Route);
        Assert.Equal("5c05308086f7746b2101e90b", router.Current.SelectedEntity);
        Assert.Equal(V2FocusReason.IntelHeading, opened.Focus?.Reason);
        Assert.Equal(V2Routes.Plan, router.CurrentDestination);
    }

    [Fact]
    public void A_refusal_changes_nothing_and_asks_for_no_focus()
    {
        var router = Router(V2ShellMode.VariantA);
        var navigations = 0;
        router.Navigated += (_, _) => navigations++;

        var results = new[]
        {
            router.NavigateToAddress("#/home"),
            router.Navigate(V2Routes.Home),
            router.Navigate(V2Routes.Item),
            router.OpenIntel("../../escape", "invoker"),
            router.CloseIntel(),
            router.Back(),
            router.Forward(),
        };

        Assert.All(results, result =>
        {
            Assert.False(result.Succeeded);
            Assert.Null(result.Focus);
            Assert.False(string.IsNullOrWhiteSpace(result.Failure));
        });
        Assert.Equal(0, navigations);
        Assert.Equal("#/setup", router.CurrentAddress);
    }

    [Fact]
    public void Restoring_a_place_clears_history_and_returns_focus_where_it_was()
    {
        var router = Router(V2ShellMode.VariantA);
        router.Navigate(V2Routes.Raid);
        router.Navigate(V2Routes.Debrief);

        var restored = router.Restore(new(V2Routes.Hideout), "station-lavatory", "hideout-level-lavatory");

        Assert.True(restored.Succeeded);
        Assert.Equal("#/plan/hideout", router.CurrentAddress);
        Assert.Equal("station-lavatory", router.Current.SelectedEntity);
        Assert.Equal(new V2FocusRequest("hideout-level-lavatory", V2FocusReason.Restored), restored.Focus);
        Assert.False(router.CanGoBack);
        Assert.False(router.Restore(new(V2Routes.Home), null, null).Succeeded);
    }

    [Fact]
    public void History_is_bounded_and_forgets_the_oldest_entries_first()
    {
        var router = new V2ShellRouter(V2ShellVariants.A, V2RouteRegistry.Default, historyLimit: 3);

        foreach (var route in new[] { V2Routes.Raid, V2Routes.Plan, V2Routes.Team, V2Routes.Debrief, V2Routes.Items })
        {
            router.Navigate(route);
        }

        Assert.Equal(3, router.BackEntries.Count);
        Assert.Equal(V2Routes.Plan, router.BackEntries[0].Location.Route);
    }

    [Fact]
    public void Recorded_focus_and_selection_do_not_make_history()
    {
        var router = Router(V2ShellMode.VariantA);
        router.Navigate(V2Routes.Plan);

        router.Select("quest-debut");
        router.RecordFocus("quest-row-debut");
        router.Navigate(V2Routes.Team);
        router.Back();

        Assert.Single(router.BackEntries);
        Assert.Equal("quest-debut", router.Current.SelectedEntity);
        Assert.Equal("quest-row-debut", router.Current.FocusTarget);
    }

    [Fact]
    public void Hostile_selection_focus_and_invoker_references_are_refused()
    {
        var router = Router(V2ShellMode.VariantA);

        Assert.Throws<ArgumentException>(() => router.Select("../../inventory"));
        Assert.Throws<ArgumentException>(() => router.RecordFocus("../../other-window"));
        Assert.False(router.Navigate(V2Routes.Raid, "../../other-window").Succeeded);
        Assert.False(router.Restore(new(V2Routes.Raid), "../../inventory", null).Succeeded);
        Assert.False(router.Restore(new(V2Routes.Item, Item: ".."), null, null).Succeeded);
        Assert.False(router.Restore(new(V2Routes.Raid, IntelItem: ".."), null, null).Succeeded);
        Assert.False(router.Restore(new(V2Routes.Raid), null, new string('a', V2ShellPreviewState.MaxFocusTargetLength + 1)).Succeeded);
        Assert.Equal("#/setup", router.CurrentAddress);
        Assert.False(router.CanGoBack);
    }

    private static V2ShellRouter Router(V2ShellMode mode) => new(V2ShellVariants.For(mode), V2RouteRegistry.Default);
}
