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
import io
import json
import re
import resource
import shutil
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any, Callable, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))

from gates import GateError, check_verification_run  # noqa: E402
from resource_limits import (  # noqa: E402
    MAX_ARCHIVE_BYTES,
    MAX_ARCHIVE_MEMBERS,
    MAX_ARTIFACT_COUNT,
    MAX_EXPANDED_BYTES,
    MAX_JSON_BYTES,
    MAX_MEMBER_BYTES,
    MIN_FREE_RESERVE_BYTES,
    ResourceLimitError,
    copy_stream,
    decode_json,
    sha256_file as bounded_sha256_file,
)


DIGEST = re.compile(r"^sha256:([0-9a-f]{64})$")
SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._+-]*$")
MAX_ARTIFACT_BYTES = MAX_ARCHIVE_BYTES

Runner = Callable[[Sequence[str], Path | None], subprocess.CompletedProcess]


class ArtifactError(RuntimeError):
    """An artifact is missing, ambiguous, expired, or not the bytes the producer uploaded."""


def run_process(arguments: Sequence[str], output: Path | None) -> subprocess.CompletedProcess:
    maximum = MAX_JSON_BYTES if output is None else MAX_ARTIFACT_BYTES
    memory = io.BytesIO() if output is None else None
    target = memory if memory is not None else output.open("wb")
    with tempfile.TemporaryFile() as errors:
        process = subprocess.Popen(
            list(arguments), stdout=subprocess.PIPE, stderr=errors,
            preexec_fn=lambda: resource.setrlimit(
                resource.RLIMIT_FSIZE, (MAX_ARTIFACT_BYTES, MAX_ARTIFACT_BYTES)),
        )
        try:
            assert process.stdout is not None
            copy_stream(process.stdout, target, maximum=maximum, label=f"output from {arguments[0]}")
            return_code = process.wait()
        except ResourceLimitError as exception:
            process.kill()
            process.wait()
            return_code = 125
            errors.write(f"\n{exception}\n".encode())
        finally:
            process.stdout.close() if process.stdout is not None else None
            if memory is None:
                target.close()
        errors.seek(0)
        stderr = errors.read(MAX_JSON_BYTES + 1)
    if len(stderr) > MAX_JSON_BYTES:
        stderr = stderr[:MAX_JSON_BYTES] + b"\nstderr truncated at the release-input limit\n"
    if output is not None and return_code == 125 and output.exists():
        output.unlink()
    return subprocess.CompletedProcess(list(arguments), return_code, memory.getvalue() if memory is not None else b"", stderr)


def sha256_file(path: Path) -> str:
    return bounded_sha256_file(path, path.name, MAX_ARTIFACT_BYTES)


def gh_json(runner: Runner, endpoint: str) -> Any:
    result = runner(["gh", "api", endpoint], None)
    if result.returncode != 0:
        raise ArtifactError(f"GitHub API request failed for {endpoint}: {(result.stderr or b'').decode(errors='replace').strip()}")
    try:
        return decode_json(result.stdout, endpoint)
    except ResourceLimitError as exception:
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
    if not isinstance(size, int) or isinstance(size, bool) or size <= 0 or size > MAX_ARTIFACT_BYTES:
        raise ArtifactError(f"artifact {name} reports an implausible size: {size!r}")
    artifact_id = artifact.get("id")
    if not isinstance(artifact_id, int) or isinstance(artifact_id, bool) or artifact_id <= 0:
        raise ArtifactError(f"artifact {name} has no bounded positive identifier")
    return {"name": name, "id": artifact_id, "sha256": match.group(1), "size": size}


def extract(archive: Path, destination: Path) -> list[dict[str, Any]]:
    """Unpacks one artifact archive within fixed compressed, expanded and member limits."""
    archive_size = archive.stat().st_size
    if archive_size <= 0 or archive_size > MAX_ARTIFACT_BYTES:
        raise ArtifactError(f"{archive.name} is outside the compressed archive size limit")
    destination.mkdir(parents=True, exist_ok=False)
    files: list[dict[str, Any]] = []
    try:
        with zipfile.ZipFile(archive) as bundle:
            members = bundle.infolist()
            if len(members) > MAX_ARCHIVE_MEMBERS:
                raise ArtifactError(
                    f"{archive.name} has {len(members)} entries, above the {MAX_ARCHIVE_MEMBERS}-entry limit")
            if any(member.file_size < 0 or member.file_size > MAX_MEMBER_BYTES for member in members):
                raise ArtifactError(f"{archive.name} contains a file above the per-file size limit")
            expanded = sum(member.file_size for member in members)
            if expanded > MAX_EXPANDED_BYTES:
                raise ArtifactError(f"{archive.name} exceeds the {MAX_EXPANDED_BYTES}-byte expanded-size limit")
            if shutil.disk_usage(destination).free < expanded + MIN_FREE_RESERVE_BYTES:
                raise ArtifactError(f"there is not enough free space to extract bounded artifact {archive.name}")
            names = [member.filename for member in members]
            if len(names) != len(set(names)):
                raise ArtifactError(f"{archive.name} repeats an entry")
            for info in members:
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
                with bundle.open(info) as source, target.open("xb") as sink:
                    actual_size = copy_stream(source, sink, maximum=info.file_size,
                                              label=f"{archive.name} entry {info.filename}")
                if actual_size != info.file_size:
                    raise ArtifactError(
                        f"{archive.name} entry {info.filename} expanded to {actual_size}, not {info.file_size} bytes")
                files.append({"path": path.as_posix(), "sha256": sha256_file(target), "size": target.stat().st_size})
    except (zipfile.BadZipFile, ResourceLimitError) as exception:
        shutil.rmtree(destination, ignore_errors=True)
        raise ArtifactError(f"{archive.name} is not a readable bounded archive: {exception}") from exception
    except Exception:
        shutil.rmtree(destination, ignore_errors=True)
        raise
    if not files:
        shutil.rmtree(destination, ignore_errors=True)
        raise ArtifactError(f"{archive.name} is empty")
    return sorted(files, key=lambda item: item["path"])


def fetch(runner: Runner, repository: str, run_id: str, names: list[str], output: Path) -> dict[str, Any]:
    if re.fullmatch(r"[1-9][0-9]{0,19}", run_id) is None:
        raise ArtifactError("the verification run id must be a bounded positive decimal identifier")
    if (not names or len(names) > MAX_ARTIFACT_COUNT or len(names) != len(set(names))
            or not all(SAFE_NAME.fullmatch(name) for name in names)):
        raise ArtifactError("artifact names must be distinct and plain")
    run = gh_json(runner, f"repos/{repository}/actions/runs/{run_id}")
    try:
        sha = check_verification_run(run, repository)
    except GateError as exception:
        raise ArtifactError(str(exception)) from exception
    listing = gh_json(runner, f"repos/{repository}/actions/runs/{run_id}/artifacts?per_page=100")
    if output.is_symlink():
        raise ArtifactError("the verification-artifact output directory is redirected")
    output.mkdir(parents=True, exist_ok=True)
    artifacts = []
    for name in names:
        selected = select_artifact(listing, name, run_id, sha)
        archive = output / f".{name}.zip"
        if archive.exists() or archive.is_symlink():
            raise ArtifactError(f"the temporary archive path for {name} already exists")
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
