#!/usr/bin/env python3
"""Create a deterministic signed-shape bundle for the Windows offline installer fixture."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from sigstore_fixture import bundle_for, bundle_text  # noqa: E402


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--installer", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--feed", required=True)
    parser.add_argument("--inconsistent-rollback", action="store_true")
    args = parser.parse_args()

    installer_path = args.bundle / args.installer
    installer = installer_path.read_bytes()
    (args.bundle / f"{args.installer}.sigstore.json").write_text(bundle_text(installer), encoding="utf-8")
    manifest = json.dumps({
        "schemaVersion": 1,
        "version": args.version,
        "commit": args.commit,
        "artifacts": [{
            "name": args.installer,
            "component": "desktop",
            "role": "installer",
            "sha256": digest(installer),
            "size": len(installer),
        }],
    }, separators=(",", ":")).encode()
    (args.bundle / "release-manifest.json").write_bytes(manifest)
    (args.bundle / "release-manifest.json.sigstore.json").write_text(bundle_text(manifest), encoding="utf-8")

    release = {
        "version": args.version,
        "commit": args.commit,
        "buildTag": f"v2-build-{args.version}",
        "manifestName": "release-manifest.json",
        "manifestSha256": digest(manifest),
    }
    rollback_from = {
        **release,
        "version": "9.9.9",
        "commit": "f" * 40,
        "buildTag": "v2-build-9.9.9",
        "manifestSha256": "f" * 64,
    } if args.inconsistent_rollback else None
    payload = json.dumps({
        "schemaVersion": 1,
        "mediaType": "application/vnd.tarkov-companion.release-index.v1+json",
        "feedRepository": args.feed,
        "ring": "stable",
        "generation": 7,
        "updatedUtc": "2026-09-15T00:00:00Z",
        "paused": False,
        "release": release,
        "previous": rollback_from,
        "lastKnownGood": release,
        "highWaterVersion": "9.9.9" if args.inconsistent_rollback else args.version,
        "rollback": {"generation": 7, "from": rollback_from} if args.inconsistent_rollback else None,
        "authorization": {
            "action": "promote",
            "actor": "windows-fixture",
            "reason": "offline installer verification",
            "workflowRunId": "1",
            "verificationRunId": None,
            "sourceRing": "beta",
            "sourceGeneration": 6,
            "previousGeneration": 6,
        },
    }, separators=(",", ":")).encode()
    (args.bundle / "release-index-g0000000007.json").write_text(json.dumps({
        "schemaVersion": 1,
        "mediaType": "application/vnd.tarkov-companion.signed-release-index.v1+json",
        "payloadBase64": base64.b64encode(payload).decode(),
        "sigstoreBundle": bundle_for(payload),
    }, separators=(",", ":")), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
