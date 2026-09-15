using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class RuntimeStateStoreTests
{
    [Fact]
    public void PublicationCopiesMutableCollectionsAndAdvancesLocalRevision()
    {
        var store = Store();
        var names = new List<string> { "masked-file.png" };

        store.Update(current => current with { RecentScreenshotNames = names });
        names.Add("late-mutation.png");

        Assert.Equal(1, store.Current.LocalRevision);
        Assert.Equal(["masked-file.png"], store.Current.RecentScreenshotNames);
        Assert.False(store.Current.RecentScreenshotNames is List<string>);
    }

    [Fact]
    public void PublicationAlsoCopiesNestedPartyEquipment()
    {
        var store = Store();
        var equipment = new List<GroupEquipmentItem> { new("item-1", "template-1", null, "slot-1") };
        var members = new List<GroupMember>
        {
            new("profile-1", 42, "member", "Usec", 20, false, true, null, equipment),
        };

        store.Update(current => current with
        {
            Squad = new(members, null, null, DateTimeOffset.UnixEpoch),
        });
        equipment.Add(new("item-2", "template-2", null, "slot-2"));
        members.Clear();

        var published = Assert.Single(store.Current.Squad.Members);
        Assert.Single(published.Equipment);
        Assert.False(published.Equipment is List<GroupEquipmentItem>);
    }

    [Fact]
    public void ThrowingSubscriberCannotFailProducerOrPreventLaterSubscriber()
    {
        var store = Store();
        var laterSubscriberCalls = 0;
        store.Changed += (_, _) => throw new InvalidOperationException("subscriber private detail");
        store.Changed += (_, _) => laterSubscriberCalls++;

        var exception = Record.Exception(() => store.Update(current => current with { DatabaseReady = true }));

        Assert.Null(exception);
        Assert.Equal(1, laterSubscriberCalls);
        Assert.True(store.Current.DatabaseReady);
        Assert.Equal(1, store.Current.Resources.StateSubscriberFaults);
        Assert.Equal(2, store.Current.LocalRevision);
    }

    [Fact]
    public void RevisionsRemainMonotonicAcrossIndependentFeaturePublications()
    {
        var store = Store();
        var revisions = new List<long>();
        store.Changed += (_, _) => revisions.Add(store.Current.LocalRevision);

        store.Update(current => current with { DatabaseReady = true });
        store.Update(current => current with { Data = current.Data with { ItemCount = 2 } });
        store.Update(current => current with { Data = current.Data with { ItemCount = 3 } });

        Assert.Equal([1L, 2L, 3L], revisions);
    }

    /// <summary>Background publishers never run a subscriber concurrently with another.</summary>
    [Fact]
    public async Task SubscribersAreNotifiedOneAtATimeAcrossPublishingThreads()
    {
        var store = Store();
        var active = 0;
        var overlaps = 0;
        var notifications = 0;
        store.Changed += (_, _) =>
        {
            if (Interlocked.Increment(ref active) > 1)
            {
                Interlocked.Increment(ref overlaps);
            }

            Thread.SpinWait(2_000);
            Interlocked.Increment(ref notifications);
            Interlocked.Decrement(ref active);
        };
        var publishers = Enumerable.Range(0, 4)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    for (var index = 0; index < 100; index++)
                    {
                        store.Update(current => current with { Data = current.Data with { ItemCount = index } });
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        await Task.WhenAll(publishers).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(0, overlaps);
        Assert.Equal(400, notifications);
        Assert.Equal(400, store.Current.LocalRevision);
    }

    private static RuntimeStateStore Store() => new(new(
        DemoMode: false,
        Offline: true,
        GameMode.Regular,
        "en",
        TimeSpan.FromHours(1),
        TimeSpan.FromSeconds(5)));
}
