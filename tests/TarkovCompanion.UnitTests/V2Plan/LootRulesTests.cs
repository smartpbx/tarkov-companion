using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Core.Domain.Profiles;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.UnitTests.Profiles;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>[#902 P9] Plan › Keep › Loot rules lists what a Loot Scan wrote, and each can be undone.</summary>
public sealed class LootRulesTests
{
    [Fact]
    public void Every_loot_choice_in_the_profile_is_listed_and_stash_rules_are_not()
    {
        var progress = new ProfileProgress(
            20,
            wishlistItemIds: new HashSet<string> { "wish" },
            itemOverrides: new Dictionary<string, string>
            {
                ["leave"] = LootScanProfileRules.LeaveRule,
                ["take"] = LootScanProfileRules.TakeRule,
                ["sell"] = "SellFlea",
                ["guarded"] = LootScanProfileRules.ProtectedRule,
                ["future"] = "SomethingNewer",
            },
            pins: [new ProfilePin(LootScanProfileRules.ItemPinKind, "pinned", 0, null), new ProfilePin("task", "a-quest", 1, null)]);

        var rules = LootRules.From(progress);

        Assert.Equal(
            [
                new LootRuleEntry("pinned", LootRuleKind.Pinned),
                new LootRuleEntry("wish", LootRuleKind.Wishlist),
                new LootRuleEntry("future", LootRuleKind.Unrecognised),
                new LootRuleEntry("leave", LootRuleKind.AlwaysLeave),
                new LootRuleEntry("take", LootRuleKind.AlwaysTake),
            ],
            rules);
    }

    [Fact]
    public async Task Removing_always_leave_clears_it_from_the_profile_and_the_list()
    {
        var runtime = await RuntimeAsync(new ProfileProgress(
            20,
            itemOverrides: new Dictionary<string, string>
            {
                ["bolts"] = LootScanProfileRules.LeaveRule,
                ["gpu"] = LootScanProfileRules.TakeRule,
            }));
        var controls = new ProfileControls(runtime);
        var rules = new LootRulesViewModel(runtime, controls, (id, _) => Task.FromResult<string?>(id == "bolts" ? "Bolts" : null));
        await rules.RefreshAsync();
        var leave = Assert.Single(rules.Rows, row => row.Entry.Kind == LootRuleKind.AlwaysLeave);
        Assert.Equal("Bolts", leave.Name);
        Assert.Equal("gpu", Assert.Single(rules.Rows, row => row.Entry.Kind == LootRuleKind.AlwaysTake).Name);

        await rules.RemoveAsync(leave.Entry);

        Assert.Equal(LootScanItemRule.None, controls.RuleFor("bolts"));
        Assert.Equal(LootScanItemRule.AlwaysTake, controls.RuleFor("gpu"));
        Assert.DoesNotContain(rules.Rows, row => row.ItemId == "bolts");
        Assert.Single(rules.Rows);
    }

    [Fact]
    public async Task Removing_a_pin_or_a_wishlist_entry_clears_only_that()
    {
        var runtime = await RuntimeAsync(new ProfileProgress(
            20,
            wishlistItemIds: new HashSet<string> { "ledx" },
            pins: [new ProfilePin(LootScanProfileRules.ItemPinKind, "ledx", 0, null)]));
        var controls = new ProfileControls(runtime);
        var rules = new LootRulesViewModel(runtime, controls, (_, _) => Task.FromResult<string?>("LEDX"));
        await rules.RefreshAsync();

        await rules.RemoveAsync(new LootRuleEntry("ledx", LootRuleKind.Pinned));

        Assert.False(controls.IsPinned("ledx"));
        Assert.True(controls.IsWishlisted("ledx"));
        Assert.Equal(LootRuleKind.Wishlist, Assert.Single(rules.Rows).Entry.Kind);
    }

    [Fact]
    public async Task The_list_follows_a_rule_set_elsewhere_and_the_chip_counts_it()
    {
        var runtime = await RuntimeAsync(new ProfileProgress(20));
        var controls = new ProfileControls(runtime);
        var rules = new LootRulesViewModel(runtime, controls, (_, _) => Task.FromResult<string?>(null));
        await rules.RefreshAsync();
        Assert.True(rules.HasNoRows);

        // A Loot Scan result's "Always leave" writes through the same controls.
        await controls.SetRuleAsync("salewa", LootScanItemRule.AlwaysLeave);
        await rules.RefreshAsync();

        Assert.Equal("salewa", Assert.Single(rules.Rows).Name);
        Assert.EndsWith("1", rules.ButtonLabel, StringComparison.Ordinal);
    }

    private static async Task<ProfileRuntimeContextService> RuntimeAsync(ProfileProgress progress)
    {
        var profiles = new ProfileContextService(new MemoryProfileStore(), new ProfileClock(Now));
        var profile = Profile(Context(Id(902), "generation-a", ProfileGameMode.Pvp), "unused");
        await profiles.CreateAsync(new(profile.Context, profile.Name, progress, true), CancellationToken.None);
        var runtime = new ProfileRuntimeContextService(profiles);
        await runtime.InitializeAsync(CancellationToken.None);
        return runtime;
    }

    /// <summary>The writes <c>LootScanWorkspaceControls</c> makes, without a scan to decide again.</summary>
    private sealed class ProfileControls(IProfileRuntimeContextService profiles) : ILootScanWorkspaceControls
    {
        public RecommendationRaidRisk Risk => RecommendationRaidRisk.Low;

        public RecommendationRaidPhase? Phase => null;

        private ProfileProgress Progress => profiles.Current.ActiveProfile!.Progress;

        public bool IsPinned(string itemId) => LootScanProfileRules.IsPinned(Progress, itemId);

        public bool IsWishlisted(string itemId) => LootScanProfileRules.IsWishlisted(Progress, itemId);

        public LootScanItemRule RuleFor(string itemId) => LootScanProfileRules.RuleFor(Progress, itemId);

        public Task SetRiskAsync(RecommendationRaidRisk risk) => Task.CompletedTask;

        public Task SetPhaseAsync(RecommendationRaidPhase? phase) => Task.CompletedTask;

        public Task SetPinnedAsync(string itemId, bool pinned) => WriteAsync(progress => LootScanProfileRules.WithPin(progress, itemId, pinned));

        public Task SetWishlistedAsync(string itemId, bool wishlisted) =>
            WriteAsync(progress => LootScanProfileRules.WithWishlist(progress, itemId, wishlisted));

        public Task SetRuleAsync(string itemId, LootScanItemRule rule) => WriteAsync(progress => LootScanProfileRules.WithRule(
            progress,
            itemId,
            rule switch
            {
                LootScanItemRule.AlwaysTake => LootScanProfileRules.TakeRule,
                LootScanItemRule.AlwaysLeave => LootScanProfileRules.LeaveRule,
                _ => null,
            }));

        private Task WriteAsync(Func<ProfileProgress, ProfileProgress> change) =>
            profiles.UpdateActiveProgressAsync(profiles.Current, change(Progress), CancellationToken.None);
    }
}
