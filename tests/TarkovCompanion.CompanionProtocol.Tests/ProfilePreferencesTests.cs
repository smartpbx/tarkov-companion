using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class ProfilePreferencesTests
{
    [Fact]
    public void DesktopActivationNormalizesAnOlderReadableSchemaAndTabletMutationPreservesOtherFields()
    {
        var older = new ProfilePreferencesDocument(
            PreferenceContext(),
            new PreferenceSchemaVersion(1, 0),
            Preferences().Items,
            Preferences().ProtectedItemRules,
            Preferences().RecommendationOverrides,
            Preferences().FavoriteLoadouts,
            []);
        var activated = Apply(
            InitialState(),
            new ActivateProfilePreferencesCommand(
                Command(60),
                new AggregateRevision(1),
                Now,
                Now.AddMinutes(1),
                older),
            DesktopContext());

        var mutationAt = Now.AddSeconds(1);
        var mutated = Apply(
            activated.State,
            new MutateProfilePreferencesCommand(
                Command(61),
                new AggregateRevision(2),
                mutationAt,
                mutationAt.AddMinutes(1),
                PreferenceContext(),
                new PreferenceSchemaVersion(1, 0),
                new SetItemPreferenceMutation(new ItemPreference("item-gpu", true, true, 1))),
            TabletContext(mutationAt));

        Assert.Equal(CommandDisposition.Applied, activated.Acknowledgement.Disposition);
        Assert.Equal(PreferenceSchemaCompatibility.Migratable, PreferenceSchemaPolicy.Classify(older.SchemaVersion));
        Assert.Equal(PreferenceSchemaVersion.Current, activated.State.ProfilePreferences.ActiveProfile!.SchemaVersion);
        Assert.Equal(CommandDisposition.Applied, mutated.Acknowledgement.Disposition);
        Assert.Equal(2, mutated.State.ProfilePreferences.Cursor.Revision.Value);
        Assert.Equal(["item-gpu", "item-ledx"], mutated.State.ProfilePreferences.ActiveProfile!.Items.Select(item => item.ItemId));
        Assert.Single(mutated.State.ProfilePreferences.ActiveProfile.ProtectedItemRules);
        Assert.Single(mutated.State.ProfilePreferences.ActiveProfile.RecommendationOverrides);
        Assert.Single(mutated.State.ProfilePreferences.ActiveProfile.FavoriteLoadouts);
        Assert.Empty(mutated.State.ProfilePreferences.ActiveProfile.SharedPersonalization);
        Assert.Equal(TabletDevice, mutated.State.ProfilePreferences.LastChangedOrigin!.DeviceId);
        Assert.Equal(mutationAt, mutated.State.ProfilePreferences.ChangedUtc);
        var update = Assert.IsType<ProfilePreferencesCanonicalUpdate>(mutated.Update);
        Assert.Equal(update.Origin, update.State.LastChangedOrigin);
        Assert.Equal(update.ChangedUtc, update.State.ChangedUtc);
    }

    [Fact]
    public void VersionOneZeroWireDocumentMayOmitTheFieldIntroducedByOneOne()
    {
        var node = GoldenNode("commands/activate-profile-preferences.json");
        node["command"]!["preferences"]!.AsObject().Remove("sharedPersonalization");

        Assert.Empty(new SchemaValidator(SchemaNode()).Validate(node));
        var envelope = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(
            Encoding.UTF8.GetBytes(node.ToJsonString()));
        var command = Assert.IsType<ActivateProfilePreferencesCommand>(envelope.Command);
        var applied = DesktopCanonicalStateMachine.Apply(InitialState(), envelope, DesktopContext());

        Assert.Empty(command.Preferences.SharedPersonalization);
        Assert.Equal(CommandDisposition.Applied, applied.Acknowledgement.Disposition);
        Assert.Equal(PreferenceSchemaVersion.Current, applied.State.ProfilePreferences.ActiveProfile!.SchemaVersion);
        Assert.Empty(applied.State.ProfilePreferences.ActiveProfile.SharedPersonalization);
    }

    [Fact]
    public void EveryClosedMutationChangesOnlyItsNamedPreferenceCollection()
    {
        var state = Activate();
        ProfilePreferenceMutation[] mutations =
        [
            new RemoveItemPreferenceMutation("item-ledx"),
            new UpsertProtectedItemRuleMutation(new ProtectedItemRule("rule-gpu", ProtectedItemSelectorKind.Item, "item-gpu", ProtectedItemDisposition.AskBeforeDiscard, 2)),
            new DeleteProtectedItemRuleMutation("rule-ledx"),
            new SetRecommendationOverrideMutation(new RecommendationOverride("item-gpu", RecommendationOverrideAction.Keep, null)),
            new DeleteRecommendationOverrideMutation("item-ledx"),
            new UpsertFavoriteLoadoutMutation(new FavoriteLoadout("loadout-2", "Factory", [new FavoriteLoadoutItem("primary", "item-smg", 1)])),
            new DeleteFavoriteLoadoutMutation("loadout-1"),
            new SetSharedPersonalizationMutation(new SharedPersonalization(SharedPersonalizationKind.Route, "route-1", true)),
            new DeleteSharedPersonalizationMutation(SharedPersonalizationKind.Loadout, "loadout-1"),
        ];

        for (var index = 0; index < mutations.Length; index++)
        {
            var at = Now.AddMilliseconds(index + 1);
            var reduction = Apply(
                state,
                new MutateProfilePreferencesCommand(
                    Command(70 + index),
                    state.ProfilePreferences.Cursor.Revision.Next(),
                    at,
                    at.AddMinutes(1),
                    PreferenceContext(),
                    PreferenceSchemaVersion.Current,
                    mutations[index]),
                TabletContext(at));
            Assert.Equal(CommandDisposition.Applied, reduction.Acknowledgement.Disposition);
            state = reduction.State;
        }

        var preferences = state.ProfilePreferences.ActiveProfile!;
        Assert.Empty(preferences.Items);
        Assert.Equal("rule-gpu", Assert.Single(preferences.ProtectedItemRules).RuleId);
        Assert.Equal("item-gpu", Assert.Single(preferences.RecommendationOverrides).ItemId);
        Assert.Equal("loadout-2", Assert.Single(preferences.FavoriteLoadouts).LoadoutId);
        var shared = Assert.Single(preferences.SharedPersonalization);
        Assert.Equal(SharedPersonalizationKind.Route, shared.Kind);
        Assert.Equal("route-1", shared.ReferenceId);
    }

    [Fact]
    public void EveryClosedMutationRoundTripsAndAnUnknownMutationFailsClosed()
    {
        ProfilePreferenceMutation[] mutations =
        [
            new SetItemPreferenceMutation(new ItemPreference("item-gpu", true, true, 1)),
            new RemoveItemPreferenceMutation("item-ledx"),
            new UpsertProtectedItemRuleMutation(new ProtectedItemRule("rule-gpu", ProtectedItemSelectorKind.Item, "item-gpu", ProtectedItemDisposition.AskBeforeDiscard, 2)),
            new DeleteProtectedItemRuleMutation("rule-ledx"),
            new SetRecommendationOverrideMutation(new RecommendationOverride("item-gpu", RecommendationOverrideAction.Keep, null)),
            new DeleteRecommendationOverrideMutation("item-ledx"),
            new UpsertFavoriteLoadoutMutation(new FavoriteLoadout("loadout-2", "Factory", [new FavoriteLoadoutItem("primary", "item-smg", 1)])),
            new DeleteFavoriteLoadoutMutation("loadout-1"),
            new SetSharedPersonalizationMutation(new SharedPersonalization(SharedPersonalizationKind.Route, "route-1", true)),
            new DeleteSharedPersonalizationMutation(SharedPersonalizationKind.Loadout, "loadout-1"),
        ];

        for (var index = 0; index < mutations.Length; index++)
        {
            var command = new MutateProfilePreferencesCommand(
                Command(80 + index),
                new AggregateRevision(2),
                Now,
                Now.AddMinutes(1),
                PreferenceContext(),
                PreferenceSchemaVersion.Current,
                mutations[index]);
            var wire = CompanionProtocolJson.Serialize(Envelope(command));
            var roundTrip = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(wire);
            var decoded = Assert.IsType<MutateProfilePreferencesCommand>(roundTrip.Command);

            Assert.Equal(mutations[index].GetType(), decoded.Mutation.GetType());
            AssertJsonEqual(wire, CompanionProtocolJson.Serialize(roundTrip));
        }

        var hostile = JsonNode.Parse(CompanionProtocolJson.Serialize(Envelope(new MutateProfilePreferencesCommand(
            Command(89),
            new AggregateRevision(2),
            Now,
            Now.AddMinutes(1),
            PreferenceContext(),
            PreferenceSchemaVersion.Current,
            mutations[0]))))!;
        hostile["command"]!["mutation"]!["type"] = "futureMutation";

        Assert.Throws<JsonException>(() => CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(
            Encoding.UTF8.GetBytes(hostile.ToJsonString())));
    }

    [Fact]
    public void WrongProfileStaleSchemaAndMissingCapabilityCannotChangeCanonicalPreferences()
    {
        var active = Activate();
        var wrongProfile = Apply(
            active,
            Mutation(Command(90), 2, PreferenceContext(2), PreferenceSchemaVersion.Current),
            TabletContext());
        var futureSchema = Apply(
            active,
            Mutation(Command(91), 2, PreferenceContext(), new PreferenceSchemaVersion(1, 2)),
            TabletContext());
        var wrongMajor = Apply(
            active,
            Mutation(Command(92), 2, PreferenceContext(), new PreferenceSchemaVersion(2, 0)),
            TabletContext());
        var unauthorized = Apply(
            active,
            Mutation(Command(93), 2, PreferenceContext(), PreferenceSchemaVersion.Current),
            TabletContext(capabilities: [DeviceCapability.FollowDesktop]));
        var stale = Apply(
            active,
            Mutation(Command(94), 1, PreferenceContext(), PreferenceSchemaVersion.Current),
            TabletContext());

        Assert.Equal(CommandDisposition.RequiresSnapshot, wrongProfile.Acknowledgement.Disposition);
        Assert.Same(active, wrongProfile.State);
        Assert.Equal(CommandDisposition.UnsupportedPreferenceSchema, futureSchema.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.UnsupportedPreferenceSchema, wrongMajor.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.RejectedUnauthorized, unauthorized.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.RejectedConflict, stale.Acknowledgement.Disposition);
        Assert.All(
            new[] { futureSchema, wrongMajor, unauthorized, stale },
            reduction => Assert.Same(active, reduction.State));
    }

    [Fact]
    public void ResetDeleteAndProfileSwitchAreExplicitAndNeverExposeThePreviousProfile()
    {
        var active = Activate();
        var resetAt = Now.AddSeconds(1);
        var reset = Apply(
            active,
            new ResetProfilePreferencesCommand(
                Command(100),
                new AggregateRevision(2),
                resetAt,
                resetAt.AddMinutes(1),
                PreferenceContext(),
                new PreferenceSchemaVersion(1, 0)),
            TabletContext(resetAt));
        var deletedAt = Now.AddSeconds(2);
        var deleted = Apply(
            reset.State,
            new DeleteProfilePreferencesCommand(
                Command(101),
                new AggregateRevision(3),
                deletedAt,
                deletedAt.AddMinutes(1),
                PreferenceContext(),
                PreferenceSchemaVersion.Current),
            TabletContext(deletedAt));
        var switchedAt = Now.AddSeconds(3);
        var switched = Apply(
            deleted.State,
            new ActivateProfilePreferencesCommand(
                Command(102),
                new AggregateRevision(4),
                switchedAt,
                switchedAt.AddMinutes(1),
                Preferences(2)),
            DesktopContext(switchedAt));

        Assert.Empty(reset.State.ProfilePreferences.ActiveProfile!.Items);
        Assert.Empty(reset.State.ProfilePreferences.ActiveProfile.ProtectedItemRules);
        Assert.Null(deleted.State.ProfilePreferences.ActiveProfile);
        Assert.Equal(PreferenceContext(2), switched.State.ProfilePreferences.ActiveProfile!.Context);
        Assert.DoesNotContain(
            switched.State.ProfilePreferences.ActiveProfile.Items,
            item => item.ItemId.Contains("profile-1", StringComparison.Ordinal));
        Assert.Equal(4, switched.State.ProfilePreferences.Cursor.Revision.Value);
    }

    [Fact]
    public void SnapshotUpdateAndAcknowledgementCarryThePreferenceCursorAcrossReconnect()
    {
        var initial = InitialState();
        var activated = ActivateReduction(initial);
        var replica = new CanonicalReplica(initial, new DeliverySequence(0), false);
        var observed = replica.Observe(new ServerEnvelope(
            CompanionProtocolVersion.Current,
            TabletSession,
            DesktopDevice,
            Now,
            new DeliverySequence(1),
            new CanonicalUpdateMessage(activated.Update!)));
        var acknowledgement = observed.Replica.CreateDeliveryAcknowledgement(
            CompanionProtocolVersion.Current,
            TabletSession,
            Now.AddSeconds(1));
        var preferenceAck = Assert.Single(
            acknowledgement!.AggregateAcknowledgements,
            value => value.Aggregate == CanonicalAggregateKind.ProfilePreferences);

        Assert.Equal(ReplicaDisposition.Applied, observed.Disposition);
        Assert.Equal(PreferenceContext(), observed.Replica.State!.ProfilePreferences.ActiveProfile!.Context);
        Assert.Equal(Preferences().Items.Select(item => item.ItemId), observed.Replica.State.ProfilePreferences.ActiveProfile.Items.Select(item => item.ItemId));
        Assert.Equal(new AggregateRevision(1), preferenceAck.Revision);
        Assert.Equal(Command(60), preferenceAck.AppliedChangeId);

        var plan = ReconnectPlanner.Plan(
            activated.State,
            observed.Replica.CreateReconnectRequest(CompanionProtocolVersion.Current, TabletSession, Now.AddSeconds(1)),
            DeliveryLedger.Empty,
            TabletDevice,
            CompanionProtocolVersion.Current);
        Assert.Equal(ReconnectDisposition.FullSnapshot, plan.Plan.Disposition);
        Assert.Equal(PreferenceContext(), plan.Plan.Snapshot!.ProfilePreferences.ActiveProfile!.Context);
        Assert.Single(plan.Plan.Snapshot.ProfilePreferences.ActiveProfile.Items);
    }

    [Fact]
    public void FutureOptionalMutationFieldsCannotEraseExistingPreferenceCollections()
    {
        var active = Activate();
        var node = JsonNode.Parse(File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "Golden",
            "commands",
            "mutate-profile-preferences.json")))!;
        node["command"]!["mutation"]!["futureRecommendationPolicy"] = "leave-existing";
        var envelope = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(Encoding.UTF8.GetBytes(node.ToJsonString()));
        var applied = DesktopCanonicalStateMachine.Apply(active, envelope, TabletContext(Now.AddMinutes(1)));

        Assert.Equal(CommandDisposition.Applied, applied.Acknowledgement.Disposition);
        Assert.Single(applied.State.ProfilePreferences.ActiveProfile!.ProtectedItemRules);
        Assert.Single(applied.State.ProfilePreferences.ActiveProfile.RecommendationOverrides);
        Assert.Single(applied.State.ProfilePreferences.ActiveProfile.FavoriteLoadouts);
        Assert.Single(applied.State.ProfilePreferences.ActiveProfile.SharedPersonalization);
        Assert.Equal(2, applied.State.ProfilePreferences.ActiveProfile.Items.Count);
    }

    [Fact]
    public void PreferenceDocumentsEnforceCollectionIdentityAndSizeBounds()
    {
        var context = PreferenceContext();
        Assert.Throws<ArgumentException>(() => new ProfilePreferencesDocument(
            context,
            PreferenceSchemaVersion.Current,
            [new ItemPreference("same", true, false, 0), new ItemPreference("same", false, true, 1)],
            [],
            [],
            [],
            []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProfilePreferencesDocument(
            context,
            PreferenceSchemaVersion.Current,
            Enumerable.Range(0, ProtocolBounds.MaxProfilePreferenceItems + 1)
                .Select(index => new ItemPreference($"item-{index}", true, false, index))
                .ToArray(),
            [],
            [],
            [],
            []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FavoriteLoadout(
            "too-large",
            "Too large",
            Enumerable.Range(0, ProtocolBounds.MaxFavoriteLoadoutItems + 1)
                .Select(index => new FavoriteLoadoutItem($"slot-{index}", $"item-{index}", 1))
                .ToArray()));
        Assert.Throws<ArgumentException>(() => new ProfilePreferencesAggregate(
            new AggregateCursor(new AggregateRevision(1), Command(1)),
            Preferences(),
            null,
            null));
    }

    private static CanonicalCompanionState Activate() => ActivateReduction(InitialState()).State;

    private static CommandReduction ActivateReduction(CanonicalCompanionState state) => Apply(
        state,
        new ActivateProfilePreferencesCommand(
            Command(60),
            new AggregateRevision(1),
            Now,
            Now.AddMinutes(1),
            Preferences()),
        DesktopContext());

    private static MutateProfilePreferencesCommand Mutation(
        CommandId commandId,
        long revision,
        PreferenceProfileContext context,
        PreferenceSchemaVersion schemaVersion) => new(
        commandId,
        new AggregateRevision(revision),
        Now,
        Now.AddMinutes(1),
        context,
        schemaVersion,
        new SetItemPreferenceMutation(new ItemPreference("item-gpu", true, false, 1)));
}
