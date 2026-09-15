"""Generates paired-protocol handshake, key-schedule, and relay vectors from the normative spec."""
import datetime, hashlib, hmac, ipaddress, json, os, sys
from primitives import *

assert self_test()
OUT = sys.argv[1]

def ms(text):
    return int(datetime.datetime.fromisoformat(text).timestamp() * 1000)

def ts(text):  # System.Text.Json DateTimeOffset spelling with zero offset
    return text

def gid(text): return {"value": text}

V = {"major": 2, "minor": 0}

# ---------- keys ----------
def keypair(label):
    d = private_key(label)
    q = mul(d)
    return d, q

d_dev, q_dev = keypair("paired-vector/tablet-device-es256")
d_id, q_id = keypair("paired-vector/desktop-identity-es256")
d_te, q_te = keypair("paired-vector/tablet-ephemeral-pairing")
d_de, q_de = keypair("paired-vector/desktop-ephemeral-pairing")
d_te2, q_te2 = keypair("paired-vector/tablet-ephemeral-resume")
d_de2, q_de2 = keypair("paired-vector/desktop-ephemeral-resume")

cose = bytes.fromhex("a5010203262001215820") + q_dev[0].to_bytes(32, "big") + b"\x22\x58\x20" + q_dev[1].to_bytes(32, "big")
assert len(cose) == 77
device_key_id = hashlib.sha256(cose).digest()
credential_id = hashlib.sha256(b"paired-vector/credential-id").digest()[:16]
identity_spki = spki(q_id)
identity_key_id = hashlib.sha256(identity_spki).digest()
te_spki, de_spki = spki(q_te), spki(q_de)
te2_spki, de2_spki = spki(q_te2), spki(q_de2)
client_nonce = hashlib.sha256(b"paired-vector/client-nonce-pairing").digest()
desktop_nonce = hashlib.sha256(b"paired-vector/desktop-nonce-pairing").digest()
client_nonce2 = hashlib.sha256(b"paired-vector/client-nonce-resume").digest()
desktop_nonce2 = hashlib.sha256(b"paired-vector/desktop-nonce-resume").digest()

ATTEMPT = "5a1d3c2e-7b10-4c00-8a00-000000000001"
CHALLENGE = "5a1d3c2e-7b10-4c00-8a00-000000000010"
CHALLENGE2 = "5a1d3c2e-7b10-4c00-8a00-000000000011"
DEVICE = "10000000-0000-4000-8000-000000000002"
SESSION = "20000000-0000-4000-8000-000000000002"
SESSION2 = "20000000-0000-4000-8000-000000000003"
CHANNEL = "90000000-0000-4000-8000-000000000001"
CHANNEL2 = "90000000-0000-4000-8000-000000000002"
EPOCH = "30000000-0000-0000-0000-000000000001"

OFFERED, OFFER_EXPIRES = "2026-09-14T20:00:00+00:00", "2026-09-14T20:05:00+00:00"
ISSUED, CH_EXPIRES = "2026-09-14T20:00:30+00:00", "2026-09-14T20:02:30+00:00"
SESSION_EXPIRES = "2026-09-15T08:00:00+00:00"
ESTABLISHED = "2026-09-14T20:00:40+00:00"
ISSUED2, CH2_EXPIRES = "2026-09-14T21:00:00+00:00", "2026-09-14T21:02:00+00:00"
SESSION2_EXPIRES = "2026-09-15T09:00:00+00:00"
ESTABLISHED2 = "2026-09-14T21:00:10+00:00"

def eph_json(point_spki): return {"algorithm": "EcdhP256", "subjectPublicKeyInfoBase64Url": b64u(point_spki)}
identity_json = {"keyId": gid(b64u(identity_key_id)), "algorithm": "EcdsaP256Sha256", "subjectPublicKeyInfoBase64Url": b64u(identity_spki)}
device_key_json = {"keyId": gid(b64u(device_key_id)), "algorithm": "WebAuthnEs256", "credentialIdBase64Url": b64u(credential_id), "cosePublicKeyBase64Url": b64u(cose)}

# ---------- nonce commitment, pairing request context, sealed name, commitment, code ----------
nonce_commitment = hashlib.sha256(utf8("TarkovCompanion.PairedDevice/v2/desktop-nonce-commitment") + lp(desktop_nonce)).digest()
context = (utf8("TarkovCompanion.PairedDevice/v2/pairing-request-context") + version(2, 0) + uid(ATTEMPT)
           + lp(identity_key_id) + lp(de_spki) + lp(nonce_commitment) + lp(device_key_id) + lp(credential_id)
           + lp(te_spki) + lp(client_nonce) + i64(ms(OFFER_EXPIRES)))
