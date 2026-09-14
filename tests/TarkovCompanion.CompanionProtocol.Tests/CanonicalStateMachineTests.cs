using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class CanonicalStateMachineTests
{
    [Fact]
    public void ControlIsDesktopApprovedLeasedAndPreemptible()
    {
        var state = ProtocolTestData.InitialState();
        var request = new RequestControlCommand(
            ProtocolTestData.Command(1),
            new AggregateRevision(1),
            ProtocolTestData.Now,
            ProtocolTestData.Now.AddMinutes(1),
            TimeSpan.FromMinutes(2));
        var pending = DesktopCanonicalStateMachine.Apply(
            state,
            ProtocolTestData.Envelope(request),
            ProtocolTestData.TabletContext());

        Assert.Equal(CommandDisposition.PendingDesktopApproval, pending.Acknowledgement.Disposition);
        Assert.Equal(CompanionInteractionMode.ControlPending, Mode(pending.State));
        Assert.Null(pending.State.DeviceModes.ControlLease);

        var approveAt = ProtocolTestData.Now.AddSeconds(1);
        var approve = new ResolveControlCommand(
            ProtocolTestData.Command(2),
            new AggregateRevision(2),
            approveAt,
            approveAt.AddMinutes(1),
            request.CommandId,
            true,
            new ControlLeaseId(Guid.Parse("41000000-0000-0000-0000-000000000001")));
        var controlled = DesktopCanonicalStateMachine.Apply(
            pending.State,
            ProtocolTestData.Envelope(approve, ProtocolTestData.DesktopSession),
            ProtocolTestData.DesktopContext(approveAt));

        Assert.Equal(CommandDisposition.Applied, controlled.Acknowledgement.Disposition);
        Assert.Equal(CompanionInteractionMode.Control, Mode(controlled.State));
        Assert.NotNull(controlled.State.DeviceModes.ControlLease);

        var navigateAt = approveAt.AddSeconds(1);
        var navigate = new ControlWorkspaceCommand(
            ProtocolTestData.Command(3),
            new AggregateRevision(1),
            navigateAt,
            navigateAt.AddMinutes(1),
            new NavigateWorkspaceAction(WorkspaceKind.Plan, null, null, null));
        var navigated = DesktopCanonicalStateMachine.Apply(
            controlled.State,
            ProtocolTestData.Envelope(navigate),
            ProtocolTestData.TabletContext(navigateAt));

        Assert.Equal(CommandDisposition.Applied, navigated.Acknowledgement.Disposition);
        Assert.Equal(WorkspaceKind.Plan, navigated.State.Workspace.Projection.Workspace);

        var preemptAt = navigateAt.AddSeconds(1);
        var preempt = new PreemptControlCommand(
            ProtocolTestData.Command(4),
            new AggregateRevision(3),
            preemptAt,
            preemptAt.AddMinutes(1),
            "desktop-user-resumed-control");
        var preempted = DesktopCanonicalStateMachine.Apply(
            navigated.State,
            ProtocolTestData.Envelope(preempt, ProtocolTestData.DesktopSession),
            ProtocolTestData.DesktopContext(preemptAt));

        Assert.Equal(CompanionInteractionMode.Follow, Mode(preempted.State));
        Assert.Null(preempted.State.DeviceModes.ControlLease);
    }

    [Fact]
    public void DuplicateConflictStaleAndUnrelatedAggregateChangesAreDistinct()
    {
        var state = ProtocolTestData.InitialState();
        var mode = new SetInteractionModeCommand(
            ProtocolTestData.Command(10),
            new AggregateRevision(1),
            ProtocolTestData.Now,
            ProtocolTestData.Now.AddMinutes(1),
            CompanionInteractionMode.Independent);
        var changedMode = DesktopCanonicalStateMachine.Apply(
            state,
            ProtocolTestData.Envelope(mode),
            ProtocolTestData.TabletContext());

        var create = Upsert(11, 1, 0, ProtocolTestData.Now.AddSeconds(1));
        var applied = DesktopCanonicalStateMachine.Apply(
            changedMode.State,
            ProtocolTestData.Envelope(create),
            ProtocolTestData.TabletContext(ProtocolTestData.Now.AddSeconds(1)));
        var duplicate = DesktopCanonicalStateMachine.Apply(
            applied.State,
            ProtocolTestData.Envelope(create),
            ProtocolTestData.TabletContext(ProtocolTestData.Now.AddSeconds(2)));
        var occupied = Upsert(12, 1, 0, ProtocolTestData.Now.AddSeconds(2));
        var conflict = DesktopCanonicalStateMachine.Apply(
            applied.State,
            ProtocolTestData.Envelope(occupied),
            ProtocolTestData.TabletContext(ProtocolTestData.Now.AddSeconds(2)));
        var edit = Upsert(13, 2, 1, ProtocolTestData.Now.AddSeconds(2));
        var edited = DesktopCanonicalStateMachine.Apply(
            applied.State,
            ProtocolTestData.Envelope(edit),
            ProtocolTestData.TabletContext(ProtocolTestData.Now.AddSeconds(2)));
        var stale = DesktopCanonicalStateMachine.Apply(
            edited.State,
            ProtocolTestData.Envelope(occupied),
            ProtocolTestData.TabletContext(ProtocolTestData.Now.AddSeconds(3)));

        Assert.Equal(CommandDisposition.Applied, applied.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.Duplicate, duplicate.Acknowledgement.Disposition);
        Assert.Equal(applied.State.GlobalRevision, duplicate.State.GlobalRevision);
        Assert.Equal(CommandDisposition.RejectedConflict, conflict.Acknowledgement.Disposition);
        Assert.NotNull(conflict.Acknowledgement.CanonicalState);
        Assert.Equal(CommandDisposition.RejectedStale, stale.Acknowledgement.Disposition);
        Assert.Equal(1, changedMode.State.DeviceModes.Cursor.Revision.Value);
        Assert.Equal(1, applied.State.Marks.Cursor.Revision.Value);
    }

    [Fact]
    public void IndependentViewRequiresExplicitShowAndCurrentOfflinePreview()
    {
        var state = ProtocolTestData.InitialState();
        var independent = new SetInteractionModeCommand(
            ProtocolTestData.Command(20),
            new AggregateRevision(1),
            ProtocolTestData.Now,
            ProtocolTestData.Now.AddMinutes(1),
            CompanionInteractionMode.Independent);
        state = DesktopCanonicalStateMachine.Apply(
            state,
            ProtocolTestData.Envelope(independent),
            ProtocolTestData.TabletContext()).State;

        var previewedAt = ProtocolTestData.Now.AddSeconds(1);
        var preview = new OfflineQueuePreview(
            ProtocolTestData.Now,
            previewedAt,
            state.AuthorityEpoch,
            state.Workspace.Cursor.Revision);
        var show = new ShowOnDesktopCommand(
            ProtocolTestData.Command(21),
            new AggregateRevision(1),
            ProtocolTestData.Now,
            ProtocolTestData.Now.AddMinutes(1),
            ProtocolTestData.Projection("woods"),
            preview);
        var applied = DesktopCanonicalStateMachine.Apply(
            state,
            ProtocolTestData.Envelope(show),
            ProtocolTestData.TabletContext(previewedAt));

        var stalePreview = new ShowOnDesktopCommand(
            ProtocolTestData.Command(22),
            new AggregateRevision(2),
            ProtocolTestData.Now,
            ProtocolTestData.Now.AddMinutes(1),
            ProtocolTestData.Projection("factory"),
            preview);
        var rejected = DesktopCanonicalStateMachine.Apply(
            applied.State,
            ProtocolTestData.Envelope(stalePreview),
            ProtocolTestData.TabletContext(previewedAt));

        Assert.Equal("woods", applied.State.Workspace.Projection.MapId);
        Assert.Equal(CompanionInteractionMode.Independent, Mode(applied.State));
        Assert.Equal(CommandDisposition.RequiresPreview, rejected.Acknowledgement.Disposition);
        Assert.NotNull(rejected.Acknowledgement.CanonicalState);
        Assert.DoesNotContain(
            typeof(CompanionCommand).Assembly.GetTypes(),
            type => type.Name.Contains("ReplayIndependent", StringComparison.Ordinal));
    }

    [Fact]
    public void PingIsServerAuthoredExpiresAtFortyFiveSecondsAndIsPruned()
    {
        var state = ProtocolTestData.InitialState();
        var command = Upsert(30, 1, 0, ProtocolTestData.Now, MapMarkKind.Ping);
        var applied = DesktopCanonicalStateMachine.Apply(
            state,
            ProtocolTestData.Envelope(command),
            ProtocolTestData.TabletContext());
        var ping = Assert.Single(applied.State.Marks.Marks);

        Assert.Equal(ProtocolTestData.TabletDevice, ping.AuthorDeviceId);
        Assert.Equal(ProtocolBounds.PingLifetime, ping.ExpiresUtc - ping.CreatedUtc);

        var maintained = DesktopCanonicalStateMachine.ApplyMaintenance(
            applied.State,
            ProtocolTestData.Now.AddSeconds(46));
        Assert.Empty(maintained.State.Marks.Marks);
        Assert.Single(maintained.Updates);
    }

    [Fact]
    public void TeamMarkRequiresExplicitPublicationCapability()
    {
        var draft = new MapMarkDraft(
            MapMarkKind.Waypoint,
            MapMarkScope.Team,
            new MapCoordinate("customs", "ground", CoordinateSpaceKind.World, "tarkov-dev-1", 1, 2, 3),
            "Team plan",
            "#00AACC",
            null);
        var command = new UpsertMarkCommand(
            ProtocolTestData.Command(31),
            new AggregateRevision(1),
            ProtocolTestData.Now,
            ProtocolTestData.Now.AddMinutes(1),
            ProtocolTestData.Mark(31),
            0,
            draft);

        var rejected = DesktopCanonicalStateMachine.Apply(
            ProtocolTestData.InitialState(),
            ProtocolTestData.Envelope(command),
            ProtocolTestData.TabletContext());

        Assert.Equal(CommandDisposition.RejectedUnauthorized, rejected.Acknowledgement.Disposition);
    }

    [Theory]
    [InlineData(ContextualCapturePurpose.LootDecision, ScanIntent.Loot)]
    [InlineData(ContextualCapturePurpose.FullStash, ScanIntent.Stash)]
    [InlineData(ContextualCapturePurpose.Ammo, ScanIntent.Ammo)]
    [InlineData(ContextualCapturePurpose.Keys, ScanIntent.Keys)]
    [InlineData(ContextualCapturePurpose.QuestAndFutureQuestItems, ScanIntent.QuestItems)]
    [InlineData(ContextualCapturePurpose.MapAndExtracts, ScanIntent.ExtractsAndMap)]
    [InlineData(ContextualCapturePurpose.HealthAndCharacter, ScanIntent.HealthAndCharacter)]
    [InlineData(ContextualCapturePurpose.AutoDetect, ScanIntent.Auto)]
    public void CapturePurposesMapToFrozenCoreIntents(ContextualCapturePurpose purpose, ScanIntent expected)
    {
        var intent = CaptureIntent(purpose);
        Assert.Equal(expected, intent.CoreIntent);
    }

    [Fact]
    public void CaptureIntentCarriesServerDerivedOriginProgressResultReviewAndCorrections()
    {
        var state = ProtocolTestData.InitialState();
        var request = CaptureCommand(40, 1, ContextualCapturePurpose.LootDecision, ProtocolTestData.Now);
        var armed = DesktopCanonicalStateMachine.Apply(
            state,
            ProtocolTestData.Envelope(request),
            ProtocolTestData.TabletContext());
        var intent = armed.State.CaptureIntent.ActiveIntent!;

        Assert.Equal(ProtocolTestData.TabletDevice, intent.InitiatingDeviceId);
        Assert.Equal(CompanionSurfaceKind.TabletLandscape, intent.InitiatingSurface);
        Assert.Equal(ContextualCaptureStatus.Armed, intent.Status);

        var progressAt = ProtocolTestData.Now.AddSeconds(1);
        var progress = new ReportCaptureProgressCommand(
            ProtocolTestData.Command(41),
            new AggregateRevision(2),
            progressAt,
            progressAt.AddSeconds(40),
            request.IntentId,
            ContextualCaptureProgressPhase.Decoding,
            30,
            "artifact-1",
            0,
            "decoding-visible-capture");
        var progressed = DesktopCanonicalStateMachine.Apply(
            armed.State,
            ProtocolTestData.Envelope(progress, ProtocolTestData.DesktopSession),
            ProtocolTestData.DesktopContext(progressAt));

        var resultAt = ProtocolTestData.Now.AddSeconds(2);
        var result = new ContextualCaptureResult(
            "result-1",
            "artifact-1",
            0,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            RecognizedContext.Loot,
            resultAt,
            ProtocolTestData.ScreenshotProvenance(resultAt));
        var publish = new PublishCaptureResultCommand(
            ProtocolTestData.Command(42),
            new AggregateRevision(3),
            resultAt,
            resultAt.AddSeconds(40),
            request.IntentId,
            result,
            [new ContextualCaptureGuidance(CaptureGuidanceKind.ConfirmResult, "review", "Review the recognized items.", 0)]);
        var published = DesktopCanonicalStateMachine.Apply(
            progressed.State,
            ProtocolTestData.Envelope(publish, ProtocolTestData.DesktopSession),
            ProtocolTestData.DesktopContext(resultAt));

        var reviewAt = ProtocolTestData.Now.AddSeconds(3);
        var needsCorrection = new ReviewCaptureResultCommand(
            ProtocolTestData.Command(43),
            new AggregateRevision(4),
            reviewAt,
            reviewAt.AddSeconds(30),
            request.IntentId,
            CaptureReviewDisposition.NeedsCorrection,
            "One item name is wrong.");
        var reviewed = DesktopCanonicalStateMachine.Apply(
            published.State,
            ProtocolTestData.Envelope(needsCorrection),
            ProtocolTestData.TabletContext(reviewAt));

        var correctionAt = ProtocolTestData.Now.AddSeconds(4);
        var correction = new CorrectCaptureResultCommand(
            ProtocolTestData.Command(44),
            new AggregateRevision(5),
            correctionAt,
            correctionAt.AddSeconds(30),
            request.IntentId,
            CaptureCorrectionKind.ItemIdentity,
            "loot.items.0.canonicalId",
            "item-corrected",
            "manual-review");
        var corrected = DesktopCanonicalStateMachine.Apply(
            reviewed.State,
            ProtocolTestData.Envelope(correction),
            ProtocolTestData.TabletContext(correctionAt));

        var acceptAt = ProtocolTestData.Now.AddSeconds(5);
        var accept = new ReviewCaptureResultCommand(
            ProtocolTestData.Command(45),
            new AggregateRevision(6),
            acceptAt,
            acceptAt.AddSeconds(30),
            request.IntentId,
            CaptureReviewDisposition.Accepted,
            null);
        var completed = DesktopCanonicalStateMachine.Apply(
            corrected.State,
            ProtocolTestData.Envelope(accept),
            ProtocolTestData.TabletContext(acceptAt));
        var final = completed.State.CaptureIntent.ActiveIntent!;

        Assert.Equal(ContextualCaptureStatus.Complete, final.Status);
        Assert.NotNull(final.Result);
        Assert.Single(final.Guidance);
        Assert.Single(final.Corrections);
        Assert.Equal(CaptureReviewDisposition.Accepted, final.Review!.Disposition);
    }

    [Fact]
    public void ExpiredCommandCannotApplyToALaterState()
    {
        var command = Upsert(50, 1, 0, ProtocolTestData.Now);
        var received = ProtocolTestData.Now.AddMinutes(1);
        var result = DesktopCanonicalStateMachine.Apply(
            ProtocolTestData.InitialState(),
            ProtocolTestData.Envelope(command),
            ProtocolTestData.TabletContext(received));

        Assert.Equal(CommandDisposition.RejectedExpired, result.Acknowledgement.Disposition);
        Assert.Empty(result.State.Marks.Marks);
    }

    private static CompanionInteractionMode Mode(CanonicalCompanionState state) =>
        Assert.Single(state.DeviceModes.Devices, item => item.DeviceId == ProtocolTestData.TabletDevice).Mode;

    private static UpsertMarkCommand Upsert(
        int command,
        long aggregateRevision,
        long markRevision,
        DateTimeOffset now,
        MapMarkKind kind = MapMarkKind.Waypoint) => new(
        ProtocolTestData.Command(command),
        new AggregateRevision(aggregateRevision),
        now,
        now.AddSeconds(30),
        ProtocolTestData.Mark(1),
        markRevision,
        new MapMarkDraft(
            kind,
            MapMarkScope.PairedDevice,
            new MapCoordinate("customs", "ground", CoordinateSpaceKind.World, "tarkov-dev-1", command, 2, 3),
            "Mark",
            "#00AACC",
            null));

    private static RequestCaptureIntentCommand CaptureCommand(
        int command,
        long revision,
        ContextualCapturePurpose purpose,
        DateTimeOffset now) => new(
        ProtocolTestData.Command(command),
        new AggregateRevision(revision),
        now,
        now.AddMinutes(1),
        ProtocolTestData.Capture(1),
        "capture-correlation",
        ProtocolTestData.CaptureSession(1),
        purpose,
        new CompanionCaptureContext("customs", "ground", "profile", null, [], [], []));

    private static ContextualCaptureIntent CaptureIntent(ContextualCapturePurpose purpose) => new(
        ProtocolTestData.Capture(99),
        "mapping",
        ProtocolTestData.CaptureSession(99),
        purpose,
        ProtocolTestData.TabletDevice,
        CompanionSurfaceKind.TabletLandscape,
        ProtocolTestData.Now,
        ProtocolTestData.Now.AddMinutes(1),
        ContextualCaptureStatus.Armed,
        new CompanionCaptureContext(null, null, null, null, [], [], []),
        [],
        null,
        [],
        null,
        []);
}
