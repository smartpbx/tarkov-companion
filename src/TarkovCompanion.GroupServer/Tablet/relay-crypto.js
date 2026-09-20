// Byte-exact port of TarkovCompanion.CompanionProtocol.PairingCryptography's relay-frame sealing
// (EncodeRelayNonce, EncodeRelayAdditionalAuthenticatedData, SealRelayFrame, OpenRelayFrame) and
// ProtocolBinaryWriter's length-prefixed encoding. Verified against
// tests/TarkovCompanion.CompanionProtocol.Tests/Golden/crypto/paired-handshake-vectors.json's
// "relayFrame" vector by scripts/test-relay-crypto.mjs (nonce/AAD bytes), plus a self-consistent
// seal/open round trip (AES-256-GCM itself is WebCrypto's, not reimplemented here).
//
// A plain UMD-lite module rather than an ES module: this page is one file with no build step, and
// a <script src> sibling stays that way while still being `require`-able from Node for testing.
(function (root, factory) {
  const exported = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = exported;
  } else {
    root.RelayFrameCrypto = exported;
  }
})(typeof self !== "undefined" ? self : globalThis, () => {
  "use strict";

  const RELAY_AAD_DOMAIN = "TarkovCompanion.PairedDevice/v2/relay-aad";
  const RELAY_CREDENTIAL_AAD_DOMAIN = "TarkovCompanion.PairedDevice/v2/relay-credential";
  const RELAY_NONCE_BYTES = 12;
  const RELAY_TAG_BYTES = 16;
  const RELAY_CIPHERTEXT_CHUNK_CHARACTERS = 1024;
  const CIPHER_SUITE_P256_HKDF_SHA256_AES256_GCM = 1;
  const PAYLOAD_KIND = {
    ClientCommandEnvelope: 1,
    ClientDeliveryAcknowledgement: 2,
    ReconnectRequest: 3,
    ServerEnvelope: 4,
    ReconnectPlan: 5,
  };
  const DIRECTION = { TabletToDesktop: 1, DesktopToTablet: 2 };

  const textEncoder = new TextEncoder();
  const textDecoder = new TextDecoder();

  function base64UrlEncode(bytes) {
    let binary = "";
    for (const byte of bytes) {
      binary += String.fromCharCode(byte);
    }

    return btoa(binary).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
  }

  function base64UrlDecode(value) {
    const padded = value.replace(/-/g, "+").replace(/_/g, "/");
    const remainder = padded.length % 4;
    const withPadding = remainder === 0 ? padded : padded + "=".repeat(4 - remainder);
    const binary = atob(withPadding);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) {
      bytes[index] = binary.charCodeAt(index);
    }

    return bytes;
  }

  function concatBytes(...parts) {
    const total = parts.reduce((sum, part) => sum + part.length, 0);
    const result = new Uint8Array(total);
    let offset = 0;
    for (const part of parts) {
      result.set(part, offset);
      offset += part.length;
    }

    return result;
  }

  function writeUInt16BE(value) {
    const bytes = new Uint8Array(2);
    new DataView(bytes.buffer).setUint16(0, value, false);
    return bytes;
  }

  function writeUInt32BE(value) {
    const bytes = new Uint8Array(4);
    new DataView(bytes.buffer).setUint32(0, value, false);
    return bytes;
  }

  // BigInt because a sender sequence or Unix-millisecond instant exceeds Number's safe 32-bit
  // DataView setters; ProtocolBounds bounds both well inside Number.MAX_SAFE_INTEGER, so BigInt
  // round-trips through Number exactly for every value this module ever writes or reads.
  function writeUInt64BE(value) {
    const bytes = new Uint8Array(8);
    new DataView(bytes.buffer).setBigUint64(0, BigInt(value), false);
    return bytes;
  }

  function writeInt64BE(value) {
    const bytes = new Uint8Array(8);
    new DataView(bytes.buffer).setBigInt64(0, BigInt(value), false);
    return bytes;
  }

  /// A protocol id's "value" field, dash-free, as raw bytes: the RFC 9562 string form already is
  /// the big-endian byte order .NET's `Guid.TryWriteBytes(bigEndian: true)` produces, so no
  /// byte-swapping is needed the way it would be from `Guid`'s little-endian internal layout.
  function uuidToBytes(uuid) {
    const hex = uuid.replace(/-/g, "");
    if (hex.length !== 32) {
      throw new Error(`Not a UUID: ${uuid}`);
    }

    const bytes = new Uint8Array(16);
    for (let index = 0; index < 16; index += 1) {
      bytes[index] = Number.parseInt(hex.substr(index * 2, 2), 16);
    }

    return bytes;
  }

  // ProtocolBinaryWriter.Bytes/Utf8: a uint32-BE length prefix followed by the raw bytes.
  function writeLengthPrefixed(bytes) {
    return concatBytes(writeUInt32BE(bytes.length), bytes);
  }

  function writeUtf8(value) {
    return writeLengthPrefixed(textEncoder.encode(value));
  }

  /// PairingCryptography.EncodeRelayNonce: 4-byte BE key epoch ‖ 8-byte BE sender sequence.
  function encodeRelayNonce(keyEpoch, senderSequence) {
    return concatBytes(writeUInt32BE(keyEpoch), writeUInt64BE(senderSequence));
  }

  /// PairingCryptography.EncodeRelayAdditionalAuthenticatedData, field for field.
  function encodeRelayAdditionalAuthenticatedData(fields) {
    return concatBytes(
      writeUtf8(RELAY_AAD_DOMAIN),
      writeUInt16BE(fields.direction),
      writeUInt16BE(fields.protocolVersionMajor),
      writeUInt16BE(fields.protocolVersionMinor),
      uuidToBytes(fields.channelId),
      uuidToBytes(fields.sessionId),
      writeUInt32BE(fields.keyEpoch),
      writeUInt64BE(fields.senderSequence),
      writeUInt16BE(fields.cipherSuite),
      writeUInt32BE(fields.ciphertextLength),
      writeInt64BE(fields.issuedUnixMs),
      writeInt64BE(fields.expiresUnixMs),
    );
  }

  function chunk(text, size) {
    const chunks = [];
    for (let offset = 0; offset < text.length; offset += size) {
      chunks.push(text.slice(offset, offset + size));
    }

    return chunks;
  }

  /// Seals one plaintext root into the OpaqueRelayFrame wire shape
  /// (src/TarkovCompanion.CompanionProtocol/Envelopes.cs), matching
  /// PairingCryptography.SealRelayFrame byte for byte except for the AES-GCM primitive itself,
  /// which is WebCrypto's.
  /// A traffic key as raw bytes, or as a CryptoKey that was already imported. A tablet that keeps
  /// its session across a page reload stores the imported, non-extractable form in IndexedDB, so
  /// the key material itself can be used from script but never read back out of storage by it.
  async function trafficCipherKey(trafficKey, usage) {
    if (typeof CryptoKey !== "undefined" && trafficKey instanceof CryptoKey) return trafficKey;
    return crypto.subtle.importKey("raw", trafficKey, "AES-GCM", false, [usage]);
  }

  /// Imports one direction's traffic key for keeping: non-extractable, and usable only the way
  /// that direction is ever used from a tablet (it seals tablet-to-desktop, opens desktop-to-tablet).
  function importStoredTrafficKey(rawKey, usage) {
    return crypto.subtle.importKey("raw", rawKey, "AES-GCM", false, [usage]);
  }

  async function sealRelayFrame({
    trafficKey,
    direction,
    kind,
    protocolVersion,
    channelId,
    sessionId,
    keyEpoch,
    senderSequence,
    issuedUtc,
    expiresUtc,
    json,
  }) {
    const plaintext = concatBytes(writeUInt16BE(kind), json);
    const nonce = encodeRelayNonce(keyEpoch, senderSequence);
    const aad = encodeRelayAdditionalAuthenticatedData({
      direction,
      protocolVersionMajor: protocolVersion.major,
      protocolVersionMinor: protocolVersion.minor,
      channelId,
      sessionId,
      keyEpoch,
      senderSequence,
      cipherSuite: CIPHER_SUITE_P256_HKDF_SHA256_AES256_GCM,
      ciphertextLength: plaintext.length,
      issuedUnixMs: issuedUtc,
      expiresUnixMs: expiresUtc,
    });
    const key = await trafficCipherKey(trafficKey, "encrypt");
    const sealed = new Uint8Array(
      await crypto.subtle.encrypt({ name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: RELAY_TAG_BYTES * 8 }, key, plaintext),
    );
    // WebCrypto appends the tag to the ciphertext; OpaqueRelayFrame carries them as separate
    // fields, matching AesGcm.Encrypt's own separate ciphertext/tag output on the .NET side.
    const ciphertext = sealed.slice(0, sealed.length - RELAY_TAG_BYTES);
    const tag = sealed.slice(sealed.length - RELAY_TAG_BYTES);
    return {
      protocolVersion,
      channelId: { value: channelId },
      sessionId: { value: sessionId },
      keyEpoch,
      senderSequence,
      cipherSuite: "P256HkdfSha256Aes256Gcm",
      nonceBase64Url: base64UrlEncode(nonce),
      ciphertextLength: ciphertext.length,
      ciphertextChunksBase64Url: chunk(base64UrlEncode(ciphertext), RELAY_CIPHERTEXT_CHUNK_CHARACTERS),
      authenticationTagBase64Url: base64UrlEncode(tag),
      issuedUtc: new Date(issuedUtc).toISOString(),
      expiresUtc: new Date(expiresUtc).toISOString(),
    };
  }

  /// Opens an OpaqueRelayFrame (as read from JSON) with the receiver's directional key. Throws on
  /// any authentication failure, the same fail-closed shape PairingCryptography.OpenRelayFrame has.
  async function openRelayFrame({ trafficKey, direction, frame }) {
    const ciphertext = base64UrlDecode(frame.ciphertextChunksBase64Url.join(""));
    const tag = base64UrlDecode(frame.authenticationTagBase64Url);
    const nonce = base64UrlDecode(frame.nonceBase64Url);
    const aad = encodeRelayAdditionalAuthenticatedData({
      direction,
      protocolVersionMajor: frame.protocolVersion.major,
      protocolVersionMinor: frame.protocolVersion.minor,
      channelId: frame.channelId.value,
      sessionId: frame.sessionId.value,
      keyEpoch: frame.keyEpoch,
      senderSequence: frame.senderSequence,
      cipherSuite: CIPHER_SUITE_P256_HKDF_SHA256_AES256_GCM,
      ciphertextLength: frame.ciphertextLength,
      issuedUnixMs: Date.parse(frame.issuedUtc),
      expiresUnixMs: Date.parse(frame.expiresUtc),
    });
    const key = await trafficCipherKey(trafficKey, "decrypt");
    const plaintext = new Uint8Array(
      await crypto.subtle.decrypt(
        { name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: RELAY_TAG_BYTES * 8 },
        key,
        concatBytes(ciphertext, tag),
      ),
    );
    if (plaintext.length < 3) {
      throw new Error("The authenticated relay payload is too short to carry a kind and a root.");
    }

    const kind = new DataView(plaintext.buffer, plaintext.byteOffset, 2).getUint16(0, false);
    return { kind, json: plaintext.slice(2) };
  }

  function encodeRelayCredentialAad(attemptId, expiresUnixMs) {
    return concatBytes(writeUtf8(RELAY_CREDENTIAL_AAD_DOMAIN), uuidToBytes(attemptId), writeInt64BE(expiresUnixMs));
  }

  /// The tablet's own relay bearer secret, sealed for its one-time trip through the pairing
  /// mailbox (v2r-tablet-marks-sync, ABUSE-PAIRED-LIVE-BEARER-THEFT). Mirrors
  /// TarkovCompanion.CompanionProtocol.RelayCredentialCryptography.Seal byte for byte, reusing
  /// this same module's AES-256-GCM primitive but with its own domain-separated AAD — never call
  /// this with a session/channel/keyEpoch the way sealRelayFrame is, it has none. The nonce is
  /// fresh and random per call (CompanionPairingMailbox.SubmitRelaySession is first-write-wins, but
  /// a caller sealing this has no way to know that itself, and a fixed nonce reused across two
  /// seals under the same key would be AES-GCM nonce reuse — it leaks the two plaintexts' XOR and
  /// the authenticator's key).
  async function sealPairingCredential({ trafficKey, attemptId, credential, expiresUtc }) {
    const nonce = crypto.getRandomValues(new Uint8Array(RELAY_NONCE_BYTES));
    const plaintext = textEncoder.encode(credential);
    const aad = encodeRelayCredentialAad(attemptId, expiresUtc);
    const key = await trafficCipherKey(trafficKey, "encrypt");
    const sealed = new Uint8Array(
      await crypto.subtle.encrypt(
        { name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: RELAY_TAG_BYTES * 8 },
        key,
        plaintext,
      ),
    );
    const ciphertext = sealed.slice(0, sealed.length - RELAY_TAG_BYTES);
    const tag = sealed.slice(sealed.length - RELAY_TAG_BYTES);
    return {
      nonceBase64Url: base64UrlEncode(nonce),
      ciphertextBase64Url: base64UrlEncode(ciphertext),
      authenticationTagBase64Url: base64UrlEncode(tag),
      expiresUtc: new Date(expiresUtc).toISOString(),
    };
  }

  /// Opens a sealed credential; any authentication failure throws.
  async function openPairingCredential({ trafficKey, attemptId, sealedCredential }) {
    const nonce = base64UrlDecode(sealedCredential.nonceBase64Url);
    const ciphertext = base64UrlDecode(sealedCredential.ciphertextBase64Url);
    const tag = base64UrlDecode(sealedCredential.authenticationTagBase64Url);
    const aad = encodeRelayCredentialAad(attemptId, Date.parse(sealedCredential.expiresUtc));
    const key = await trafficCipherKey(trafficKey, "decrypt");
    const plaintext = new Uint8Array(
      await crypto.subtle.decrypt(
        { name: "AES-GCM", iv: nonce, additionalData: aad, tagLength: RELAY_TAG_BYTES * 8 },
        key,
        concatBytes(ciphertext, tag),
      ),
    );
    return textDecoder.decode(plaintext);
  }

  // WebCrypto signs ECDSA as the two 32-byte integers side by side (IEEE P1363). A WebAuthn
  // assertion carries them as an ASN.1 DER SEQUENCE of two INTEGERs, which is what the desktop's
  // proof verifier and the relay's door both check, so every device-key signature this page sends
  // goes through here. Sent raw, the proof never verified and no browser could ever pair.
  function derInteger(bytes) {
    let start = 0;
    while (start < bytes.length - 1 && bytes[start] === 0) start += 1;
    const trimmed = bytes.slice(start);
    const padded = trimmed[0] & 0x80 ? concatBytes(new Uint8Array([0]), trimmed) : trimmed;
    return concatBytes(new Uint8Array([0x02, padded.length]), padded);
  }

  function ecdsaP1363ToDer(signature) {
    if (signature.length !== 64) throw new Error("A P-256 signature is two 32-byte integers.");
    const body = concatBytes(derInteger(signature.slice(0, 32)), derInteger(signature.slice(32)));
    return concatBytes(new Uint8Array([0x30, body.length]), body);
  }

  const DEVICE_DOOR_DOMAIN = "TarkovCompanion.PairedDevice/v2/relay-resume-door";

  /// What a returning device signs for the relay (RelayPossessionChallenges.DeviceDoorChallenge):
  /// a hash of the relay's nonce under its own label, never the bare nonce. A device-key
  /// assertion over a bare 32-byte value is what the desktop accepts as a handshake proof.
  async function deviceDoorChallenge(nonceBase64Url) {
    const input = concatBytes(textEncoder.encode(DEVICE_DOOR_DOMAIN), base64UrlDecode(nonceBase64Url));
    return base64UrlEncode(new Uint8Array(await crypto.subtle.digest("SHA-256", input)));
  }

  // The desktop reads its relay queue every two seconds whether or not anything is happening, so
  // the relay's "last heard from the owner" is what says the desktop is there. Several missed
  // reads, not one: a laptop on wifi drops a request now and then.
  const OWNER_SILENT_MS = 15_000;
  // Only for a relay too old to report the owner: the age of the map it last published, which is
  // wrong for a desktop sitting still on one map (it publishes nothing) and is all there is.
  const SURFACE_STALE_MS = 20_000;

  /// Whether the tablet should say the desktop is there. `ownerSeenMs` is the relay's
  /// X-Relay-Owner-Seen-Ms header (null from an older relay), `publishedUtc` the map's own stamp.
  function desktopOnline({ ownerSeenMs, publishedUtc, nowMs }) {
    if (ownerSeenMs !== null && ownerSeenMs !== undefined && ownerSeenMs !== "") {
      const silentFor = Number(ownerSeenMs);
      if (Number.isFinite(silentFor)) return silentFor < OWNER_SILENT_MS;
    }

    if (!publishedUtc) return false;
    const age = nowMs - Date.parse(publishedUtc);
    return Number.isFinite(age) && age < SURFACE_STALE_MS;
  }

  return {
    PAYLOAD_KIND,
    DIRECTION,
    DEVICE_DOOR_DOMAIN,
    OWNER_SILENT_MS,
    desktopOnline,
    ecdsaP1363ToDer,
    deviceDoorChallenge,
    RELAY_AAD_DOMAIN,
    RELAY_CREDENTIAL_AAD_DOMAIN,
    base64UrlEncode,
    base64UrlDecode,
    encodeRelayNonce,
    encodeRelayAdditionalAuthenticatedData,
    importStoredTrafficKey,
    sealRelayFrame,
    openRelayFrame,
    sealPairingCredential,
    openPairingCredential,
    textDecoder,
    textEncoder,
  };
});
