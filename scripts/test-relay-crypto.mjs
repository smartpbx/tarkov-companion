// Node test for src/TarkovCompanion.GroupServer/Tablet/relay-crypto.js: verifies the relay-frame
// nonce/AAD byte encoding against the independently computed golden vector (the part that is
// genuinely at risk of a porting bug — AES-256-GCM itself is WebCrypto's, not reimplemented here),
// plus a self-consistent seal/open round trip. Run with `node scripts/test-relay-crypto.mjs`.
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const here = path.dirname(fileURLToPath(import.meta.url));
const crypto = (await import(path.join(here, "../src/TarkovCompanion.GroupServer/Tablet/relay-crypto.js"))).default;

function toHex(bytes) {
  return Array.from(bytes).map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

let failures = 0;
function check(name, condition) {
  if (condition) {
    console.log(`ok - ${name}`);
  } else {
    failures += 1;
    console.error(`FAIL - ${name}`);
  }
}

// --- Nonce/AAD encoding against the golden vector ---------------------------------------------
const vectors = JSON.parse(
  readFileSync(path.join(here, "../tests/TarkovCompanion.CompanionProtocol.Tests/Golden/crypto/paired-handshake-vectors.json"), "utf8"),
);
const opaqueFrameFixture = JSON.parse(
  readFileSync(path.join(here, "../tests/TarkovCompanion.CompanionProtocol.Tests/Golden/relay/opaque-relay-frame.json"), "utf8"),
);
const relayFrameVector = vectors.relayFrame;

check(
  "relayFrame vector direction is TabletToDesktop",
  relayFrameVector.direction === "TabletToDesktop",
);

const nonce = crypto.encodeRelayNonce(opaqueFrameFixture.keyEpoch, opaqueFrameFixture.senderSequence);
check("encodeRelayNonce matches the golden vector", toHex(nonce) === relayFrameVector.nonceHex);

const aad = crypto.encodeRelayAdditionalAuthenticatedData({
  direction: crypto.DIRECTION.TabletToDesktop,
  protocolVersionMajor: opaqueFrameFixture.protocolVersion.major,
  protocolVersionMinor: opaqueFrameFixture.protocolVersion.minor,
  channelId: opaqueFrameFixture.channelId.value,
  sessionId: opaqueFrameFixture.sessionId.value,
  keyEpoch: opaqueFrameFixture.keyEpoch,
  senderSequence: opaqueFrameFixture.senderSequence,
  cipherSuite: 1,
  ciphertextLength: opaqueFrameFixture.ciphertextLength,
  issuedUnixMs: Date.parse(opaqueFrameFixture.issuedUtc),
  expiresUnixMs: Date.parse(opaqueFrameFixture.expiresUtc),
});
check(
  "encodeRelayAdditionalAuthenticatedData matches the golden vector",
  toHex(aad) === relayFrameVector.additionalAuthenticatedDataHex,
);

// The vector's own plaintext framing (2-byte BE kind ‖ UTF-8 JSON) — proves this module's
// ClientCommandEnvelope-sealing callers build the exact same plaintext the vector expects.
const expectedPlaintextHex = relayFrameVector.plaintextHex;
const kindBytes = expectedPlaintextHex.slice(0, 4);
check("relayFrame vector's plaintext kind is ClientCommandEnvelope (0001)", kindBytes === "0001");
const payloadHexFromVector = expectedPlaintextHex.slice(4);
const payloadBytes = crypto.textEncoder.encode(relayFrameVector.payloadUtf8);
check(
  "relayFrame vector's payloadUtf8 matches its own plaintextHex JSON bytes",
  toHex(payloadBytes) === payloadHexFromVector,
);

// --- Self-consistent seal/open round trip (AES-256-GCM correctness + separate ciphertext/tag) --
const trafficKey = crypto.base64UrlDecode("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY"); // 32 bytes
const protocolVersion = { major: 2, minor: 0 };
const channelId = "90000000-0000-4000-8000-000000000001";
const sessionId = "20000000-0000-4000-8000-000000000002";
const now = Date.parse("2026-09-14T20:01:00.000Z");
const commandJson = crypto.textEncoder.encode(JSON.stringify({ hello: "tablet", n: 1 }));

const sealed = await crypto.sealRelayFrame({
  trafficKey,
  direction: crypto.DIRECTION.TabletToDesktop,
  kind: crypto.PAYLOAD_KIND.ClientCommandEnvelope,
  protocolVersion,
  channelId,
  sessionId,
  keyEpoch: 1,
  senderSequence: 1,
  issuedUtc: now,
  expiresUtc: now + 60_000,
  json: commandJson,
});

check("sealRelayFrame's ciphertextLength matches the plaintext length", sealed.ciphertextLength === commandJson.length + 2);
check(
  "sealRelayFrame's authentication tag is 16 bytes",
  crypto.base64UrlDecode(sealed.authenticationTagBase64Url).length === 16,
);

const opened = await crypto.openRelayFrame({ trafficKey, direction: crypto.DIRECTION.TabletToDesktop, frame: sealed });
check("openRelayFrame recovers the sealed kind", opened.kind === crypto.PAYLOAD_KIND.ClientCommandEnvelope);
check(
  "openRelayFrame recovers the exact sealed JSON bytes",
  crypto.textDecoder.decode(opened.json) === JSON.stringify({ hello: "tablet", n: 1 }),
);

// Tampering (wrong direction on open, i.e. a different AAD) must fail closed.
let tamperThrew = false;
try {
  await crypto.openRelayFrame({ trafficKey, direction: crypto.DIRECTION.DesktopToTablet, frame: sealed });
} catch {
  tamperThrew = true;
}
check("opening with the wrong direction (wrong AAD) throws rather than returning garbage", tamperThrew);

// --- Sealed relay credential (v2r-tablet-marks-sync, ABUSE-PAIRED-LIVE-BEARER-THEFT) -----------
const credentialAttemptId = "5a1d3c2e-7b10-4c00-8a00-000000000010";
const credentialTrafficKey = crypto.base64UrlDecode("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY");
const credentialExpiresUtc = Date.parse("2026-09-17T12:01:00.000Z");
const sealedCredential = await crypto.sealPairingCredential({
  trafficKey: credentialTrafficKey,
  attemptId: credentialAttemptId,
  credential: "a-tablet-bearer-secret",
  expiresUtc: credentialExpiresUtc,
});
check(
  "sealPairingCredential's tag is 16 bytes",
  crypto.base64UrlDecode(sealedCredential.authenticationTagBase64Url).length === 16,
);

const openedCredential = await crypto.openPairingCredential({
  trafficKey: credentialTrafficKey,
  attemptId: credentialAttemptId,
  sealedCredential,
});
check("openPairingCredential recovers the exact sealed secret", openedCredential === "a-tablet-bearer-secret");

let wrongAttemptThrew = false;
try {
  await crypto.openPairingCredential({
    trafficKey: credentialTrafficKey,
    attemptId: "00000000-0000-0000-0000-000000000000",
    sealedCredential,
  });
} catch {
  wrongAttemptThrew = true;
}
check("opening a sealed credential against the wrong attempt id throws (AAD binds it)", wrongAttemptThrew);

check("sealPairingCredential's nonce is 12 bytes", crypto.base64UrlDecode(sealedCredential.nonceBase64Url).length === 12);

// Two seals of different secrets under the same traffic key must never share a nonce — a fixed
// nonce reused across calls would be AES-GCM nonce reuse (leaks plaintext XOR and the GHASH key).
const secondSealedCredential = await crypto.sealPairingCredential({
  trafficKey: credentialTrafficKey,
  attemptId: credentialAttemptId,
  credential: "a-different-tablet-bearer-secret",
  expiresUtc: credentialExpiresUtc,
});
check(
  "two seals under the same key produce unrelated (non-identical) random nonces",
  sealedCredential.nonceBase64Url !== secondSealedCredential.nonceBase64Url,
);
check(
  "the second seal opens back to its own distinct secret",
  (await crypto.openPairingCredential({
    trafficKey: credentialTrafficKey,
    attemptId: credentialAttemptId,
    sealedCredential: secondSealedCredential,
  })) === "a-different-tablet-bearer-secret",
);

// --- Device-key signatures (#289) ---------------------------------------------------------------
// WebCrypto signs P1363; the desktop's verifier and the relay's door check DER. Node's own verify
// is the independent judge: it accepts the converted signature as DER, over many signatures so
// the high-bit and leading-zero cases of both integers are all met.
const nodeCrypto = await import("node:crypto");
const deviceKeyPair = await globalThis.crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
const devicePublicKey = nodeCrypto.createPublicKey({
  key: Buffer.from(await globalThis.crypto.subtle.exportKey("spki", deviceKeyPair.publicKey)),
  format: "der",
  type: "spki",
});
let derVerified = 0;
const derLengths = new Set();
for (let i = 0; i < 200; i += 1) {
  const message = new TextEncoder().encode(`assertion ${i}`);
  const raw = new Uint8Array(await globalThis.crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, deviceKeyPair.privateKey, message));
  const der = crypto.ecdsaP1363ToDer(raw);
  derLengths.add(der.length);
  if (nodeCrypto.verify("sha256", message, { key: devicePublicKey, dsaEncoding: "der" }, der)) derVerified += 1;
}
check("200 WebCrypto signatures all verify as DER after ecdsaP1363ToDer", derVerified === 200);
check("both padded and unpadded integers were met", derLengths.size > 1);
check(
  "a raw P1363 signature does not verify as DER (what the page used to send)",
  !nodeCrypto.verify(
    "sha256",
    new TextEncoder().encode("assertion"),
    { key: devicePublicKey, dsaEncoding: "der" },
    new Uint8Array(await globalThis.crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, deviceKeyPair.privateKey, new TextEncoder().encode("assertion"))),
  ),
);
const shortInteger = new Uint8Array(64);
shortInteger[31] = 0x01; // r = 1
shortInteger[32] = 0x80; // s has its high bit set
check(
  "ecdsaP1363ToDer trims leading zeros and pads a high bit",
  toHex(crypto.ecdsaP1363ToDer(shortInteger)) === "3026020101022100" + "80" + "00".repeat(31),
);

