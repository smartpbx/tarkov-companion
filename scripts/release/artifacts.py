#!/usr/bin/env python3
"""Fetch a verification run's artifacts by the digest GitHub recorded when they were uploaded.

`gh run download` hands back whatever it downloaded. The producer's upload-artifact computed a
sha256 of each artifact archive, and GitHub keeps it on the artifact. Fetching the archive and
refusing it unless its bytes have that digest binds everything downstream to what the
verification run uploaded, rather than to what a later download happened to return. The digests,
the run's own facts and every extracted file's digest are written to a record. The release
manifest and the provenance predicate are built from that record, not from the publishing run's
idea of itself.

Measured on verification run 34924256898: the sha256 of each archive from
`GET /actions/artifacts/{id}/zip` equals that artifact's recorded `digest`.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import subprocess
import sys
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any, Callable, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))

from gates import GateError, check_verification_run  # noqa: E402


DIGEST = re.compile(r"^sha256:([0-9a-f]{64})$")
SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._+-]*$")
MAX_ARTIFACT_BYTES = 2 * 1024 * 1024 * 1024

Runner = Callable[[Sequence[str], Path | None], subprocess.CompletedProcess]


class ArtifactError(RuntimeError):
    """An artifact is missing, ambiguous, expired, or not the bytes the producer uploaded."""


def run_process(arguments: Sequence[str], output: Path | None) -> subprocess.CompletedProcess:
    if output is None:
        return subprocess.run(list(arguments), capture_output=True, check=False)
    with output.open("wb") as stream:
        return subprocess.run(list(arguments), stdout=stream, stderr=subprocess.PIPE, check=False)


def sha256_file(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def gh_json(runner: Runner, endpoint: str) -> Any:
    result = runner(["gh", "api", endpoint], None)
    if result.returncode != 0:
        raise ArtifactError(f"GitHub API request failed for {endpoint}: {(result.stderr or b'').decode(errors='replace').strip()}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as exception:
        raise ArtifactError(f"GitHub returned unreadable JSON for {endpoint}") from exception


def select_artifact(listing: dict[str, Any], name: str, run_id: str, sha: str) -> dict[str, Any]:
    matches = [item for item in listing.get("artifacts") or [] if isinstance(item, dict) and item.get("name") == name]
    if len(matches) != 1:
        raise ArtifactError(f"verification run {run_id} has {len(matches)} artifacts named {name}, not exactly one")
    artifact = matches[0]
    if artifact.get("expired") is not False:
        raise ArtifactError(f"artifact {name} has expired or does not say whether it has")
    origin = artifact.get("workflow_run") or {}
    if str(origin.get("id")) != run_id or origin.get("head_sha") != sha:
        raise ArtifactError(f"artifact {name} does not belong to verification run {run_id} at {sha}")
    match = DIGEST.fullmatch(str(artifact.get("digest") or ""))
    if match is None:
        raise ArtifactError(f"artifact {name} has no recorded sha256 digest, so its bytes cannot be bound to the upload")
    size = artifact.get("size_in_bytes")
    if not isinstance(size, int) or size <= 0 or size > MAX_ARTIFACT_BYTES:
        raise ArtifactError(f"artifact {name} reports an implausible size: {size!r}")
    return {"name": name, "id": artifact.get("id"), "sha256": match.group(1), "size": size}


def extract(archive: Path, destination: Path) -> list[dict[str, Any]]:
    """Unpacks one artifact archive, refusing links, climbs, repeats and anything but files."""
    destination.mkdir(parents=True, exist_ok=False)
    files: list[dict[str, Any]] = []
    try:
        with zipfile.ZipFile(archive) as bundle:
            names = bundle.namelist()
            if len(names) != len(set(names)):
                raise ArtifactError(f"{archive.name} repeats an entry")
            for info in bundle.infolist():
                path = PurePosixPath(info.filename)
                if path.is_absolute() or ".." in path.parts or "\\" in info.filename:
                    raise ArtifactError(f"{archive.name} contains an unsafe path: {info.filename}")
                if info.is_dir():
                    continue
                mode = (info.external_attr >> 16) & 0o170000
                if mode not in (0, 0o100000):
                    raise ArtifactError(f"{archive.name} contains a link or special file: {info.filename}")
                if not all(SAFE_NAME.fullmatch(part) for part in path.parts):
                    raise ArtifactError(f"{archive.name} contains an unsafe name: {info.filename}")
                target = destination.joinpath(*path.parts)
                target.parent.mkdir(parents=True, exist_ok=True)
                with bundle.open(info) as source, target.open("wb") as sink:
                    for chunk in iter(lambda: source.read(1024 * 1024), b""):
                        sink.write(chunk)
                files.append({"path": path.as_posix(), "sha256": sha256_file(target), "size": target.stat().st_size})
    except zipfile.BadZipFile as exception:
        raise ArtifactError(f"{archive.name} is not a readable archive: {exception}") from exception
    if not files:
        raise ArtifactError(f"{archive.name} is empty")
    return sorted(files, key=lambda item: item["path"])


def fetch(runner: Runner, repository: str, run_id: str, names: list[str], output: Path) -> dict[str, Any]:
    if not run_id.isdigit():
        raise ArtifactError("the verification run id must be numeric")
    if len(names) != len(set(names)) or not all(SAFE_NAME.fullmatch(name) for name in names):
        raise ArtifactError("artifact names must be distinct and plain")
    run = gh_json(runner, f"repos/{repository}/actions/runs/{run_id}")
    try:
        sha = check_verification_run(run, repository)
    except GateError as exception:
        raise ArtifactError(str(exception)) from exception
    listing = gh_json(runner, f"repos/{repository}/actions/runs/{run_id}/artifacts?per_page=100")
    output.mkdir(parents=True, exist_ok=True)
    artifacts = []
    for name in names:
        selected = select_artifact(listing, name, run_id, sha)
        archive = output / f".{name}.zip"
        result = runner(["gh", "api", f"repos/{repository}/actions/artifacts/{selected['id']}/zip"], archive)
        if result.returncode != 0:
            raise ArtifactError(f"downloading artifact {name} failed: {(result.stderr or b'').decode(errors='replace').strip()}")
        actual = sha256_file(archive)
        if actual != selected["sha256"]:
            raise ArtifactError(f"artifact {name} downloaded as sha256 {actual}, but the verification run uploaded {selected['sha256']}")
        if archive.stat().st_size != selected["size"]:
            raise ArtifactError(f"artifact {name} downloaded as {archive.stat().st_size} bytes, not {selected['size']}")
        selected["files"] = extract(archive, output / name)
        archive.unlink()
        artifacts.append(selected)
    return {
        "schemaVersion": 1,
        "repository": repository,
        "verificationRun": {
            "id": int(run_id),
            "attempt": run.get("run_attempt"),
            "number": run.get("run_number"),
            "workflowPath": run.get("path"),
            "event": run.get("event"),
            "headBranch": run.get("head_branch"),
            "headSha": sha,
            "url": f"https://github.com/{repository}/actions/runs/{run_id}/attempts/{run.get('run_attempt')}",
            "startedAt": run.get("run_started_at"),
            "updatedAt": run.get("updated_at"),
        },
        "artifacts": artifacts,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    subparsers = parser.add_subparsers(dest="command", required=True)
    fetch_parser = subparsers.add_parser("fetch")
    fetch_parser.add_argument("--repository", required=True)
    fetch_parser.add_argument("--run-id", required=True)
    fetch_parser.add_argument("--name", action="append", required=True)
    fetch_parser.add_argument("--output", type=Path, required=True)
    fetch_parser.add_argument("--record", type=Path, required=True)
    args = parser.parse_args()
    try:
        record = fetch(run_process, args.repository, args.run_id, args.name, args.output)
    except (ArtifactError, OSError) as exception:
        print(f"verification artifacts refused: {exception}", file=sys.stderr)
        return 1
    args.record.parent.mkdir(parents=True, exist_ok=True)
    args.record.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    for artifact in record["artifacts"]:
        print(f"{artifact['name']}: sha256 {artifact['sha256']}, {len(artifact['files'])} files")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
