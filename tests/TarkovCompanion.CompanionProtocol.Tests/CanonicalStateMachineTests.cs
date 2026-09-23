using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class CanonicalStateMachineTests
{
    private static readonly ControlLeaseId Lease = new(Guid.Parse("41000000-0000-0000-0000-000000000001"));

    [Fact]
    public void ControlIsDesktopApprovedLeasedAndPreemptible()
    {
        var request = new RequestControlCommand(Command(1), new AggregateRevision(1), Now, Now.AddMinutes(1), TimeSpan.FromMinutes(2));
        var pending = Apply(InitialState(), request, TabletContext());

        Assert.Equal(CommandDisposition.Applied, pending.Acknowledgement.Disposition);
        Assert.Equal("desktop-approval-required", pending.Acknowledgement.Code);
        Assert.Equal(CompanionInteractionMode.ControlPending, pending.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Null(pending.State.DeviceModes.ControlLease);

        var approveAt = Now.AddSeconds(1);
        var approve = new ResolveControlCommand(Command(2), new AggregateRevision(2), approveAt, approveAt.AddMinutes(1), request.CommandId, true, Lease);
        var controlled = Apply(pending.State, approve, DesktopContext(approveAt));
        var lease = controlled.State.DeviceModes.ControlLease;

        Assert.Equal(CommandDisposition.Applied, controlled.Acknowledgement.Disposition);
        Assert.Equal(CompanionInteractionMode.Control, controlled.State.DeviceModes.ModeOf(TabletDevice));
        Assert.NotNull(lease);
        Assert.Equal(TabletSession, lease.SessionId);
        Assert.Equal(approveAt.AddMinutes(2), lease.ExpiresUtc);

        var navigateAt = approveAt.AddSeconds(1);
        var navigate = new ControlWorkspaceCommand(
            Command(3),
            new AggregateRevision(1),
            navigateAt,
            navigateAt.AddMinutes(1),
            new NavigateWorkspaceAction(WorkspaceKind.Plan, null, null, null));
        var navigated = Apply(controlled.State, navigate, TabletContext(navigateAt));

        Assert.Equal(CommandDisposition.Applied, navigated.Acknowledgement.Disposition);
        Assert.Equal(WorkspaceKind.Plan, navigated.State.Workspace.Projection.Workspace);

        var preemptAt = navigateAt.AddSeconds(1);
        var preempt = new PreemptControlCommand(Command(4), new AggregateRevision(3), preemptAt, preemptAt.AddMinutes(1), "desktop-user-resumed-control");
        var preempted = Apply(navigated.State, preempt, DesktopContext(preemptAt));

        Assert.Equal(CompanionInteractionMode.Follow, preempted.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Null(preempted.State.DeviceModes.ControlLease);

        var afterPreempt = new ControlWorkspaceCommand(
            Command(5),
            new AggregateRevision(2),
            preemptAt,
            preemptAt.AddMinutes(1),
            new SearchWorkspaceAction("marked room"));
        Assert.Equal(
            CommandDisposition.RejectedUnauthorized,
            Apply(preempted.State, afterPreempt, TabletContext(preemptAt)).Acknowledgement.Disposition);
    }

    [Fact]
    public void ActiveControlRenewsTheLeaseButAnIdleTabletStillLapses()
    {
        var request = new RequestControlCommand(Command(1), new AggregateRevision(1), Now, Now.AddMinutes(1), TimeSpan.FromMinutes(2));
        var pending = Apply(InitialState(), request, TabletContext());
        var approveAt = Now.AddSeconds(1);
        var approve = new ResolveControlCommand(Command(2), new AggregateRevision(2), approveAt, approveAt.AddMinutes(1), request.CommandId, true, Lease);
        var controlled = Apply(pending.State, approve, DesktopContext(approveAt));
        Assert.Equal(approveAt.AddMinutes(2), controlled.State.DeviceModes.ControlLease!.ExpiresUtc);

        // An action arrives near the end of the original two-minute window.
        var actAt = approveAt.AddSeconds(90);
        var navigate = new ControlWorkspaceCommand(
            Command(3),
            new AggregateRevision(1),
            actAt,
            actAt.AddMinutes(1),
            new NavigateWorkspaceAction(WorkspaceKind.Plan, null, null, null));
        var acted = Apply(controlled.State, navigate, TabletContext(actAt));

        Assert.Equal(CommandDisposition.Applied, acted.Acknowledgement.Disposition);
        var renewedLease = acted.State.DeviceModes.ControlLease;
        Assert.NotNull(renewedLease);
        Assert.Equal(Lease, renewedLease!.LeaseId);
        Assert.Equal(actAt.AddMinutes(2), renewedLease.ExpiresUtc);
        // Only the workspace change is delivered; the renewal rides along silently.
        Assert.IsType<WorkspaceCanonicalUpdate>(acted.Update);

        // Past the ORIGINAL expiry (approveAt+120s) but before the renewed one: still in Control.
        var stillWithinRenewal = DesktopCanonicalStateMachine.ApplyMaintenance(
            acted.State,
            approveAt.AddSeconds(125),
            [PairedTablet()],
            [ActiveSession(PairedTablet(), TabletSession)]);
        Assert.Equal(CompanionInteractionMode.Control, stillWithinRenewal.State.DeviceModes.ModeOf(TabletDevice));
        Assert.NotNull(stillWithinRenewal.State.DeviceModes.ControlLease);

        // No further action: idle past the renewed expiry (actAt+120s) still lapses to Follow.
        var afterIdle = DesktopCanonicalStateMachine.ApplyMaintenance(
            acted.State,
            actAt.AddMinutes(2).AddSeconds(1),
            [PairedTablet()],
            [ActiveSession(PairedTablet(), TabletSession)]);
        Assert.Equal(CompanionInteractionMode.Follow, afterIdle.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Null(afterIdle.State.DeviceModes.ControlLease);
    }

    [Fact]
    public void TakeBackEndsARenewedLeaseAtOnce()
    {
        var request = new RequestControlCommand(Command(1), new AggregateRevision(1), Now, Now.AddMinutes(1), TimeSpan.FromMinutes(2));
        var pending = Apply(InitialState(), request, TabletContext());
        var approveAt = Now.AddSeconds(1);
        var approve = new ResolveControlCommand(Command(2), new AggregateRevision(2), approveAt, approveAt.AddMinutes(1), request.CommandId, true, Lease);
        var controlled = Apply(pending.State, approve, DesktopContext(approveAt));

        var actAt = approveAt.AddSeconds(90);
        var navigate = new ControlWorkspaceCommand(
            Command(3),
            new AggregateRevision(1),
            actAt,
            actAt.AddMinutes(1),
            new NavigateWorkspaceAction(WorkspaceKind.Plan, null, null, null));
        var acted = Apply(controlled.State, navigate, TabletContext(actAt));

        var preemptAt = actAt.AddSeconds(1);
        var preempt = new PreemptControlCommand(Command(4), new AggregateRevision(3), preemptAt, preemptAt.AddMinutes(1), "desktop-user-resumed-control");
        var preempted = Apply(acted.State, preempt, DesktopContext(preemptAt));

        Assert.Equal(CompanionInteractionMode.Follow, preempted.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Null(preempted.State.DeviceModes.ControlLease);
    }

    [Fact]
    public void DeniedRequestLeavesTheExistingLeaseHolderInControl()
    {
        var otherRequest = new RequestControlCommand(Command(1), new AggregateRevision(1), Now, Now.AddMinutes(1), TimeSpan.FromMinutes(2));
        var state = Apply(InitialState(), otherRequest, OtherContext()).State;
        state = Apply(
            state,
            new ResolveControlCommand(Command(2), new AggregateRevision(2), Now, Now.AddMinutes(1), otherRequest.CommandId, true, Lease),
            DesktopContext()).State;

        var tabletRequest = new RequestControlCommand(Command(3), new AggregateRevision(3), Now, Now.AddMinutes(1), TimeSpan.FromMinutes(1));
        state = Apply(state, tabletRequest, TabletContext()).State;
        var denied = Apply(
            state,
            new ResolveControlCommand(Command(4), new AggregateRevision(4), Now, Now.AddMinutes(1), tabletRequest.CommandId, false, null),
            DesktopContext());

        Assert.Equal("control-denied", denied.Acknowledgement.Code);
        Assert.Equal(CompanionInteractionMode.Follow, denied.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Equal(CompanionInteractionMode.Control, denied.State.DeviceModes.ModeOf(OtherDevice));
        Assert.Equal(OtherDevice, denied.State.DeviceModes.ControlLease!.DeviceId);
        Assert.Null(denied.State.DeviceModes.PendingControl);
    }

    [Fact]
    public void AControlRequesterCannotApproveItsOwnRequest()
    {
        var capabilities = TabletCapabilities.Append(DeviceCapability.ResolveControlRequests).ToArray();
        var request = new RequestControlCommand(Command(1), new AggregateRevision(1), Now, Now.AddMinutes(1), TimeSpan.FromMinutes(2));
        var state = Apply(InitialState(), request, TabletContext(capabilities: capabilities)).State;

        var selfApproval = Apply(
            state,
            new ResolveControlCommand(Command(2), new AggregateRevision(2), Now, Now.AddMinutes(1), request.CommandId, true, Lease),
            TabletContext(capabilities: capabilities));

        Assert.Equal(CommandDisposition.RejectedUnauthorized, selfApproval.Acknowledgement.Disposition);
        Assert.Equal("control-self-approval-denied", selfApproval.Acknowledgement.Code);
        Assert.Same(state, selfApproval.State);
    }

    [Fact]
    public void DuplicateConflictStaleGapAndUnrelatedAggregateChangesAreDistinct()
    {
        var changedMode = Apply(InitialState(), SetMode(10, 1, Now, CompanionInteractionMode.Independent), TabletContext());
        var create = Upsert(11, 1, 0, Now.AddSeconds(1));
        var applied = Apply(changedMode.State, create, TabletContext(Now.AddSeconds(1)));
        var duplicate = Apply(applied.State, create, TabletContext(Now.AddSeconds(2)));
        var occupied = Upsert(12, 1, 0, Now.AddSeconds(2), mark: 2);
        var conflict = Apply(applied.State, occupied, TabletContext(Now.AddSeconds(2)));
        var edit = Upsert(13, 2, 1, Now.AddSeconds(2));
        var edited = Apply(applied.State, edit, TabletContext(Now.AddSeconds(2)));
        var stale = Apply(edited.State, occupied, TabletContext(Now.AddSeconds(3)));
        var gap = Apply(edited.State, Upsert(14, 5, 0, Now.AddSeconds(3), mark: 3), TabletContext(Now.AddSeconds(3)));

        Assert.Equal(CommandDisposition.Applied, applied.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.Applied, duplicate.Acknowledgement.Disposition);
        Assert.Equal("duplicate-command", duplicate.Acknowledgement.Code);
        Assert.Equal(create.CommandId, duplicate.Acknowledgement.AppliedChangeId);
        Assert.Equal(duplicate.Acknowledgement.AppliedRevision, duplicate.Acknowledgement.RequestedRevision);
        Assert.Same(applied.State, duplicate.State);
        Assert.Null(duplicate.Update);

        Assert.Equal(CommandDisposition.RejectedConflict, conflict.Acknowledgement.Disposition);
        Assert.Equal(create.CommandId, conflict.Acknowledgement.AppliedChangeId);
        Assert.NotNull(conflict.Acknowledgement.CanonicalState);

        Assert.Equal(CommandDisposition.RejectedStale, stale.Acknowledgement.Disposition);
        Assert.Equal(edit.CommandId, stale.Acknowledgement.AppliedChangeId);
        Assert.Equal(2, stale.Acknowledgement.AppliedRevision.Value);

        Assert.Equal(CommandDisposition.RequiresSnapshot, gap.Acknowledgement.Disposition);
        Assert.Equal("revision-gap", gap.Acknowledgement.Code);
        Assert.NotNull(gap.Acknowledgement.CanonicalState);

        Assert.Equal(1, changedMode.State.DeviceModes.Cursor.Revision.Value);
        Assert.Equal(1, applied.State.Marks.Cursor.Revision.Value);
        Assert.Equal(1, applied.State.DeviceModes.Cursor.Revision.Value);
    }

    [Fact]
    public void IndependentViewRequiresExplicitShowAndCurrentOfflinePreview()
    {
        var state = Apply(InitialState(), SetMode(20, 1, Now, CompanionInteractionMode.Independent), TabletContext()).State;
        var previewedAt = Now.AddSeconds(1);
        var preview = new OfflineQueuePreview(Now, previewedAt, state.AuthorityEpoch, state.Workspace.Cursor.Revision);
        var show = new ShowOnDesktopCommand(Command(21), new AggregateRevision(1), Now, Now.AddMinutes(10), Projection("woods"), preview);
        var applied = Apply(state, show, TabletContext(previewedAt));
        var stalePreview = new ShowOnDesktopCommand(Command(22), new AggregateRevision(2), Now, Now.AddMinutes(10), Projection("factory"), preview);
        var rejected = Apply(applied.State, stalePreview, TabletContext(previewedAt));
        var following = Apply(
            InitialState(),
            new ShowOnDesktopCommand(Command(23), new AggregateRevision(1), Now, Now.AddMinutes(1), Projection("factory")),
            TabletContext());

        Assert.Equal("woods", applied.State.Workspace.Projection.MapId);
        Assert.Equal(CompanionInteractionMode.Independent, applied.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Equal(CommandDisposition.RequiresPreview, rejected.Acknowledgement.Disposition);
        Assert.NotNull(rejected.Acknowledgement.CanonicalState);
        Assert.Equal("show-requires-independent-mode", following.Acknowledgement.Code);
        Assert.DoesNotContain(
            typeof(CompanionCommand).Assembly.GetTypes(),
            type => type.Name.Contains("ReplayIndependent", StringComparison.Ordinal));
    }

    [Fact]
    public void ShowOnDesktopCannotOpenOrDismissASensitiveDialogWithoutTheAdministrativeCapability()
    {
        var state = Apply(InitialState(), SetMode(24, 1, Now, CompanionInteractionMode.Independent), TabletContext()).State;
        var opening = new ShowOnDesktopCommand(
            Command(25),
            new AggregateRevision(1),
            Now,
            Now.AddMinutes(1),
            Projection("customs", WorkspaceDialogKind.PairingApproval));

        var denied = Apply(state, opening, TabletContext());
        var administrator = Apply(
            state,
            opening,
            TabletContext(capabilities: TabletCapabilities.Append(DeviceCapability.ManageDevices).ToArray()));

        Assert.Equal(CommandDisposition.RejectedUnauthorized, denied.Acknowledgement.Disposition);
        Assert.Equal("sensitive-dialog-capability-required", denied.Acknowledgement.Code);
        Assert.Equal(CommandDisposition.Applied, administrator.Acknowledgement.Disposition);
        Assert.Equal(WorkspaceDialogKind.PairingApproval, administrator.State.Workspace.Projection.Dialog);
    }

    [Fact]
    public void PingIsServerAuthoredExpiresAtFortyFiveSecondsAndIsPruned()
    {
        var applied = Apply(InitialState(), Upsert(30, 1, 0, Now, kind: MapMarkKind.Ping), TabletContext());
        var ping = Assert.Single(applied.State.Marks.Marks);
        var device = PairedTablet();

        Assert.Equal(TabletDevice, ping.AuthorDeviceId);
        Assert.Equal(ProtocolBounds.PingLifetime, ping.ExpiresUtc!.Value - ping.CreatedUtc);

        var maintained = DesktopCanonicalStateMachine.ApplyMaintenance(
            applied.State,
            Now.AddSeconds(46),
            [device],
            [ActiveSession(device, TabletSession)]);

        Assert.Empty(maintained.State.Marks.Marks);
        var update = Assert.Single(maintained.Updates);
        Assert.Equal(8, ProtocolGuardVersion(update.ChangeId));
    }

    [Fact]
    public void TeamMarkPublicationEditAndDeletionRequireTheExplicitCapability()
    {
        var publisher = TabletContext(capabilities: TabletCapabilities.Append(DeviceCapability.PublishTeamMarks).ToArray());
        var denied = Apply(InitialState(), Upsert(31, 1, 0, Now, scope: MapMarkScope.Team), TabletContext());
        var published = Apply(InitialState(), Upsert(32, 1, 0, Now, scope: MapMarkScope.Team), publisher);
        var demoted = Apply(published.State, Upsert(33, 2, 1, Now, scope: MapMarkScope.PairedDevice), TabletContext());
        var deleted = Apply(
            published.State,
            new DeleteMarkCommand(Command(34), new AggregateRevision(2), Now, Now.AddMinutes(1), Mark(1), 1),
            TabletContext());

        Assert.Equal(CommandDisposition.RejectedUnauthorized, denied.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.Applied, published.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.RejectedUnauthorized, demoted.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.RejectedUnauthorized, deleted.Acknowledgement.Disposition);
    }

    [Fact]
    public void AMarkRevisionMismatchReturnsTheCanonicalSnapshot()
    {
        var created = Apply(InitialState(), Upsert(35, 1, 0, Now), TabletContext());
        var mismatch = Apply(created.State, Upsert(36, 2, 5, Now), TabletContext());
        var missing = Apply(
            created.State,
            new DeleteMarkCommand(Command(37), new AggregateRevision(2), Now, Now.AddMinutes(1), Mark(9), 1),
            TabletContext());

        Assert.Equal(CommandDisposition.RequiresSnapshot, mismatch.Acknowledgement.Disposition);
        Assert.Equal("mark-revision-mismatch", mismatch.Acknowledgement.Code);
        Assert.Same(created.State, mismatch.Acknowledgement.CanonicalState);
        Assert.Equal("mark-not-found", missing.Acknowledgement.Code);
    }

    [Fact]
    public void CaptureIntentsReuseTheCompleteCoreCaptureIntentSet()
    {
        Assert.Equal(
            Enum.GetValues<ScanIntent>(),
            PairedScanIntents.Allowed.ToArray());
        var flea = new RequestCaptureIntentCommand(
            Command(98),
            new AggregateRevision(1),
            Now,
            Now.AddMinutes(1),
            Capture(98),
            "flea",
            CaptureSession(98),
            ScanIntent.Flea,
            new CompanionCaptureContext(null, null, null, null, [], [], []));
        Assert.Equal(ScanIntent.Flea, flea.Intent);

        var armed = Apply(InitialState(), CaptureCommand(40, 1, Now), TabletContext());
        var intent = armed.State.CaptureIntent.ActiveIntent!;
        var projected = Assert.IsType<CaptureCanonicalUpdate>(armed.Update).ToRevisionedState();

        Assert.Equal(new CaptureIntentState(ScanIntent.Loot, Now, Now.AddMinutes(1)), intent.State);
        Assert.NotNull(projected);
        Assert.Same(intent.State, projected.Value);
        Assert.Equal(new StateStreamId($"paired/{Epoch.Value:D}/CaptureIntent"), projected.StreamId);
        Assert.Equal(1, projected.Revision.Value);
        Assert.Equal(new StateChangeId(Command(40).Value), projected.ChangeId);
        Assert.Equal(V2ContractVersion.Current, projected.ContractVersion);
        Assert.Equal(new WorkspaceOrigin(Workspace, TabletDevice, WorkspaceOriginKind.PairedDevice, TabletInstance), projected.Origin);
    }

    [Fact]
    public void EveryCommittedChangeCarriesV2WorkspaceDeviceInstanceOriginAndContractVersion()
    {
        var mode = Apply(InitialState(), SetMode(70, 1, Now, CompanionInteractionMode.Independent), TabletContext());
        var desktop = DesktopCanonicalStateMachine.Apply(
            mode.State,
            Envelope(new UpdateDesktopWorkspaceCommand(Command(71), new AggregateRevision(1), Now, Now.AddMinutes(1), Projection("woods")), DesktopSession),
            DesktopContext());
        var first = Apply(desktop.State, Upsert(72, 1, 0, Now), TabletContext());
        var second = Apply(first.State, Upsert(73, 2, 0, Now, mark: 2, kind: MapMarkKind.Ping), TabletContext());
        var device = PairedTablet();
        var maintained = DesktopCanonicalStateMachine.ApplyMaintenance(second.State, Now.AddSeconds(46), [device], [ActiveSession(device, TabletSession)]);

        Assert.Equal(new WorkspaceOrigin(Workspace, TabletDevice, WorkspaceOriginKind.PairedDevice, TabletInstance), mode.Update!.Origin);
        Assert.Equal(new WorkspaceOrigin(Workspace, DesktopDevice, WorkspaceOriginKind.DesktopApplication, DesktopInstance), desktop.Update!.Origin);
        Assert.All(
            new CanonicalUpdate[] { mode.Update!, desktop.Update!, first.Update!, second.Update! }.Concat(maintained.Updates),
            update => Assert.Equal(V2ContractVersion.Current, update.ContractVersion));

        // A marks change projects only the marks it wrote, each on its own epoch-scoped Core stream at
        // the marks aggregate revision, which keeps increasing even if a mark id is deleted and re-created.
        var projected = Assert.Single(Assert.IsType<MarksCanonicalUpdate>(second.Update).ToRevisionedStates());
        Assert.Equal(new StateStreamId($"paired/{Epoch.Value:D}/Marks/{Mark(2).Value:D}"), projected.StreamId);
        Assert.Equal(2, projected.Revision.Value);
        Assert.Equal(second.State.Marks.Marks.Single(mark => mark.MarkId == Mark(2)).State, projected.Value);
        Assert.Equal(Command(73), second.State.Marks.Marks.Single(mark => mark.MarkId == Mark(2)).LastChangeId);
        Assert.Equal(Command(72), second.State.Marks.Marks.Single(mark => mark.MarkId == Mark(1)).LastChangeId);

        var expiry = Assert.IsType<MarksCanonicalUpdate>(Assert.Single(maintained.Updates));
        Assert.Equal(new WorkspaceOrigin(Workspace, DesktopDevice, WorkspaceOriginKind.DesktopApplication, DesktopInstance), expiry.Origin);
        Assert.Empty(expiry.ToRevisionedStates());

        var recreated = Apply(
            maintained.State,
            Upsert(75, maintained.State.Marks.Cursor.Revision.Value + 1, 0, Now.AddSeconds(47), mark: 2),
            TabletContext(Now.AddSeconds(47)));
        var recreatedProjection = Assert.Single(Assert.IsType<MarksCanonicalUpdate>(recreated.Update).ToRevisionedStates());
        Assert.Equal(projected.StreamId, recreatedProjection.StreamId);
        Assert.True(recreatedProjection.Revision.Value > projected.Revision.Value);
        Assert.Equal(1, recreated.State.Marks.Marks.Single(mark => mark.MarkId == Mark(2)).Revision);
    }

    [Fact]
    public void MarkLabelsUseTheCoreEightyCharacterCap()
    {
        Assert.Equal(80, MapMarkState.MaxLabelLength);
        Assert.Throws<ArgumentOutOfRangeException>(() => Draft(label: new string('x', MapMarkState.MaxLabelLength + 1)));
        Assert.Equal(CommandDisposition.Applied, Apply(InitialState(), UpsertWith(74, Draft(label: new string('x', MapMarkState.MaxLabelLength))), TabletContext()).Acknowledgement.Disposition);
    }

    [Fact]
    public void CaptureIntentCarriesServerDerivedOriginProgressResultReviewAndCorrections()
    {
        var published = PublishedCapture(ScreenshotProvenance(Now.AddSeconds(2)));
        var intent = published.State.CaptureIntent.ActiveIntent!;

        Assert.Equal(TabletDevice, intent.InitiatingDeviceId);
        Assert.Equal(CompanionSurfaceKind.TabletLandscape, intent.InitiatingSurface);
        Assert.Equal(ContextualCaptureStatus.AwaitingReview, intent.Status);

        var reviewAt = Now.AddSeconds(3);
        var reviewed = Apply(
            published.State,
            new ReviewCaptureResultCommand(Command(43), new AggregateRevision(4), reviewAt, reviewAt.AddSeconds(30), Capture(1), CaptureReviewDisposition.NeedsCorrection, "One item name is wrong."),
            TabletContext(reviewAt));
        var correctionAt = Now.AddSeconds(4);
        var corrected = Apply(
            reviewed.State,
            new CorrectCaptureResultCommand(Command(44), new AggregateRevision(5), correctionAt, correctionAt.AddSeconds(30), Capture(1), CaptureCorrectionKind.ItemIdentity, "loot.items.0.canonicalId", "item-corrected", "manual-review"),
            TabletContext(correctionAt));
        var acceptAt = Now.AddSeconds(5);
        var completed = Apply(
            corrected.State,
            new ReviewCaptureResultCommand(Command(45), new AggregateRevision(6), acceptAt, acceptAt.AddSeconds(30), Capture(1), CaptureReviewDisposition.Accepted, null),
            TabletContext(acceptAt));
        var final = completed.State.CaptureIntent.ActiveIntent!;

        Assert.Equal(ContextualCaptureStatus.Complete, final.Status);
        Assert.NotNull(final.Result);
        Assert.Single(final.Guidance);
        Assert.Single(final.Corrections);
        Assert.Equal(CaptureReviewDisposition.Accepted, final.Review!.Disposition);
    }

    [Fact]
    public void PairedDevicesCannotReportCaptureEvidenceEvenWhenGrantedEveryCapability()
    {
        var armed = Apply(InitialState(), CaptureCommand(40, 1, Now), TabletContext());
        var progressAt = Now.AddSeconds(1);
        var progressCommand = new ReportCaptureProgressCommand(
            Command(41),
            new AggregateRevision(2),
            progressAt,
            progressAt.AddSeconds(30),
            Capture(1),
            ContextualCaptureProgressPhase.Decoding,
            30,
            "artifact-1",
            0,
            "decoding-visible-capture");
        var pairedProgress = Apply(
            armed.State,
            progressCommand,
            TabletContext(progressAt, Enum.GetValues<DeviceCapability>()));
        var desktopProgress = DesktopCanonicalStateMachine.Apply(
            armed.State,
            Envelope(progressCommand, DesktopSession),
            DesktopContext(progressAt));
        var resultAt = progressAt.AddSeconds(1);
        var resultCommand = new PublishCaptureResultCommand(
            Command(42),
            new AggregateRevision(3),
            resultAt,
            resultAt.AddSeconds(30),
            Capture(1),
            new ContextualCaptureResult(
                "result-1",
                "artifact-1",
                0,
                new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
                RecognizedContext.Loot,
                resultAt,
                ScreenshotProvenance(resultAt)),
            []);
        var pairedResult = Apply(
            desktopProgress.State,
            resultCommand,
            TabletContext(resultAt, Enum.GetValues<DeviceCapability>()));

        Assert.All(new[] { pairedProgress, pairedResult }, reduction =>
        {
            Assert.Equal(CommandDisposition.RejectedUnauthorized, reduction.Acknowledgement.Disposition);
            Assert.Equal("capability-denied", reduction.Acknowledgement.Code);
            Assert.Null(reduction.Update);
        });
        Assert.Equal(CommandDisposition.Applied, desktopProgress.Acknowledgement.Disposition);
    }

    [Fact]
    public void CaptureResultsCannotPrecedeTheirRequestOrCorrelatedProgress()
    {
        var armed = Apply(InitialState(), CaptureCommand(40, 1, Now), TabletContext());
        var progressAt = Now.AddSeconds(2);
        var progressed = DesktopCanonicalStateMachine.Apply(
            armed.State,
            Envelope(
                new ReportCaptureProgressCommand(
                    Command(41),
                    new AggregateRevision(2),
                    progressAt,
                    progressAt.AddSeconds(30),
                    Capture(1),
                    ContextualCaptureProgressPhase.Decoding,
                    30,
                    "artifact-1",
                    0,
                    null),
                DesktopSession),
            DesktopContext(progressAt));

        CommandReduction Publish(DateTimeOffset completedUtc, int command) =>
            DesktopCanonicalStateMachine.Apply(
                progressed.State,
                Envelope(
                    new PublishCaptureResultCommand(
                        Command(command),
                        new AggregateRevision(3),
                        progressAt.AddSeconds(1),
                        progressAt.AddSeconds(30),
                        Capture(1),
                        new ContextualCaptureResult(
                            $"result-{command}",
                            "artifact-1",
                            0,
                            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
                            RecognizedContext.Loot,
                            completedUtc,
                            ScreenshotProvenance(completedUtc)),
                        []),
                    DesktopSession),
                DesktopContext(progressAt.AddSeconds(1)));

        var beforeRequest = Publish(Now.AddMilliseconds(-1), 42);
        var beforeProgress = Publish(Now.AddSeconds(1), 43);

        Assert.All(new[] { beforeRequest, beforeProgress }, reduction =>
        {
            Assert.Equal(CommandDisposition.RejectedInvalidState, reduction.Acknowledgement.Disposition);
            Assert.Equal("invalid-capture-result", reduction.Acknowledgement.Code);
            Assert.Null(reduction.Update);
            Assert.Same(progressed.State, reduction.State);
        });
    }

    [Fact]
    public void AFollowUpCaptureKeepsThePublishedResultButAReviewedResultCannotBeReplaced()
    {
        var published = PublishedCapture(ScreenshotProvenance(Now.AddSeconds(2)));
        var followUpAt = Now.AddSeconds(3);
        var followUp = DesktopCanonicalStateMachine.Apply(
            published.State,
            Envelope(
                new ReportCaptureProgressCommand(Command(47), new AggregateRevision(4), followUpAt, followUpAt.AddSeconds(30), Capture(1), ContextualCaptureProgressPhase.Settling, 10, "artifact-2", 1, "scroll-for-overlap"),
                DesktopSession),
            DesktopContext(followUpAt));
        var reviewed = Apply(
            followUp.State,
            new ReviewCaptureResultCommand(Command(48), new AggregateRevision(5), followUpAt, followUpAt.AddSeconds(30), Capture(1), CaptureReviewDisposition.NeedsCorrection, null),
            TabletContext(followUpAt));
        var republished = DesktopCanonicalStateMachine.Apply(
            reviewed.State,
            Envelope(
                new PublishCaptureResultCommand(
                    Command(49),
                    new AggregateRevision(6),
                    followUpAt,
                    followUpAt.AddSeconds(30),
                    Capture(1),
                    new ContextualCaptureResult("result-2", "artifact-2", 1, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current), RecognizedContext.Loot, followUpAt, ScreenshotProvenance(followUpAt)),
                    []),
                DesktopSession),
            DesktopContext(followUpAt));

        Assert.Equal(CommandDisposition.Applied, followUp.Acknowledgement.Disposition);
        Assert.Equal(ContextualCaptureStatus.AwaitingReview, followUp.State.CaptureIntent.ActiveIntent!.Status);
        Assert.Equal("result-1", followUp.State.CaptureIntent.ActiveIntent!.Result!.ResultId);
        Assert.Equal(CommandDisposition.Applied, reviewed.Acknowledgement.Disposition);
        Assert.Equal("capture-result-already-reviewed", republished.Acknowledgement.Code);
    }

    [Fact]
    public void EditingAPingNeverExtendsItsLifetimeBeyondFortyFiveSecondsFromCreation()
    {
        var created = Apply(InitialState(), Upsert(55, 1, 0, Now, kind: MapMarkKind.Ping), TabletContext());
        var edited = Apply(created.State, Upsert(56, 2, 1, Now.AddSeconds(30), kind: MapMarkKind.Ping, x: 7), TabletContext(Now.AddSeconds(30)));
        var late = Apply(created.State, Upsert(57, 2, 1, Now.AddSeconds(50), kind: MapMarkKind.Ping, x: 7), TabletContext(Now.AddSeconds(50)));
        var ping = Assert.Single(edited.State.Marks.Marks);

        Assert.Equal(CommandDisposition.Applied, edited.Acknowledgement.Disposition);
        Assert.Equal(Now.Add(ProtocolBounds.PingLifetime), ping.ExpiresUtc);
        Assert.Equal(2, ping.Revision);
        Assert.Equal(CommandDisposition.RejectedInvalidState, late.Acknowledgement.Disposition);
    }

    [Fact]
    public void ASecondCaptureRequestCannotReplaceAnActiveIntent()
    {
        var armed = Apply(InitialState(), CaptureCommand(40, 1, Now), TabletContext());
        var second = Apply(armed.State, CaptureCommand(46, 2, Now.AddSeconds(1), intent: 2), OtherContext(Now.AddSeconds(1)));

        Assert.Equal(CommandDisposition.RejectedInvalidState, second.Acknowledgement.Disposition);
        Assert.Equal("capture-intent-already-active", second.Acknowledgement.Code);
        Assert.Equal(Capture(1), second.State.CaptureIntent.ActiveIntent!.IntentId);
    }

    [Fact]
    public void MaintenanceExpiresAnIntentAwaitingReviewAndKeepsItsResult()
    {
        var published = PublishedCapture(ScreenshotProvenance(Now.AddSeconds(2)));
        var device = PairedTablet();
        var maintained = DesktopCanonicalStateMachine.ApplyMaintenance(
            published.State,
            Now.AddMinutes(3),
            [device],
            [ActiveSession(device, TabletSession)]);
        var expired = maintained.State.CaptureIntent.ActiveIntent!;

        Assert.Equal(ContextualCaptureStatus.Expired, expired.Status);
        Assert.NotNull(expired.Result);
        Assert.IsType<CaptureCanonicalUpdate>(Assert.Single(maintained.Updates));
    }

    [Fact]
    public void ExpiredAndFutureIssuedCommandsCannotApply()
    {
        var expired = Apply(InitialState(), Upsert(50, 1, 0, Now), TabletContext(Now.AddMinutes(1)));
        var future = Apply(InitialState(), Upsert(51, 1, 0, Now.AddMinutes(3)), TabletContext());

        Assert.Equal(CommandDisposition.RejectedExpired, expired.Acknowledgement.Disposition);
        Assert.Empty(expired.State.Marks.Marks);
        Assert.Equal(CommandDisposition.RejectedInvalidState, future.Acknowledgement.Disposition);
        Assert.Equal("command-issued-in-future", future.Acknowledgement.Code);
    }

    [Fact]
    public void ACommandFromAnotherAuthorityLifetimeRequiresASnapshot()
    {
        var command = Upsert(52, 1, 0, Now);
        var result = DesktopCanonicalStateMachine.Apply(
            InitialState(),
            Envelope(command, epoch: new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099"))),
            TabletContext());

        Assert.Equal(CommandDisposition.RequiresSnapshot, result.Acknowledgement.Disposition);
        Assert.Equal("authority-epoch-mismatch", result.Acknowledgement.Code);
        Assert.NotNull(result.Acknowledgement.CanonicalState);
        Assert.Empty(result.State.Marks.Marks);
    }

    [Fact]
    public void AnUnreadableVersionIsUnsupportedButAReadableUnnegotiatedVersionIsAPeerError()
    {
        var command = Upsert(53, 1, 0, Now);
        var newer = DesktopCanonicalStateMachine.Apply(
            InitialState(),
            Envelope(command, version: new CompanionProtocolVersion(2, 1)),
            TabletContext());
        var unnegotiated = DesktopCanonicalStateMachine.Apply(
            InitialState(),
            Envelope(command),
            new AuthenticatedCommandContext(
                TabletDevice,
                TabletSession,
                TabletKey,
                TabletInstance,
                new CompanionProtocolVersion(1, 0),
                CompanionSurfaceKind.TabletLandscape,
                TabletCapabilities,
                Now,
                false));

        Assert.Equal(CommandDisposition.UnsupportedVersion, newer.Acknowledgement.Disposition);
        Assert.Equal("protocol-version-unreadable", newer.Acknowledgement.Code);
        Assert.Equal(AcknowledgementDisposition.UnsupportedVersion, newer.Acknowledgement.CoreDisposition);
        Assert.Equal(0, newer.Acknowledgement.AppliedRevision.Value);
        Assert.Null(newer.Acknowledgement.AppliedChangeId);
        Assert.Equal(CommandDisposition.RejectedInvalidState, unnegotiated.Acknowledgement.Disposition);
        Assert.Equal("version-not-negotiated", unnegotiated.Acknowledgement.Code);
        Assert.Null(unnegotiated.Acknowledgement.CoreDisposition);
        Assert.Empty(newer.State.Marks.Marks);
    }

    [Fact]
    public void AnExpiredPendingRequestOrTheRequestersExpiredLeaseDoesNotBlockANewRequest()
    {
        var otherRequest = new RequestControlCommand(Command(80), new AggregateRevision(1), Now, Now.AddMinutes(1), TimeSpan.FromMinutes(1));
        var pending = Apply(InitialState(), otherRequest, OtherContext()).State;
        var later = Now.AddMinutes(2);
        var tabletRequest = new RequestControlCommand(Command(81), new AggregateRevision(2), later, later.AddMinutes(1), TimeSpan.FromMinutes(1));

        var replaced = Apply(pending, tabletRequest, TabletContext(later));

        Assert.Equal(CommandDisposition.Applied, replaced.Acknowledgement.Disposition);
        Assert.Equal(tabletRequest.CommandId, replaced.State.DeviceModes.PendingControl!.RequestCommandId);
        Assert.Equal(CompanionInteractionMode.Follow, replaced.State.DeviceModes.ModeOf(OtherDevice));

        var granted = Apply(
            replaced.State,
            new ResolveControlCommand(Command(82), new AggregateRevision(3), later, later.AddMinutes(1), tabletRequest.CommandId, true, Lease),
            DesktopContext(later)).State;
        var afterLease = later.AddMinutes(2);
        var again = Apply(
            granted,
            new RequestControlCommand(Command(83), new AggregateRevision(4), afterLease, afterLease.AddMinutes(1), TimeSpan.FromMilliseconds(90_500)),
            TabletContext(afterLease));

        Assert.Equal(CommandDisposition.Applied, again.Acknowledgement.Disposition);
        Assert.Null(again.State.DeviceModes.ControlLease);
        Assert.Equal(CompanionInteractionMode.ControlPending, again.State.DeviceModes.ModeOf(TabletDevice));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestControlCommand(
            Command(84), new AggregateRevision(5), afterLease, afterLease.AddMinutes(1), TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void ACancelledOrFailedArtifactEndsOnlyThatCaptureButASessionTerminalEndsTheIntent()
    {
        var armed = Apply(InitialState(), CaptureCommand(40, 1, Now), TabletContext());
        var at = Now.AddSeconds(1);
        var failedArtifact = DesktopCanonicalStateMachine.Apply(
            armed.State,
            Envelope(
                new ReportCaptureProgressCommand(Command(85), new AggregateRevision(2), at, at.AddSeconds(30), Capture(1), ContextualCaptureProgressPhase.Failed, null, "artifact-1", 0, "capture-unreadable"),
                DesktopSession),
            DesktopContext(at));
        var cancelledSession = DesktopCanonicalStateMachine.Apply(
            failedArtifact.State,
            Envelope(
                new ReportCaptureProgressCommand(Command(86), new AggregateRevision(3), at, at.AddSeconds(30), Capture(1), ContextualCaptureProgressPhase.Cancelled, null, null, null, "user-cancelled"),
                DesktopSession),
            DesktopContext(at));
        var resultForFailedArtifact = DesktopCanonicalStateMachine.Apply(
            failedArtifact.State,
            Envelope(
                new PublishCaptureResultCommand(
                    Command(87),
                    new AggregateRevision(3),
                    at,
                    at.AddSeconds(30),
                    Capture(1),
                    new ContextualCaptureResult("result-9", "artifact-1", 0, new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current), RecognizedContext.Loot, at, ScreenshotProvenance(at)),
                    []),
                DesktopSession),
            DesktopContext(at));

        Assert.Equal(CommandDisposition.Applied, failedArtifact.Acknowledgement.Disposition);
        Assert.Equal(ContextualCaptureStatus.AwaitingUserCapture, failedArtifact.State.CaptureIntent.ActiveIntent!.Status);
        Assert.Equal(ContextualCaptureStatus.Cancelled, cancelledSession.State.CaptureIntent.ActiveIntent!.Status);
        Assert.Equal("result-artifact-ended", resultForFailedArtifact.Acknowledgement.Code);
    }

    [Fact]
    public void MaintenanceOfStateAtTheCommandBudgetStaysDeliverable()
    {
        var state = Apply(InitialState(), CaptureCommand(40, 1, Now), TabletContext()).State;
        string? stopCode = null;
        for (var index = 0; index < ProtocolBounds.MaxMarks && stopCode is null; index++)
        {
            var at = Now.AddMilliseconds(index);
            var result = Apply(state, UpsertWith(2000 + index, WideDraft(index), revision: index + 1, mark: 2000 + index, now: at), TabletContext(at));
            if (result.Acknowledgement.Disposition == CommandDisposition.Applied)
            {
                state = result.State;
            }
            else
            {
                stopCode = result.Acknowledgement.Code;
            }
        }

        // The armed capture's status name grows when maintenance expires it; the reserve absorbs that.
        var device = PairedTablet();
        var maintained = DesktopCanonicalStateMachine.ApplyMaintenance(state, Now.AddMinutes(3), [device], [ActiveSession(device, TabletSession)]);

        Assert.Equal("canonical-state-exceeds-delivery-bound", stopCode);
        Assert.Equal(ContextualCaptureStatus.Expired, maintained.State.CaptureIntent.ActiveIntent!.Status);
        Assert.True(CanonicalDeliveryBudget.IsDeliverable(maintained.State));
        _ = CompanionProtocolJson.Serialize(new ServerEnvelope(
            CompanionProtocolVersion.Current,
            TabletSession,
            DesktopDevice,
            Now.AddMinutes(3),
            new DeliverySequence(ProtocolBounds.MaxWireInteger),
            new CanonicalSnapshotMessage(maintained.State)));
    }

    [Fact]
    public void DesktopLocalWorkspaceChangesAreRevisionedDesktopOnlyAndNeverQueued()
    {
        var local = new UpdateDesktopWorkspaceCommand(Command(60), new AggregateRevision(1), Now, Now.AddMinutes(1), Projection("interchange"));
        var desktop = DesktopCanonicalStateMachine.Apply(InitialState(), Envelope(local, DesktopSession), DesktopContext());
        var tablet = Apply(InitialState(), local, TabletContext());
        var desktopMode = DesktopCanonicalStateMachine.Apply(
            InitialState(),
            Envelope(SetMode(61, 1, Now, CompanionInteractionMode.Independent), DesktopSession),
            DesktopContext());

        Assert.Equal(CommandDisposition.Applied, desktop.Acknowledgement.Disposition);
        Assert.Equal("interchange", desktop.State.Workspace.Projection.MapId);
        Assert.Equal(1, desktop.State.Workspace.Cursor.Revision.Value);
        Assert.IsType<WorkspaceCanonicalUpdate>(desktop.Update);
        Assert.Equal(CommandDisposition.RejectedUnauthorized, tablet.Acknowledgement.Disposition);
        Assert.False(DesktopCanonicalStateMachine.IsQueueEligible(local));
        Assert.Equal("desktop-has-no-interaction-mode", desktopMode.Acknowledgement.Code);
    }

    [Fact]
    public void StateThatCouldNotBeDeliveredIsRejectedBeforeItIsCommitted()
    {
        var state = InitialState();
        CommandReduction? rejected = null;
        for (var index = 0; index < ProtocolBounds.MaxMarks && rejected is null; index++)
        {
            var at = Now.AddMilliseconds(index);
            var command = new UpsertMarkCommand(
                Command(1000 + index),
                new AggregateRevision(index + 1),
                at,
                at.AddMinutes(1),
                Mark(1000 + index),
                0,
                WideDraft(index));
            var result = Apply(state, command, TabletContext(at));
            if (result.Acknowledgement.Disposition == CommandDisposition.Applied)
            {
                state = result.State;
            }
            else
            {
                rejected = result;
            }

            Assert.True(CanonicalDeliveryBudget.Fits(state));
        }

        Assert.NotNull(rejected);
        Assert.Equal(CommandDisposition.RejectedInvalidState, rejected.Acknowledgement.Disposition);
        Assert.Equal("canonical-state-exceeds-delivery-bound", rejected.Acknowledgement.Code);
        Assert.Same(state, rejected.State);
        var snapshot = CompanionProtocolJson.Serialize(new ServerEnvelope(
            CompanionProtocolVersion.Current,
            TabletSession,
            DesktopDevice,
            Now,
            new DeliverySequence(1),
            new CanonicalSnapshotMessage(state)));
        Assert.InRange(snapshot.Length, 1, ProtocolBounds.MaxPayloadBytes);
    }

    [Fact]
    public void CaptureProvenanceDepthIsBoundedSoTheDeepestLegalStateIsDeliverable()
    {
        Assert.Throws<ArgumentException>(() => new ContextualCaptureResult(
            "result-deep",
            "artifact-1",
            0,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            RecognizedContext.Loot,
            Now,
            ProvenanceOfDepth(ProtocolBounds.MaxCaptureProvenanceDepth + 1, Now)));

        var published = PublishedCapture(ProvenanceOfDepth(ProtocolBounds.MaxCaptureProvenanceDepth, Now.AddSeconds(2)));
        Assert.True(CanonicalDeliveryBudget.Fits(published.State));

        var cursor = published.State.CaptureIntent.Cursor;
        var envelope = new ServerEnvelope(
            CompanionProtocolVersion.Current,
            TabletSession,
            DesktopDevice,
            Now,
            new DeliverySequence(ProtocolBounds.MaxWireInteger),
            new CommandAcknowledgementMessage(new CommandAcknowledgement(
                Command(999),
                CanonicalAggregateKind.CaptureIntent,
                cursor.Revision,
                cursor.Revision,
                cursor.LastChangeId,
                published.State.GlobalRevision,
                published.State.AuthorityEpoch,
                CommandDisposition.RejectedConflict,
                "revision-occupied-by-another-command",
                published.State)));
        var payload = CompanionProtocolJson.Serialize(envelope);
        var roundTrip = CompanionProtocolJson.Deserialize<ServerEnvelope>(payload);

        AssertJsonEqual(payload, CompanionProtocolJson.Serialize(roundTrip));
    }

    internal static CommandReduction PublishedCapture(EvidenceProvenance provenance)
    {
        var armed = Apply(InitialState(), CaptureCommand(40, 1, Now), TabletContext());
        var progressAt = Now.AddSeconds(1);
        var progressed = DesktopCanonicalStateMachine.Apply(
            armed.State,
            Envelope(
                new ReportCaptureProgressCommand(Command(41), new AggregateRevision(2), progressAt, progressAt.AddSeconds(40), Capture(1), ContextualCaptureProgressPhase.Decoding, 30, "artifact-1", 0, "decoding-visible-capture"),
                DesktopSession),
            DesktopContext(progressAt));
        var resultAt = Now.AddSeconds(2);
        var result = new ContextualCaptureResult(
            "result-1",
            "artifact-1",
            0,
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            RecognizedContext.Loot,
            resultAt,
            provenance);
        var published = DesktopCanonicalStateMachine.Apply(
            progressed.State,
            Envelope(
                new PublishCaptureResultCommand(Command(42), new AggregateRevision(3), resultAt, resultAt.AddSeconds(40), Capture(1), result, [new ContextualCaptureGuidance(CaptureGuidanceKind.ConfirmResult, "review", "Review the recognized items.", 0)]),
                DesktopSession),
            DesktopContext(resultAt));
        Assert.Equal(CommandDisposition.Applied, published.Acknowledgement.Disposition);
        return published;
    }

    internal static RequestCaptureIntentCommand CaptureCommand(int command, long revision, DateTimeOffset now, int intent = 1) => new(
        Command(command),
        new AggregateRevision(revision),
        now,
        now.AddMinutes(1),
        Capture(intent),
        "capture-correlation",
        CaptureSession(intent),
        ScanIntent.Loot,
        new CompanionCaptureContext("customs", "ground", "profile", null, [], [], []));

    private static MapMarkDraft WideDraft(int index) => new(
        MapMarkKind.Note,
        MapMarkScope.PairedDevice,
        new MapMarkState(new string('m', ProtocolBounds.MaxShortStringBytes), new string('f', ProtocolBounds.MaxShortStringBytes), index, 3, new string('x', MapMarkState.MaxLabelLength), null),
        CoordinateSpaceKind.World,
        new string('p', ProtocolBounds.MaxShortStringBytes),
        2,
        "#00AACC");

    private static UpsertMarkCommand UpsertWith(int command, MapMarkDraft draft, long revision = 1, int mark = 1, DateTimeOffset? now = null)
    {
        var at = now ?? Now;
        return new UpsertMarkCommand(Command(command), new AggregateRevision(revision), at, at.AddSeconds(30), Mark(mark), 0, draft);
    }

    private static int ProtocolGuardVersion(CommandId id)
    {
        Span<byte> bytes = stackalloc byte[16];
        Assert.True(id.Value.TryWriteBytes(bytes, bigEndian: true, out _));
        return bytes[6] >> 4;
    }
}
