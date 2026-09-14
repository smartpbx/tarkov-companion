using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

public sealed class CaptureAndWorkspaceContractTests
{
    private static readonly WorkspaceOrigin Origin = new(
        new WorkspaceId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
        new CompanionDeviceId(Guid.Parse("10000000-0000-0000-0000-000000000002")),
        WorkspaceOriginKind.DesktopApplication,
        "desktop-primary");

    [Fact]
    public void CaptureProgressMustBelongToOneSessionAndBeMonotonic()
    {
        var sessionId = new CaptureSessionId(Guid.Parse("10000000-0000-0000-0000-000000000003"));
        var request = new CaptureSessionRequest(sessionId, ScanIntent.Stash, Origin, V2ContractTestData.ObservedUtc);
        var progress = new[]
        {
            new CaptureStageProgress(sessionId, 0, CaptureSessionStage.Armed, V2ContractTestData.ObservedUtc),
            new CaptureStageProgress(sessionId, 1, CaptureSessionStage.Decoding, V2ContractTestData.ObservedUtc.AddSeconds(1)),
        };

        var snapshot = new CaptureSessionSnapshot(
            request,
            progress,
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current));

        Assert.Equal(2, snapshot.Progress.Count);
        Assert.Equal(CaptureSessionStage.Decoding, snapshot.Progress[^1].Stage);
    }

    [Fact]
    public void CaptureProgressRejectsSequenceGaps()
    {
        var sessionId = new CaptureSessionId(Guid.Parse("10000000-0000-0000-0000-000000000003"));
        var request = new CaptureSessionRequest(sessionId, ScanIntent.Stash, Origin, V2ContractTestData.ObservedUtc);

        Assert.Throws<ArgumentException>(() => new CaptureSessionSnapshot(
            request,
            [new CaptureStageProgress(sessionId, 1, CaptureSessionStage.Decoding, V2ContractTestData.ObservedUtc)],
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current)));
    }

    [Fact]
    public void DefaultIdentifiersAndReversedExpiryAreRejected()
    {
        var sessionId = new CaptureSessionId(Guid.Parse("10000000-0000-0000-0000-000000000003"));

        Assert.Throws<ArgumentException>(() =>
            new CaptureSessionRequest(default, ScanIntent.Loot, Origin, V2ContractTestData.ObservedUtc));
        Assert.Throws<ArgumentException>(() =>
            new WorkspaceOrigin(default, Origin.DeviceId, WorkspaceOriginKind.PairedDevice, "tablet"));
        Assert.Throws<ArgumentException>(() => new StateAcknowledgement(
            default,
            new StateChangeId(Guid.Parse("10000000-0000-0000-0000-000000000004")),
            new StateRevision(1),
            new StateRevision(1),
            AcknowledgementDisposition.Applied,
            Origin,
            V2ContractTestData.ObservedUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureSessionRequest(
            sessionId,
            ScanIntent.Loot,
            Origin,
            V2ContractTestData.ObservedUtc,
            ExpiresUtc: V2ContractTestData.ObservedUtc.AddSeconds(-1)));
    }

    [Fact]
    public void RevisionsAdvanceWithinTheirNamedStream()
    {
        var mapRevision = new StateRevision(3);
        var stashRevision = new StateRevision(11);

        Assert.Equal(4, mapRevision.Next().Value);
        Assert.Equal(12, stashRevision.Next().Value);
        Assert.NotEqual(mapRevision, stashRevision);
    }

    [Theory]
    [InlineData(AcknowledgementDisposition.Applied)]
    [InlineData(AcknowledgementDisposition.RejectedStale)]
    [InlineData(AcknowledgementDisposition.RejectedConflict)]
    [InlineData(AcknowledgementDisposition.UnsupportedVersion)]
    public void AcknowledgementPreservesRequestedAndAppliedRevision(
        AcknowledgementDisposition disposition)
    {
        var acknowledgement = new StateAcknowledgement(
            new StateStreamId("map.markers"),
            new StateChangeId(Guid.Parse("10000000-0000-0000-0000-000000000004")),
            new StateRevision(8),
            new StateRevision(7),
            disposition,
            Origin,
            V2ContractTestData.ObservedUtc);

        Assert.Equal(8, acknowledgement.RequestedRevision.Value);
        Assert.Equal(7, acknowledgement.AppliedRevision.Value);
        Assert.Equal(disposition, acknowledgement.Disposition);
    }
}
