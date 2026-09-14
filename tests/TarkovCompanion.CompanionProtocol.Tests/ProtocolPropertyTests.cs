using System.Reflection;
using TarkovCompanion.Core.Abstractions.V2;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class ProtocolPropertyTests
{
    [Fact]
    public void RandomReorderedCommandsNeverRegressCanonicalRevisions()
    {
        var random = new Random(276);
        var state = ProtocolTestData.InitialState();
        var observedGlobal = state.GlobalRevision.Value;
        var observedMarkRevision = state.Marks.Cursor.Revision.Value;
        var nextRequested = 1L;

        for (var iteration = 0; iteration < 2_000; iteration++)
        {
            var requested = random.NextDouble() < 0.65
                ? nextRequested
                : random.NextInt64(1, nextRequested + 3);
            var now = ProtocolTestData.Now.AddMilliseconds(iteration);
            var command = new UpsertMarkCommand(
                ProtocolTestData.Command(1_000 + iteration),
                new AggregateRevision(requested),
                now,
                now.AddSeconds(30),
                ProtocolTestData.Mark(1_000 + iteration),
                0,
                new MapMarkDraft(
                    MapMarkKind.Waypoint,
                    MapMarkScope.PairedDevice,
                    new MapCoordinate("customs", null, CoordinateSpaceKind.World, "v1", random.NextDouble(), null, random.NextDouble()),
                    null,
                    "#00AACC",
                    null));
            var reduction = DesktopCanonicalStateMachine.Apply(
                state,
                ProtocolTestData.Envelope(command),
                ProtocolTestData.TabletContext(now));

            Assert.True(reduction.State.GlobalRevision.Value >= observedGlobal);
            Assert.True(reduction.State.Marks.Cursor.Revision.Value >= observedMarkRevision);
            Assert.True(reduction.State.Marks.Marks.Count <= ProtocolBounds.MaxMarks);
            if (reduction.Acknowledgement.Disposition == CommandDisposition.Applied)
            {
                nextRequested++;
            }

            state = reduction.State;
            observedGlobal = state.GlobalRevision.Value;
            observedMarkRevision = state.Marks.Cursor.Revision.Value;
        }
    }

    [Fact]
    public void CommandAndUpdateUnionsAreClosedToKnownSealedTypes()
    {
        var assembly = typeof(CompanionCommand).Assembly;
        var commands = assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(CompanionCommand).IsAssignableFrom(type))
            .ToArray();
        var updates = assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(CanonicalUpdate).IsAssignableFrom(type))
            .ToArray();

        Assert.Equal(13, commands.Length);
        Assert.Equal(4, updates.Length);
        Assert.All(commands, type => Assert.True(type.IsSealed));
        Assert.All(updates, type => Assert.True(type.IsSealed));
        var stateConstructor = Assert.Single(
            typeof(CompanionCommand).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic),
            constructor => constructor.GetParameters().Length == 5);
        Assert.True(stateConstructor.IsFamilyAndAssembly);
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
        Assert.Null(typeof(ClientCommandEnvelope).GetProperty("OriginDeviceId"));
        Assert.NotNull(typeof(ServerEnvelope).GetProperty(nameof(ServerEnvelope.AuthenticatedOriginDeviceId)));
        Assert.DoesNotContain(
            typeof(CompanionCommand).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name?.Contains("GroupServer", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void CapturePurposeSetIsClosedAndComplete()
    {
        Assert.Equal(
            new[]
            {
                "LootDecision",
                "FullStash",
                "Ammo",
                "Keys",
                "QuestAndFutureQuestItems",
                "MapAndExtracts",
                "HealthAndCharacter",
                "AutoDetect",
            },
            Enum.GetNames<ContextualCapturePurpose>());
    }
}