context_hash = hashlib.sha256(context).digest()
shared = ecdh(d_te, q_de)
assert shared == ecdh(d_de, q_te)
name = "Living-room tablet"
name_key = hkdf(shared, context_hash, b"TarkovCompanion.PairedDevice/v2/device-name-key", 32)
name_ct, name_tag = aes_gcm_encrypt(name_key, bytes(12), name.encode(), context_hash)
commitment = hashlib.sha256(utf8("TarkovCompanion.PairedDevice/v2/pairing-commitment") + lp(context_hash) + lp(name_ct) + lp(name_tag)
                            + lp(desktop_nonce)).digest()
code = "%06d" % (int.from_bytes(commitment[:4], "big") % 1_000_000)

assignment = {"protocolVersion": V, "deviceId": gid(DEVICE), "sessionId": gid(SESSION), "relayChannelId": gid(CHANNEL),
              "keyEpoch": 1, "cipherSuite": "P256HkdfSha256Aes256Gcm", "sessionExpiresUtc": SESSION_EXPIRES}

def transcript(purpose, challenge_id, attempt, commit, device, te, de, cn, dn, session, channel, epoch, session_expires, issued, expires):
    return (utf8("TarkovCompanion.PairedDevice/v2/handshake-transcript") + u16(purpose) + version(2, 0)
            + uid(challenge_id) + (uid(attempt) if attempt else bytes(16)) + lp(commit)
            + uid(device) + lp(device_key_id) + lp(credential_id) + lp(identity_key_id)
            + u16(1) + lp(te) + u16(1) + lp(de) + lp(cn) + lp(dn)
            + uid(session) + uid(channel) + u16(1) + u32(epoch)
            + i64(ms(session_expires)) + i64(ms(issued)) + i64(ms(expires)))

t1 = transcript(1, CHALLENGE, ATTEMPT, commitment, DEVICE, te_spki, de_spki, client_nonce, desktop_nonce, SESSION, CHANNEL, 1, SESSION_EXPIRES, ISSUED, CH_EXPIRES)
h1 = hashlib.sha256(t1).digest()
sig_input1 = utf8("TarkovCompanion.PairedDevice/v2/desktop-handshake-signature") + lp(h1)
r1, s1 = ecdsa_sign(d_id, sig_input1, "desktop")
assert ecdsa_verify(q_id, sig_input1, r1, s1)
k_t2d = hkdf(shared, h1, b"TarkovCompanion.PairedDevice/v2/tablet-to-desktop", 32)
k_d2t = hkdf(shared, h1, b"TarkovCompanion.PairedDevice/v2/desktop-to-tablet", 32)

# ---------- WebAuthn assertion ----------
rp_id = "companion.example"  # RFC 2606 name; a deployment pins its own tablet origin
def assertion(challenge_hash, counter, label):
    auth = hashlib.sha256(rp_id.encode()).digest() + bytes([0x05]) + u32(counter)
    client = json.dumps({"type": "webauthn.get", "challenge": b64u(challenge_hash), "origin": "https://" + rp_id, "crossOrigin": False}, separators=(",", ":")).encode()
    signed = auth + hashlib.sha256(client).digest()
    r, s = ecdsa_sign(d_dev, signed, label)
    assert ecdsa_verify(q_dev, signed, r, s)
    return {"credentialIdBase64Url": b64u(credential_id), "authenticatorDataBase64Url": b64u(auth), "clientDataJsonBase64Url": b64u(client), "signatureBase64Url": b64u(der(r, s))}

offer = {"attemptId": gid(ATTEMPT), "desktopIdentityKey": identity_json, "desktopEphemeralKey": eph_json(de_spki),
         "desktopNonceCommitmentBase64Url": b64u(nonce_commitment), "offeredUtc": OFFERED, "expiresUtc": OFFER_EXPIRES}
reveal = {"attemptId": gid(ATTEMPT), "desktopNonceBase64Url": b64u(desktop_nonce)}
request = {"attemptId": gid(ATTEMPT), "negotiatedVersion": V, "deviceKey": device_key_json, "ephemeralKey": eph_json(te_spki),
           "clientNonceBase64Url": b64u(client_nonce), "requestedDeviceName": {"ciphertextBase64Url": b64u(name_ct), "authenticationTagBase64Url": b64u(name_tag)}}
challenge = {"challengeId": gid(CHALLENGE), "purpose": "Pairing", "attemptId": gid(ATTEMPT), "pairingCommitmentBase64Url": b64u(commitment),
             "deviceKeyId": gid(b64u(device_key_id)), "credentialIdBase64Url": b64u(credential_id), "assignment": assignment,
             "desktopIdentityKey": identity_json, "desktopEphemeralKey": eph_json(de_spki), "desktopNonceBase64Url": b64u(desktop_nonce),
             "transcriptHashBase64Url": b64u(h1), "desktopSignatureBase64Url": b64u(p1363(r1, s1)), "issuedUtc": ISSUED, "expiresUtc": CH_EXPIRES}
