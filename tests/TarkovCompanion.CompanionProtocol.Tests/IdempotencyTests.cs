using System.Text;
using System.Text.Json.Nodes;
using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class IdempotencyTests
{
    [Fact]
    public void FingerprintIsDeterministicAndIndependentOfJsonPropertyOrder()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Golden", "commands"), "*.json"))
        {
            var original = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(File.ReadAllBytes(file));
            var reordered = Reverse(JsonNode.Parse(File.ReadAllBytes(file)))!;
            var reparsed = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(Encoding.UTF8.GetBytes(reordered.ToJsonString()));

            Assert.Equal(
                CanonicalCommandFingerprint.Compute(original.Command),
                CanonicalCommandFingerprint.Compute(reparsed.Command));
        }
    }

    [Fact]
    public void FingerprintCoversTheActionButNotTheDeliveryMetadataARetryRefreshes()
    {
        var golden = GoldenNode("commands/upsert-mark.json");
        var original = CanonicalCommandFingerprint.Compute(
            CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(Encoding.UTF8.GetBytes(golden.ToJsonString())).Command);
        var deliveryOnly = new Action<JsonObject>[]
        {
            command => command["commandId"]!["value"] = "40000000-0000-4000-8000-000000000999",
            command => command["requestedRevision"]!["value"] = 2,
            command => command["issuedUtc"] = "2026-09-14T20:00:01+00:00",
            command => command["expiresUtc"] = "2026-09-14T20:00:59+00:00",
        };
        foreach (var mutate in deliveryOnly)
        {
            var node = GoldenNode("commands/upsert-mark.json");
            mutate(node["command"]!.AsObject());
            var command = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(Encoding.UTF8.GetBytes(node.ToJsonString())).Command;
            Assert.Equal(original, CanonicalCommandFingerprint.Compute(command));
        }

        Assert.Equal(
            new[] { "commandId", "expiresUtc", "issuedUtc", "offlineQueuePreview", "requestedRevision" },
            CanonicalCommandFingerprint.DeliveryMetadataMembers.Order(StringComparer.Ordinal).ToArray());
        var mutations = new Action<JsonObject>[]
        {
            command => command["type"] = "deleteMark",
            command => command["markId"]!["value"] = "50000000-0000-4000-8000-000000000999",
            command => command["expectedMarkRevision"] = 3,
            command => command["mark"]!["kind"] = "Waypoint",
            command => command["mark"]!["scope"] = "Private",
            command => command["mark"]!["state"]!["label"] = "renamed",
            command => command["mark"]!["color"] = "#FF8801",
            command => command["mark"]!["state"]!["x"] = 10.25,
            command => command["mark"]!["state"]!["floorId"] = null,
            command => command["mark"]!["height"] = 3,
            command => command["mark"]!["projectionVersion"] = "tarkov-dev-2",
        };
        var fingerprints = new HashSet<CommandFingerprint> { original };

        foreach (var mutate in mutations)
        {
            var node = GoldenNode("commands/upsert-mark.json");
            mutate(node["command"]!.AsObject());
            if (node["command"]!["type"]!.GetValue<string>() == "deleteMark")
            {
                node["command"]!.AsObject().Remove("mark");
                node["command"]!["expectedMarkRevision"] = 1;
            }

            var command = CompanionProtocolJson.Deserialize<ClientCommandEnvelope>(Encoding.UTF8.GetBytes(node.ToJsonString())).Command;
            Assert.True(fingerprints.Add(CanonicalCommandFingerprint.Compute(command)), node.ToJsonString());
        }
    }

    [Fact]
    public void AnExactRetryIsADuplicateEvenAfterExpiryWhileItsChangeOccupiesTheCursor()
    {
        var command = Upsert(1, 1, 0, Now);
        var applied = Apply(InitialState(), command, TabletContext());
        var lateRetry = Apply(applied.State, command, TabletContext(Now.AddMinutes(10)));

        Assert.Equal(CommandDisposition.Applied, lateRetry.Acknowledgement.Disposition);
        Assert.Equal("duplicate-command", lateRetry.Acknowledgement.Code);
        Assert.Same(applied.State, lateRetry.State);
    }

    [Fact]
    public void ARePreviewedOfflineRetryAfterALostAcknowledgementIsTheDuplicateNotASecondApply()
    {
        var queue = OfflineActionQueue.Empty.Enqueue(new ShowOnDesktopOfflineAction(Command(91), Now, Now.AddMinutes(15), Projection("woods")));
        var state = Apply(InitialState(), SetMode(90, 1, Now, CompanionInteractionMode.Independent), TabletContext()).State;
        var previewedAt = Now.AddSeconds(1);
        var show = queue.PrepareSubmission(Command(91), state, previewedAt);
        var applied = Apply(state, show, TabletContext(previewedAt));
        var changedAt = Now.AddSeconds(2);
        var desktopChange = DesktopCanonicalStateMachine.Apply(
            applied.State,
            Envelope(new UpdateDesktopWorkspaceCommand(Command(92), new AggregateRevision(2), changedAt, changedAt.AddMinutes(1), Projection("customs")), DesktopSession),
            DesktopContext(changedAt));

        // The acknowledgement was lost, so the tablet re-previews the same draft against newer state.
        var retryAt = Now.AddSeconds(5);
        var rePreviewed = queue.PrepareSubmission(Command(91), desktopChange.State, retryAt);
        var retry = Apply(desktopChange.State, rePreviewed, TabletContext(retryAt));

        Assert.Equal(CommandDisposition.Applied, applied.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.Applied, desktopChange.Acknowledgement.Disposition);
        Assert.NotEqual(show.RequestedRevision, rePreviewed.RequestedRevision);
        Assert.NotEqual(show.OfflineQueuePreview, rePreviewed.OfflineQueuePreview);
        Assert.Equal(CommandDisposition.Applied, retry.Acknowledgement.Disposition);
        Assert.Equal("duplicate-command", retry.Acknowledgement.Code);
        Assert.Equal(1, retry.Acknowledgement.RequestedRevision.Value);
        Assert.Equal(1, retry.Acknowledgement.AppliedRevision.Value);
        Assert.Equal(Command(91), retry.Acknowledgement.AppliedChangeId);
        Assert.Same(desktopChange.State, retry.State);
        Assert.Null(retry.Update);
        Assert.Equal("customs", retry.State.Workspace.Projection.MapId);
    }

    [Fact]
    public void AMutatedPayloadUnderARetainedCommandIdIsRejectedWithoutTouchingState()
    {
        var original = Upsert(1, 1, 0, Now);
        var applied = Apply(InitialState(), original, TabletContext());
        var mutatedSameRevision = Upsert(1, 1, 0, Now, x: 99);
        var mutatedNextRevision = Upsert(1, 2, 1, Now, x: 99);
        var otherDevice = Apply(applied.State, original, OtherContext());

        foreach (var reduction in new[]
                 {
                     Apply(applied.State, mutatedSameRevision, TabletContext()),
                     Apply(applied.State, mutatedNextRevision, TabletContext()),
                     otherDevice,
                 })
        {
            Assert.Equal(CommandDisposition.RejectedCommandIdReuse, reduction.Acknowledgement.Disposition);
            Assert.Same(applied.State, reduction.State);
            Assert.Null(reduction.Update);

            // The rejection describes no revision, so it can never be read as naming this command,
            // or the reused identifier's original change, as the applied change.
            Assert.Null(reduction.Acknowledgement.CanonicalState);
            Assert.Equal(0, reduction.Acknowledgement.AppliedRevision.Value);
            Assert.Null(reduction.Acknowledgement.AppliedChangeId);
            Assert.Equal(1, reduction.State.Marks.Marks[0].State.X);
        }
    }

    [Fact]
    public void VersionEightIdentifiersAreReservedForDesktopMaintenance()
    {
        var reserved = new UpsertMarkCommand(
            new CommandId(Guid.Parse("40000000-0000-8000-8000-000000000001")),
            new AggregateRevision(1),
            Now,
            Now.AddMinutes(1),
            Mark(1),
            0,
            Draft());

        var result = Apply(InitialState(), reserved, TabletContext());

        Assert.Equal(CommandDisposition.RejectedCommandIdReuse, result.Acknowledgement.Disposition);
        Assert.Equal("command-id-reserved", result.Acknowledgement.Code);
    }

    [Fact]
    public void ReceiptsStayBoundedTheNewestChangeRemainsRecognizableAndAnEvictedRetryNeverAppliesTwice()
    {
        var state = InitialState();
        var commands = new List<UpdateDesktopWorkspaceCommand>();
        for (var index = 0; index < ProtocolBounds.MaxRecentCommands + 44; index++)
        {
            var at = Now.AddMilliseconds(index);
            var command = new UpdateDesktopWorkspaceCommand(
                Command(10_000 + index),
                new AggregateRevision(index + 1),
                at,
                at.AddMinutes(1),
                Projection($"map-{index}"));
            var result = DesktopCanonicalStateMachine.Apply(state, Envelope(command, DesktopSession), DesktopContext(at));
            Assert.Equal(CommandDisposition.Applied, result.Acknowledgement.Disposition);
            state = result.State;
            commands.Add(command);
        }

        var retryAt = Now.AddSeconds(1);
        var newest = DesktopCanonicalStateMachine.Apply(state, Envelope(commands[^1], DesktopSession), DesktopContext(retryAt));
        var evicted = DesktopCanonicalStateMachine.Apply(state, Envelope(commands[0], DesktopSession), DesktopContext(retryAt));

        // The evicted retry names an older revision here, but a re-previewed draft would request the
        // next one; the receipt horizon refuses both instead of evaluating them as new commands.
        var evictedDraftRetry = new UpdateDesktopWorkspaceCommand(
            commands[0].CommandId,
            state.Workspace.Cursor.Revision.Next(),
            commands[0].IssuedUtc,
            commands[0].ExpiresUtc,
            commands[0].Projection);
        var redrafted = DesktopCanonicalStateMachine.Apply(state, Envelope(evictedDraftRetry, DesktopSession), DesktopContext(retryAt));
        var fresh = DesktopCanonicalStateMachine.Apply(
            state,
            Envelope(new UpdateDesktopWorkspaceCommand(Command(20_000), state.Workspace.Cursor.Revision.Next(), retryAt, retryAt.AddMinutes(1), Projection("fresh")), DesktopSession),
            DesktopContext(retryAt));

        Assert.Equal(ProtocolBounds.MaxRecentCommands, state.RecentCommands.Count);
        Assert.Equal(commands[43].IssuedUtc, state.ReceiptHorizonUtc);
        Assert.Equal("duplicate-command", newest.Acknowledgement.Code);
        Assert.All(new[] { evicted, redrafted }, reduction =>
        {
            Assert.Equal(CommandDisposition.RejectedInvalidState, reduction.Acknowledgement.Disposition);
            Assert.Equal("idempotency-window-exceeded", reduction.Acknowledgement.Code);
            Assert.Null(reduction.Acknowledgement.AppliedChangeId);
            Assert.Same(state, reduction.State);
        });
        Assert.Equal(CommandDisposition.Applied, fresh.Acknowledgement.Disposition);
    }

    private static JsonNode? Reverse(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.Reverse().Select(property =>
            KeyValuePair.Create(property.Key, Reverse(property.Value?.DeepClone())))),
        JsonArray array => new JsonArray(array.Select(item => Reverse(item?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };
}
