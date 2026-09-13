using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// The first tests this service has ever had.
/// </summary>
/// <remarks>
/// It is the one component that transmits anything, it had no per-request timeout, and one
/// failed exchange used to clear the whole group off the map. None of that was covered.
/// </remarks>
public sealed class GroupSessionServiceTests
{
    /// <summary>
    /// A relay that accepts the connection and never answers does not end sharing.
    /// </summary>
    /// <remarks>
    /// This is the test the roadmap asked for first, because it is the one that catches the
    /// trap rather than the bug. The shared HttpClient has an infinite timeout, so adding a
    /// per-request CancelAfter makes the failure a TaskCanceledException — which *is* an
    /// OperationCanceledException, so the old `when (exception is not OperationCanceledException)`
    /// filter would have let it escape, fault the worker and end sharing silently for the rest
    /// of the session. Adding the timeout without also fixing the filter is worse than having
    /// no timeout at all, and only a hanging handler shows the difference.
    /// </remarks>
    [Fact]
    public async Task ARelayThatNeverAnswersIsGivenUpOnWithoutEndingSharing()
    {
        var handler = new StubHandler(_ => new TaskCompletionSource<HttpResponseMessage>().Task);
        await using var service = Service(handler, out var store);

        service.Start();
        var published = await WaitForDetailAsync(store, detail => detail.Contains("Sharing", StringComparison.Ordinal) is false
            && detail.Length > 0 && detail != "Not sharing");

        Assert.True(published, "The loop should have reported a failure rather than hanging forever.");
    }

    /// <summary>One bad exchange does not take the squad off the map.</summary>
    /// <remarks>
    /// Publishing GroupSnapshot.Off empties Members, Waypoints and Pings, and ApplySnapshot
    /// then clears every squadmate and every mark until the next tick five seconds later. A
    /// single dropped packet made the whole group disappear and come back.
    /// </remarks>
    [Fact]
    public async Task OneFailedExchangeKeepsTheGroupOnScreenAndSaysHowOldItIs()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return calls == 1
                ? Task.FromResult(Json("""{"members":[{"name":"Geo","mapId":"bigmap","x":1,"y":2,"z":3}]}"""))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        });
        await using var service = Service(handler, out var store);

        service.Start();
        await WaitForDetailAsync(store, detail => detail.Contains("last heard", StringComparison.Ordinal));

        var group = store.Current.Group;
        Assert.NotEmpty(group.Members);
        Assert.Equal("Geo", group.Members[0].Name);
        Assert.NotNull(group.StaleSince);
        Assert.Contains("last heard", group.Detail, StringComparison.Ordinal);
    }

    /// <summary>A wrong key is an answer, not a bad moment.</summary>
    [Fact]
    public async Task AWrongKeyTurnsSharingOffRatherThanGoingStale()
    {
        var handler = new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await using var service = Service(handler, out var store);

        service.Start();
        await WaitForDetailAsync(store, detail => detail == "Wrong group key");

        Assert.Equal("Wrong group key", store.Current.Group.Detail);
        Assert.Empty(store.Current.Group.Members);
        Assert.Null(store.Current.Group.StaleSince);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(42, "42s")]
    [InlineData(59, "59s")]
    [InlineData(61, "1m 1s")]
    [InlineData(185, "3m 5s")]
    public void HowLongAgoReadsAsSomebodyWouldSayIt(int seconds, string expected) =>
        Assert.Equal(expected, GroupSessionService.Ago(TimeSpan.FromSeconds(seconds)));

    private static GroupSessionService Service(StubHandler handler, out RuntimeStateStore store)
    {
        store = new(new(
            false,
            Offline: true,
            GameMode.Regular,
            "en",
            TimeSpan.FromHours(9),
            TimeSpan.FromMinutes(5)));
        return new(
            new StubSettings(),
            store,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    /// <summary>Polls the store rather than sleeping a fixed time, so the test is not a race.</summary>
    private static async Task<bool> WaitForDetailAsync(RuntimeStateStore store, Func<string, bool> matches)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (matches(store.Current.Group.Detail))
            {
                return true;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return false;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var pending = respond(request);
            // A handler that never completes must still observe cancellation, which is what a
            // real socket read does when the request's token fires.
            var cancelled = new TaskCompletionSource<HttpResponseMessage>();
            await using var registration = cancellationToken.Register(() =>
                cancelled.TrySetException(new TaskCanceledException())).ConfigureAwait(false);
            return await await Task.WhenAny(pending, cancelled.Task).ConfigureAwait(false);
        }
    }

    private sealed class StubSettings : IGroupSettingsStore
    {
        private static readonly GroupSharingSettings Settings = new(
            true,
            "https://relay.example.test/",
            "Clay",
            "a-key-long-enough",
            false,
            false);

        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Settings);

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
