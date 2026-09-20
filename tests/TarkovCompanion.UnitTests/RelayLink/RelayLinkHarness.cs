using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Tablet;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.CompanionProtocol;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.GroupServer;
using TarkovCompanion.GroupServer.Security;
using TarkovCompanion.GroupServer.StateSync;
using TarkovCompanion.GroupServer.Storage;
using TarkovCompanion.Infrastructure.Devices;
using TarkovCompanion.UnitTests.RelayDeviceSecurity;

namespace TarkovCompanion.UnitTests.RelayLink;

/// <summary>
/// The relay's admin key is one process-wide environment variable, so the tests that set it run
/// one at a time. Any value is harmless to the tests that rely on a wrong guess being refused.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RelayAdminKeyCollection
{
    public const string Name = "relay admin key";
}

/// <summary>A real in-process relay: Kestrel on a loopback port, unclaimed, with the production routes.</summary>
internal sealed class LinkRelay : IAsyncDisposable
{
    public const string AdminKey = "link-tests-admin-key-0123456789";
    private readonly WebApplication _app;
    private readonly OwnerRecoveryProtector _recovery;
    private readonly string? _previousAdminKey;

    private LinkRelay(WebApplication app, OwnerRecoveryProtector recovery, Uri origin, RelayDeviceRegistry registry, string? previousAdminKey)
    {
        _app = app;
        _recovery = recovery;
        Origin = origin;
        Registry = registry;
        _previousAdminKey = previousAdminKey;
    }

    public Uri Origin { get; }

    public RelayDeviceRegistry Registry { get; }

    /// <param name="pairedSessionHours">What <c>/health</c> states, or null for a relay older than 2026-09-20.</param>
    public static async Task<LinkRelay> StartAsync(RelayTestClock clock, int? pairedSessionHours = 720)
    {
        var previous = Environment.GetEnvironmentVariable(RelayAdmin.Variable);
        Environment.SetEnvironmentVariable(RelayAdmin.Variable, AdminKey);
        var recovery = new OwnerRecoveryProtector(Enumerable.Repeat((byte)0x41, 32).ToArray(), clock);
        var registry = await RelayDeviceRegistry.OpenAsync(clock, recovery);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<CompanionPairingMailbox>();
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapRelayCompanionRoutes(
            registry,
            new OpaqueRelayFrameHub(registry, clock),
            recovery,
            new RelayOwnerClaimGate(clock),
            new RelayMapSurfaceStore(registry, clock));
        app.MapCompanionPairingMailboxRoutes();
        // The one piece of Program.cs these tests touch that is still a top-level statement.
        app.MapGet("/health", () => pairedSessionHours is { } hours
            ? Results.Ok(new { status = "ok", pairedSessionHours = hours })
            : Results.Ok(new { status = "ok" }));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();
        return new LinkRelay(app, recovery, new Uri(address), registry, previous);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _recovery.Dispose();
        Environment.SetEnvironmentVariable(RelayAdmin.Variable, _previousAdminKey);
    }
}

/// <summary>Everything about a desktop that outlives one run of it: its identity key, its files, its protected secrets.</summary>
internal sealed class DesktopDisk : IDisposable
{
    public LinkSigner Signer { get; } = new();

    public LinkAuthorityStore AuthorityStore { get; } = new();

    public LinkSecretStore Secrets { get; } = new();

    public LinkCounterStore Counters { get; } = new();

    public LinkMarkStore Marks { get; } = new();

    public void Dispose() => Signer.Dispose();
}

/// <summary>
/// One run of the desktop, driven through the pairing panel's own view model — the thing a player
/// presses. Disposing it and starting another over the same disk is a restart.
/// </summary>
internal sealed class DesktopRun : IAsyncDisposable
{
    public const string RelyingPartyId = "companion.example";
    public const string TabletOrigin = "https://tablet.companion.example";

    private DesktopRun(
        DesktopCompanionAuthority authority,
        DesktopPairingCoordinator coordinator,
        RelayMarksBridge bridge,
        CompanionPairingViewModel panel)
    {
        Authority = authority;
        Coordinator = coordinator;
        Bridge = bridge;
        Panel = panel;
    }

    public DesktopCompanionAuthority Authority { get; }

