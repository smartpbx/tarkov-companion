using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Saying goodbye, in the three cases where a member stops being who they were.
/// </summary>
/// <remarks>
/// The relay has served `DELETE /state/{name}` since it was written. The client called it from
/// nowhere at first, then from disposal only — so turning sharing off or renaming yourself left
/// a marker on everybody else's map for the full three-minute lifetime, apparently still in the
/// raid. A rename left two: the new name where the player is, and the old one where they were.
/// </remarks>
public sealed class GroupWithdrawTests
{
    [Fact]
    public async Task TurningSharingOffSaysSoRatherThanTimingOut()
    {
        var handler = new RecordingHandler();
        var settings = new MutableSettings();
        await using var service = Service(handler, settings, out _);

        service.Start();
        await handler.WaitForAsync(HttpMethod.Post);
        settings.Disable();

        Assert.True(await handler.WaitForAsync(HttpMethod.Delete), "Sharing went off without withdrawing.");
        Assert.Equal("/state/Clay", handler.Last(HttpMethod.Delete)!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task RenamingWithdrawsTheOldNameAndNotTheNewOne()
    {
        // The case that would be silently wrong if the DELETE were built from current settings:
        // it would remove the marker just created and leave the old one standing.
        var handler = new RecordingHandler();
        var settings = new MutableSettings();
        await using var service = Service(handler, settings, out _);

        service.Start();
        await handler.WaitForAsync(HttpMethod.Post);
        settings.Rename("Clayton");

        Assert.True(await handler.WaitForAsync(HttpMethod.Delete), "A rename left the old marker standing.");
        Assert.Equal("/state/Clay", handler.Last(HttpMethod.Delete)!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task DisposingWithdrawsWhatWasRegistered()
    {
        var handler = new RecordingHandler();
        var settings = new MutableSettings();
        var service = Service(handler, settings, out _);

        service.Start();
        await handler.WaitForAsync(HttpMethod.Post);
        await service.DisposeAsync();

        Assert.Equal("/state/Clay", handler.Last(HttpMethod.Delete)?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task NoExchangeFollowsTheGoodbyeWhenTheGoodbyeEndsAHeldExchange()
    {
        // #889: the relay's DELETE raises the room's revision, which ends the leaver's own held
        // exchange. Withdrawn before the loop stopped, that loop POSTed once more after the
        // DELETE and put the member back on everybody's map for three minutes.
        var handler = new HoldingRelay();
        var settings = new MutableSettings();
        var service = Service(handler, settings, out _);

        service.Start();
        await handler.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(400);
        await service.DisposeAsync();
        await Task.Delay(300);

        var order = handler.Order;
        var goodbye = order.IndexOf("DELETE");
        Assert.True(goodbye >= 0, "No goodbye was sent.");
        Assert.DoesNotContain("POST", order.Skip(goodbye + 1));
    }

    [Fact]
    public async Task NothingIsWithdrawnWhenNothingWasEverRegistered()
    {
        // Sharing that was never on has nobody to say goodbye to, and a DELETE here would be a
        // request made about a member that does not exist.
        var handler = new RecordingHandler();
        var settings = new MutableSettings(enabled: false);
        var service = Service(handler, settings, out _);

        service.Start();
        await Task.Delay(200);
        await service.DisposeAsync();

        Assert.Null(handler.Last(HttpMethod.Delete));
    }

    [Fact]
    public async Task TheWithdrawalCarriesTheGroupKey()
    {
        // Without it the relay cannot tell which room the member is leaving, and the marker
        // stays exactly as it would have with no request at all.
        var handler = new RecordingHandler();
        var settings = new MutableSettings();
        var service = Service(handler, settings, out _);

        service.Start();
        await handler.WaitForAsync(HttpMethod.Post);
        await service.DisposeAsync();

        var sent = handler.Last(HttpMethod.Delete);
        Assert.NotNull(sent);
        Assert.Equal("a-key-long-enough", Assert.Single(sent.Headers.GetValues("X-Group-Key")));
    }

    private static GroupSessionService Service(
        HttpMessageHandler handler,
        MutableSettings settings,
        out RuntimeStateStore store)
    {
        store = new(new(
            false,
            Offline: true,
            GameMode.Regular,
            "en",
            TimeSpan.FromHours(9),
            TimeSpan.FromMinutes(5)));
        return new(
            settings,
            store,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<GroupSessionService>.Instance);
    }

    /// <summary>Settings a test can change underneath a running service, as a person would.</summary>
    private sealed class MutableSettings(bool enabled = true) : IGroupSettingsStore
    {
        private GroupSharingSettings _settings = new(
            enabled,
            "https://relay.example.test/",
            "Clay",
            "a-key-long-enough",
            false,
            false);

        public void Disable() => _settings = _settings with { IsEnabled = false };

        public void Rename(string name) => _settings = _settings with { DisplayName = name };

        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_settings);

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A relay that holds the second exchange open until a goodbye arrives, as the real one does
    /// when the DELETE raises the room's revision, and is slow to answer that goodbye.
    /// </summary>
    private sealed class HoldingRelay : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly List<string> _order = [];
        private readonly TaskCompletionSource _goodbye = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _revision;

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Order
        {
            get
            {
                lock (_lock)
                {
                    return [.. _order];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _order.Add(request.Method.Method);
            }

            if (request.Method == HttpMethod.Delete)
            {
                _goodbye.TrySetResult();
                await Task.Delay(400, CancellationToken.None);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.RequestUri!.Query.Contains("wait=", StringComparison.Ordinal))
            {
                Held.TrySetResult();
                await _goodbye.Task.WaitAsync(cancellationToken);
            }

            var revision = Interlocked.Increment(ref _revision);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"room":"r","members":[],"protocol":1,"revision":{{revision}}}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    /// <summary>Remembers every request, so a test can ask what was actually sent.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly List<HttpRequestMessage> _sent = [];

        public HttpRequestMessage? Last(HttpMethod method)
        {
            lock (_lock)
            {
                return _sent.LastOrDefault(request => request.Method == method);
            }
        }

        /// <summary>Polls rather than sleeping a fixed time, so this is not a race.</summary>
        public async Task<bool> WaitForAsync(HttpMethod method)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (Last(method) is not null)
                {
                    return true;
                }

                await Task.Delay(25);
            }

            return false;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                _sent.Add(request);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"members":[],"protocol":1}""",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
