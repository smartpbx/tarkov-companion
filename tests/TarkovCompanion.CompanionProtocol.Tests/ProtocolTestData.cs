using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.CompanionProtocol.Tests;

internal static class ProtocolTestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);
    public static readonly CompanionDeviceId DesktopDevice = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    public static readonly CompanionDeviceId TabletDevice = new(Guid.Parse("10000000-0000-0000-0000-000000000002"));
    public static readonly CompanionDeviceId OtherDevice = new(Guid.Parse("10000000-0000-0000-0000-000000000003"));
    public static readonly DeviceSessionId DesktopSession = new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    public static readonly DeviceSessionId TabletSession = new(Guid.Parse("20000000-0000-0000-0000-000000000002"));
    public static readonly DeviceSessionId OtherSession = new(Guid.Parse("20000000-0000-0000-0000-000000000003"));
    public static readonly DeviceKeyId DesktopKey = new(Thumbprint("desktop-key"));
    public static readonly DeviceKeyId TabletKey = new(Thumbprint("tablet-key"));
    public static readonly DeviceKeyId OtherKey = new(Thumbprint("other-key"));
    public static readonly AuthorityEpoch Epoch = new(Guid.Parse("30000000-0000-0000-0000-000000000001"));

    public static readonly IReadOnlyList<DeviceCapability> TabletCapabilities =
    [
        DeviceCapability.FollowDesktop,
        DeviceCapability.RequestControl,
        DeviceCapability.ShowOnDesktop,
        DeviceCapability.ManageOwnMarks,
        DeviceCapability.RequestCaptureIntent,
        DeviceCapability.ReviewCaptureResult,
    ];

    public static CanonicalCompanionState InitialState() => new(
        Epoch,
        new GlobalRevision(0),
        DesktopDevice,
        new DeviceModeAggregate(
            AggregateCursor.Empty,
            [new DeviceModeEntry(TabletDevice, CompanionInteractionMode.Follow, Now)],
            null,
            null),
        new WorkspaceAggregate(AggregateCursor.Empty, Projection("customs")),
        new MarkAggregate(AggregateCursor.Empty, []),
        new CaptureIntentAggregate(AggregateCursor.Empty, null));

    public static WorkspaceProjection Projection(string mapId = "customs", WorkspaceDialogKind? dialog = null) => new(
        WorkspaceKind.Raid,
        mapId,
        "ground",
        new WorkspaceViewport(
            new MapCoordinate(mapId, "ground", CoordinateSpaceKind.World, "tarkov-dev-1", 10, 2, 20),
            1),
        null,
        [],
        [],
        null,
        [],
        ["extracts"],
        [],
        dialog);

    public static AuthenticatedCommandContext TabletContext(
        DateTimeOffset? now = null,
        IReadOnlyList<DeviceCapability>? capabilities = null) => new(
        TabletDevice,
        TabletSession,
        TabletKey,
        CompanionProtocolVersion.Current,
        CompanionSurfaceKind.TabletLandscape,
        capabilities ?? TabletCapabilities,
        now ?? Now,
        false);

    public static AuthenticatedCommandContext OtherContext(DateTimeOffset? now = null) => new(
        OtherDevice,
        OtherSession,
        OtherKey,
        CompanionProtocolVersion.Current,
        CompanionSurfaceKind.TabletPortrait,
        TabletCapabilities,
        now ?? Now,
        false);

    public static AuthenticatedCommandContext DesktopContext(DateTimeOffset? now = null) => new(
        DesktopDevice,
        DesktopSession,
        DesktopKey,
        CompanionProtocolVersion.Current,
        CompanionSurfaceKind.Desktop,
        Enum.GetValues<DeviceCapability>(),
        now ?? Now,
        true);

    public static ClientCommandEnvelope Envelope(
        CompanionCommand command,
        DeviceSessionId? sessionId = null,
        AuthorityEpoch? epoch = null,
        CompanionProtocolVersion? version = null) =>
        new(
            version ?? CompanionProtocolVersion.Current,
            sessionId ?? TabletSession,
            epoch ?? Epoch,
            command.IssuedUtc,
            command);

    public static CommandReduction Apply(
        CanonicalCompanionState state,
        CompanionCommand command,
        AuthenticatedCommandContext context) =>
        DesktopCanonicalStateMachine.Apply(state, Envelope(command, context.SessionId), context);

    public static CommandId Command(int number) => new(Guid.Parse($"40000000-0000-0000-0000-{number:000000000000}"));

    public static MarkId Mark(int number) => new(Guid.Parse($"50000000-0000-0000-0000-{number:000000000000}"));

    public static CaptureIntentId Capture(int number) => new(Guid.Parse($"60000000-0000-0000-0000-{number:000000000000}"));

    public static CaptureSessionId CaptureSession(int number) => new(Guid.Parse($"70000000-0000-0000-0000-{number:000000000000}"));

    public static MapMarkDraft Draft(double x = 1, MapMarkKind kind = MapMarkKind.Waypoint, MapMarkScope scope = MapMarkScope.PairedDevice) => new(
        kind,
        scope,
        new MapCoordinate("customs", "ground", CoordinateSpaceKind.World, "tarkov-dev-1", x, 2, 3),
        "Mark",
        "#00AACC",
        null);

    public static UpsertMarkCommand Upsert(
        int command,
        long aggregateRevision,
        long markRevision,
        DateTimeOffset now,
        int mark = 1,
        MapMarkKind kind = MapMarkKind.Waypoint,
        MapMarkScope scope = MapMarkScope.PairedDevice,
        double x = 1) => new(
        Command(command),
        new AggregateRevision(aggregateRevision),
        now,
        now.AddSeconds(30),
        Mark(mark),
        markRevision,
        Draft(x, kind, scope));

    public static SetInteractionModeCommand SetMode(int command, long revision, DateTimeOffset now, CompanionInteractionMode mode) => new(
        Command(command),
        new AggregateRevision(revision),
        now,
        now.AddMinutes(1),
        mode);

    public static PairedDevice PairedTablet(
        CompanionDeviceId? deviceId = null,
        DateTimeOffset? now = null,
        IReadOnlyList<DeviceCapability>? capabilities = null)
    {
        var at = now ?? Now;
        return new PairedDevice(
            deviceId ?? TabletDevice,
            "Tablet",
            CryptoVectors.TabletDeviceKey(),
            DeviceAuthorizationRole.Member,
            capabilities ?? TabletCapabilities,
            DeviceLifecycleStatus.Active,
            at,
            at,
            at.AddDays(30),
            at);
    }

    public static DeviceSession ActiveSession(PairedDevice device, DeviceSessionId sessionId, DateTimeOffset? now = null)
    {
        var at = now ?? Now;
        var establishment = new SessionEstablished(
            new HandshakeChallengeId(Guid.Parse("5a1d3c2e-0000-4000-8000-000000000099")),
            HandshakePurpose.SessionResume,
            device.DeviceKey.KeyId,
            new SessionAssignment(
                CompanionProtocolVersion.Current,
                device.DeviceId,
                sessionId,
                new RelayChannelId(Guid.Parse("90000000-0000-0000-0000-000000000001")),
                1,
                RelayCipherSuite.P256HkdfSha256Aes256Gcm,
                at.AddHours(12)),
            Thumbprint("session-transcript"),
            at);
        return new DeviceSession(
            establishment,
            DeviceSessionStatus.Active,
            CompanionTransportKind.EndToEndRelay,
            CompanionSurfaceKind.TabletLandscape,
            device.Capabilities,
            at);
    }

    public static EvidenceProvenance ScreenshotProvenance(DateTimeOffset observedUtc) => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        "fixture://paired-capture",
        observedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
        new ProducerIdentity("paired-fixture", "2.0"));

    /// <summary>A derived-calculation provenance tree exactly <paramref name="depth"/> levels deep.</summary>
    public static EvidenceProvenance ProvenanceOfDepth(int depth, DateTimeOffset observedUtc)
    {
        var provenance = ScreenshotProvenance(observedUtc);
        for (var level = 1; level < depth; level++)
        {
            provenance = new EvidenceProvenance(
                EvidenceSourceClass.DerivedCalculation,
                $"fixture://derived/{level}",
                observedUtc,
                EvidenceConfidence.Certain,
                new ProducerIdentity("paired-fixture", "2.0"),
                generatedUtc: observedUtc,
                inputs: [provenance]);
        }

        return provenance;
    }

    public static string Thumbprint(string label) =>
        Base64Url.EncodeToString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(label)));

    public static string Base64UrlOf(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    public static byte[] FromBase64Url(string text) => Base64Url.DecodeFromChars(text);

    public static byte[] Golden(string relativePath) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", relativePath));

    public static JsonObject GoldenNode(string relativePath) =>
        JsonNode.Parse(Golden(relativePath))!.AsObject();

    public static T GoldenRoot<T>(string relativePath) =>
        CompanionProtocolJson.Deserialize<T>(Golden(relativePath));

    public static JsonNode SchemaNode() =>
        JsonNode.Parse(File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "Schemas", "v2", "companion-protocol.schema.json")))!;

    public static void AssertJsonEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"Expected {System.Text.Encoding.UTF8.GetString(expected)}{Environment.NewLine}Actual {System.Text.Encoding.UTF8.GetString(actual)}");
}

