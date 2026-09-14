using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class CaptureAndWorkspaceContractTests
{
    private static readonly WorkspaceOrigin Origin = V2ContractTestData.Origin;

    private static readonly CaptureSessionId SessionId = V2ContractTestData.SessionId;

    private static readonly CaptureSessionRequest Request =
        new(SessionId, ScanIntent.Stash, Origin, V2ContractTestData.ObservedUtc);

    private static readonly ResultStatus Partial = new(ResultCompleteness.Partial, FreshnessState.Current);

    [Fact]
    public void GuidedSessionTracksEachCaptureByArtifactAndOrdinal()
    {
        var snapshot = GuidedSnapshot();

        var roundTrip = JsonSerializer.Deserialize<CaptureSessionSnapshot>(
            JsonSerializer.Serialize(snapshot, V2ContractJson.Options), V2ContractJson.Options)!;

        Assert.Equal(9, roundTrip.Progress.Count);
        Assert.Equal("shot-1", roundTrip.Progress[^1].ArtifactId);
        Assert.Equal(1, roundTrip.Progress[^1].CaptureOrdinal);
    }

    [Fact]
    public void CaptureStagesMoveForwardAndEndAtTheirTerminal()
    {
        Assert.Throws<ArgumentException>(() => Snapshot(
            Partial,
            (CaptureSessionStage.Matching, "shot-0", 0),
            (CaptureSessionStage.Decoding, "shot-0", 0)));
        Assert.Throws<ArgumentException>(() => Snapshot(
            Partial,
            (CaptureSessionStage.Failed, "shot-0", 0),
            (CaptureSessionStage.Decoding, "shot-0", 0)));
        Assert.Throws<ArgumentException>(() => Snapshot(
            Partial,
            (CaptureSessionStage.Settling, "shot-0", 0),
            (CaptureSessionStage.Settling, "shot-9", 0)));
        Assert.Throws<ArgumentException>(() => Snapshot(
            Partial,
            (CaptureSessionStage.AwaitingCapture, null, null),
            (CaptureSessionStage.Armed, null, null)));
    }

    [Fact]
    public void CaptureOrdinalsNumberTheQueueFromZeroWithoutGaps()
    {
        Assert.Throws<ArgumentException>(() => Snapshot(Partial, (CaptureSessionStage.Settling, "shot-99", 99)));
        Assert.Throws<ArgumentException>(() => Snapshot(
            Partial,
            (CaptureSessionStage.Settling, "shot-0", 0),
            (CaptureSessionStage.Settling, "shot-2", 2)));
        Assert.Throws<ArgumentException>(() => Snapshot(
            Partial,
            (CaptureSessionStage.Settling, "shot-1", 1),
            (CaptureSessionStage.Settling, "shot-0", 0)));
        Assert.Throws<ArgumentException>(() => Snapshot(
            Partial,
            (CaptureSessionStage.Settling, "shot-0", 0),
            (CaptureSessionStage.Settling, "shot-0", 1)));
        Assert.Equal(
            [0, 1, 0, 1],
            Snapshot(
                Partial,
                (CaptureSessionStage.Settling, "shot-0", 0),
                (CaptureSessionStage.Settling, "shot-1", 1),
                (CaptureSessionStage.Decoding, "shot-0", 0),
                (CaptureSessionStage.Decoding, "shot-1", 1)).Progress.Select(item => item.CaptureOrdinal!.Value));
    }

    [Fact]
    public void HostileSnapshotJsonCannotHideDroppedQueueEntries()
    {
        var json = JsonSerializer.Serialize(GuidedSnapshot(), V2ContractJson.Options);

        // Every shot-1 entry renumbered: a lone high ordinal, a gap, a duplicate of shot-0, and
        // the two captures entering the queue out of order.
        Assert.ThrowsAny<ArgumentException>(() => MutateOrdinals(json, (artifact, ordinal) => artifact == "shot-1" ? 99 : ordinal));
        Assert.ThrowsAny<ArgumentException>(() => MutateOrdinals(json, (artifact, ordinal) => artifact == "shot-1" ? 2 : ordinal));
        Assert.ThrowsAny<ArgumentException>(() => MutateOrdinals(json, (artifact, ordinal) => artifact == "shot-1" ? 0 : ordinal));
        Assert.ThrowsAny<ArgumentException>(() => MutateOrdinals(json, (_, ordinal) => 1 - ordinal));
        Assert.Equal(9, MutateOrdinals(json, (_, ordinal) => ordinal)!.Progress.Count);
    }

    [Fact]
    public void SessionTerminalIsFinalAndStatusCannotOverclaim()
    {
        var complete = new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current);

        Assert.Throws<ArgumentException>(() => Snapshot(
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current),
            (CaptureSessionStage.Cancelled, null, null),
            (CaptureSessionStage.Settling, "shot-0", 0)));
        Assert.Throws<ArgumentException>(() => Snapshot(
            complete,
            (CaptureSessionStage.Settling, "shot-0", 0),
            (CaptureSessionStage.Complete, null, null)));
        Assert.Throws<ArgumentException>(() => Snapshot(complete, (CaptureSessionStage.Settling, "shot-0", 0)));
        Assert.Throws<ArgumentException>(() => Snapshot(Partial, (CaptureSessionStage.Failed, null, null)));
        Assert.Equal(
            ResultCompleteness.Complete,
            Snapshot(complete, (CaptureSessionStage.Complete, "shot-0", 0), (CaptureSessionStage.Complete, null, null)).Status.Completeness);
    }

    [Fact]
    public void StageProgressPairsArtifactWithOrdinalAndScope()
    {
        Assert.Throws<ArgumentException>(() => Progress(0, CaptureSessionStage.Decoding, null, null));
        Assert.Throws<ArgumentException>(() => Progress(0, CaptureSessionStage.Armed, "shot-0", 0));
        Assert.Throws<ArgumentException>(() => Progress(0, CaptureSessionStage.Complete, "shot-0", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => Progress(0, (CaptureSessionStage)99, null, null));
        Assert.Throws<ArgumentException>(() => new CaptureSessionSnapshot(Request, [null!], Partial));
    }

    [Fact]
    public void ProgressCannotPredateTheRequest()
    {
        Assert.Throws<ArgumentException>(() => new CaptureSessionSnapshot(
            Request,
            [new CaptureStageProgress(SessionId, 0, CaptureSessionStage.Armed, V2ContractTestData.ObservedUtc.AddSeconds(-1))],
            Partial));
    }

    [Fact]
    public void DefaultIdentifiersEnumsAndReversedExpiryAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new CaptureSessionRequest(default, ScanIntent.Loot, Origin, V2ContractTestData.ObservedUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureSessionRequest(SessionId, default, Origin, V2ContractTestData.ObservedUtc));
        Assert.Throws<ArgumentException>(() =>
            new WorkspaceOrigin(default, Origin.DeviceId, WorkspaceOriginKind.PairedDevice, "tablet"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkspaceOrigin(Origin.WorkspaceId, Origin.DeviceId, default, "tablet"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureSessionRequest(
            SessionId, ScanIntent.Loot, Origin, V2ContractTestData.ObservedUtc,
            ExpiresUtc: V2ContractTestData.ObservedUtc.AddSeconds(-1)));
    }

    [Fact]
    public void VersionsReadWithinTheirMajorUpToTheirMinorAndNegotiateDown()
    {
        var reader = new V2ContractVersion(2, 1);

        Assert.True(reader.CanRead(new V2ContractVersion(2, 0)));
        Assert.True(reader.CanRead(reader));
        Assert.False(reader.CanRead(new V2ContractVersion(2, 2)));
        Assert.False(reader.CanRead(new V2ContractVersion(3, 0)));
        Assert.False(reader.CanRead(default));
        Assert.Equal(new V2ContractVersion(2, 1), V2ContractVersion.Negotiate(new V2ContractVersion(2, 3), reader));
        Assert.Null(V2ContractVersion.Negotiate(reader, new V2ContractVersion(3, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new V2ContractVersion(2, V2ContractVersion.MaxMinor + 1));
    }

    [Theory]
    [InlineData(AcknowledgementDisposition.Applied, 8, 8, 0, true)]
    [InlineData(AcknowledgementDisposition.RejectedStale, 8, 9, 0, true)]
    [InlineData(AcknowledgementDisposition.RejectedConflict, 8, 8, 0, false)]
    [InlineData(AcknowledgementDisposition.RejectedConflict, 8, 6, 0, false)]
    [InlineData(AcknowledgementDisposition.UnsupportedVersion, 8, 6, 5, false)]
    public void AcknowledgementStatesItsRevisionsVersionsAndAppliedChangeIdentity(
        AcknowledgementDisposition disposition, long requested, long applied, int requestedMinor, bool sameAppliedChange)
    {
        var acknowledgement = Acknowledge(
            disposition, requested, applied, new V2ContractVersion(2, requestedMinor), sameAppliedChange);

        var roundTrip = JsonSerializer.Deserialize<StateAcknowledgement>(
            JsonSerializer.Serialize(acknowledgement, V2ContractJson.Options), V2ContractJson.Options)!;

        Assert.Equal(requested, roundTrip.RequestedRevision.Value);
        Assert.Equal(applied, roundTrip.AppliedRevision.Value);
        Assert.Equal(disposition, roundTrip.Disposition);
        Assert.Equal(V2ContractVersion.Current, roundTrip.ReceiverContractVersion);
        Assert.Equal(sameAppliedChange ? ChangeId() : OtherChangeId(), roundTrip.AppliedChangeId);
    }

    [Fact]
    public void DuplicateDeliveryAndDivergentChangeAtTheSameRevisionAreDistinguishedByAppliedChangeId()
    {
        // Simultaneous desktop/tablet control: both computed a change against the same prior
        // revision, so both target revision 8. Only the one that actually landed matches change ids.
        var duplicate = Acknowledge(AcknowledgementDisposition.Applied, 8, 8, V2ContractVersion.Current, sameAppliedChange: true);
        var divergent = Acknowledge(AcknowledgementDisposition.RejectedConflict, 8, 8, V2ContractVersion.Current, sameAppliedChange: false);

        Assert.Equal(duplicate.ChangeId, duplicate.AppliedChangeId);
        Assert.NotEqual(divergent.ChangeId, divergent.AppliedChangeId);
    }

    [Theory]
    [InlineData(AcknowledgementDisposition.Applied, 8, 7, 0, true)]
    [InlineData(AcknowledgementDisposition.RejectedStale, 8, 8, 0, true)]
    [InlineData(AcknowledgementDisposition.RejectedStale, 8, 7, 0, true)]
    [InlineData(AcknowledgementDisposition.RejectedConflict, 8, 8, 0, true)]
    [InlineData(AcknowledgementDisposition.UnsupportedVersion, 8, 7, 0, false)]
    [InlineData(AcknowledgementDisposition.Applied, 8, 8, 5, true)]
    [InlineData(AcknowledgementDisposition.Applied, 0, 0, 0, false)]
    public void AcknowledgementRejectsInconsistentOutcomes(
        AcknowledgementDisposition disposition, long requested, long applied, int requestedMinor, bool sameAppliedChange)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            Acknowledge(disposition, requested, applied, new V2ContractVersion(2, requestedMinor), sameAppliedChange));
    }

    [Fact]
    public void AppliedRevisionZeroAndAppliedChangeIdMustAgree()
    {
        Assert.Throws<ArgumentException>(() =>
            Acknowledge(AcknowledgementDisposition.UnsupportedVersion, 8, 0, new V2ContractVersion(2, 5), sameAppliedChange: true));
        Assert.Throws<ArgumentException>(() => new StateAcknowledgement(
            new StateStreamId("map.marks"),
            ChangeId(),
            new StateRevision(8),
            new StateRevision(0),
            OtherChangeId(),
            AcknowledgementDisposition.RejectedConflict,
            new V2ContractVersion(2, 0),
            V2ContractVersion.Current,
            Origin,
            V2ContractTestData.ObservedUtc));

        var acknowledgement = Acknowledge(
            AcknowledgementDisposition.UnsupportedVersion, 8, 0, new V2ContractVersion(2, 5), sameAppliedChange: false);
        Assert.Null(acknowledgement.AppliedChangeId);
    }

    [Fact]
    public void RevisionedStateCarriesOnlyAllowlistedPayloadsFromRevisionOne()
    {
        var mark = new MapMarkState("customs", null, 120.5, -40, "Regroup here", null);
        var state = new RevisionedState<MapMarkState>(
            new StateStreamId("map.marks"), new StateRevision(1), ChangeId(), V2ContractVersion.Current,
            Origin, V2ContractTestData.ObservedUtc, mark);

        var roundTrip = JsonSerializer.Deserialize<RevisionedState<MapMarkState>>(
            JsonSerializer.Serialize(state, V2ContractJson.Options), V2ContractJson.Options)!;

        Assert.Equal(mark, roundTrip.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RevisionedState<MapMarkState>(
            new StateStreamId("map.marks"), new StateRevision(0), ChangeId(), V2ContractVersion.Current,
            Origin, V2ContractTestData.ObservedUtc, mark));
        Assert.Throws<ArgumentException>(() => new RevisionedState<GameInputCommand>(
            new StateStreamId("control"), new StateRevision(1), ChangeId(), V2ContractVersion.Current,
            Origin, V2ContractTestData.ObservedUtc, new GameInputCommand("F")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapMarkState("customs", null, double.NaN, 0, null, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MapMarkState("customs", null, 0, 0, new string('x', 81), null));
    }

    private static CaptureSessionSnapshot GuidedSnapshot() => Snapshot(
        Partial,
        (CaptureSessionStage.Armed, null, null),
        (CaptureSessionStage.AwaitingCapture, null, null),
        (CaptureSessionStage.Settling, "shot-0", 0),
        (CaptureSessionStage.Settling, "shot-1", 1),
        (CaptureSessionStage.Decoding, "shot-0", 0),
        (CaptureSessionStage.Settling, "shot-0", 0),
        (CaptureSessionStage.Decoding, "shot-0", 0),
        (CaptureSessionStage.Complete, "shot-0", 0),
        (CaptureSessionStage.Failed, "shot-1", 1));

    private static CaptureSessionSnapshot? MutateOrdinals(string json, Func<string, int, int> renumber)
    {
        var node = JsonNode.Parse(json)!;
        foreach (var entry in node["progress"]!.AsArray())
        {
            var item = entry!;
            if (item["artifactId"]?.GetValue<string>() is { } artifact)
            {
                item["captureOrdinal"] = renumber(artifact, item["captureOrdinal"]!.GetValue<int>());
            }
        }

        return JsonSerializer.Deserialize<CaptureSessionSnapshot>(node.ToJsonString(), V2ContractJson.Options);
    }

    private static StateChangeId ChangeId() => new(Guid.Parse("10000000-0000-0000-0000-000000000004"));

    private static StateChangeId OtherChangeId() => new(Guid.Parse("10000000-0000-0000-0000-000000000005"));

    // sameAppliedChange picks whether the change occupying AppliedRevision is this acknowledged
    // change (a first apply, or a safe duplicate redelivery of it) or a different one (a
    // same-revision divergent change from simultaneous desktop/tablet control). AppliedRevision
    // zero always carries no applied change id, per the zero-means-no-applied-state invariant.
    private static StateAcknowledgement Acknowledge(
        AcknowledgementDisposition disposition, long requested, long applied, V2ContractVersion requestedVersion,
        bool sameAppliedChange) => new(
        new StateStreamId("map.marks"),
        ChangeId(),
        new StateRevision(requested),
        new StateRevision(applied),
        applied == 0 ? (sameAppliedChange ? ChangeId() : null) : (sameAppliedChange ? ChangeId() : OtherChangeId()),
        disposition,
        requestedVersion,
        V2ContractVersion.Current,
        Origin,
        V2ContractTestData.ObservedUtc);

    private static CaptureStageProgress Progress(long sequence, CaptureSessionStage stage, string? artifactId, int? ordinal) => new(
        SessionId, sequence, stage, V2ContractTestData.ObservedUtc.AddSeconds(sequence), artifactId, ordinal);

    private static CaptureSessionSnapshot Snapshot(
        ResultStatus status,
        params (CaptureSessionStage Stage, string? ArtifactId, int? Ordinal)[] stages) => new(
        Request,
        stages.Select((stage, index) => Progress(index, stage.Stage, stage.ArtifactId, stage.Ordinal)).ToArray(),
        status);

    private sealed record GameInputCommand(string Key) : IWorkspaceStatePayload;
}
