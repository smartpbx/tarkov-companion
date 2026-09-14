using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.CompanionProtocol.Tests;

internal static class ProtocolTestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);
    public static readonly CompanionDeviceId DesktopDevice = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    public static readonly CompanionDeviceId TabletDevice = new(Guid.Parse("10000000-0000-0000-0000-000000000002"));
    public static readonly CompanionDeviceId OtherDevice = new(Guid.Parse("10000000-0000-0000-0000-000000000003"));
    public static readonly DeviceSessionId DesktopSession = new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    public static readonly DeviceSessionId TabletSession = new(Guid.Parse("20000000-0000-0000-0000-000000000002"));
    public static readonly DeviceKeyId DesktopKey = new("ZGVza3RvcC1rZXktaWQ");
    public static readonly DeviceKeyId TabletKey = new("dGFibGV0LWtleS1pZA");
    public static readonly AuthorityEpoch Epoch = new(Guid.Parse("30000000-0000-0000-0000-000000000001"));

    public static readonly IReadOnlyList<DeviceCapability> TabletCapabilities =
    [
        DeviceCapability.FollowDesktop,
        DeviceCapability.RequestControl,
        DeviceCapability.ShowOnDesktop,
        DeviceCapability.ManageOwnMarks,
        DeviceCapability.RequestCaptureIntent,
        DeviceCapability.ReviewCaptureResult,
    ];

    public static CanonicalCompanionState InitialState() => new(
        Epoch,
        new GlobalRevision(0),
        DesktopDevice,
        new DeviceModeAggregate(
            AggregateCursor.Empty,
            [new DeviceModeEntry(TabletDevice, CompanionInteractionMode.Follow, Now)],
            null,
            null),
        new WorkspaceAggregate(AggregateCursor.Empty, Projection("customs")),
        new MarkAggregate(AggregateCursor.Empty, []),
        new CaptureIntentAggregate(AggregateCursor.Empty, null));

    public static WorkspaceProjection Projection(string mapId = "customs") => new(
        WorkspaceKind.Raid,
        mapId,
        "ground",
        new WorkspaceViewport(
            new MapCoordinate(mapId, "ground", CoordinateSpaceKind.World, "tarkov-dev-1", 10, 2, 20),
            1),
        null,
        [],
        [],
        null,
        [],
        ["extracts"],
        [],
        null);

    public static AuthenticatedCommandContext TabletContext(DateTimeOffset? now = null) => new(
        TabletDevice,
        TabletSession,
        TabletKey,
        CompanionSurfaceKind.TabletLandscape,
        TabletCapabilities,
        now ?? Now,
        false);

    public static AuthenticatedCommandContext DesktopContext(DateTimeOffset? now = null) => new(
        DesktopDevice,
        DesktopSession,
        DesktopKey,
        CompanionSurfaceKind.Desktop,
        Enum.GetValues<DeviceCapability>(),
        now ?? Now,
        true);

    public static ClientCommandEnvelope Envelope(
        CompanionCommand command,
        DeviceSessionId? sessionId = null,
        DateTimeOffset? sentUtc = null) =>
        new(
            CompanionProtocolVersion.Current,
            sessionId ?? TabletSession,
            sentUtc ?? command.IssuedUtc,
            command);

    public static CommandId Command(int number) => new(Guid.Parse($"40000000-0000-0000-0000-{number:000000000000}"));

    public static MarkId Mark(int number) => new(Guid.Parse($"50000000-0000-0000-0000-{number:000000000000}"));

    public static CaptureIntentId Capture(int number) => new(Guid.Parse($"60000000-0000-0000-0000-{number:000000000000}"));

    public static CaptureSessionId CaptureSession(int number) => new(Guid.Parse($"70000000-0000-0000-0000-{number:000000000000}"));

    public static EvidenceProvenance ScreenshotProvenance(DateTimeOffset observedUtc) => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        "fixture://paired-capture",
        observedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
        new ProducerIdentity("paired-fixture", "2.0"));
}
