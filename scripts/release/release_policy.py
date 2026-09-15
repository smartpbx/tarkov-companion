#!/usr/bin/env python3
"""Create, select, and check the signed release-ring decisions.

A ring (canary, beta, stable) is a sequence of signed index files named by generation. Each
file is created once and never replaced, so a reader always sees a whole decision or none, and
two writers that start from the same generation cannot both win: the second one's file name is
already taken. Every rule about what a transition may do lives here rather than in workflow
expressions, where it could not be fixture-tested.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import sys
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

from resource_limits import (  # noqa: E402
    MAX_JSON_BYTES,
    MAX_SIGNATURE_BYTES,
    ResourceLimitError,
    decode_json,
    read_bytes,
    read_json as bounded_read_json,
)


SCHEMA_VERSION = 1
INDEX_MEDIA_TYPE = "application/vnd.tarkov-companion.release-index.v1+json"
ENVELOPE_MEDIA_TYPE = "application/vnd.tarkov-companion.signed-release-index.v1+json"
RINGS = ("canary", "beta", "stable")
ACTIONS = ("publish", "promote", "pause", "resume", "mark-lkg", "rollback")
PROMOTION_SOURCE = {"beta": "canary", "stable": "beta"}
SEMVER = re.compile(
    r"(?=.{1,128}\Z)^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?$"
)
INDEX_NAME = re.compile(r"^release-index-g([0-9]{10})\.json$")
REPOSITORY = re.compile(r"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")
HEX_40 = re.compile(r"^[0-9a-f]{40}$")
HEX_64 = re.compile(r"^[0-9a-f]{64}$")
MAX_VERSION_NUMBER = 2_147_483_647
MAX_VERSION_LENGTH = 128
MAX_GENERATION = 9_999_999_999
MAX_ACTOR_LENGTH = 256
MAX_REASON_LENGTH = 2048
SOURCE_REPOSITORY = "smartpbx/tarkov-companion"

INDEX_FIELDS = {
    "schemaVersion", "mediaType", "feedRepository", "ring", "generation", "updatedUtc",
    "paused", "release", "previous", "lastKnownGood", "highWaterVersion", "rollback",
    "authorization",
}
AUTHORIZATION_FIELDS = {
    "action", "actor", "reason", "workflowRunId", "verificationRunId", "sourceRing",
    "sourceGeneration", "previousGeneration",
}


class PolicyError(ValueError):
    """A requested release transition is unsafe or malformed."""


class PolicySuperseded(PolicyError):
    """A verified build arrived after a newer one already entered canary. Nothing to do."""


def read_json(path: Path) -> dict[str, Any]:
    try:
        value = bounded_read_json(path)
    except (OSError, ResourceLimitError) as exception:
        raise PolicyError(f"could not read JSON from {path}: {exception}") from exception
    if not isinstance(value, dict):
        raise PolicyError(f"{path} must contain a JSON object")
    return value


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def utc_text(value: datetime) -> str:
    return value.astimezone(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")


def semver_key(value: str) -> tuple[int, int, int, tuple[tuple[int, int, Any], ...]]:
    """Order versions by SemVer 2.0 precedence, where a prerelease sorts before its release."""
    # Bound before applying the expression. Besides keeping every consumer's accepted language
    # identical, this prevents a hostile signed field from making regex work scale without limit.
    match = SEMVER.fullmatch(value) if isinstance(value, str) and 0 < len(value) <= MAX_VERSION_LENGTH else None
    if match is None:
        raise PolicyError(f"version is not a supported semantic version: {value!r}")
    major, minor, patch = (int(match.group(index)) for index in range(1, 4))
    if any(part > MAX_VERSION_NUMBER for part in (major, minor, patch)):
        raise PolicyError(f"version has a numeric identifier outside the supported range: {value!r}")
    prerelease = match.group(4)
    if prerelease is None:
        return major, minor, patch, ((1, 0, 0),)
    parts = prerelease.split(".")
    if any(part.isdigit() and (len(part) > 10 or int(part) > MAX_VERSION_NUMBER) for part in parts):
        raise PolicyError(f"version has a numeric identifier outside the supported range: {value!r}")
    return major, minor, patch, tuple((0, 0, int(part)) if part.isdigit() else (0, 1, part) for part in parts)


def index_name(generation: int) -> str:
    if not isinstance(generation, int) or generation < 1 or generation > MAX_GENERATION:
        raise PolicyError(f"generation is out of range: {generation!r}")
    return f"release-index-g{generation:010d}.json"


def generation_of(name: str) -> int | None:
    match = INDEX_NAME.fullmatch(name)
    return int(match.group(1)) if match else None


def select_current(names: list[str]) -> tuple[int, str | None]:
    """The newest decision in a ring listing, or generation zero for a ring never written.

    Names that merely resemble an index are ignored rather than trusted: the signature check
    that follows is on one exact file, and a stray name must not be able to become it.
    """
    best = (0, None)
    for name in names:
        generation = generation_of(name)
        if generation is not None and generation > best[0]:
            best = (generation, name)
    return best


def validate_release(value: Any, label: str = "release") -> dict[str, Any]:
    if not isinstance(value, dict):
        raise PolicyError(f"{label} must be an object")
    expected = {"version", "commit", "buildTag", "manifestName", "manifestSha256"}
    if set(value) != expected:
        raise PolicyError(f"{label} must contain exactly {sorted(expected)}")
    semver_key(value["version"])
    if not isinstance(value["commit"], str) or HEX_40.fullmatch(value["commit"]) is None:
        raise PolicyError(f"{label}.commit must be a full lowercase Git commit")
    if not isinstance(value["manifestSha256"], str) or HEX_64.fullmatch(value["manifestSha256"]) is None:
        raise PolicyError(f"{label}.manifestSha256 must be a lowercase SHA-256")
    if value["buildTag"] != f"v2-build-{value['version']}":
        raise PolicyError(f"{label}.buildTag does not agree with its version")
    if value["manifestName"] != "release-manifest.json":
        raise PolicyError(f"{label}.manifestName is not the canonical manifest asset")
    return deepcopy(value)


def validate_index(
    value: dict[str, Any],
    *,
    ring: str,
    feed_repository: str,
    generation: int | None = None,
    label: str = "index",
) -> dict[str, Any]:
    if not isinstance(feed_repository, str) or REPOSITORY.fullmatch(feed_repository) is None:
        raise PolicyError("the feed repository must be owner/name")
    if feed_repository.casefold() == SOURCE_REPOSITORY:
        raise PolicyError("the public source repository cannot be the authenticated v2 feed")
    if not isinstance(value, dict) or set(value) != INDEX_FIELDS:
        raise PolicyError(f"{label} must contain exactly {sorted(INDEX_FIELDS)}")
    if value.get("schemaVersion") != SCHEMA_VERSION or value.get("mediaType") != INDEX_MEDIA_TYPE:
        raise PolicyError(f"{label} has an unsupported schema or media type")
    if value.get("feedRepository") != feed_repository:
        raise PolicyError(f"{label} belongs to feed {value.get('feedRepository')!r}, not {feed_repository!r}")
    if value.get("ring") != ring:
        raise PolicyError(f"{label}.ring is {value.get('ring')!r}, expected {ring!r}")
    index_generation = value.get("generation")
    if (not isinstance(index_generation, int) or isinstance(index_generation, bool)
            or index_generation < 1 or index_generation > MAX_GENERATION):
        raise PolicyError(f"{label}.generation must be a positive integer")
    if generation is not None and index_generation != generation:
        raise PolicyError(f"{label} says generation {index_generation} inside file generation {generation}")
    if not isinstance(value.get("paused"), bool):
        raise PolicyError(f"{label}.paused must be a boolean")
    try:
        updated_text = value.get("updatedUtc")
        if not isinstance(updated_text, str):
            raise ValueError
        updated = datetime.strptime(updated_text, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
    except ValueError as exception:
        raise PolicyError(f"{label}.updatedUtc must be a canonical UTC timestamp") from exception
    if updated > datetime.now(timezone.utc) + timedelta(minutes=5):
        raise PolicyError(f"{label}.updatedUtc is implausibly far in the future")
    release = validate_release(value.get("release"), f"{label}.release")
    for name in ("previous", "lastKnownGood"):
        if value.get(name) is not None:
            validate_release(value[name], f"{label}.{name}")
    high_water = value.get("highWaterVersion")
    if semver_key(high_water) < semver_key(release["version"]):
        raise PolicyError(f"{label}.highWaterVersion is below its own release")
    rollback = value.get("rollback")
    if rollback is not None:
        if (not isinstance(rollback, dict) or set(rollback) != {"generation", "from"}
                or not isinstance(rollback.get("generation"), int)
                or isinstance(rollback.get("generation"), bool)
                or rollback["generation"] < 1):
            raise PolicyError(f"{label}.rollback is malformed")
        rollback_from = validate_release(rollback.get("from"), f"{label}.rollback.from")
        if rollback["generation"] > index_generation:
            raise PolicyError(f"{label}.rollback claims a future generation")
        if rollback_from != value.get("previous") or release != value.get("lastKnownGood") or rollback_from == release:
            raise PolicyError(f"{label}.rollback is inconsistent with its release, previous, or last-known-good")
    authorization = value.get("authorization")
    if not isinstance(authorization, dict) or set(authorization) != AUTHORIZATION_FIELDS:
        raise PolicyError(f"{label}.authorization must contain exactly {sorted(AUTHORIZATION_FIELDS)}")
    action = authorization.get("action")
    if action not in ACTIONS:
        raise PolicyError(f"{label}.authorization.action is not a known release action")
    actor = authorization.get("actor")
    reason = authorization.get("reason")
    workflow_run_id = authorization.get("workflowRunId")
    verification_run_id = authorization.get("verificationRunId")
    source_ring = authorization.get("sourceRing")
    source_generation = authorization.get("sourceGeneration")
    previous_generation = authorization.get("previousGeneration")
    if (not isinstance(actor, str) or not actor or actor != actor.strip() or len(actor) > MAX_ACTOR_LENGTH
            or not isinstance(reason, str) or len(reason) > MAX_REASON_LENGTH
            or not _is_decimal_id(workflow_run_id)
            or verification_run_id is not None and not _is_decimal_id(verification_run_id)
            or source_ring is not None and source_ring not in ("canary", "beta")
            or source_generation is not None and (
                not isinstance(source_generation, int) or isinstance(source_generation, bool)
                or source_generation < 1 or source_generation > MAX_GENERATION
            )
            or (source_ring is None) != (source_generation is None)
            or (action == "promote") != (source_ring is not None)
            or action == "promote" and source_ring != PROMOTION_SOURCE.get(ring)
            or action == "publish" and (ring != "canary" or verification_run_id is None)
            or action != "publish" and verification_run_id is not None
            or action in ("publish", "promote") and value["paused"]
            or action == "pause" and not value["paused"]
            or action == "resume" and value["paused"]
            or action == "rollback" and rollback is None
            or rollback is not None and action not in ("rollback", "pause", "resume")):
        raise PolicyError(f"{label}.authorization is inconsistent with its release transition")
    if (not isinstance(previous_generation, int) or isinstance(previous_generation, bool)
            or previous_generation != index_generation - 1):
        raise PolicyError(f"{label} does not follow directly from the previous generation")
    return deepcopy(value)


def _is_decimal_id(value: Any) -> bool:
    return (isinstance(value, str) and 0 < len(value) <= 20 and value.isascii()
            and value[0] in "123456789" and value.isdecimal())


def release_from_manifest(path: Path) -> dict[str, Any]:
    try:
        payload = read_bytes(path, "release manifest", MAX_JSON_BYTES)
        manifest = decode_json(payload, "release manifest")
    except (OSError, ResourceLimitError) as exception:
        raise PolicyError(f"could not read release manifest: {exception}") from exception
    if not isinstance(manifest, dict):
        raise PolicyError("release manifest must contain a JSON object")
    if manifest.get("schemaVersion") != SCHEMA_VERSION:
        raise PolicyError("release manifest schema is unsupported")
    return validate_release({
        "version": manifest.get("version"),
        "commit": manifest.get("commit"),
        "buildTag": f"v2-build-{manifest.get('version')}",
        "manifestName": "release-manifest.json",
        "manifestSha256": hashlib.sha256(payload).hexdigest(),
    })


def _require_advance(candidate: dict[str, Any], current: dict[str, Any] | None, ring: str) -> None:
    # Against the high-water mark rather than the current release. After a rollback the ring
    # serves an older build, and without this a verification run that finished late could
    # "advance" the ring back into the range of the build somebody just rolled away from.
    if current is not None and semver_key(candidate["version"]) <= semver_key(current["highWaterVersion"]):
        raise PolicyError(
            f"{candidate['version']} does not advance {ring} past {current['highWaterVersion']}"
        )


def next_index(
    *,
    ring: str,
    action: str,
    actor: str,
    feed_repository: str,
    workflow_run_id: str,
    current: dict[str, Any] | None = None,
    current_generation: int = 0,
    manifest_path: Path | None = None,
    source: dict[str, Any] | None = None,
    source_generation: int | None = None,
    verification_run_id: str | None = None,
    reason: str = "",
    now: datetime | None = None,
) -> dict[str, Any]:
    if ring not in RINGS:
        raise PolicyError(f"unknown release ring: {ring!r}")
    if action not in ACTIONS:
        raise PolicyError(f"unknown release action: {action!r}")
    if not isinstance(actor, str) or not actor or actor != actor.strip() or len(actor) > MAX_ACTOR_LENGTH:
        raise PolicyError("the authorizing actor is required")
    if not isinstance(feed_repository, str) or REPOSITORY.fullmatch(feed_repository) is None:
        raise PolicyError("the feed repository must be owner/name")
    if feed_repository.casefold() == SOURCE_REPOSITORY:
        raise PolicyError("the public source repository cannot be the authenticated v2 feed")
    if not _is_decimal_id(workflow_run_id):
        raise PolicyError("the workflow run id must be a bounded decimal identifier")
    if verification_run_id is not None and not _is_decimal_id(verification_run_id):
        raise PolicyError("the verification run id must be a bounded decimal identifier")
    if not isinstance(reason, str) or len(reason) > MAX_REASON_LENGTH:
        raise PolicyError("the release reason is outside its length limit")
    if (current is None) != (current_generation == 0):
        raise PolicyError("the current index and its generation disagree about whether the ring exists")
    if current is not None:
        current = validate_index(
            current, ring=ring, feed_repository=feed_repository, generation=current_generation, label="current"
        )

    generation = current_generation + 1
    release = deepcopy(current["release"]) if current else None
    previous = deepcopy(current.get("previous")) if current else None
    last_known_good = deepcopy(current.get("lastKnownGood")) if current else None
    high_water = current["highWaterVersion"] if current else None
    rollback = deepcopy(current.get("rollback")) if current else None
    paused = current["paused"] if current else False
    source_ring: str | None = None

    if action in ("publish", "promote"):
        if current is not None and current["paused"]:
            raise PolicyError(f"{ring} is paused; resume it before moving it forward")
        if action == "publish":
            if ring != "canary":
                raise PolicyError("new verified builds may enter only the canary ring")
            if manifest_path is None or not verification_run_id:
                raise PolicyError("publish requires a verified release manifest and its verification run")
            candidate = release_from_manifest(manifest_path)
            try:
                _require_advance(candidate, current, ring)
            except PolicyError as exception:
                # Normal when two merges verify out of order, so it is reported, not failed.
                raise PolicySuperseded(str(exception)) from exception
        else:
            source_ring = PROMOTION_SOURCE.get(ring)
            if source_ring is None:
                raise PolicyError("canary receives verified builds; it is not a promotion target")
            if source is None or source_generation is None:
                raise PolicyError("promotion requires the signed source ring index")
            source = validate_index(
                source, ring=source_ring, feed_repository=feed_repository, generation=source_generation, label="source"
            )
            if source["paused"]:
                raise PolicyError(f"the {source_ring} source ring is paused")
            candidate = deepcopy(source["release"])
        _require_advance(candidate, current, ring)
        if release is not None:
            previous = deepcopy(release)
            # A ring that has never had a release marked good falls back to the one it was
            # serving: that build was accepted into this ring, the new one has not been yet.
            if last_known_good is None:
                last_known_good = deepcopy(release)
        release = candidate
        high_water = candidate["version"]
        rollback = None
    elif current is None:
        raise PolicyError(f"{action} requires an existing {ring} ring")
    elif action == "pause":
        if paused:
            raise PolicyError(f"{ring} is already paused")
        paused = True
    elif action == "resume":
        if not paused:
            raise PolicyError(f"{ring} is not paused")
        paused = False
    elif action == "mark-lkg":
        if last_known_good == release:
            raise PolicyError(f"{ring}'s current release is already its last-known-good release")
        last_known_good = deepcopy(release)
    elif action == "rollback":
        if last_known_good is None:
            raise PolicyError(f"{ring} has no last-known-good release to roll back to")
        if last_known_good == release:
            raise PolicyError(f"{ring} is already on its last-known-good release")
        # A pause survives a rollback. Rolling back is recovery, and the reason to be paused is
        # usually the same reason to roll back: nothing should move forward again by itself.
        rollback = {"generation": generation, "from": deepcopy(release)}
        previous = deepcopy(release)
        release = deepcopy(last_known_good)

    assert release is not None and high_water is not None
    return {
        "schemaVersion": SCHEMA_VERSION,
        "mediaType": INDEX_MEDIA_TYPE,
        "feedRepository": feed_repository,
        "ring": ring,
        "generation": generation,
        "updatedUtc": utc_text(now or datetime.now(timezone.utc)),
        "paused": paused,
        "release": release,
        "previous": previous,
        "lastKnownGood": last_known_good,
        "highWaterVersion": high_water,
        "rollback": rollback,
        "authorization": {
            "action": action,
            "actor": actor,
            "reason": reason,
            "workflowRunId": workflow_run_id,
            "verificationRunId": verification_run_id,
            "sourceRing": source_ring,
            "sourceGeneration": source_generation if source_ring else None,
            "previousGeneration": current_generation,
        },
    }


def pack_envelope(payload_path: Path, bundle_path: Path, output_path: Path) -> None:
    """One file holding the exact signed bytes and their bundle, so publication is one create."""
    payload = read_bytes(payload_path, "release index", MAX_JSON_BYTES)
    if not isinstance(decode_json(payload, "release index"), dict):
        raise PolicyError("release index must be a JSON object")
    bundle = bounded_read_json(bundle_path, maximum=MAX_SIGNATURE_BYTES)
    if not isinstance(bundle, dict):
        raise PolicyError("signature bundle must be a JSON object")
    envelope = {
        "schemaVersion": SCHEMA_VERSION,
        "mediaType": ENVELOPE_MEDIA_TYPE,
        "payloadBase64": base64.b64encode(payload).decode("ascii"),
        "sigstoreBundle": bundle,
    }
    write_json(output_path, envelope)


def unpack_envelope(envelope_path: Path, payload_path: Path, bundle_path: Path) -> None:
    envelope = read_json(envelope_path)
    if (set(envelope) != {"schemaVersion", "mediaType", "payloadBase64", "sigstoreBundle"}
            or envelope.get("schemaVersion") != SCHEMA_VERSION
            or envelope.get("mediaType") != ENVELOPE_MEDIA_TYPE):
        raise PolicyError("signed envelope schema or media type is unsupported")
    encoded = envelope.get("payloadBase64")
    bundle = envelope.get("sigstoreBundle")
    if not isinstance(encoded, str) or not isinstance(bundle, dict):
        raise PolicyError("signed envelope is incomplete")
    if len(encoded) > ((MAX_JSON_BYTES + 2) // 3) * 4:
        raise PolicyError("signed envelope payload exceeds the release-index size limit")
    try:
        payload = base64.b64decode(encoded, validate=True)
        parsed = decode_json(payload, "signed envelope payload")
    except (ValueError, ResourceLimitError) as exception:
        raise PolicyError("signed envelope payload is not valid base64 JSON") from exception
    if not isinstance(parsed, dict):
        raise PolicyError("signed envelope payload must be an object")
    payload_path.parent.mkdir(parents=True, exist_ok=True)
    payload_path.write_bytes(payload)
    write_json(bundle_path, bundle)


def command_select(args: argparse.Namespace) -> None:
    listing = bounded_read_json(args.listing)
    if not isinstance(listing, list) or not all(isinstance(item, str) for item in listing):
        raise PolicyError("the ring listing must be a JSON array of file names")
    generation, name = select_current(listing)
    print(json.dumps({"generation": generation, "name": name}, sort_keys=True))


def command_inspect(args: argparse.Namespace) -> None:
    index = validate_index(
        read_json(args.payload), ring=args.ring, feed_repository=args.feed, generation=args.generation
    )
    print(json.dumps({
        "generation": index["generation"],
        "paused": index["paused"],
        "version": index["release"]["version"],
        "commit": index["release"]["commit"],
        "buildTag": index["release"]["buildTag"],
        "manifestSha256": index["release"]["manifestSha256"],
        "rollback": index["rollback"] is not None,
        "action": index["authorization"]["action"],
    }, sort_keys=True))


def command_next(args: argparse.Namespace) -> None:
    result = next_index(
        ring=args.ring,
        action=args.action,
        actor=args.actor,
        feed_repository=args.feed,
        workflow_run_id=args.workflow_run_id,
        current=read_json(args.current) if args.current else None,
        current_generation=args.current_generation,
        manifest_path=args.manifest,
        source=read_json(args.source) if args.source else None,
        source_generation=args.source_generation,
        verification_run_id=args.verification_run_id,
        reason=args.reason,
    )
    write_json(args.output, result)
    print(index_name(result["generation"]))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    select = subparsers.add_parser("select", help="choose the newest index name from a ring listing")
    select.add_argument("--listing", type=Path, required=True)
    select.set_defaults(handler=command_select)

    inspect = subparsers.add_parser("inspect", help="validate a verified index payload")
    inspect.add_argument("--payload", type=Path, required=True)
    inspect.add_argument("--ring", choices=RINGS, required=True)
    inspect.add_argument("--feed", required=True)
    inspect.add_argument("--generation", type=int, required=True)
    inspect.set_defaults(handler=command_inspect)

    nxt = subparsers.add_parser("next", help="create the next ring index payload")
    nxt.add_argument("--ring", choices=RINGS, required=True)
    nxt.add_argument("--action", choices=ACTIONS, required=True)
    nxt.add_argument("--actor", required=True)
    nxt.add_argument("--feed", required=True)
    nxt.add_argument("--workflow-run-id", required=True)
    nxt.add_argument("--current", type=Path)
    nxt.add_argument("--current-generation", type=int, default=0)
    nxt.add_argument("--source", type=Path)
    nxt.add_argument("--source-generation", type=int)
    nxt.add_argument("--manifest", type=Path)
    nxt.add_argument("--verification-run-id")
    nxt.add_argument("--reason", default="")
    nxt.add_argument("--output", type=Path, required=True)
    nxt.set_defaults(handler=command_next)

    pack = subparsers.add_parser("pack-envelope", help="combine an index and its Sigstore bundle")
    pack.add_argument("--payload", type=Path, required=True)
    pack.add_argument("--bundle", type=Path, required=True)
    pack.add_argument("--output", type=Path, required=True)
    pack.set_defaults(handler=lambda args: pack_envelope(args.payload, args.bundle, args.output))

    unpack = subparsers.add_parser("unpack-envelope", help="extract an index and bundle for verification")
    unpack.add_argument("--envelope", type=Path, required=True)
    unpack.add_argument("--payload", type=Path, required=True)
    unpack.add_argument("--bundle", type=Path, required=True)
    unpack.set_defaults(handler=lambda args: unpack_envelope(args.envelope, args.payload, args.bundle))
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        args.handler(args)
    except PolicySuperseded as exception:
        print(f"release superseded: {exception}", file=sys.stderr)
        return 3
    except (PolicyError, OSError, json.JSONDecodeError, ResourceLimitError) as exception:
        print(f"release policy refused: {exception}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