    public DesktopPairingCoordinator Coordinator { get; }

    public RelayMarksBridge Bridge { get; }

    public CompanionPairingViewModel Panel { get; }

    /// <summary>Starts a run and waits until it has looked for a kept claim, as the app does at startup.</summary>
    public static async Task<DesktopRun> StartAsync(DesktopDisk disk, Uri relayOrigin, RelayTestClock clock, bool protectedStorage = true)
    {
        var authority = await DesktopCompanionAuthority.OpenAsync(disk.AuthorityStore, LinkState.Initial());
        var coordinator = new DesktopPairingCoordinator(
            authority,
            disk.Signer,
            new WebAuthnDeviceKeyProofVerifier(RelyingPartyId, TabletOrigin, disk.Counters));
        var bridge = new RelayMarksBridge(
            authority,
            disk.Marks,
            clock,
            protectedStorage ? new RelayLinkVault(disk.Secrets) : null);
        var panel = new CompanionPairingViewModel(
            authority,
            new CompanionPairingAvailability(coordinator, relayOrigin, disk.Signer),
            clock,
            bridge)
        {
            MailboxPollInterval = TimeSpan.FromMilliseconds(20),
        };
        await panel.RelayLinkRestored;
        return new DesktopRun(authority, coordinator, bridge, panel);
    }

    /// <summary>Types the admin key and presses Claim.</summary>
    public async Task ClaimAsync(string adminKey = LinkRelay.AdminKey)
    {
        Panel.AdminKeyInput = adminKey;
        await ((AsyncDelegateCommand)Panel.ClaimRelayCommand).ExecuteAsync();
    }

    /// <summary>
    /// Presses Start pairing, lets the tablet run its half, compares the two codes the way the
    /// player is asked to, presses Approve, and waits for the panel to say how it ended.
    /// </summary>
    public async Task<string> PairAsync(TabletSimulator tablet, string name)
    {
        await ((AsyncDelegateCommand)Panel.StartPairingCommand).ExecuteAsync();
        Assert.True(Panel.IsAwaitingTablet, Panel.StatusMessage);
        var tabletSide = tablet.PairAsync(Panel.PairingCode!, name);
        await LinkWait.UntilAsync(() => Panel.IsAwaitingApproval || Panel.IsIdle || tabletSide.IsFaulted, "the tablet's request to reach the panel");
        if (tabletSide.IsFaulted)
        {
            await tabletSide;
        }

        Assert.True(Panel.IsAwaitingApproval, Panel.StatusMessage);
        Assert.Equal(name, Panel.RequestedDisplayName);
        await LinkWait.UntilAsync(() => tablet.VerificationCode is not null || tabletSide.IsFaulted, "the tablet to show its code");
        Assert.Equal(tablet.VerificationCode, Panel.VerificationCode);
        await ((AsyncDelegateCommand)Panel.ApproveCommand).ExecuteAsync();
        await LinkWait.UntilAsync(() => Panel.IsIdle, "the ceremony to finish on the desktop");
        await tabletSide;
        return Panel.StatusMessage ?? string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        Panel.Dispose();
        await Bridge.DisposeAsync();
        Coordinator.Dispose();
        Authority.Dispose();
    }
}

internal static class LinkWait
{
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what + ".");
            await Task.Delay(10);
        }
    }
}

/// <summary>
/// What the tablet page does, in C#: the same ceremony, the same sealed frames over the same relay
/// routes, the same things kept across a reload (its device key, its session and its sequence).
/// </summary>
internal sealed class TabletSimulator : IDisposable
{
    private readonly ECDsa _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly HttpClient _relay;
    private readonly RelayTestClock _clock;
    private uint _signatureCounter;
    private byte[] _tabletToDesktopKey = [];
    private byte[] _desktopToTabletKey = [];
    private SessionAssignment? _assignment;
    private string? _credential;
    private long _senderSequence;
    private long _afterDeliveryId;

    public TabletSimulator(Uri relayOrigin, RelayTestClock clock)
    {
        _relay = new HttpClient { BaseAddress = new Uri(relayOrigin.AbsoluteUri.TrimEnd('/') + "/") };
        _clock = clock;
    }

