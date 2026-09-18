using TarkovCompanion.App.Services.V2.Shell;

namespace TarkovCompanion.UnitTests.V2Shell;

/// <summary>
/// One route and capability model, two declarative presentations of it.
/// </summary>
/// <remarks>
/// The #265 comparison is only fair if the variants differ in labels and placement and in nothing
/// else, so these tests pin what must be identical (capabilities, routes, V1 coverage) and what
/// must be recorded exactly as the validation package describes it (labels, order, landing,
/// Search, Intel, Stash and Setup placement).
/// </remarks>
public sealed class V2ShellRegistryTests
{
    [Fact]
    public void Stable_ids_are_the_recorded_ones()
    {
        // A route or capability id is persisted in preview state and addressed by tests and
        // tablets. Changing one is a migration, so the list is spelled out rather than derived.
        Assert.Equal(
            [
                "home", "raid", "raid.loot", "items", "items.ammo", "items.keys", "items.flea", "item", "stash",
                "plan", "plan.hideout", "plan.loadout", "plan.events", "team", "team.group", "team.tablet",
                "debrief", "setup",
            ],
            V2RouteRegistry.Default.Routes.Select(route => route.Id.Value));
        Assert.Equal(V2Capabilities.All.Count, V2Capabilities.All.Distinct().Count());
    }

