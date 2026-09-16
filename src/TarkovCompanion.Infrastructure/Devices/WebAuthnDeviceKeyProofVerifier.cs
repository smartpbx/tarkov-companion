using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.CompanionProtocol;

namespace TarkovCompanion.Infrastructure.Devices;

/// <summary>
/// Verifies the browser authenticator proof against one deployment-pinned RP id and HTTPS origin.
/// </summary>
/// <remarks>
/// The protocol has already bound the credential, challenge, user-presence, and user-verification
/// flags before this seam. This verifier independently checks the RP hash, exact origin, ES256
/// signature, and atomic monotonic counter so a valid assertion from another site or an older
/// ceremony cannot authorize the desktop.
/// </remarks>
public sealed class WebAuthnDeviceKeyProofVerifier : IDeviceKeyProofVerifier
{
    private const int RpIdHashLength = 32;
    private const int FlagsOffset = 32;
    private const int SignatureCounterOffset = 33;
    private const int SignatureCounterLength = 4;
    private const byte UserPresentFlag = 0x01;
    private const byte UserVerifiedFlag = 0x04;

    private readonly string _origin;
    private readonly byte[] _relyingPartyIdHash;
    private readonly IDeviceSignatureCounterStore _counterStore;

    public WebAuthnDeviceKeyProofVerifier(
        string relyingPartyId,
        string origin,
        IDeviceSignatureCounterStore counterStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relyingPartyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        _counterStore = counterStore ?? throw new ArgumentNullException(nameof(counterStore));

        var normalizedRpId = relyingPartyId.Trim().ToLowerInvariant();
        if (normalizedRpId.Length > 253 ||
            normalizedRpId.StartsWith('.') ||
            normalizedRpId.EndsWith('.') ||
            normalizedRpId.Contains("..", StringComparison.Ordinal) ||
            Uri.CheckHostName(normalizedRpId) != UriHostNameType.Dns ||
            IPAddress.TryParse(normalizedRpId, out _))
        {
            throw new ArgumentException("The WebAuthn relying-party id must be a bounded DNS name.", nameof(relyingPartyId));
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsedOrigin) ||
            !string.Equals(parsedOrigin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(parsedOrigin.UserInfo) ||
            parsedOrigin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(parsedOrigin.Query) ||
            !string.IsNullOrEmpty(parsedOrigin.Fragment) ||
            IPAddress.TryParse(parsedOrigin.IdnHost, out _))
        {
            throw new ArgumentException("The WebAuthn origin must be one exact HTTPS DNS origin.", nameof(origin));
        }

        _origin = parsedOrigin.GetLeftPart(UriPartial.Authority);
        if (!string.Equals(origin, _origin, StringComparison.Ordinal) ||
            !(string.Equals(parsedOrigin.IdnHost, normalizedRpId, StringComparison.Ordinal) ||
              parsedOrigin.IdnHost.EndsWith('.' + normalizedRpId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The WebAuthn origin must be canonical and within its relying-party id.", nameof(origin));
        }

        _relyingPartyIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRpId));
    }

    public async ValueTask<bool> VerifyAsync(
        DevicePublicKey deviceKey,
        HandshakeChallenge challenge,
        DeviceKeyProof proof,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deviceKey);
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(proof);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (deviceKey.Algorithm != DeviceKeyAlgorithm.WebAuthnEs256 ||
                proof.ChallengeId != challenge.ChallengeId ||
                challenge.DeviceKeyId != deviceKey.KeyId ||
                !string.Equals(
                    challenge.CredentialIdBase64Url,
                    deviceKey.CredentialIdBase64Url,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    proof.Assertion.CredentialIdBase64Url,
                    deviceKey.CredentialIdBase64Url,
                    StringComparison.Ordinal) ||
                !proof.Assertion.ClientDataMatches(challenge.TranscriptHashBase64Url))
            {
                return false;
            }

            var authenticatorData = Base64Url.DecodeFromChars(proof.Assertion.AuthenticatorDataBase64Url);
            var clientData = Base64Url.DecodeFromChars(proof.Assertion.ClientDataJsonBase64Url);
            var signature = Base64Url.DecodeFromChars(proof.Assertion.SignatureBase64Url);
            if (authenticatorData.Length < SignatureCounterOffset + SignatureCounterLength ||
                !CryptographicOperations.FixedTimeEquals(
                    authenticatorData.AsSpan(0, RpIdHashLength),
                    _relyingPartyIdHash) ||
                (authenticatorData[FlagsOffset] & (UserPresentFlag | UserVerifiedFlag)) !=
                (UserPresentFlag | UserVerifiedFlag) ||
                !HasExactOrigin(clientData, challenge.TranscriptHashBase64Url))
            {
                return false;
            }

            var clientDataHash = SHA256.HashData(clientData);
            var signedData = new byte[authenticatorData.Length + clientDataHash.Length];
            authenticatorData.CopyTo(signedData, 0);
            clientDataHash.CopyTo(signedData, authenticatorData.Length);
            try
            {
                if (!VerifySignature(deviceKey, signedData, signature))
                {
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(clientDataHash);
                CryptographicOperations.ZeroMemory(signedData);
            }

            var counter = BinaryPrimitives.ReadUInt32BigEndian(
                authenticatorData.AsSpan(SignatureCounterOffset, SignatureCounterLength));
            return await _counterStore.TryAcceptAsync(
                new DeviceSignatureCounter(deviceKey.KeyId, challenge.ChallengeId, counter, challenge.IssuedUtc),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException or JsonException or FormatException)
        {
            return false;
        }
    }

    private bool HasExactOrigin(byte[] clientData, string expectedChallenge)
    {
        using var document = JsonDocument.Parse(clientData, new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return false;
            }
        }

        return document.RootElement.TryGetProperty("type", out var type) &&
               type.ValueKind == JsonValueKind.String && type.ValueEquals("webauthn.get") &&
               document.RootElement.TryGetProperty("challenge", out var challenge) &&
               challenge.ValueKind == JsonValueKind.String && challenge.ValueEquals(expectedChallenge) &&
               document.RootElement.TryGetProperty("origin", out var origin) &&
               origin.ValueKind == JsonValueKind.String && origin.ValueEquals(_origin) &&
               (!document.RootElement.TryGetProperty("crossOrigin", out var crossOrigin) ||
                crossOrigin.ValueKind == JsonValueKind.False);
    }

    private static bool VerifySignature(
        DevicePublicKey deviceKey,
        ReadOnlySpan<byte> signedData,
        ReadOnlySpan<byte> signature)
    {
        var cose = Base64Url.DecodeFromChars(deviceKey.CosePublicKeyBase64Url);
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = cose.AsSpan(10, 32).ToArray(),
                Y = cose.AsSpan(45, 32).ToArray(),
            },
        };
        using var key = ECDsa.Create(parameters);
        return key.VerifyData(
            signedData,
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
    }
}