proof = {"challengeId": gid(CHALLENGE), "assertion": assertion(h1, 1, "pairing")}
established = {"challengeId": gid(CHALLENGE), "purpose": "Pairing", "deviceKeyId": gid(b64u(device_key_id)), "assignment": assignment,
               "transcriptHashBase64Url": b64u(h1), "establishedUtc": ESTABLISHED}

# ---------- session resume ----------
assignment2 = {"protocolVersion": V, "deviceId": gid(DEVICE), "sessionId": gid(SESSION2), "relayChannelId": gid(CHANNEL2),
               "keyEpoch": 2, "cipherSuite": "P256HkdfSha256Aes256Gcm", "sessionExpiresUtc": SESSION2_EXPIRES}
resume = {"negotiatedVersion": V, "deviceId": gid(DEVICE), "deviceKeyId": gid(b64u(device_key_id)), "credentialIdBase64Url": b64u(credential_id),
          "ephemeralKey": eph_json(te2_spki), "clientNonceBase64Url": b64u(client_nonce2)}
t2 = transcript(2, CHALLENGE2, None, b"", DEVICE, te2_spki, de2_spki, client_nonce2, desktop_nonce2, SESSION2, CHANNEL2, 2, SESSION2_EXPIRES, ISSUED2, CH2_EXPIRES)
h2 = hashlib.sha256(t2).digest()
sig_input2 = utf8("TarkovCompanion.PairedDevice/v2/desktop-handshake-signature") + lp(h2)
r2, s2 = ecdsa_sign(d_id, sig_input2, "desktop-resume")
shared2 = ecdh(d_te2, q_de2)
challenge2 = {"challengeId": gid(CHALLENGE2), "purpose": "SessionResume", "attemptId": None, "pairingCommitmentBase64Url": None,
              "deviceKeyId": gid(b64u(device_key_id)), "credentialIdBase64Url": b64u(credential_id), "assignment": assignment2,
              "desktopIdentityKey": identity_json, "desktopEphemeralKey": eph_json(de2_spki), "desktopNonceBase64Url": b64u(desktop_nonce2),
              "transcriptHashBase64Url": b64u(h2), "desktopSignatureBase64Url": b64u(p1363(r2, s2)), "issuedUtc": ISSUED2, "expiresUtc": CH2_EXPIRES}
proof2 = {"challengeId": gid(CHALLENGE2), "assertion": assertion(h2, 2, "resume")}
established2 = {"challengeId": gid(CHALLENGE2), "purpose": "SessionResume", "deviceKeyId": gid(b64u(device_key_id)), "assignment": assignment2,
                "transcriptHashBase64Url": b64u(h2), "establishedUtc": ESTABLISHED2}

# ---------- relay frame (tablet to desktop, first session) ----------
plaintext_obj = {
    "protocolVersion": V, "sessionId": gid(SESSION), "authorityEpoch": gid(EPOCH), "clientSentUtc": "2026-09-14T20:01:00+00:00",
    "command": {"type": "setInteractionMode", "commandId": gid("40000000-0000-4000-8000-000000000001"), "requestedRevision": {"value": 1},
                "issuedUtc": "2026-09-14T20:01:00+00:00", "expiresUtc": "2026-09-14T20:02:00+00:00", "offlineQueuePreview": None, "mode": "Independent"}}
payload_json = json.dumps(plaintext_obj, separators=(",", ":")).encode()
plaintext = u16(1) + payload_json  # payload kind 1: ClientCommandEnvelope
FRAME_ISSUED, FRAME_EXPIRES = "2026-09-14T20:01:00+00:00", "2026-09-14T20:02:00+00:00"
nonce = u32(1) + u64(1)
aad = (utf8("TarkovCompanion.PairedDevice/v2/relay-aad") + u16(1) + version(2, 0) + uid(CHANNEL) + uid(SESSION)
       + u32(1) + u64(1) + u16(1) + u32(len(plaintext)) + i64(ms(FRAME_ISSUED)) + i64(ms(FRAME_EXPIRES)))