/// <summary>The independently computed handshake vectors and test-only keys under Golden/crypto.</summary>
internal static class CryptoVectors
{
    private static readonly Lazy<JsonObject> Document = new(() => ProtocolTestData.GoldenNode("crypto/paired-handshake-vectors.json"));

    public static JsonObject Root => Document.Value;

    public static string Text(params string[] path)
    {
        JsonNode node = Root;
        foreach (var segment in path)
        {
            node = node[segment]!;
        }

        return node.GetValue<string>();
    }

    public static byte[] Hex(params string[] path) => Convert.FromHexString(Text(path));

    public static ECParameters Parameters(string key, bool includePrivate) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        D = includePrivate ? Hex("testOnlyKeys", key, "dHex") : null,
        Q = new ECPoint { X = Hex("testOnlyKeys", key, "xHex"), Y = Hex("testOnlyKeys", key, "yHex") },
    };

    public static ECDsa SigningKey(string key) => ECDsa.Create(Parameters(key, includePrivate: true));

    public static ECDiffieHellman AgreementKey(string key) => ECDiffieHellman.Create(Parameters(key, includePrivate: true));

    public static EphemeralPublicKey Ephemeral(string key)
    {
        using var agreement = AgreementKey(key);
        return new EphemeralPublicKey(EphemeralKeyAlgorithm.EcdhP256, ProtocolTestData.Base64UrlOf(agreement.ExportSubjectPublicKeyInfo()));
    }

    public static DesktopIdentityKey DesktopIdentity()
    {
        using var signing = SigningKey("desktopIdentity");
        var spki = signing.ExportSubjectPublicKeyInfo();
        return new DesktopIdentityKey(
            new DeviceKeyId(ProtocolTestData.Base64UrlOf(SHA256.HashData(spki))),
            DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
            ProtocolTestData.Base64UrlOf(spki));
    }

    public static DevicePublicKey TabletDeviceKey()
    {
        var cose = CoseEs256(Hex("testOnlyKeys", "tabletDevice", "xHex"), Hex("testOnlyKeys", "tabletDevice", "yHex"));
        return new DevicePublicKey(
            new DeviceKeyId(ProtocolTestData.Base64UrlOf(SHA256.HashData(cose))),
            DeviceKeyAlgorithm.WebAuthnEs256,
            ProtocolTestData.Base64UrlOf(SHA256.HashData("paired-vector/credential-id"u8)[..16]),
            ProtocolTestData.Base64UrlOf(cose));
    }

    /// <summary>The CTAP2 canonical COSE_Key map for an ES256 P-256 public key.</summary>
    public static byte[] CoseEs256(byte[] x, byte[] y) =>
        [0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20, .. x, 0x22, 0x58, 0x20, .. y];
}

