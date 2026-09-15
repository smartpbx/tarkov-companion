"""A stand-in for Sigstore that keeps the properties the release scripts depend on.

Real signing needs the publisher's OIDC identity, which no pull request has. What the fixtures
need is narrower: bundles shaped exactly like cosign v3's standardized v0.3 message-signature
bundles, bound to the bytes they sign and to a certificate identity, and a cosign that refuses
anything else. The "certificate" here is the claims as JSON, which is enough for the fake to
check every identity flag the scripts pass.

The fake cosign is installed by content, like the real one: every consumer refuses a cosign whose
sha256 is not pinned, so the tests pin this one through TARKOV_COSIGN_SHA256.
"""

from __future__ import annotations

import base64
import hashlib
import json
from pathlib import Path


MEDIA_TYPE = "application/vnd.dev.sigstore.bundle.v0.3+json"
PUBLISHER = {
    "identity": "https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main",
    "issuer": "https://token.actions.githubusercontent.com",
    "repository": "smartpbx/tarkov-companion",
    "ref": "refs/heads/main",
}


def bundle_for(data: bytes, **claims: str) -> dict:
    certificate = {**PUBLISHER, **claims}
    return {
        "mediaType": MEDIA_TYPE,
        "verificationMaterial": {
            "certificate": {"rawBytes": base64.b64encode(json.dumps(certificate, sort_keys=True).encode()).decode()},
            "tlogEntries": [{"logIndex": "1", "kindVersion": {"kind": "hashedrekord", "version": "0.0.1"}}],
        },
        "messageSignature": {
            "messageDigest": {"algorithm": "SHA2_256", "digest": base64.b64encode(hashlib.sha256(data).digest()).decode()},
            "signature": base64.b64encode(b"fixture signature").decode(),
        },
    }


def bundle_text(data: bytes, **claims: str) -> str:
    return json.dumps(bundle_for(data, **claims))


# A P-256 public key, as an attacker would put one where a legacy bundle's certificate goes.
BARE_PUBLIC_KEY = (
    "-----BEGIN PUBLIC KEY-----\n"
    "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEbz9n6T2kS9J3o0yWcW3u0t0c0m5L\n"
    "l0p7q3C0u2QmQ1f2yJzJb7l6v4GJv6aOaVn6w8bJz2x0yK6X1y4mE6s3Aw==\n"
    "-----END PUBLIC KEY-----\n"
)


def hostile_bundles(data: bytes) -> dict[str, object]:
    """Bundles a verifier must refuse for these bytes before any cryptography is consulted.

    Each is a shape this publisher never produces. The first is GHSA-fx35-mq7g-6g98: a legacy
    bundle whose `cert` is a bare public key, which cosign before v3.1.3 verified without
    checking the certificate identity at all.
    """
    good = bundle_for(data)
    legacy = {
        "base64Signature": base64.b64encode(b"signature over the payload").decode(),
        "cert": base64.b64encode(BARE_PUBLIC_KEY.encode()).decode(),
        "rekorBundle": {"SignedEntryTimestamp": "", "Payload": {"body": "", "integratedTime": 0, "logIndex": 0, "logID": ""}},
    }

    def changed(path: tuple[str, ...], value: object) -> dict:
        bundle = json.loads(json.dumps(good))
        target = bundle
        for key in path[:-1]:
            target = target[key]
        if value is _REMOVE:
            del target[path[-1]]
        else:
            target[path[-1]] = value
        return bundle

    return {
        "legacy bundle carrying a bare public key (GHSA-fx35-mq7g-6g98)": legacy,
        "legacy fields beside a standardized bundle": {**good, **legacy},
        "v0.1 media type": changed(("mediaType",), "application/vnd.dev.sigstore.bundle+json;version=0.1"),
        "no media type": changed(("mediaType",), _REMOVE),
        "DSSE envelope instead of a message signature": {
            "mediaType": MEDIA_TYPE,
            "verificationMaterial": good["verificationMaterial"],
            "dsseEnvelope": {"payload": base64.b64encode(data).decode(), "payloadType": "application/vnd.in-toto+json",
                             "signatures": [{"sig": "c2ln"}]},
        },
        "public key hint instead of a certificate": {
            **good,
            "verificationMaterial": {"publicKey": {"hint": "attacker"}, "tlogEntries": good["verificationMaterial"]["tlogEntries"]},
        },
        "certificate chain beside the certificate": changed(
            ("verificationMaterial", "x509CertificateChain"), {"certificates": [{"rawBytes": "Y2hhaW4="}]}),
        "no transparency log entry": changed(("verificationMaterial", "tlogEntries"), []),
        "two transparency log entries": changed(
            ("verificationMaterial", "tlogEntries"), good["verificationMaterial"]["tlogEntries"] * 2),
        "a digest of other bytes": changed(
            ("messageSignature", "messageDigest", "digest"), base64.b64encode(hashlib.sha256(data + b"!").digest()).decode()),
        "a SHA2_512 digest": changed(("messageSignature", "messageDigest", "algorithm"), "SHA2_512"),
        "an empty signature": changed(("messageSignature", "signature"), ""),
        "not an object": [good],
    }


