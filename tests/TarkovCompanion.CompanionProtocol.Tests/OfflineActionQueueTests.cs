using static TarkovCompanion.CompanionProtocol.Tests.ProtocolTestData;

namespace TarkovCompanion.CompanionProtocol.Tests;

public sealed class OfflineActionQueueTests
{
    [Fact]
    public void TheQueueHoldsAtMostSixtyFourUniqueExplicitActions()
    {
        var queue = OfflineActionQueue.Empty;
        for (var index = 0; index < ProtocolBounds.MaxOfflineQueueItems; index++)
        {
            queue = queue.Enqueue(MarkAction(index, Now));
        }

        Assert.True(queue.IsFull);
        Assert.Equal(ProtocolBounds.MaxOfflineQueueItems, queue.Actions.Count);
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(MarkAction(999, Now)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OfflineActionQueue(
            Enumerable.Range(0, ProtocolBounds.MaxOfflineQueueItems + 1).Select(index => (OfflineAction)MarkAction(index, Now)).ToArray()));
        Assert.Throws<ArgumentException>(() => queue.Remove(Command(0)).Enqueue(MarkAction(1, Now)));
        Assert.Equal(ProtocolBounds.MaxOfflineQueueItems - 1, queue.Remove(Command(0)).Actions.Count);
    }

    [Fact]
    public void OnlyShowOnDesktopMarkMutationAndCaptureRequestHaveOfflineDraftTypes()
    {
        var draftTypes = typeof(OfflineAction).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(OfflineAction).IsAssignableFrom(type))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal);
        var eligible = typeof(CompanionCommand).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(CompanionCommand).IsAssignableFrom(type))
            .Where(type => type.GetConstructors().Any(constructor =>
                constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(OfflineQueuePreview))))
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(
            new[] { "DeleteMarkOfflineAction", "RequestCaptureIntentOfflineAction", "ShowOnDesktopOfflineAction", "UpsertMarkOfflineAction" },
            draftTypes);
        Assert.Equal(
            new[] { "DeleteMarkCommand", "RequestCaptureIntentCommand", "ShowOnDesktopCommand", "UpsertMarkCommand" },
            eligible);
    }

    [Fact]
    public void ExpiredActionsArePrunedAndCanNeverBeSubmitted()
    {
        var queue = OfflineActionQueue.Empty
            .Enqueue(MarkAction(1, Now))
            .Enqueue(MarkAction(2, Now.AddMinutes(10)));

        var pruned = queue.PruneExpired(Now.Add(ProtocolBounds.OfflineQueueLifetime));

        Assert.Equal(Command(2), Assert.Single(pruned.Actions).ActionId);
        Assert.Throws<InvalidOperationException>(() => queue.PrepareSubmission(Command(1), InitialState(), Now.Add(ProtocolBounds.OfflineQueueLifetime)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UpsertMarkOfflineAction(Command(3), Now, Now.AddMinutes(16), Mark(3), 0, Draft()));
    }

    [Fact]
    public void ASubmissionAppliesOnlyAgainstTheStateTheUserPreviewed()
    {
        var reconnected = Apply(InitialState(), Upsert(10, 1, 0, Now.AddMinutes(1), mark: 50), OtherContext(Now.AddMinutes(1))).State;
        var queue = OfflineActionQueue.Empty
            .Enqueue(MarkAction(1, Now))
            .Enqueue(MarkAction(2, Now));
        var previewedAt = Now.AddMinutes(2);

        var first = queue.PrepareSubmission(Command(1), reconnected, previewedAt);
        var applied = Apply(reconnected, first, TabletContext(previewedAt));
        var stalePreview = queue.PrepareSubmission(Command(2), reconnected, previewedAt);
        var rejected = Apply(applied.State, stalePreview, TabletContext(previewedAt));
        var represented = queue.PrepareSubmission(Command(2), applied.State, previewedAt.AddSeconds(5));
        var resubmitted = Apply(applied.State, represented, TabletContext(previewedAt.AddSeconds(5)));
        var retried = Apply(resubmitted.State, represented, TabletContext(previewedAt.AddSeconds(6)));

        Assert.Equal(new AggregateRevision(2), first.RequestedRevision);
        Assert.Equal(CommandDisposition.Applied, applied.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.RequiresPreview, rejected.Acknowledgement.Disposition);
        Assert.Equal(CommandDisposition.Applied, resubmitted.Acknowledgement.Disposition);
        Assert.Equal("duplicate-command", retried.Acknowledgement.Code);
        Assert.Equal(3, resubmitted.State.Marks.Marks.Count);
    }

    [Fact]
    public void AnOfflineSubmissionFromAnotherAuthorityLifetimeRequiresPreview()
    {
        var queue = OfflineActionQueue.Empty.Enqueue(new ShowOnDesktopOfflineAction(Command(20), Now, Now.AddMinutes(15), Projection("woods")));
        var independent = Apply(InitialState(), SetMode(21, 1, Now, CompanionInteractionMode.Independent), TabletContext()).State;
        var previewedElsewhere = new CanonicalCompanionState(
            new AuthorityEpoch(Guid.Parse("30000000-0000-0000-0000-000000000099")),
            independent.GlobalRevision,
            independent.DesktopDeviceId,
            independent.DeviceModes,
            independent.Workspace,
            independent.Marks,
            independent.CaptureIntent);
        var command = queue.PrepareSubmission(Command(20), previewedElsewhere, Now.AddMinutes(1));

        var result = Apply(independent, command, TabletContext(Now.AddMinutes(1)));

        Assert.Equal(CommandDisposition.RequiresPreview, result.Acknowledgement.Disposition);
        Assert.Equal("offline-action-needs-current-preview", result.Acknowledgement.Code);
    }

    private static UpsertMarkOfflineAction MarkAction(int number, DateTimeOffset queuedUtc) => new(
        Command(number),
        queuedUtc,
        queuedUtc.Add(ProtocolBounds.OfflineQueueLifetime),
        Mark(number),
        0,
        Draft(number));
}