frame_ct, frame_tag = aes_gcm_encrypt(k_t2d, nonce, plaintext, aad)
text = b64u(frame_ct)
chunks = [text[i:i + 1024] for i in range(0, len(text), 1024)]
frame = {"protocolVersion": V, "channelId": gid(CHANNEL), "sessionId": gid(SESSION), "keyEpoch": 1, "senderSequence": 1,
         "cipherSuite": "P256HkdfSha256Aes256Gcm", "nonceBase64Url": b64u(nonce), "ciphertextLength": len(frame_ct),
         "ciphertextChunksBase64Url": chunks, "authenticationTagBase64Url": b64u(frame_tag), "issuedUtc": FRAME_ISSUED, "expiresUtc": FRAME_EXPIRES}

# ---------- transport binding ----------
source_key = hashlib.sha256(b"paired-vector/source-hash-key").digest()
def source_hash(address):
    ip = ipaddress.ip_address(address)
    if isinstance(ip, ipaddress.IPv6Address) and ip.ipv4_mapped is not None:
        ip = ip.ipv4_mapped
    raw = ip.packed if ip.version == 4 else ip.packed[:8]
    return b64u(hmac.new(source_key, utf8("TarkovCompanion.PairedDevice/v2/pairing-source") + lp(raw), hashlib.sha256).digest())
PAIRING_CODE = "7K2M9QXR4T"
transport_binding = {
    "sourceHashKeyHex": source_key.hex(),
    "sourceHashes": [{"address": a, "sourceHash": source_hash(a)} for a in ["192.168.1.20", "::ffff:192.168.1.20", "2001:db8:1234:5678::1", "2001:db8:1234:5678:ffff::9"]],
    "testPairingCode": PAIRING_CODE,
    "qrPayload": "TARKOV-COMPANION-PAIR/2.0/" + PAIRING_CODE + "/" + b64u(identity_key_id),
}
assert transport_binding["sourceHashes"][0]["sourceHash"] == transport_binding["sourceHashes"][1]["sourceHash"]
assert transport_binding["sourceHashes"][2]["sourceHash"] == transport_binding["sourceHashes"][3]["sourceHash"]

vectors = {
    "comment": "Computed by an independent pure-Python implementation of docs/PAIRED_DEVICE_PROTOCOL.md (not by the C# library). Private keys are test-only.",
    "testOnlyKeys": {
        name: {"dHex": d.to_bytes(32, "big").hex(), "xHex": q[0].to_bytes(32, "big").hex(), "yHex": q[1].to_bytes(32, "big").hex()}
        for name, d, q in [
            ("tabletDevice", d_dev, q_dev), ("desktopIdentity", d_id, q_id),
            ("tabletEphemeralPairing", d_te, q_te), ("desktopEphemeralPairing", d_de, q_de),
            ("tabletEphemeralResume", d_te2, q_te2), ("desktopEphemeralResume", d_de2, q_de2)]
    },
    "rpId": rp_id,
    "pairing": {
        "requestContextHex": context.hex(),
        "requestContextHashBase64Url": b64u(context_hash),
        "sharedSecretHex": shared.hex(),
        "deviceName": name,
        "deviceNameKeyHex": name_key.hex(),
        "desktopNonceCommitmentBase64Url": b64u(nonce_commitment),
        "commitmentBase64Url": b64u(commitment),
        "verificationCode": code,
        "transcriptHex": t1.hex(),
        "transcriptHashBase64Url": b64u(h1),
        "desktopSignatureInputHex": sig_input1.hex(),
        "tabletToDesktopKeyHex": k_t2d.hex(),
        "desktopToTabletKeyHex": k_d2t.hex(),
    },
    "sessionResume": {
        "sharedSecretHex": shared2.hex(),
        "transcriptHex": t2.hex(),
        "transcriptHashBase64Url": b64u(h2),
    },
    "relayFrame": {
        "direction": "TabletToDesktop",
        "payloadKind": "ClientCommandEnvelope",
        "nonceHex": nonce.hex(),
        "additionalAuthenticatedDataHex": aad.hex(),
        "plaintextHex": plaintext.hex(),
        "payloadUtf8": payload_json.decode(),
    },
    "transportBinding": transport_binding,
}

def write(relative, obj):
    path = os.path.join(OUT, relative)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as handle:
        json.dump(obj, handle, indent=2)
        handle.write("\n")

write("crypto/paired-handshake-vectors.json", vectors)
write("handshake/pairing-offer.json", offer)
write("handshake/pairing-request.json", request)
write("handshake/pairing-nonce-reveal.json", reveal)
write("handshake/pairing-challenge.json", challenge)
write("handshake/pairing-proof.json", proof)
write("handshake/pairing-established.json", established)
write("handshake/session-resume-request.json", resume)
write("handshake/session-resume-challenge.json", challenge2)
write("handshake/session-resume-proof.json", proof2)
write("handshake/session-resume-established.json", established2)
write("relay/opaque-relay-frame.json", frame)
print("verification code", code, "frame bytes", len(frame_ct))