_REMOVE = object()


FAKE_COSIGN = r'''#!/usr/bin/env python3
import base64, hashlib, json, os, sys

arguments = sys.argv[1:]
log = os.environ.get("FAKE_COSIGN_LOG")
if log:
    with open(log, "a", encoding="utf-8") as stream:
        stream.write(" ".join(arguments) + "\n")
executable_log = os.environ.get("FAKE_COSIGN_EXECUTABLE_LOG")
if executable_log:
    with open(executable_log, "a", encoding="utf-8") as stream:
        stream.write(os.path.realpath(sys.argv[0]) + "\n")
root = os.environ.get("FAKE_ROOT")
if root and any(name in os.environ for name in ("GH_TOKEN", "GITHUB_TOKEN")):
    with open(os.path.join(root, "leaked-token.log"), "a", encoding="utf-8") as stream:
        stream.write("cosign saw a GitHub token\n")

def option(name):
    return arguments[arguments.index(name) + 1] if name in arguments else None

subject = arguments[-1]
data = open(subject, "rb").read()
digest = base64.b64encode(hashlib.sha256(data).digest()).decode()
claims = {
    "identity": "https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main",
    "issuer": "https://token.actions.githubusercontent.com",
    "repository": "smartpbx/tarkov-companion",
    "ref": "refs/heads/main",
}
claims.update(json.loads(os.environ.get("FAKE_COSIGN_CLAIMS", "{}")))

if arguments[0] == "sign-blob":
    bundle = {
        "mediaType": "application/vnd.dev.sigstore.bundle.v0.3+json",
        "verificationMaterial": {
            "certificate": {"rawBytes": base64.b64encode(json.dumps(claims, sort_keys=True).encode()).decode()},
            "tlogEntries": [{"logIndex": "1", "kindVersion": {"kind": "hashedrekord", "version": "0.0.1"}}],
        },
        "messageSignature": {"messageDigest": {"algorithm": "SHA2_256", "digest": digest},
                             "signature": base64.b64encode(b"fixture signature").decode()},
    }
    json.dump(bundle, open(option("--bundle"), "w"))
    sys.exit(0)
if arguments[0] != "verify-blob":
    sys.exit("fake cosign: unexpected command")

rejected = os.environ.get("FAKE_COSIGN_REJECT")
if rejected and rejected in os.path.basename(subject):
    sys.exit(f"fake cosign: rejected {subject}")
root_file = option("--trusted-root")
if not root_file or not open(root_file).read().strip():
    sys.exit("fake cosign: no trust root")
bundle = json.load(open(option("--bundle")))
if bundle.get("mediaType") != "application/vnd.dev.sigstore.bundle.v0.3+json":
    sys.exit("fake cosign: not a standardized bundle")
if bundle["messageSignature"]["messageDigest"]["digest"] != digest:
    sys.exit("fake cosign: signature does not match")
certificate = json.loads(base64.b64decode(bundle["verificationMaterial"]["certificate"]["rawBytes"]))
for flag, claim in (("--certificate-identity", "identity"), ("--certificate-oidc-issuer", "issuer"),
                    ("--certificate-github-workflow-repository", "repository"),
                    ("--certificate-github-workflow-ref", "ref")):
    expected = option(flag)
    if expected is None:
        sys.exit(f"fake cosign: {flag} was not required")
    if certificate.get(claim) != expected:
        sys.exit(f"fake cosign: certificate {claim} {certificate.get(claim)!r} is not {expected!r}")
print("Verified OK", file=sys.stderr)
'''


def install_fake_cosign(directory: Path) -> str:
    """Writes the fake cosign into directory and returns the digest a test must pin."""
    directory.mkdir(parents=True, exist_ok=True)
    directory.chmod(0o755)
    path = directory / "cosign"
    path.write_text(FAKE_COSIGN, encoding="utf-8")
    path.chmod(0o755)
    return hashlib.sha256(path.read_bytes()).hexdigest()
