using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;

namespace TarkovCompanion.UnitTests.RelayDeviceSecurity;

/// <summary>
/// [#553] What lets a desktop register: the group key, checked as the group routes check it, and
/// its own key. Never the operator's admin key.
/// </summary>
public sealed class RelayDesktopRegistrationRouteTests
{
    private const string GroupKeyOfTheRoom = "the-friends-group-key";

    [Fact]
    public async Task ADesktopRegistersWithItsGroupKeyAndIsRefusedWithoutOne()
    {
        await using var relay = await Relay.StartAsync();
        using var signer = new Signer();

        var noKey = await relay.RegisterAsync(signer, groupKey: null);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        // The operator's admin key is not a group key and opens nothing here.
        var adminOnly = await relay.RegisterAsync(signer, groupKey: null, adminKey: Relay.AdminKey);
        Assert.Equal(HttpStatusCode.Unauthorized, adminOnly.StatusCode);

        // A well-formed key for a room the operator never registered.
        var wrongRoom = await relay.RegisterAsync(signer, groupKey: "some-other-groups-key");
        Assert.Equal(HttpStatusCode.Forbidden, wrongRoom.StatusCode);

        var registered = await relay.RegisterAsync(signer, GroupKeyOfTheRoom);
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var credential = JsonSerializer.Deserialize<JsonElement>(await registered.Content.ReadAsStringAsync());

        // The session it was issued is an owner's, of its own tenant: its frame queue answers.
        using var read = new HttpRequestMessage(HttpMethod.Get, "v2/companion/relay/frames");
        read.Headers.Add("X-Relay-Session", credential.GetProperty("sessionId").GetString());
        read.Headers.Add("X-Relay-Credential", credential.GetProperty("credential").GetString());
        Assert.Equal(HttpStatusCode.OK, (await relay.Client.SendAsync(read)).StatusCode);

        // Nobody claimed anything: the legacy registry is as empty as it started.
        Assert.False(relay.Legacy.CanAuthenticate);
    }

    [Fact]
    public async Task ARegistrationIsNotReplayable()
    {
        await using var relay = await Relay.StartAsync();
        using var signer = new Signer();
        var body = await relay.BuildRegistrationAsync(signer);

        Assert.Equal(HttpStatusCode.OK, (await relay.PostRegistrationAsync(body, GroupKeyOfTheRoom)).StatusCode);
        var replayed = await relay.PostRegistrationAsync(body, GroupKeyOfTheRoom);

        Assert.Equal(HttpStatusCode.BadRequest, replayed.StatusCode);
        Assert.Equal("\"challenge-rejected\"", await replayed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AClockSkewRefusalCarriesTheSignedServerMinusClaimOffset()
    {
        await using var relay = await Relay.StartAsync();
        using var signer = new Signer();
        var desktopFourHoursFast = relay.UtcNow.AddHours(4);

        var response = await relay.PostRegistrationAsync(
            await relay.BuildRegistrationAsync(signer, desktopFourHoursFast),
            GroupKeyOfTheRoom);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var refusal = JsonSerializer.Deserialize<RelayClockSkewRefusal>(
            await response.Content.ReadAsStringAsync(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("clock-skew", refusal?.Code);
        Assert.Equal(-14_400, refusal?.OffsetSeconds);
    }

    private sealed class Relay : IAsyncDisposable
    {
        public const string AdminKey = "operator-admin-key-0123456789abcdef";
        private readonly WebApplication _app;
        private readonly OwnerRecoveryProtector _recovery;
        private readonly RelayTestClock _clock;
        private readonly CompanionDeviceId _deviceId = new(Guid.NewGuid());

        private Relay(WebApplication app, OwnerRecoveryProtector recovery, RelayTestClock clock, RelayDeviceRegistry legacy, HttpClient client)
        {
            _app = app;
            _recovery = recovery;
            _clock = clock;
            Legacy = legacy;
            Client = client;
        }

        public HttpClient Client { get; }

        public RelayDeviceRegistry Legacy { get; }

        public DateTimeOffset UtcNow => _clock.UtcNow;

        public static async Task<Relay> StartAsync()
        {
            var clock = new RelayTestClock(RelaySecurityTestFactory.Now);
            var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x44, 32).ToArray(), clock);
            var legacy = await RelayDeviceRegistry.OpenAsync(clock, recovery);
            // A closed relay: the operator registered one room, so that is the only one served.
            var rooms = new GroupRoomRegistry(clock);
            Assert.NotNull(rooms.Add("Friends", GroupKeyOfTheRoom));

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton(rooms);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapRelayCompanionRoutes(
                legacy,
                new OpaqueRelayFrameHub(legacy, clock),
                recovery,
                new RelayOwnerClaimGate(clock),
                new RelayMapSurfaceStore(legacy, clock));
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Relay(app, recovery, clock, legacy, new HttpClient { BaseAddress = new Uri(address.TrimEnd('/') + "/") });
        }

        public async Task<byte[]> BuildRegistrationAsync(
            IDesktopIdentitySigner signer,
            DateTimeOffset? claimUtc = null)
        {
            using var asked = await Client.PostAsync("v2/companion/relay/possession/challenge", null);
            var nonce = JsonSerializer.Deserialize<JsonElement>(await asked.Content.ReadAsStringAsync())
                .GetProperty("nonceBase64Url").GetString()!;
            return DesktopRelayOwnerClaim.Build(signer, _deviceId, claimUtc ?? _clock.UtcNow, nonce).ToJsonBody();
        }

        public async Task<HttpResponseMessage> RegisterAsync(IDesktopIdentitySigner signer, string? groupKey, string? adminKey = null) =>
            await PostRegistrationAsync(await BuildRegistrationAsync(signer), groupKey, adminKey);

        public Task<HttpResponseMessage> PostRegistrationAsync(byte[] body, string? groupKey, string? adminKey = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, RelayCompanionRoutes.RegisterDesktopPath.TrimStart('/'))
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (groupKey is not null)
            {
                request.Headers.Add("X-Group-Key", groupKey);
            }

            if (adminKey is not null)
            {
                request.Headers.Add("X-Admin-Key", adminKey);
            }

            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _recovery.Dispose();
        }
    }

    private sealed class Signer : IDesktopIdentitySigner, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public Signer()
        {
            var spki = _key.ExportSubjectPublicKeyInfo();
            PublicKey = new DesktopIdentityKey(
                new DeviceKeyId(RelaySecurityTestFactory.Base64Url(SHA256.HashData(spki))),
                DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
                RelaySecurityTestFactory.Base64Url(spki));
        }

        public DesktopIdentityKey PublicKey { get; }

        public byte[] Sign(ReadOnlySpan<byte> signatureInput) =>
            _key.SignData(signatureInput.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        public void Dispose() => _key.Dispose();
    }
}
