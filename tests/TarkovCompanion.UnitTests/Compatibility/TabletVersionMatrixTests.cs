using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.UnitTests.Compatibility;

/// <summary>
/// [#294] The paired tablet across builds: what older tablet pages send, applied by the current
/// desktop, and what the current desktop sends, read the way older pages read it.
/// </summary>
/// <remarks>
/// The page is embedded in the relay binary, so the tablet a player holds is whatever the relay was
/// last deployed with, while the desktop it talks to is whatever that player last installed. The
/// fixtures are the command bodies and read paths of the page at v2-rough-11 (the first page that
/// drew the desktop's scene) and at 53a3b743 (the page with mark scope, lifetime and routes).
/// </remarks>
public sealed class TabletVersionMatrixTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);
    private static readonly CompanionDeviceId Desktop = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly CompanionDeviceId Tablet = new(Guid.Parse("10000000-0000-0000-0000-000000000002"));
    private static readonly DeviceSessionId DesktopSession = new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    private static readonly DeviceSessionId TabletSession = new(Guid.Parse("20000000-0000-0000-0000-000000000002"));

    [Theory]
    [InlineData("tablet-commands-v2-rough-11.json")]
    [InlineData("tablet-commands-53a3b743.json")]
    public void EveryCommandAnOlderTabletSendsIsParsedAndAppliedByTheCurrentDesktop(string fixture)
    {
        var played = Play(fixture);

        Assert.All(played.Steps, step => Assert.True(
            step.Disposition == CommandDisposition.Applied,
            $"{fixture}: \"{step.Name}\" was {step.Disposition} ({step.Code})."));
    }

    [Fact]
    public void AnOldTabletsMarksLandWhereItPutThem()
    {
        var state = Play("tablet-commands-v2-rough-11.json").State;

        var waypoint = Assert.Single(state.Marks.Marks);
        Assert.Equal(MapMarkKind.Waypoint, waypoint.Kind);
        Assert.Equal(MapMarkScope.PairedDevice, waypoint.Scope);
        Assert.Equal(("customs", 30.0, 15.0, "Dorms"), (waypoint.State.MapId, waypoint.State.X, waypoint.State.Y, waypoint.State.Label));
        Assert.Equal("salewa", state.Workspace.Projection.SearchQuery);
    }

    [Fact]
    public void TheDeployedTabletsScopeLifetimeAndRouteSurviveTheCurrentDesktop()
    {
        var marks = Play("tablet-commands-53a3b743.json").State.Marks.Marks;

        var timed = marks.Single(mark => mark.State.Label == "Dorms");
        Assert.Equal(MapMarkLifetime.FiveMinutes, timed.Lifetime);
        Assert.Equal(Now.AddMinutes(5), timed.State.ExpiresUtc);
        Assert.Equal(MapMarkScope.Private, marks.Single(mark => mark.Kind == MapMarkKind.Ping).Scope);
        var route = marks.Where(mark => mark.RouteId is not null).OrderBy(mark => mark.RouteStep).ToArray();
        Assert.Equal([1, 2], route.Select(mark => mark.RouteStep!.Value));
        Assert.Single(route.Select(mark => mark.RouteId).Distinct());
    }

    /// <summary>The current desktop's canonical messages still carry every path each older page reads.</summary>
    [Theory]
    [InlineData("v2-rough-11", "tablet-commands-v2-rough-11.json")]
    [InlineData("53a3b743", "tablet-commands-53a3b743.json")]
    public void TheCurrentDesktopsMessagesCarryWhatOlderPagesRead(string page, string fixture)
    {
        var reads = CompatibilityFixtures.Node("tablet-reads.json")[page]!;
        var played = Play(fixture);
        var marksUpdate = played.Updates.OfType<MarksCanonicalUpdate>().Last();
        var workspaceUpdate = played.Updates.OfType<WorkspaceCanonicalUpdate>().Last();

        Assert.Empty(CompatibilityFixtures.Missing(Server(new CanonicalSnapshotMessage(played.State)), Paths(reads, "canonicalSnapshot")));
        Assert.Empty(CompatibilityFixtures.Missing(Server(new CanonicalUpdateMessage(marksUpdate)), Paths(reads, "marksUpdate")));
        Assert.Empty(CompatibilityFixtures.Missing(Server(new CanonicalUpdateMessage(workspaceUpdate)), Paths(reads, "workspaceUpdate")));
        // The values the pages switch on, not only the keys.
        Assert.Equal("canonicalSnapshot", Server(new CanonicalSnapshotMessage(played.State))["message"]!["type"]!.GetValue<string>());
        Assert.Equal("marks", Server(new CanonicalUpdateMessage(marksUpdate))["message"]!["update"]!["type"]!.GetValue<string>());
        Assert.Equal(
            Tablet.Value.ToString(),
            Server(new CanonicalUpdateMessage(workspaceUpdate))["message"]!["update"]!["origin"]!["deviceId"]!["value"]!.GetValue<string>());
    }

    /// <summary>The map surface the current desktop publishes still carries every path each older page reads.</summary>
    [Theory]
    [InlineData("v2-rough-11")]
    [InlineData("53a3b743")]
    public void TheCurrentMapSurfaceCarriesWhatOlderPagesRead(string page)
    {
        var reads = CompatibilityFixtures.Node("tablet-reads.json")[page]!;
        var surface = JsonNode.Parse(TabletMapSurfaceJson.Serialize(Surface()))!;

        Assert.Empty(CompatibilityFixtures.Missing(surface, Paths(reads, "surface")));
    }

    private static IEnumerable<string> Paths(JsonNode reads, string part) =>
        reads[part]!.AsArray().Select(path => path!.GetValue<string>());

    private static JsonNode Server(ServerMessage message) => JsonNode.Parse(CompanionProtocolJson.Serialize(new ServerEnvelope(
        CompanionProtocolVersion.Current,
        TabletSession,
        Desktop,
        Now,
        new DeliverySequence(1),
        message)))!;

    private sealed record Step(string Name, CommandDisposition Disposition, string? Code);

    private sealed record Played(CanonicalCompanionState State, IReadOnlyList<Step> Steps, IReadOnlyList<CanonicalUpdate> Updates);

    /// <summary>
    /// Parses each recorded envelope with the protocol's own reader and applies it with the desktop's
    /// own reducer, approving a control request the way the desktop's owner does.
    /// </summary>
    private static Played Play(string fixture)
    {
        var state = InitialState();
        var steps = new List<Step>();
        var updates = new List<CanonicalUpdate>();
        foreach (var recorded in CompatibilityFixtures.Node(fixture).AsArray())
        {
            var envelope = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(
                Encoding.UTF8.GetBytes(recorded!["envelope"]!.ToJsonString()));
            var reduced = DesktopCanonicalStateMachine.Apply(state, envelope, TabletContext());
            steps.Add(new(recorded["step"]!.GetValue<string>(), reduced.Acknowledgement.Disposition, reduced.Acknowledgement.Code));
            state = reduced.State;
            if (reduced.Update is { } update)
            {
                updates.Add(update);
            }

            if (envelope.Command is RequestControlCommand request)
            {
                var approve = new ResolveControlCommand(
                    new CommandId(Guid.Parse("4d000000-0000-4000-8000-000000000001")),
                    new AggregateRevision(state.DeviceModes.Cursor.Revision.Value + 1),
                    Now,
                    Now.AddMinutes(1),
                    request.CommandId,
                    true,
                    new ControlLeaseId(Guid.Parse("41000000-0000-4000-8000-000000000001")));
                var approved = DesktopCanonicalStateMachine.Apply(
                    state,
                    new ClientCommandEnvelope(CompanionProtocolVersion.Current, DesktopSession, state.AuthorityEpoch, Now, approve),
                    DesktopContext());
                Assert.Equal(CommandDisposition.Applied, approved.Acknowledgement.Disposition);
                state = approved.State;
            }
        }

        return new(state, steps, updates);
    }

    private static CanonicalCompanionState InitialState() => new(
        new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000001")),
        new WorkspaceId(Guid.Parse("80000000-0000-4000-8000-000000000001")),
        "desktop-install-1",
        new GlobalRevision(0),
        Desktop,
        new DeviceModeAggregate(AggregateCursor.Empty, [new DeviceModeEntry(Tablet, CompanionInteractionMode.Follow, Now)], null, null),
        new WorkspaceAggregate(AggregateCursor.Empty, new WorkspaceProjection(
            WorkspaceKind.Raid,
            "customs",
            "ground",
            new WorkspaceViewport(new MapCoordinate("customs", "ground", CoordinateSpaceKind.World, "tarkov-dev-1", 10, 2, 20), 1),
            null,
            [],
            [],
            null,
            [],
            ["extracts"],
            [],
            null)),
        new MarkAggregate(AggregateCursor.Empty, []),
        new CaptureIntentAggregate(AggregateCursor.Empty, null),
        ProfilePreferencesAggregate.Empty);

    // What a paired tablet is granted (DesktopCompanionAuthority's member grant).
    private static AuthenticatedCommandContext TabletContext() => new(
        Tablet,
        TabletSession,
        new DeviceKeyId(Thumbprint("tablet-key")),
        "tablet-install-1",
        CompanionProtocolVersion.Current,
        CompanionSurfaceKind.TabletLandscape,
        [
            DeviceCapability.FollowDesktop,
            DeviceCapability.RequestControl,
            DeviceCapability.ShowOnDesktop,
            DeviceCapability.ManageOwnMarks,
            DeviceCapability.RequestCaptureIntent,
            DeviceCapability.ReviewCaptureResult,
        ],
        Now,
        false);

    private static AuthenticatedCommandContext DesktopContext() => AuthenticatedCommandContext.ForDesktop(
        Desktop,
        DesktopSession,
        new DeviceKeyId(Thumbprint("desktop-key")),
        "desktop-install-1",
        Now);

    private static string Thumbprint(string label) =>
        System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(label)));

    /// <summary>A surface with every optional part filled, so an absent key is a renamed or removed field.</summary>
    private static TabletMapSurface Surface() => new(
        7,
        "customs",
        "Customs",
        "customs-default",
        "tarkov-dev-1",
        new TabletMapPlan(-400, -300, 700, 250),
        new TabletMapArtwork("image/png", new string('a', 64), 4096, 2048),
        [new TabletMapAttribution("Map by the-hideout", "https://example.test/map.svg", "https://example.test/licence", new string('b', 64), "Reviewed")],
        ["ground", "second"],
        [new TabletMapLayer("extracts", "Extracts", 10, true)],
        [new TabletMapObject("ping-1", "extracts", "Ping", "UserAuthored", "Here", null, [10, 20], ["ground"], 90, false, false, Now.AddSeconds(45), "#E69F00")],
        new TabletMapView("ground", 150, -25, 2, "Landmark", "extract-zb-1011"),
        new TabletSearch("salewa", [new TabletSearchResult("item-salewa", "Salewa first aid kit", "Salewa", 31_747, 15_090, false, "Medical", "1x2", 15_873, "Therapist", "https://example.test/wiki/Salewa")]),
        null,
        Now,
        Now,
        new TabletLootResult("scan-1", Now, "Loot Scan", "2 items", [new TabletLootRow("Salewa", "Take", "Take", "31,747 ₽", "Worth the slot")], 0),
        new TabletMapLootFilter(20_000, "PerSlot"),
        [new TabletMapChoice("customs", "Customs")],
        TabletCaptureReview.Bounded("stash", "snapshot-1", Now, "Stash scan", "2 named", "Keep 1", [new TabletReviewRow("Salewa", "Keep", "Good", "x1", "Screenshot · 93%", "Quest")]),
        TabletCaptureReview.Bounded("flea", "artifact-1", Now, "Offers for Salewa", "1 row read", null, [new TabletReviewRow("#1 best buy · ₽20,000 each", "Good buy", "Good", "1 unit", "read 97% sure", "Under the average")]));
}
