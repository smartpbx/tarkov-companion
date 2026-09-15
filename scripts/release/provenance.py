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
from pathlib import Path
from typing import Any


BUILD_TYPE = "https://github.com/smartpbx/tarkov-companion/blob/main/docs/RELEASES.md#verified-build-provenance"
HEX_40 = re.compile(r"^[0-9a-f]{40}$")
HEX_64 = re.compile(r"^[0-9a-f]{64}$")


class ProvenanceError(ValueError):
    """The record does not describe one protected-main verification run and its artifacts."""


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exception:
        raise ProvenanceError(f"could not read {path}: {exception}") from exception


def predicate(record: dict[str, Any], manifest: dict[str, Any], publisher: dict[str, str]) -> dict[str, Any]:
    run = record.get("verificationRun") or {}
    repository = record.get("repository")
    sha = run.get("headSha")
    if record.get("schemaVersion") != 1 or not isinstance(repository, str) or not HEX_40.fullmatch(str(sha)):
        raise ProvenanceError("the artifact record does not name a repository and a full commit")
    if run.get("workflowPath") != ".github/workflows/windows-verify.yml" or run.get("event") != "push" or run.get("headBranch") != "main":
        raise ProvenanceError("the artifact record is not from a push-to-main Windows verification run")
    if manifest.get("commit") != sha or str((manifest.get("source") or {}).get("verificationRunId")) != str(run.get("id")):
        raise ProvenanceError("the manifest and the artifact record describe different builds")
    artifacts = record.get("artifacts") or []
    if not artifacts or any(not HEX_64.fullmatch(str(item.get("sha256"))) for item in artifacts):
        raise ProvenanceError("the artifact record has no digest-bound artifacts")
    for key in ("repository", "workflowRef", "runId", "runAttempt"):
        if not publisher.get(key):
            raise ProvenanceError(f"the publisher's {key} is required")

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
                "startedOn": run.get("startedAt"),
                "finishedOn": run.get("updatedAt"),
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
    except ProvenanceError as exception:
        print(f"provenance refused: {exception}", file=sys.stderr)
        return 1
    args.output.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
