using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

/// <summary>
/// Holds the paired acknowledgement to the v2 contract's disposition table, both by construction
/// and by replaying reducer output through the Core <see cref="StateAcknowledgement"/> rules.
/// </summary>
public sealed class AcknowledgementContractTests
{
    private static readonly CommandId This = Command(1);
    private static readonly CommandId Other = Command(2);

    [Fact]
    public void TheDispositionTableRejectsInconsistentAcknowledgements()
    {
        var state = StateWithWorkspaceAt(3, Other);

        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.Applied, 3, 3, Other, null));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.Applied, 3, 2, This, null));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.RejectedStale, 3, 3, Other, state));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.RejectedConflict, 4, 3, Other, state));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.RejectedExpired, 3, 3, This, null));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.RejectedStale, 2, 3, Other, null));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.Applied, 3, 3, This, StateWithWorkspaceAt(3, This)));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.RejectedConflict, 3, 3, Other, StateWithWorkspaceAt(3, Command(9))));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.RejectedInvalidState, 1, 0, Other, null));

        Assert.Equal(CommandDisposition.Applied, Ack(CommandDisposition.Applied, 3, 3, This, null).Disposition);
        Assert.Equal(CommandDisposition.RejectedStale, Ack(CommandDisposition.RejectedStale, 2, 3, Other, state).Disposition);
        Assert.Equal(CommandDisposition.RejectedConflict, Ack(CommandDisposition.RejectedConflict, 3, 3, Other, state).Disposition);
        Assert.Equal(CommandDisposition.RequiresSnapshot, Ack(CommandDisposition.RequiresSnapshot, 9, 3, Other, state).Disposition);

        // Only Applied names this command. Every rejection without canonical state, identifier reuse
        // and unsupported version included, describes no revision at all.
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.RejectedCommandIdReuse, 3, 3, This, StateWithWorkspaceAt(3, This)));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.RejectedCommandIdReuse, 3, 3, Other, null));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.UnsupportedVersion, 3, 3, Other, null));
        Assert.Throws<ArgumentException>(() => Ack(CommandDisposition.RequiresPreview, 3, 3, This, StateWithWorkspaceAt(3, This)));
        foreach (var disposition in Enum.GetValues<CommandDisposition>().Where(item => !CarriesState(item) && item != CommandDisposition.Applied))
        {
            var acknowledgement = Ack(disposition, 3, 0, null, null);
            Assert.Equal(0, acknowledgement.AppliedRevision.Value);
            Assert.Null(acknowledgement.AppliedChangeId);
        }
    }

    [Fact]
    public void TheV2ContractDocumentGovernsPairedAcknowledgementsWithoutDrift()
    {
        var contract = File.ReadAllText(RepositoryFile("docs", "V2_CONTRACT.md"));
        var protocol = File.ReadAllText(RepositoryFile("docs", "PAIRED_DEVICE_PROTOCOL.md"));

        Assert.Contains("docs/PAIRED_DEVICE_PROTOCOL.md", contract, StringComparison.Ordinal);
        Assert.Contains("only `Applied` may name the acknowledged change", contract, StringComparison.Ordinal);
        Assert.Contains("`MapMarkState`", contract, StringComparison.Ordinal);
        Assert.Contains("`CaptureIntentState`", contract, StringComparison.Ordinal);
        Assert.Contains($"{MapMarkState.MaxLabelLength} characters", contract, StringComparison.Ordinal);
        Assert.Contains($"{MapMarkState.MaxLabelLength} characters", protocol, StringComparison.Ordinal);
        foreach (var disposition in Enum.GetNames<AcknowledgementDisposition>())
        {
            Assert.Contains($"| `{disposition}` |", contract, StringComparison.Ordinal);
            Assert.Contains($"| `{disposition}` |", protocol, StringComparison.Ordinal);
        }

        foreach (var disposition in Enum.GetNames<CommandDisposition>())
        {
            Assert.Contains($"`{disposition}`", protocol, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReducerAcknowledgementsSharingAV2DispositionSatisfyTheCoreStateAcknowledgementRules()
    {
        var create = Upsert(10, 1, 0, Now);
        var applied = Apply(InitialState(), create, TabletContext());
        var duplicate = Apply(applied.State, create, TabletContext(Now.AddSeconds(1)));
        var conflict = Apply(applied.State, Upsert(11, 1, 0, Now, mark: 2), TabletContext());
        var edited = Apply(applied.State, Upsert(12, 2, 1, Now), TabletContext());
        var stale = Apply(edited.State, Upsert(13, 1, 0, Now, mark: 3), TabletContext());
        var unsupported = DesktopCanonicalStateMachine.Apply(
            InitialState(),
            Envelope(Upsert(14, 1, 0, Now), version: new CompanionProtocolVersion(2, 1)),
            TabletContext());

        foreach (var acknowledgement in new[] { applied, duplicate, conflict, stale, unsupported }.Select(item => item.Acknowledgement))
        {
            Assert.NotNull(acknowledgement.CoreDisposition);
            AssertCoreAccepts(acknowledgement);
        }
    }

    [Fact]
    public void HostileCommandSequencesNeverEscapeTheReducerOrBreakAcknowledgementRules()
    {
        var random = new Random(27_604);
        var state = InitialState();
        var device = PairedTablet();
        var other = PairedTablet(OtherDevice);
        var sessions = new[] { ActiveSession(device, TabletSession), ActiveSession(other, OtherSession) };
        var now = Now;
        var history = new List<CompanionCommand>();

        for (var iteration = 0; iteration < 3_000; iteration++)
        {
            now = now.AddMilliseconds(random.Next(1, 2_500));
            if (random.NextDouble() < 0.03)
            {
                var maintained = DesktopCanonicalStateMachine.ApplyMaintenance(state, now, [device, other], sessions);
                Assert.All(maintained.Updates, update => Assert.Equal(state.AuthorityEpoch, update.AuthorityEpoch));
                state = maintained.State;
                continue;
            }

            var context = random.Next(3) switch
            {
                0 => DesktopContext(now),
                1 => OtherContext(now),
                _ => TabletContext(now, random.NextDouble() < 0.2 ? Enum.GetValues<DeviceCapability>() : TabletCapabilities),
            };
            var command = history.Count > 0 && random.NextDouble() < 0.1
                ? history[random.Next(history.Count)]
                : RandomCommand(random, state, now, history.Count);
            history.Add(command);
            var envelope = new ClientCommandEnvelope(
                random.NextDouble() < 0.02 ? new CompanionProtocolVersion(2, 1) : CompanionProtocolVersion.Current,
                random.NextDouble() < 0.02 ? OtherSession : context.SessionId,
                random.NextDouble() < 0.02 ? new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099")) : state.AuthorityEpoch,
                now,
                command);

            var reduction = DesktopCanonicalStateMachine.Apply(state, envelope, context);
            var acknowledgement = reduction.Acknowledgement;

            Assert.Equal(command.CommandId, acknowledgement.CommandId);
            Assert.Equal(reduction.State.AuthorityEpoch, acknowledgement.AuthorityEpoch);
            Assert.True(CanonicalDeliveryBudget.Fits(reduction.State));
            Assert.InRange(reduction.State.RecentCommands.Count, 0, ProtocolBounds.MaxRecentCommands);
            if (acknowledgement.Disposition != CommandDisposition.Applied)
            {
                Assert.Same(state, reduction.State);
                Assert.Null(reduction.Update);
                Assert.NotEqual(command.CommandId, acknowledgement.AppliedChangeId);
                if (!CarriesState(acknowledgement.Disposition))
                {
                    Assert.Equal(0, acknowledgement.AppliedRevision.Value);
                    Assert.Null(acknowledgement.CanonicalState);
                }
            }
            else if (acknowledgement.Code == "duplicate-command")
            {
                Assert.Equal(command.CommandId, acknowledgement.AppliedChangeId);
                Assert.Equal(acknowledgement.RequestedRevision, acknowledgement.AppliedRevision);
                Assert.Same(state, reduction.State);
            }
            else
            {
                Assert.Equal(state.GlobalRevision.Next(), reduction.State.GlobalRevision);
                Assert.NotNull(reduction.Update);
                Assert.Equal(command.CommandId, reduction.Update.ChangeId);
                Assert.Equal(command.RequestedRevision, reduction.State.Cursor(command.Aggregate).Revision);
            }

            if (acknowledgement.CoreDisposition is not null)
            {
                AssertCoreAccepts(acknowledgement);
            }

            state = reduction.State;
        }
    }

    private static bool CarriesState(CommandDisposition disposition) => disposition is
        CommandDisposition.RejectedStale or CommandDisposition.RejectedConflict or
        CommandDisposition.RequiresPreview or CommandDisposition.RequiresSnapshot;

    private static string RepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
            {
                return Path.Combine(segments.Prepend(directory.FullName).ToArray());
            }
        }

        throw new DirectoryNotFoundException("The repository root was not found above the test output.");
    }

    private static void AssertCoreAccepts(CommandAcknowledgement acknowledgement)
    {
        var receiver = V2ContractVersion.Current;
        var requested = acknowledgement.Disposition == CommandDisposition.UnsupportedVersion
            ? new V2ContractVersion(2, 1)
            : receiver;
        var core = new StateAcknowledgement(
            new StateStreamId($"paired/{acknowledgement.Aggregate}"),
            new StateChangeId(acknowledgement.CommandId.Value),
            new StateRevision(acknowledgement.RequestedRevision.Value),
            new StateRevision(acknowledgement.AppliedRevision.Value),
            acknowledgement.AppliedChangeId is { } change ? new StateChangeId(change.Value) : null,
            acknowledgement.CoreDisposition!.Value,
            requested,
            receiver,
            new WorkspaceOrigin(
                new WorkspaceId(Guid.Parse("80000000-0000-0000-0000-000000000001")),
                TabletDevice,
                WorkspaceOriginKind.PairedDevice,
                "paired-tablet"),
            Now);
        Assert.Equal(acknowledgement.CoreDisposition, core.Disposition);
    }

    private static CommandAcknowledgement Ack(
        CommandDisposition disposition,
        long requested,
        long applied,
        CommandId? appliedChange,
        CanonicalCompanionState? state) =>
        new(
            This,
            CanonicalAggregateKind.Workspace,
            new AggregateRevision(requested),
            new AggregateRevision(applied),
            appliedChange,
            state?.GlobalRevision ?? new GlobalRevision(3),
            Epoch,
            disposition,
            "contract-check",
            state);

    private static CanonicalCompanionState StateWithWorkspaceAt(long revision, CommandId change)
    {
        var initial = InitialState();
        return new CanonicalCompanionState(
            initial.AuthorityEpoch,
            initial.WorkspaceId,
            initial.DesktopInstanceId,
            new GlobalRevision(3),
            initial.DesktopDeviceId,
            initial.DeviceModes,
            new WorkspaceAggregate(new AggregateCursor(new AggregateRevision(revision), change), Projection()),
            initial.Marks,
            initial.CaptureIntent,
            initial.ProfilePreferences);
    }

    private static CompanionCommand RandomCommand(Random random, CanonicalCompanionState state, DateTimeOffset now, int sequence)
    {
        var id = Command(50_000 + sequence);
        var issued = now.AddMilliseconds(-random.Next(0, 90_000));
        var expires = issued.AddSeconds(random.Next(1, 300));
        AggregateRevision Revision(CanonicalAggregateKind aggregate) =>
            new(Math.Max(1, state.Cursor(aggregate).Revision.Value + random.Next(-1, 3)));
        var capture = state.CaptureIntent.ActiveIntent?.IntentId ?? Capture(1);
        var artifact = random.NextDouble() < 0.5 ? "artifact-1" : null;
        var preferenceContext = state.ProfilePreferences.ActiveProfile?.Context ?? PreferenceContext();
        var preferenceSchema = random.NextDouble() < 0.1
            ? new PreferenceSchemaVersion(1, 2)
            : random.NextDouble() < 0.2
                ? new PreferenceSchemaVersion(1, 0)
                : PreferenceSchemaVersion.Current;
        return random.Next(19) switch
        {
            0 => new SetInteractionModeCommand(id, Revision(CanonicalAggregateKind.DeviceModes), issued, expires, random.NextDouble() < 0.5 ? CompanionInteractionMode.Follow : CompanionInteractionMode.Independent),
            1 => new RequestControlCommand(id, Revision(CanonicalAggregateKind.DeviceModes), issued, expires, TimeSpan.FromSeconds(random.Next(1, 300))),
            2 => random.NextDouble() < 0.5
                ? new ResolveControlCommand(id, Revision(CanonicalAggregateKind.DeviceModes), issued, expires, state.DeviceModes.PendingControl?.RequestCommandId ?? Command(1), false, null)
                : new ResolveControlCommand(id, Revision(CanonicalAggregateKind.DeviceModes), issued, expires, state.DeviceModes.PendingControl?.RequestCommandId ?? Command(1), true, new ControlLeaseId(Guid.Parse("41000000-0000-0000-0000-000000000001"))),
            3 => new PreemptControlCommand(id, Revision(CanonicalAggregateKind.DeviceModes), issued, expires, "fuzz"),
            4 => new UpdateDesktopWorkspaceCommand(id, Revision(CanonicalAggregateKind.Workspace), issued, expires, Projection(random.NextDouble() < 0.5 ? "woods" : "customs")),
            5 => new ControlWorkspaceCommand(id, Revision(CanonicalAggregateKind.Workspace), issued, expires, random.Next(5) switch
            {
                0 => new NavigateWorkspaceAction(WorkspaceKind.Intel, "woods", null, null),
                1 => new SearchWorkspaceAction("fuzz"),
                2 => new FilterWorkspaceAction(["extracts"], []),
                3 => new SelectWorkspaceAction(new WorkspaceSelection(WorkspaceSelectionKind.Item, "item-1", null, null)),
                _ => new OpenWorkspaceDialogAction(WorkspaceDialogKind.DeviceRevocation),
            }),
            6 => new ShowOnDesktopCommand(
                id,
                Revision(CanonicalAggregateKind.Workspace),
                issued,
                expires,
                Projection("factory", random.NextDouble() < 0.2 ? WorkspaceDialogKind.PairingApproval : null),
                random.NextDouble() < 0.3
                    ? new OfflineQueuePreview(issued, issued, state.AuthorityEpoch, state.Workspace.Cursor.Revision)
                    : null),
            7 => new UpsertMarkCommand(
                id,
                Revision(CanonicalAggregateKind.Marks),
                issued,
                expires,
                Mark(random.Next(1, 4)),
                random.Next(0, 3),
                Draft(random.NextDouble(), random.NextDouble() < 0.3 ? MapMarkKind.Ping : MapMarkKind.Waypoint, random.NextDouble() < 0.1 ? MapMarkScope.Team : MapMarkScope.PairedDevice)),
            8 => new DeleteMarkCommand(id, Revision(CanonicalAggregateKind.Marks), issued, expires, Mark(random.Next(1, 4)), random.Next(1, 3)),
            9 => new RequestCaptureIntentCommand(id, Revision(CanonicalAggregateKind.CaptureIntent), issued, expires, Capture(random.Next(1, 3)), "fuzz", CaptureSession(1), ScanIntent.Auto, new CompanionCaptureContext(null, null, null, null, [], [], [])),
            10 => new ReportCaptureProgressCommand(id, Revision(CanonicalAggregateKind.CaptureIntent), issued, expires, capture, (ContextualCaptureProgressPhase)random.Next(1, 14), random.Next(0, 101), artifact, artifact is null ? null : 0, null),
            11 => new PublishCaptureResultCommand(
                id,
                Revision(CanonicalAggregateKind.CaptureIntent),
                issued,
                expires,
                capture,
                new ContextualCaptureResult("result-1", "artifact-1", 0, new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current), null, issued, ScreenshotProvenance(issued)),
                []),
            12 => new ReviewCaptureResultCommand(id, Revision(CanonicalAggregateKind.CaptureIntent), issued, expires, capture, random.NextDouble() < 0.5 ? CaptureReviewDisposition.Accepted : CaptureReviewDisposition.NeedsCorrection, null),
            13 => new CorrectCaptureResultCommand(id, Revision(CanonicalAggregateKind.CaptureIntent), issued, expires, capture, CaptureCorrectionKind.Quantity, "loot.items.0.quantity", "2", null),
            14 => new ActivateProfilePreferencesCommand(
                id,
                Revision(CanonicalAggregateKind.ProfilePreferences),
                issued,
                expires,
                Preferences(random.Next(1, 3))),
            15 => new MutateProfilePreferencesCommand(
                id,
                Revision(CanonicalAggregateKind.ProfilePreferences),
                issued,
                expires,
                preferenceContext,
                preferenceSchema,
                RandomPreferenceMutation(random)),
            16 => new ResetProfilePreferencesCommand(
                id,
                Revision(CanonicalAggregateKind.ProfilePreferences),
                issued,
                expires,
                preferenceContext,
                preferenceSchema),
            17 => new DeleteProfilePreferencesCommand(
                id,
                Revision(CanonicalAggregateKind.ProfilePreferences),
                issued,
                expires,
                preferenceContext,
                preferenceSchema),
            _ => new UpsertMarkCommand(
                new CommandId(Guid.Parse("40000000-0000-8000-8000-000000000001")),
                Revision(CanonicalAggregateKind.Marks),
                issued,
                expires,
                Mark(1),
                0,
                Draft()),
        };
    }

    private static ProfilePreferenceMutation RandomPreferenceMutation(Random random)
    {
        var suffix = random.Next(1, 8);
        return random.Next(10) switch
        {
            0 => new SetItemPreferenceMutation(new ItemPreference($"item-{suffix}", true, random.NextDouble() < 0.5, suffix)),
            1 => new RemoveItemPreferenceMutation($"item-{suffix}"),
            2 => new UpsertProtectedItemRuleMutation(new ProtectedItemRule(
                $"rule-{suffix}",
                (ProtectedItemSelectorKind)random.Next(1, 4),
                $"selector-{suffix}",
                (ProtectedItemDisposition)random.Next(1, 3),
                random.Next(0, 5))),
            3 => new DeleteProtectedItemRuleMutation($"rule-{suffix}"),
            4 => new SetRecommendationOverrideMutation(new RecommendationOverride(
                $"item-{suffix}",
                (RecommendationOverrideAction)random.Next(1, 5),
                random.NextDouble() < 0.5 ? null : "fuzz")),
            5 => new DeleteRecommendationOverrideMutation($"item-{suffix}"),
            6 => new UpsertFavoriteLoadoutMutation(new FavoriteLoadout(
                $"loadout-{suffix}",
                $"Loadout {suffix}",
                [new FavoriteLoadoutItem("primary", $"item-{suffix}", 1)])),
            7 => new DeleteFavoriteLoadoutMutation($"loadout-{suffix}"),
            8 => new SetSharedPersonalizationMutation(new SharedPersonalization(
                (SharedPersonalizationKind)random.Next(1, 5),
                $"shared-{suffix}",
                random.NextDouble() < 0.5)),
            _ => new DeleteSharedPersonalizationMutation(
                (SharedPersonalizationKind)random.Next(1, 5),
                $"shared-{suffix}"),
        };
    }
}
