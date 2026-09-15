#!/usr/bin/env python3
"""Checks that must pass before a verified build may become a release candidate.

Each check reads facts GitHub or the .NET SDK reports and refuses on anything it does not
recognise, because the failure these guard against is a release that looked fine: a run from a
branch that only resembled main, a CI failure the publisher never looked at, or a vulnerability
audit that could not reach its data and so found nothing.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import time
from pathlib import Path
from typing import Any, Callable, Sequence


HEX_40 = re.compile(r"^[0-9a-f]{40}$")
VERIFICATION_WORKFLOW = ".github/workflows/windows-verify.yml"

Runner = Callable[[Sequence[str]], subprocess.CompletedProcess]


class GateError(RuntimeError):
    """A release gate did not pass."""


def run_process(arguments: Sequence[str]) -> subprocess.CompletedProcess:
    return subprocess.run(list(arguments), capture_output=True, text=True, check=False)


def gh_json(runner: Runner, endpoint: str) -> Any:
    result = runner(["gh", "api", endpoint])
    if result.returncode != 0:
        raise GateError(f"GitHub API request failed for {endpoint}: {result.stderr.strip()}")
    try:
        return json.loads(result.stdout)
    except json.JSONDecodeError as exception:
        raise GateError(f"GitHub returned unreadable JSON for {endpoint}") from exception


def check_verification_run(run: dict[str, Any], repository: str) -> str:
    """The completed run that may feed canary: a push to this repository's main that passed."""
    expected = {
        "path": VERIFICATION_WORKFLOW,
        "event": "push",
        "head_branch": "main",
        "status": "completed",
        "conclusion": "success",
    }
    for key, value in expected.items():
        if run.get(key) != value:
            raise GateError(f"verification run {key} is {run.get(key)!r}, not {value!r}")
    if (run.get("repository") or {}).get("full_name") != repository or (run.get("head_repository") or {}).get("full_name") != repository:
        raise GateError("verification run did not build this repository's own branch")
    sha = run.get("head_sha")
    if not isinstance(sha, str) or HEX_40.fullmatch(sha) is None:
        raise GateError("verification run has no full head commit")
    return sha


def verification_run(runner: Runner, repository: str, run_id: str) -> dict[str, Any]:
    if re.fullmatch(r"[1-9][0-9]{0,19}", run_id) is None:
        raise GateError("the verification run id must be a bounded positive decimal identifier")
    run = gh_json(runner, f"repos/{repository}/actions/runs/{run_id}")
    sha = check_verification_run(run, repository)
    # Still reachable from main, so a force-push that removed the commit cannot be released.
    comparison = gh_json(runner, f"repos/{repository}/compare/{sha}...main")
    if comparison.get("status") not in ("ahead", "identical"):
        raise GateError(f"{sha} is not an ancestor of main (compare status {comparison.get('status')!r})")
    return {"sha": sha, "runNumber": run.get("run_number"), "runAttempt": run.get("run_attempt")}


def latest_ci_run(runs: list[dict[str, Any]], sha: str, workflow_path: str) -> dict[str, Any] | None:
    candidates = [
        run for run in runs
        if run.get("head_sha") == sha and run.get("event") == "push" and run.get("head_branch") == "main"
        and run.get("path") == workflow_path
    ]
    if not candidates:
        return None
    return max(candidates, key=lambda run: (str(run.get("created_at")), int(run.get("run_attempt") or 0)))


def wait_for_ci(runner: Runner, repository: str, sha: str, workflow: str, timeout_seconds: int,
                interval_seconds: int = 60, sleep: Callable[[float], None] = time.sleep) -> dict[str, Any]:
    """The same commit's CI must have succeeded. Waits for a run in progress, never for ever."""
    if HEX_40.fullmatch(sha) is None:
        raise GateError("the commit must be a full lowercase Git commit")
    workflow_path = f".github/workflows/{workflow}"
    deadline = timeout_seconds
    while True:
        payload = gh_json(
            runner, f"repos/{repository}/actions/workflows/{workflow}/runs?head_sha={sha}&event=push&branch=main&per_page=20"
        )
        run = latest_ci_run(payload.get("workflow_runs") or [], sha, workflow_path)
        if run is not None and run.get("status") == "completed":
            if run.get("conclusion") != "success":
                raise GateError(f"{workflow} for {sha} concluded {run.get('conclusion')!r}")
            return {"id": run.get("id"), "conclusion": "success"}
        if deadline <= 0:
            state = "no run" if run is None else f"status {run.get('status')!r}"
            raise GateError(f"{workflow} for {sha} did not complete in time ({state})")
        wait = min(interval_seconds, deadline)
        sleep(wait)
        deadline -= wait


def vulnerable_packages(report: dict[str, Any]) -> list[str]:
    """Every vulnerable package in `dotnet list package --vulnerable --format json` output.

    An audit that could not consult a vulnerability source reports nothing vulnerable, which is
    the same answer as a clean graph. Errors and a missing source list are therefore failures,
    not silence.
    """
    if report.get("version") != 1:
        raise GateError("unsupported dotnet list package report version")
    errors = [problem.get("text", "") for problem in report.get("problems") or [] if problem.get("level") == "error"]
    if errors:
        raise GateError("the vulnerability audit reported errors: " + "; ".join(errors))
    if not report.get("sources"):
        raise GateError("the vulnerability audit names no package source it consulted")
    projects = report.get("projects")
    if not isinstance(projects, list) or not projects:
        raise GateError("the vulnerability audit covered no projects")
    found: set[str] = set()
    for project in projects:
        for framework in project.get("frameworks") or []:
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(kind) or []:
                    for vulnerability in package.get("vulnerabilities") or []:
                        found.add(
                            f"{package.get('id')} {package.get('resolvedVersion')} "
                            f"({vulnerability.get('severity')}: {vulnerability.get('advisoryurl')})"
                        )
    return sorted(found)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    subparsers = parser.add_subparsers(dest="command", required=True)

    verification = subparsers.add_parser("verification-run")
    verification.add_argument("--repository", required=True)
    verification.add_argument("--run-id", required=True)

    ci = subparsers.add_parser("ci-run")
    ci.add_argument("--repository", required=True)
    ci.add_argument("--sha", required=True)
    ci.add_argument("--workflow", default="ci.yml")
    ci.add_argument("--timeout-seconds", type=int, default=1800)

    vulnerabilities = subparsers.add_parser("vulnerabilities")
    vulnerabilities.add_argument("--report", type=Path, required=True)

    args = parser.parse_args()
    try:
        if args.command == "verification-run":
            result: Any = verification_run(run_process, args.repository, args.run_id)
        elif args.command == "ci-run":
            result = wait_for_ci(run_process, args.repository, args.sha, args.workflow, args.timeout_seconds)
        else:
            report = json.loads(args.report.read_text(encoding="utf-8-sig"))
            found = vulnerable_packages(report)
            if found:
                raise GateError("vulnerable packages: " + "; ".join(found))
            result = {"vulnerablePackages": 0, "projects": len(report["projects"])}
    except (GateError, OSError, json.JSONDecodeError) as exception:
        print(f"release gate failed: {exception}", file=sys.stderr)
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