    [Theory]
    [InlineData("Raid")]
    [InlineData("Plan")]
    [InlineData("raid/loot")]
    [InlineData("")]
    [InlineData("raid loot")]
    public void An_identifier_is_never_a_label_or_a_path(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new V2RouteId(value));
        Assert.ThrowsAny<ArgumentException>(() => new V2CapabilityId(value));
    }

    [Fact]
    public void Every_V1_destination_stays_reachable_through_exactly_one_route()
    {
        var hosted = V2RouteRegistry.Default.Routes
            .Where(route => route.LegacyPage is not null)
            .Select(route => route.LegacyPage!)
            .ToArray();

        Assert.Equal(V2ShellTestData.V1Destinations.Order(StringComparer.Ordinal), hosted.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheRaidRouteHostsTheCockpitRatherThanTheLegacyPage()
    {
        var raid = V2RouteRegistry.Default[V2Routes.Raid];

        Assert.Equal(V2RouteContent.RaidCockpit, raid.Content);
        Assert.Null(raid.LegacyPage);
    }

    [Fact]
    public void ThePlanAndHideoutRoutesHostWorkspacesRatherThanTheLegacyPages()
    {
        var registry = V2RouteRegistry.Default;

        Assert.Equal(V2RouteContent.Workspace, registry[V2Routes.Plan].Content);
        Assert.Null(registry[V2Routes.Plan].LegacyPage);
        Assert.Equal(V2RouteContent.Workspace, registry[V2Routes.Hideout].Content);
        Assert.Null(registry[V2Routes.Hideout].LegacyPage);
    }

    [Fact]
    public void AmmoKeysAndFleaHostNativeWorkspacesUnderIntelRatherThanTheLegacyPages()
    {
        var registry = V2RouteRegistry.Default;

        foreach (var route in new[] { V2Routes.Ammo, V2Routes.Keys, V2Routes.Flea })
        {
            Assert.Equal(V2RouteContent.Workspace, registry[route].Content);
            Assert.Null(registry[route].LegacyPage);
            Assert.Equal(V2Routes.Items, registry[route].Parent);
        }
    }

    [Fact]
    public void Both_variants_expose_every_capability_and_the_same_ones()
    {
        var registry = V2RouteRegistry.Default;
        var a = V2ShellVariants.A.ExposedCapabilities(registry);
        var b = V2ShellVariants.B.ExposedCapabilities(registry);

        Assert.True(a.SetEquals(b), "Variant A and B expose different capabilities.");
        Assert.True(a.SetEquals(V2Capabilities.All), "A variant is missing a capability.");
    }

    [Fact]
    public void Variant_a_is_recorded_as_the_workspace_rail()
    {
        var a = V2ShellVariants.A;

        Assert.Equal(["Raid", "Intel", "Plan", "Team", "Debrief"], a.Destinations.Select(item => V2ShellText.Get(item.LabelKey)));
        Assert.Equal("Setup & Admin", V2ShellText.Get(a.Setup.LabelKey));
        Assert.Equal(V2NavigationStyle.Rail, a.Navigation);
        Assert.Equal(V2SetupPlacement.LabelledRailSection, a.SetupPlacement);
        Assert.Equal(V2SearchPlacement.InsideItemsWorkspace, a.SearchPlacement);
        Assert.Equal(V2IntelPlacement.Workspace, a.IntelPlacement);
        Assert.Equal(V2Routes.Setup, a.Landing);
        Assert.False(a.Addresses.ContainsKey(V2Routes.Home));
        Assert.Equal("intel/stash", a.Addresses[V2Routes.Stash]);
        Assert.Equal("tablet", a.Addresses[V2Routes.Tablet]);
    }

    [Fact]
    public void Variant_b_is_recorded_as_the_workflow_hub()
    {
        var b = V2ShellVariants.B;

        Assert.Equal(["Home", "Raid", "Prepare", "Team", "History"], b.Destinations.Select(item => V2ShellText.Get(item.LabelKey)));
        Assert.Equal("Setup", V2ShellText.Get(b.Setup.LabelKey));
        Assert.Equal(V2NavigationStyle.Row, b.Navigation);
        Assert.Equal(V2SetupPlacement.HeaderLink, b.SetupPlacement);
        Assert.Equal(V2SearchPlacement.Header, b.SearchPlacement);
        Assert.Equal(V2IntelPlacement.BesideCurrentPage, b.IntelPlacement);
        Assert.Equal(V2Routes.Home, b.Landing);
        Assert.Equal("prepare/stash", b.Addresses[V2Routes.Stash]);
        Assert.Equal("tablet", b.Addresses[V2Routes.Tablet]);
    }

    [Fact]
    public void Stash_scan_is_highlighted_under_its_explicit_variant_destination()
    {
        var registry = V2RouteRegistry.Default;

        Assert.Equal(V2Routes.Items, new V2ShellRouter(V2ShellVariants.A, registry).DestinationOf(V2Routes.Stash));
        Assert.Equal(V2Routes.Plan, new V2ShellRouter(V2ShellVariants.B, registry).DestinationOf(V2Routes.Stash));
        Assert.False(new V2ShellRouter(V2ShellVariants.B, registry).DestinationOf(V2Routes.Items).HasValue);
    }

    [Theory]
    [InlineData(V2ShellMode.VariantA)]
    [InlineData(V2ShellMode.VariantB)]
    public void Tablet_has_the_documented_stable_address_and_is_highlighted_under_team(V2ShellMode mode)
    {
        var router = new V2ShellRouter(V2ShellVariants.For(mode), V2RouteRegistry.Default);

        var opened = router.NavigateToAddress("#/tablet");

        Assert.True(opened.Succeeded, opened.Failure);
        Assert.Equal(V2Routes.Tablet, router.Current.Location.Route);
        Assert.Equal(V2Routes.Team, router.CurrentDestination);
        Assert.Equal("#/tablet", router.CurrentAddress);
    }

    [Theory]
    [InlineData(V2ShellMode.VariantA)]
    [InlineData(V2ShellMode.VariantB)]
    public void Every_parameter_free_route_is_offered_globally_and_in_its_local_group(V2ShellMode mode)
    {
        var variant = V2ShellVariants.For(mode);
        var registry = V2RouteRegistry.Default;
        var commands = V2ShellCommands.For(variant, registry);
        var addressable = registry.Routes
            .Where(route => !route.TakesItem && variant.Addresses.ContainsKey(route.Id))
            .ToArray();

        Assert.All(addressable, route =>
            Assert.Contains(commands, command => command.Kind == V2ShellCommandKind.Navigate && command.Route == route.Id));
        foreach (var route in addressable)
        {
            var sections = registry.VisibleSections(variant, route.Id);
            Assert.Contains(sections, section => section.Id == route.Id);
            Assert.Equal(sections.Count, sections.Select(section => section.Id).Distinct().Count());
        }
    }

    [Fact]
    public void Local_groups_include_retained_children_and_the_variant_specific_stash_location()
    {
        var registry = V2RouteRegistry.Default;

        Assert.Equal(
            [V2Routes.Items, V2Routes.Ammo, V2Routes.Keys, V2Routes.Flea, V2Routes.Stash],
            registry.VisibleSections(V2ShellVariants.A, V2Routes.Items).Select(route => route.Id));
        Assert.Equal(
            [V2Routes.Plan, V2Routes.Hideout, V2Routes.Loadout, V2Routes.Events, V2Routes.Stash],
            registry.VisibleSections(V2ShellVariants.B, V2Routes.Plan).Select(route => route.Id));
        Assert.Equal(
            [V2Routes.Team, V2Routes.Group, V2Routes.Tablet],
            registry.VisibleSections(V2ShellVariants.A, V2Routes.Tablet).Select(route => route.Id));
    }

    [Theory]
    [InlineData(V2ShellMode.VariantA)]
    [InlineData(V2ShellMode.VariantB)]
    public void Every_label_and_heading_has_text(V2ShellMode mode)
    {
        var variant = V2ShellVariants.For(mode);
        var keys = variant.Destinations.Select(item => item.LabelKey)
            .Append(variant.Setup.LabelKey)
            .Append(variant.NameKey)
            .Concat(V2RouteRegistry.Default.Routes.Select(route => route.HeadingKey));

        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(V2ShellText.Get(key))));
    }

    [Theory]
    [InlineData(V2ShellMode.VariantA)]
    [InlineData(V2ShellMode.VariantB)]
    public void Every_address_round_trips_in_its_own_variant(V2ShellMode mode)
    {
        var variant = V2ShellVariants.For(mode);
        var codec = new V2AddressCodec(variant, V2RouteRegistry.Default);

        foreach (var route in variant.Addresses.Keys)
        {
            var locations = new List<V2ShellLocation>();
            if (V2RouteRegistry.Default[route].TakesItem)
            {
                if (variant.IntelPlacement == V2IntelPlacement.Workspace)
                {
                    locations.Add(new(route, Item: "5c05308086f7746b2101e90b"));
                }
            }
            else
            {
                locations.Add(new(route));
                if (variant.IntelPlacement == V2IntelPlacement.BesideCurrentPage)
                {
                    locations.Add(new(route, IntelItem: "electric-drill"));
                }
            }

            foreach (var location in locations)
            {
                var address = codec.Format(location);
                var parsed = codec.Parse(address);

                Assert.StartsWith(V2AddressCodec.Prefix, address, StringComparison.Ordinal);
                Assert.True(parsed.Succeeded, parsed.Failure);
                Assert.Equal(location, parsed.Location);
            }
        }
    }

    [Fact]
    public void The_storyboard_addresses_are_the_native_addresses()
    {
        var a = new V2AddressCodec(V2ShellVariants.A, V2RouteRegistry.Default);
        var b = new V2AddressCodec(V2ShellVariants.B, V2RouteRegistry.Default);

        Assert.Equal(new V2ShellLocation(V2Routes.Item, Item: "electric-drill"), a.Parse("#/intel/item/electric-drill").Location);
        Assert.Equal(new V2ShellLocation(V2Routes.Loot, IntelItem: "electric-drill"), b.Parse("#/raid/loot/intel/electric-drill").Location);
        Assert.Equal(new V2ShellLocation(V2Routes.Debrief), b.Parse("#/history").Location);
        Assert.Equal(new V2ShellLocation(V2Routes.Tablet), a.Parse("#/tablet").Location);
        Assert.Equal(new V2ShellLocation(V2Routes.Tablet), b.Parse("#/tablet").Location);
        Assert.Equal(new V2ShellLocation(V2Routes.Plan), a.Parse("plan").Location);
    }

    [Theory]
    [InlineData("v2-a", "#/home")]
    [InlineData("v2-a", "#/raid/loot/intel/electric-drill")]
    [InlineData("v2-b", "#/intel/item/electric-drill")]
    [InlineData("v2-b", "#/intel/electric-drill")]
    [InlineData("v2-b", "#/raid/intel")]
    [InlineData("v2-a", "#/../Config/shell.json")]
    [InlineData("v2-a", "#/raid//loot")]
    [InlineData("v2-a", "#/raid/loot?next=1")]
    [InlineData("v2-a", "C:\\Users\\player")]
    [InlineData("v2-a", "https://example.invalid/raid")]
    [InlineData("v2-b", "")]
    public void Foreign_or_unsafe_addresses_are_refused(string token, string address)
    {
        var codec = new V2AddressCodec(V2ShellVariants.For(V2ShellModes.Parse(token)), V2RouteRegistry.Default);

        var parsed = codec.Parse(address);

        Assert.False(parsed.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Failure));
    }

    [Fact]
    public void An_overlong_address_is_refused()
    {
        var codec = new V2AddressCodec(V2ShellVariants.A, V2RouteRegistry.Default);

        Assert.False(codec.Parse("#/intel/item/" + new string('a', V2AddressCodec.MaxAddressLength)).Succeeded);
    }
}