    public CompanionDeviceId DeviceId => _assignment!.DeviceId;

    public AuthorityEpoch? AuthorityEpoch { get; private set; }

    /// <summary>The six-digit code this tablet is showing, once it has one.</summary>
    public string? VerificationCode { get; private set; }

    /// <summary>
    /// The tablet's half of the ceremony over the relay's pairing mailbox, route for route what
    /// the page's <c>beginPairing</c> does.
    /// </summary>
    public async Task PairAsync(string pairingCode, string name)
    {
        VerificationCode = null;
        using var resolve = new HttpRequestMessage(HttpMethod.Post, "v2/companion/pairing/offers/resolve");
        resolve.Headers.Add("Tarkov-Pairing-Code", pairingCode.Replace("-", string.Empty, StringComparison.Ordinal));
        using var resolved = await _relay.SendAsync(resolve);
        Assert.Equal(System.Net.HttpStatusCode.OK, resolved.StatusCode);
        var offer = CompanionProtocolJson.Deserialize<PairingOffer>(await resolved.Content.ReadAsByteArrayAsync());
        var attempt = offer.AttemptId.Value.ToString("D");

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var request = PairingRequestFor(offer, ephemeral, name);
        await PostMailboxAsync($"v2/companion/pairing/requests/{attempt}", CompanionProtocolJson.Serialize(request));

        var reveal = await PollMailboxAsync<PairingNonceReveal>($"v2/companion/pairing/reveals/{attempt}");
        Assert.True(PairingCryptography.IsRevealOf(offer, reveal));
        VerificationCode = PairingCryptography.ComputeVerificationCode(offer, request, reveal);

        var challenge = await PollMailboxAsync<HandshakeChallenge>($"v2/companion/pairing/challenges/{attempt}");
        await PostMailboxAsync($"v2/companion/pairing/proofs/{attempt}", CompanionProtocolJson.Serialize(Proof(challenge)));
        var established = await PollMailboxAsync<SessionEstablished>($"v2/companion/pairing/established/{attempt}");
        Assert.True(established.Answers(challenge));

        var sharedSecret = PairingCryptography.DeriveP256SharedSecret(ephemeral, challenge.DesktopEphemeralKey);
        _tabletToDesktopKey = PairingCryptography.DeriveTrafficKey(sharedSecret, challenge.TranscriptHashBase64Url, PairingTrafficDirection.TabletToDesktop);
        _desktopToTabletKey = PairingCryptography.DeriveTrafficKey(sharedSecret, challenge.TranscriptHashBase64Url, PairingTrafficDirection.DesktopToTablet);
        CryptographicOperations.ZeroMemory(sharedSecret);
        _assignment = established.Assignment;
        _senderSequence = 0;
        _afterDeliveryId = 0;
        AuthorityEpoch = null;
        _credential = null;

        // The relay credential arrives last, sealed, through the same mailbox. A desktop the
        // relay refused to register this tablet for never leaves one.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (_credential is null && DateTime.UtcNow < deadline)
        {
            using var response = await _relay.GetAsync($"v2/companion/pairing/relay-session/{attempt}");
            if (response.IsSuccessStatusCode)
            {
                var sealedCredential = JsonSerializer.Deserialize<SealedRelayCredential>(
                    await response.Content.ReadAsStringAsync(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                _credential = RelayCredentialCryptography.Open(_desktopToTabletKey, offer.AttemptId.Value, sealedCredential);
            }
            else
            {
                await Task.Delay(20);
            }
        }
    }

    /// <summary>Whether the desktop got this tablet registered on the relay and handed it a credential.</summary>
    public bool HasRelayCredential => _credential is not null;

    private async Task PostMailboxAsync(string path, byte[] body)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _relay.PostAsync(path, content);
        Assert.True(response.IsSuccessStatusCode, $"{path} answered {(int)response.StatusCode}");
    }

    private async Task<T> PollMailboxAsync<T>(string path)
        where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            using var response = await _relay.GetAsync(path);
            if (response.IsSuccessStatusCode)
            {
                return CompanionProtocolJson.Deserialize<T>(await response.Content.ReadAsByteArrayAsync());
            }

            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + path + ".");
            await Task.Delay(20);
        }
    }

    /// <summary>A page reload: everything held only in memory is gone, the kept session is not.</summary>
    public void Reload()
    {
        AuthorityEpoch = null;
        _afterDeliveryId = 0;
    }

    /// <summary>What the page does with a reconnect plan's snapshot: it becomes what the tablet holds.</summary>
    public void AdoptSnapshot(CanonicalCompanionState snapshot) => AuthorityEpoch = snapshot.AuthorityEpoch;

    public Task<HttpResponseMessage> ReadFramesRawAsync() => SendAsync(HttpMethod.Get, $"v2/companion/relay/frames?after={_afterDeliveryId}", null);

    /// <summary>Reads and opens every queued frame, returning what each one carried.</summary>
    public async Task<IReadOnlyList<RelayPayload>> ReadAsync()
    {
        using var response = await ReadFramesRawAsync();
        response.EnsureSuccessStatusCode();
        var opened = new List<RelayPayload>();
        foreach (var (deliveryId, frame) in RelayMarksBridge.ParseFrameBatch(await response.Content.ReadAsStringAsync()))
        {
            _afterDeliveryId = Math.Max(_afterDeliveryId, deliveryId);
            if (frame is null)
            {
                continue;
            }

            var payload = PairingCryptography.OpenRelayFrame(_desktopToTabletKey, PairingTrafficDirection.DesktopToTablet, frame);
            opened.Add(payload);
            if (payload.Kind == RelayPayloadKind.ServerEnvelope &&
                CompanionProtocolJson.Deserialize<ServerEnvelope>(payload.Json.Span).Message is CanonicalSnapshotMessage snapshot)
            {
                AuthorityEpoch = snapshot.State.AuthorityEpoch;
            }
        }

        return opened;
    }

    public async Task<ReconnectRequestId> RequestResyncAsync()
    {
        var requestId = new ReconnectRequestId(Guid.NewGuid());
        var request = new ReconnectRequest(
            CompanionProtocolVersion.Current,
            _assignment!.SessionId,
            requestId,
            authorityEpoch: null,
            new GlobalRevision(0),
            new DeliverySequence(0),
            []);
        using var response = await PublishAsync(RelayPayloadKind.ReconnectRequest, CompanionProtocolJson.Serialize(request));
        response.EnsureSuccessStatusCode();
        return requestId;
    }

    public async Task<HttpResponseMessage> DropWaypointAsync(double x, double y, string label)
    {
        var now = _clock.UtcNow;
        var command = new UpsertMarkCommand(
            new CommandId(Guid.NewGuid()),
            new AggregateRevision(1),
            now,
            now.AddSeconds(30),
            new MarkId(Guid.NewGuid()),
            0,
            new MapMarkDraft(
                MapMarkKind.Waypoint,
                MapMarkScope.PairedDevice,
                new MapMarkState("customs", null, x, y, label, null),
                CoordinateSpaceKind.World,
                "v1",
                null,
                "#22D3EE"));
        var envelope = new ClientCommandEnvelope(
            CompanionProtocolVersion.Current,
            _assignment!.SessionId,
            AuthorityEpoch ?? throw new InvalidOperationException("The tablet holds no canonical state yet."),
            now,
            command);
        return await PublishAsync(RelayPayloadKind.ClientCommandEnvelope, CompanionProtocolJson.Serialize(envelope));
    }

    private Task<HttpResponseMessage> PublishAsync(RelayPayloadKind kind, byte[] json)
    {
        var now = _clock.UtcNow;
        var frame = PairingCryptography.SealRelayFrame(
            _tabletToDesktopKey,
            PairingTrafficDirection.TabletToDesktop,
            kind,
            CompanionProtocolVersion.Current,
            _assignment!.RelayChannelId,
            _assignment.SessionId,
            _assignment.KeyEpoch,
            ++_senderSequence,
            now,
            now.AddSeconds(60),
            json);
        return SendAsync(HttpMethod.Post, "v2/companion/relay/frames", CompanionProtocolJson.Serialize(frame));
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, byte[]? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        request.Headers.Add("X-Relay-Session", _assignment!.SessionId.Value.ToString("D"));
        request.Headers.Add("X-Relay-Credential", _credential ?? "none");
        return _relay.SendAsync(request);
    }

    private PairingRequest PairingRequestFor(PairingOffer offer, ECDiffieHellman ephemeral, string name)
    {
        var ephemeralPublicKey = new EphemeralPublicKey(
            EphemeralKeyAlgorithm.EcdhP256,
            Base64Url.EncodeToString(ephemeral.ExportSubjectPublicKeyInfo()));
        var clientNonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(ProtocolBounds.PairingNonceBytes));
        var sharedSecret = PairingCryptography.DeriveP256SharedSecret(ephemeral, offer.DesktopEphemeralKey);
        try
        {
            var deviceKey = PublicDeviceKey();
            var context = PairingCryptography.ComputePairingRequestContextHash(
                offer,
                CompanionProtocolVersion.Current,
                deviceKey,
                ephemeralPublicKey,
                clientNonce);
            return new PairingRequest(
                offer.AttemptId,
                CompanionProtocolVersion.Current,
                deviceKey,
                ephemeralPublicKey,
                clientNonce,
                PairingCryptography.SealDeviceName(sharedSecret, context, name));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    private DeviceKeyProof Proof(HandshakeChallenge challenge)
    {
        var authenticatorData = new byte[ProtocolBounds.MinAuthenticatorDataBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(DesktopRun.RelyingPartyId)).CopyTo(authenticatorData, 0);
        authenticatorData[32] = 0x05;
        BinaryPrimitives.WriteUInt32BigEndian(authenticatorData.AsSpan(33, 4), ++_signatureCounter);
        var clientData = Encoding.UTF8.GetBytes(
            $"{{\"type\":\"webauthn.get\",\"challenge\":\"{challenge.TranscriptHashBase64Url}\",\"origin\":\"{DesktopRun.TabletOrigin}\",\"crossOrigin\":false}}");
        byte[] signedData = [.. authenticatorData, .. SHA256.HashData(clientData)];
        var signature = _deviceKey.SignData(signedData, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new DeviceKeyProof(
            challenge.ChallengeId,
            new WebAuthnAssertion(
                PublicDeviceKey().CredentialIdBase64Url,
                Base64Url.EncodeToString(authenticatorData),
                Base64Url.EncodeToString(clientData),
                Base64Url.EncodeToString(signature)));
    }

    // One key per browser profile, for good — which is exactly why a second pairing of the same
    // tablet used to collide with the first.
    private DevicePublicKey PublicDeviceKey()
    {
        var parameters = _deviceKey.ExportParameters(includePrivateParameters: false);
        byte[] cose =
        [
            0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20,
            .. parameters.Q.X!,
            0x22, 0x58, 0x20,
            .. parameters.Q.Y!,
        ];
        return new DevicePublicKey(
            new DeviceKeyId(Base64Url.EncodeToString(SHA256.HashData(cose))),
            DeviceKeyAlgorithm.WebAuthnEs256,
            Base64Url.EncodeToString(SHA256.HashData(cose)[..16]),
            Base64Url.EncodeToString(cose));
    }

    public void Dispose()
    {
        _deviceKey.Dispose();
        _relay.Dispose();
    }
}

internal static class LinkState
{
    public static CanonicalCompanionState Initial() => new(
        new AuthorityEpoch(Guid.Parse("30000000-0000-4000-8000-0000000000aa")),
        new WorkspaceId(Guid.Parse("80000000-0000-4000-8000-0000000000aa")),
        "desktop-link-tests",
        new GlobalRevision(0),
        new CompanionDeviceId(Guid.Parse("10000000-0000-4000-8000-0000000000aa")),
        new DeviceModeAggregate(AggregateCursor.Empty, [], null, null),
        new WorkspaceAggregate(
            AggregateCursor.Empty,
            new WorkspaceProjection(WorkspaceKind.Raid, null, null, null, null, [], [], null, [], [], [], null)),
        new MarkAggregate(AggregateCursor.Empty, []),
        new CaptureIntentAggregate(AggregateCursor.Empty, null),
        ProfilePreferencesAggregate.Empty);
}

internal sealed class LinkSigner : IDesktopIdentitySigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public LinkSigner()
    {
        var publicKey = _key.ExportSubjectPublicKeyInfo();
        PublicKey = new DesktopIdentityKey(
            new DeviceKeyId(Base64Url.EncodeToString(SHA256.HashData(publicKey))),
            DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
            Base64Url.EncodeToString(publicKey));
    }

    public DesktopIdentityKey PublicKey { get; }

    public byte[] Sign(ReadOnlySpan<byte> signatureInput) => _key.SignData(
        signatureInput,
        HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}

/// <summary>The authority's file, which a restart reads back. One run holds it at a time.</summary>
internal sealed class LinkAuthorityStore : IDesktopCompanionAuthorityStore
{
    private DesktopCompanionAuthorityState? _state;
    private int _leased;

    public ValueTask<IDisposable> AcquireExclusiveLeaseAsync(CancellationToken cancellationToken = default) =>
        Interlocked.CompareExchange(ref _leased, 1, 0) == 0
            ? ValueTask.FromResult<IDisposable>(new Lease(this))
            : throw new InvalidOperationException("The previous desktop run is still holding the authority store.");

    public ValueTask<DesktopCompanionAuthorityState?> LoadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_state);

    public ValueTask SaveAsync(DesktopCompanionAuthorityState state, CancellationToken cancellationToken = default)
    {
        _state = state;
        return ValueTask.CompletedTask;
    }

    private sealed class Lease(LinkAuthorityStore owner) : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref owner._leased, 0);
    }
}

