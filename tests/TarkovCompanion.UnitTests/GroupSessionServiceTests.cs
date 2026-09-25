using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.Localization;
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
        Assert.Contains("last heard", Words(group), StringComparison.Ordinal);
    }

    /// <summary>
    /// A squadmate's quest ids arrive as ids, for the receiver to place.
    /// </summary>
    /// <remarks>
    /// The names beside them are what a person reads, and they were all the exchange carried:
    /// a squadmate's list could be printed and nothing else. The id is the key the local quest
    /// catalog is indexed by, so with it the receiver can ask its own catalog which maps the
    /// group's quests point at and rank tonight's options by where they overlap.
    /// </remarks>
    [Fact]
    public async Task ASquadmatesQuestIdsSurviveTheExchange()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(
            """{"members":[{"name":"Geo","quests":["Debut"],"questIds":["5936d90786f7742b1420ba5b"]}]}""")));
        await using var service = Service(handler, out var store);

        service.Start();
        await WaitForDetailAsync(store, _ => store.Current.Group.Members.Count > 0);

        var member = store.Current.Group.Members.Single();
        Assert.Equal(["Debut"], member.Quests);
        Assert.Equal(["5936d90786f7742b1420ba5b"], member.QuestIds);
    }

    /// <summary>
    /// A squadmate whose companion predates the ids is read rather than refused.
    /// </summary>
    [Fact]
    public async Task AMemberWhoSendsNoQuestIdsIsStillRead()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(
            """{"members":[{"name":"Geo","quests":["Debut"]}]}""")));
        await using var service = Service(handler, out var store);

        service.Start();
        await WaitForDetailAsync(store, _ => store.Current.Group.Members.Count > 0);

        Assert.Empty(store.Current.Group.Members.Single().QuestIds);
    }

    /// <summary>A refused key is an answer, not a bad moment — and it says which rule it broke.</summary>
    /// <remarks>
    /// This asserted "Wrong group key" until the message was corrected. A 401 from this relay
    /// means the key failed a length test; it cannot mean the key is wrong, because the key is
    /// the room, and a different key is a different room that answers 200 with nobody in it.
    /// </remarks>
    [Fact]
    public async Task ARefusedKeyTurnsSharingOffRatherThanGoingStale()
    {
        var handler = new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        await using var service = Service(handler, out var store);

        service.Start();
        await WaitForDetailAsync(store, detail => detail.Contains("must be between", StringComparison.Ordinal));

        Assert.Equal(
            $"The group key must be between {GroupKeyLimits.Minimum} and {GroupKeyLimits.Maximum} characters",
            Words(store.Current.Group));
        Assert.Empty(store.Current.Group.Members);
        Assert.Null(store.Current.Group.StaleSince);
    }

    /// <summary>
    /// A report over the relay's size budget is trimmed and sent rather than refused whole.
    /// </summary>
    [Fact]
    public async Task AnOversizedReportIsTrimmedToFitBeforeItIsSent()
    {
        string? sentBody = null;
        var handler = new StubHandler(async request =>
        {
            sentBody = await request.Content!.ReadAsStringAsync();
            return Json("""{"reference":"abc123456789","detail":"Sent."}""");
        });
        await using var service = Service(handler, out _);

        // Far past the relay's 64 KiB budget.
        var huge = new string('x', 200_000);
        var result = await service.ReportProblemAsync(huge, CancellationToken.None);

        Assert.NotNull(sentBody);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(sentBody!) <= 64 * 1024,
            "The body sent to the relay should fit within its size budget.");
        Assert.Contains("trimmed", sentBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sent", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// A 413 is Kestrel's own refusal before the endpoint ran; its body is not a detail worth
    /// showing, so the player is told plainly instead of "the relay refused it (413)".
    /// </summary>
    [Fact]
    public async Task A413IsExplainedPlainlyRatherThanShownAsARawStatusCode()
    {
        var handler = new StubHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)));
        await using var service = Service(handler, out _);

        var result = await service.ReportProblemAsync("a small report", CancellationToken.None);

        Assert.Contains("too large", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("413", result, StringComparison.Ordinal);
    }

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

    /// <summary>[#314] The status line in the English the player reads.</summary>
    private static string Words(GroupSnapshot group)
    {
        using var scope = UiText.Scope(UiText.Create("en", _ => { }));
        return SetupText.GroupStatus(group);
    }

    /// <summary>Polls the store rather than sleeping a fixed time, so the test is not a race.</summary>
    private static async Task<bool> WaitForDetailAsync(RuntimeStateStore store, Func<string, bool> matches)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (matches(Words(store.Current.Group)))
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