// The same vector RelayKeyPossessionTests pins on the relay's side.
check(
  "deviceDoorChallenge matches the relay's RelayPossessionChallenges.DeviceDoorChallenge",
  (await crypto.deviceDoorChallenge("BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc")) === "-q2dGLxQOuZDew4Vnle_BxIkC34nnaISB0d_sTTFdzc",
);

// --- "The desktop is offline" (#407) -------------------------------------------------------------
// The false alarm: a desktop sitting still on one map publishes nothing, so its map is minutes
// old while the relay heard from it a second ago.
const tenMinutesAgo = new Date(Date.now() - 10 * 60_000).toISOString();
check(
  "an old map from a desktop the relay just heard from is online",
  crypto.desktopOnline({ ownerSeenMs: "1200", publishedUtc: tenMinutesAgo, nowMs: Date.now() }) === true,
);
check(
  "a fresh map from a desktop the relay has not heard from is offline",
  crypto.desktopOnline({ ownerSeenMs: "45000", publishedUtc: new Date().toISOString(), nowMs: Date.now() }) === false,
);
check(
  "no map at all, desktop just heard from: online (it has no map open yet)",
  crypto.desktopOnline({ ownerSeenMs: "0", publishedUtc: null, nowMs: Date.now() }) === true,
);
check(
  "an older relay that does not report the owner falls back to the map's age",
  crypto.desktopOnline({ ownerSeenMs: null, publishedUtc: tenMinutesAgo, nowMs: Date.now() }) === false &&
    crypto.desktopOnline({ ownerSeenMs: null, publishedUtc: new Date().toISOString(), nowMs: Date.now() }) === true,
);

if (failures > 0) {
  console.error(`\n${failures} check(s) failed.`);
  process.exit(1);
}

console.log("\nAll relay-crypto checks passed.");