/// <summary>Stands in for the DPAPI store: the same interface, the same keying, in memory.</summary>
internal sealed class LinkSecretStore : IIntegrationSecretStore
{
    private readonly Dictionary<IntegrationSecretReference, string> _secrets = [];

    public bool IsAvailable => true;

    public int Count => _secrets.Count;

    public IReadOnlyCollection<IntegrationSecretKind> Kinds => _secrets.Keys.Select(reference => reference.Kind).ToArray();

    public Task SaveAsync(IntegrationSecretReference reference, string secret, CancellationToken cancellationToken)
    {
        // The real store refuses anything over 4096 characters, and a kept session has to fit.
        Assert.InRange(secret.Length, 1, 4096);
        _secrets[reference] = secret;
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(IntegrationSecretReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(_secrets.GetValueOrDefault(reference));

    public Task<bool> ExistsAsync(IntegrationSecretReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(_secrets.ContainsKey(reference));

    public Task DeleteAsync(IntegrationSecretReference reference, CancellationToken cancellationToken)
    {
        _secrets.Remove(reference);
        return Task.CompletedTask;
    }
}

internal sealed class LinkCounterStore : IDeviceSignatureCounterStore
{
    private readonly Dictionary<DeviceKeyId, DeviceSignatureCounter> _counters = [];

    public ValueTask<bool> TryAcceptAsync(DeviceSignatureCounter candidate, CancellationToken cancellationToken)
    {
        if (_counters.TryGetValue(candidate.DeviceKeyId, out var existing) &&
            existing.ChallengeId != candidate.ChallengeId &&
            existing.SignatureCounter > 0 && candidate.SignatureCounter <= existing.SignatureCounter)
        {
            return ValueTask.FromResult(false);
        }

        _counters[candidate.DeviceKeyId] = candidate;
        return ValueTask.FromResult(true);
    }
}

internal sealed class LinkMarkStore : IRaidMarkStore
{
    private readonly List<RaidMark> _marks = [];

    public IReadOnlyList<RaidMark> Marks => _marks;

#pragma warning disable CS0067 // nothing in these tests listens for a local change
    public event Action? Changed;
#pragma warning restore CS0067

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<RaidMark> AddAsync(RaidMarkKind kind, string mapId, string? floorId, double x, double y, string? label, CancellationToken cancellationToken = default)
    {
        var mark = new RaidMark(Guid.NewGuid(), kind, new MapMarkState(mapId, floorId, x, y, label, null), DateTimeOffset.UtcNow);
        _marks.Add(mark);
        return Task.FromResult(mark);
    }

    public Task MoveAsync(Guid id, double x, double y, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RenameAsync(Guid id, string? label, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _marks.RemoveAll(mark => mark.Id == id);
        return Task.CompletedTask;
    }
}