/// <summary>A desktop identity signer backed by a test-only in-memory key.</summary>
internal sealed class TestDesktopSigner : IDesktopIdentitySigner, IDisposable
{
    private readonly ECDsa _key;

    public TestDesktopSigner(ECDsa key)
    {
        _key = key;
        var spki = key.ExportSubjectPublicKeyInfo();
        PublicKey = new DesktopIdentityKey(
            new DeviceKeyId(ProtocolTestData.Base64UrlOf(SHA256.HashData(spki))),
            DesktopIdentityKeyAlgorithm.EcdsaP256Sha256,
            ProtocolTestData.Base64UrlOf(spki));
    }

    public DesktopIdentityKey PublicKey { get; }

    public static TestDesktopSigner FromVectors() => new(CryptoVectors.SigningKey("desktopIdentity"));

    public static TestDesktopSigner Random() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public byte[] Sign(ReadOnlySpan<byte> signatureInput) =>
        _key.SignData(signatureInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// A test-only WebAuthn ES256 verifier. It demonstrates that the wire assertion carries everything
/// a relying party needs: the COSE key, authenticator data, client data, and DER signature.
/// </summary>
internal sealed class ReferenceWebAuthnVerifier(string rpId) : IDeviceKeyProofVerifier
{
    public int Calls { get; private set; }

    public ValueTask<bool> VerifyAsync(
        DevicePublicKey deviceKey,
        HandshakeChallenge challenge,
        DeviceKeyProof proof,
        CancellationToken cancellationToken)
    {
        Calls++;
        var cose = ProtocolTestData.FromBase64Url(deviceKey.CosePublicKeyBase64Url);
        if (cose.Length != 77 || cose[0] != 0xA5)
        {
            return ValueTask.FromResult(false);
        }

        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = cose[10..42], Y = cose[45..77] },
        });
        var authenticatorData = ProtocolTestData.FromBase64Url(proof.Assertion.AuthenticatorDataBase64Url);
        var clientData = ProtocolTestData.FromBase64Url(proof.Assertion.ClientDataJsonBase64Url);
        if (!authenticatorData.AsSpan(0, 32).SequenceEqual(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rpId))))
        {
            return ValueTask.FromResult(false);
        }

        using var client = JsonDocument.Parse(clientData);
        if (!client.RootElement.GetProperty("origin").ValueEquals($"https://{rpId}"))
        {
            return ValueTask.FromResult(false);
        }

        byte[] signed = [.. authenticatorData, .. SHA256.HashData(clientData)];
        return ValueTask.FromResult(key.VerifyData(
            signed,
            ProtocolTestData.FromBase64Url(proof.Assertion.SignatureBase64Url),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }
}

/// <summary>Produces real WebAuthn-shaped assertions with the test-only tablet device key.</summary>
internal static class TestAuthenticator
{
    public const string RpId = "tarkov-companion.local";

    public static DeviceKeyProof Prove(
        HandshakeChallenge challenge,
        byte flags = 0x05,
        string type = "webauthn.get",
        string? challengeOverride = null,
        string? credentialOverride = null)
    {
        using var key = CryptoVectors.SigningKey("tabletDevice");
        byte[] authenticatorData = [.. SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RpId)), flags, 0, 0, 0, 7];
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["type"] = type,
            ["challenge"] = challengeOverride ?? challenge.TranscriptHashBase64Url,
            ["origin"] = $"https://{RpId}",
            ["crossOrigin"] = false,
        });
        byte[] signed = [.. authenticatorData, .. SHA256.HashData(clientData)];
        var signature = key.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new DeviceKeyProof(
            challenge.ChallengeId,
            new WebAuthnAssertion(
                credentialOverride ?? challenge.CredentialIdBase64Url,
                ProtocolTestData.Base64UrlOf(authenticatorData),
                ProtocolTestData.Base64UrlOf(clientData),
                ProtocolTestData.Base64UrlOf(signature)));
    }
}
