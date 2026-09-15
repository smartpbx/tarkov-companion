using System.Reflection;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class ProtocolPropertyTests
{
    [Fact]
    public void RandomReorderedCommandsNeverRegressCanonicalRevisions()
    {
        var random = new Random(276);
        var state = InitialState();
        var nextRequested = 1L;

        for (var iteration = 0; iteration < 2_000; iteration++)
        {
            var requested = random.NextDouble() < 0.65
                ? nextRequested
                : random.NextInt64(1, nextRequested + 3);
            var now = Now.AddMilliseconds(iteration);
            var command = new UpsertMarkCommand(
                Command(1_000 + iteration),
                new AggregateRevision(requested),
                now,
                now.AddSeconds(30),
                Mark(1_000 + iteration),
                0,
                new MapMarkDraft(
                    MapMarkKind.Waypoint,
                    MapMarkScope.PairedDevice,
                    new TarkovCompanion.Core.Abstractions.V2.MapMarkState("customs", null, random.NextDouble(), random.NextDouble(), null, null),
                    CoordinateSpaceKind.World,
                    "v1",
                    null,
                    "#00AACC"));
            var reduction = Apply(state, command, TabletContext(now));

            Assert.True(reduction.State.GlobalRevision.Value >= state.GlobalRevision.Value);
            Assert.True(reduction.State.Marks.Cursor.Revision.Value >= state.Marks.Cursor.Revision.Value);
            Assert.InRange(reduction.State.Marks.Marks.Count, 0, ProtocolBounds.MaxMarks);
            if (reduction.Acknowledgement.Disposition == CommandDisposition.Applied)
            {
                nextRequested++;
            }

            state = reduction.State;
        }
    }

    [Fact]
    public void CommandUpdateMessageActionAndOfflineUnionsAreClosedToKnownSealedTypes()
    {
        Assert.Equal(14, ConcreteSubtypes<CompanionCommand>().Length);
        Assert.Equal(4, ConcreteSubtypes<CanonicalUpdate>().Length);
        Assert.Equal(4, ConcreteSubtypes<ServerMessage>().Length);
        Assert.Equal(5, ConcreteSubtypes<WorkspaceAction>().Length);
        Assert.Equal(4, ConcreteSubtypes<OfflineAction>().Length);
        Assert.All(
            ConcreteSubtypes<CompanionCommand>()
                .Concat(ConcreteSubtypes<CanonicalUpdate>())
                .Concat(ConcreteSubtypes<ServerMessage>())
                .Concat(ConcreteSubtypes<WorkspaceAction>())
                .Concat(ConcreteSubtypes<OfflineAction>()),
            type => Assert.True(type.IsSealed, type.Name));
        foreach (var union in new[] { typeof(CompanionCommand), typeof(CanonicalUpdate), typeof(ServerMessage), typeof(WorkspaceAction), typeof(OfflineAction) })
        {
            Assert.All(
                union.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != union)),
                constructor => Assert.True(constructor.IsFamilyAndAssembly, $"{union.Name} cannot be extended outside the protocol assembly."));
        }
    }

    [Fact]
    public void PairedProtocolCannotSerializeAsAGroupMemberOrCarryProhibitedCapabilities()
    {
        var publicNames = typeof(CompanionCommand).Assembly.GetExportedTypes()
            .SelectMany(type =>
                new[] { type.Name }
                    .Concat(type.GetProperties().Select(property => property.Name))
                    .Concat(type.IsEnum ? Enum.GetNames(type) : []))
            .ToArray();
        var prohibited = new[]
        {
            "SquadMember",
            "PlayerPosition",
            "EnemyPosition",
            "GameMemory",
            "PacketCapture",
            "InputInjection",
            "GameOverlay",
            "Aim",
            "CombatAutomation",
        };

        Assert.All(prohibited, term => Assert.DoesNotContain(
            publicNames,
            name => name.Contains(term, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(
            typeof(CompanionCommand).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.Contains("GroupServer", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void ClientRootsCannotAssertServerIdentityTimeOrOrderAndRelayFramesCarryNoStateVocabulary()
    {
        var serverOnly = new[] { "AuthenticatedOriginDeviceId", "OriginDeviceId", "DeviceId", "ServerUtc", "DeliverySequence", "Capabilities", "Role" };
        foreach (var clientRoot in new[] { typeof(ClientCommandEnvelope), typeof(ClientDeliveryAcknowledgement), typeof(ReconnectRequest) })
        {
            Assert.DoesNotContain(clientRoot.GetProperties(), property => serverOnly.Contains(property.Name, StringComparer.Ordinal));
        }

        var relayVocabulary = new[] { "Map", "Floor", "Mark", "Capture", "Coordinate", "Selection", "Workspace", "Name", "Label", "Search" };
        Assert.DoesNotContain(
            typeof(OpaqueRelayFrame).GetProperties(),
            property => relayVocabulary.Any(word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(
            typeof(PairingRequest).GetProperties(),
            property => property.PropertyType == typeof(string) && property.Name.Contains("Name", StringComparison.Ordinal));
    }

    [Fact]
    public void PairedCaptureIntentsAreTheClosedCoreScanIntentSetWithoutFlea()
    {
        Assert.Equal(
            new[]
            {
                "Auto",
                "Loot",
                "Stash",
                "Ammo",
                "Keys",
                "QuestItems",
                "ExtractsAndMap",
                "HealthAndCharacter",
            },
            PairedScanIntents.Allowed.Select(intent => intent.ToString()).ToArray());
    }

    private static Type[] ConcreteSubtypes<TBase>() =>
        typeof(TBase).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(TBase).IsAssignableFrom(type))
            .ToArray();
}
