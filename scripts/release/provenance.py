#!/usr/bin/env python3
"""Write the SLSA provenance predicate for a release from what the verification run produced.

The attestation used to be generated with no predicate, so actions/attest described the run it
was in: publish.yml, triggered by workflow_run, at the default branch's head. That run builds
nothing. It downloads what Windows verification built at some other commit, so its provenance
named the wrong builder, the wrong invocation and, whenever main had moved on, the wrong source.

This predicate is explicit instead. The builder is the verification workflow at protected main.
The invocation is that run and attempt. The source is the commit it built. The inputs are the
artifact archives it uploaded, with the digests GitHub recorded (see artifacts.py). The publishing
run appears only as what it is: the party that observed those facts through the GitHub API and
signs the attestation. Its certificate says so independently.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))

from resource_limits import MAX_ARTIFACT_COUNT, ResourceLimitError, read_json as bounded_read_json  # noqa: E402


BUILD_TYPE = "https://github.com/smartpbx/tarkov-companion/blob/main/docs/RELEASES.md#verified-build-provenance"
HEX_40 = re.compile(r"^[0-9a-f]{40}$")
HEX_64 = re.compile(r"^[0-9a-f]{64}$")
REPOSITORY = re.compile(r"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")
DECIMAL_ID = re.compile(r"^[1-9][0-9]{0,19}$")
SOURCE_REPOSITORY = "smartpbx/tarkov-companion"
VERIFICATION_ARTIFACTS = {"windows-release-payload", "group-server-release"}


class ProvenanceError(ValueError):
    """The record does not describe one protected-main verification run and its artifacts."""


def read_json(path: Path) -> Any:
    try:
        return bounded_read_json(path)
    except (OSError, ResourceLimitError) as exception:
        raise ProvenanceError(f"could not read {path}: {exception}") from exception


def canonical_timestamp(value: Any, label: str) -> str:
    if not isinstance(value, str):
        raise ProvenanceError(f"{label} is not a canonical UTC timestamp")
    try:
        datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ")
    except ValueError as exception:
        raise ProvenanceError(f"{label} is not a canonical UTC timestamp") from exception
    return value


def predicate(record: dict[str, Any], manifest: dict[str, Any], publisher: dict[str, str]) -> dict[str, Any]:
    if not isinstance(record, dict) or not isinstance(manifest, dict):
        raise ProvenanceError("the artifact record and manifest must be JSON objects")
    run = record.get("verificationRun") or {}
    repository = record.get("repository")
    sha = run.get("headSha")
    if (record.get("schemaVersion") != 1 or repository != SOURCE_REPOSITORY
            or REPOSITORY.fullmatch(str(repository)) is None or not isinstance(run, dict)
            or not HEX_40.fullmatch(str(sha))):
        raise ProvenanceError("the artifact record does not name a repository and a full commit")
    if run.get("workflowPath") != ".github/workflows/windows-verify.yml" or run.get("event") != "push" or run.get("headBranch") != "main":
        raise ProvenanceError("the artifact record is not from a push-to-main Windows verification run")
    run_id = str(run.get("id"))
    run_attempt = run.get("attempt")
    if DECIMAL_ID.fullmatch(run_id) is None or not isinstance(run_attempt, int) or isinstance(run_attempt, bool) or run_attempt <= 0:
        raise ProvenanceError("the verification invocation has no bounded run id and attempt")
    started = canonical_timestamp(run.get("startedAt"), "verification start")
    finished = canonical_timestamp(run.get("updatedAt"), "verification finish")
    if finished < started:
        raise ProvenanceError("the verification run finished before it started")

    source = manifest.get("source") or {}
    if (manifest.get("commit") != sha or not isinstance(source, dict)
            or source.get("repository") != repository or source.get("branch") != "main"
            or source.get("verificationWorkflow") != run.get("workflowPath")
            or str(source.get("verificationRunId")) != run_id
            or source.get("verificationRunAttempt") != run_attempt):
        raise ProvenanceError("the manifest and the artifact record describe different builds")
    artifacts = record.get("artifacts") or []
    if (not isinstance(artifacts, list) or not artifacts or len(artifacts) > MAX_ARTIFACT_COUNT
            or any(not isinstance(item, dict) or not HEX_64.fullmatch(str(item.get("sha256")))
                   or not isinstance(item.get("id"), int) or isinstance(item.get("id"), bool) or item["id"] <= 0
                   or not isinstance(item.get("size"), int) or isinstance(item.get("size"), bool) or item["size"] <= 0
                   for item in artifacts)):
        raise ProvenanceError("the artifact record has no digest-bound artifacts")
    artifact_names = [item.get("name") for item in artifacts]
    if len(artifact_names) != len(set(artifact_names)) or set(artifact_names) != VERIFICATION_ARTIFACTS:
        raise ProvenanceError("the artifact record does not contain exactly the two reviewed producer artifacts")
    manifest_artifacts = source.get("verificationArtifacts")
    expected_artifacts = [
        {"name": item["name"], "sha256": item["sha256"], "size": item["size"]}
        for item in artifacts
    ]
    if manifest_artifacts != expected_artifacts:
        raise ProvenanceError("the manifest's producer-artifact record differs from the provenance inputs")
    for key in ("repository", "workflowRef", "runId", "runAttempt"):
        if not publisher.get(key):
            raise ProvenanceError(f"the publisher's {key} is required")
    if (publisher["repository"] != SOURCE_REPOSITORY
            or publisher["workflowRef"] !=
            f"{SOURCE_REPOSITORY}/.github/workflows/publish.yml@refs/heads/main"
            or DECIMAL_ID.fullmatch(publisher["runId"]) is None
            or DECIMAL_ID.fullmatch(publisher["runAttempt"]) is None):
        raise ProvenanceError("the attester is not the reviewed protected-main publish workflow")

    server = "https://github.com"
    return {
        "buildDefinition": {
            "buildType": BUILD_TYPE,
            "externalParameters": {
                "workflow": {
                    "repository": f"{server}/{repository}",
                    "path": run["workflowPath"],
                    "ref": "refs/heads/main",
                },
            },
            "internalParameters": {
                "github": {"event_name": run["event"], "run_number": run.get("number")},
                "release": {"version": manifest.get("version"), "versions": manifest.get("versions")},
            },
            "resolvedDependencies": [
                {"uri": f"git+{server}/{repository}@refs/heads/main", "digest": {"gitCommit": sha}},
                *[
                    {
                        "name": item["name"],
                        "uri": f"{server}/{repository}/actions/runs/{run['id']}/artifacts/{item['id']}",
                        "digest": {"sha256": item["sha256"]},
                    }
                    for item in artifacts
                ],
            ],
        },
        "runDetails": {
            "builder": {"id": f"{server}/{repository}/{run['workflowPath']}@refs/heads/main"},
            "metadata": {
                "invocationId": run.get("url") or f"{server}/{repository}/actions/runs/{run['id']}/attempts/{run.get('attempt')}",
                "startedOn": started,
                "finishedOn": finished,
            },
            "byproducts": [
                {
                    "name": "attested-by",
                    "uri": f"{server}/{publisher['repository']}/actions/runs/{publisher['runId']}/attempts/{publisher['runAttempt']}",
                    "annotations": {
                        "workflowRef": publisher["workflowRef"],
                        "role": "observed the verification run and its artifact digests through the GitHub API, reconciled and signed the release; built nothing",
                    },
                },
            ],
        },
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--record", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--publisher-repository", required=True)
    parser.add_argument("--publisher-workflow-ref", required=True)
    parser.add_argument("--publisher-run-id", required=True)
    parser.add_argument("--publisher-run-attempt", required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        value = predicate(read_json(args.record), read_json(args.manifest), {
            "repository": args.publisher_repository,
            "workflowRef": args.publisher_workflow_ref,
            "runId": args.publisher_run_id,
            "runAttempt": args.publisher_run_attempt,
        })
    except (ProvenanceError, ResourceLimitError) as exception:
        print(f"provenance refused: {exception}", file=sys.stderr)
        return 1
    args.output.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
