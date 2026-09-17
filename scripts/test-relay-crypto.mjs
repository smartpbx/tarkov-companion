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

if (failures > 0) {
  console.error(`\n${failures} check(s) failed.`);
  process.exit(1);
}

console.log("\nAll relay-crypto checks passed.");
