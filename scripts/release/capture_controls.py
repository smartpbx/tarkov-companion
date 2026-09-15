#!/usr/bin/env python3
"""Capture the repository, environment and feed controls the release design depends on.

The workflows ask for protected environments, a private feed and a protected main branch, but
YAML cannot prove any of those exist. This reads what GitHub reports, compares it with what the
release design requires, and writes the observation down with who captured it and when, so a
release record cites evidence instead of repeating the checklist. It only reads.

Settings the token cannot see are recorded as unreadable, never as met.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable, Sequence


RINGS = ("canary", "beta", "stable")
HTTP_STATUS = re.compile(r"\(HTTP ([0-9]{3})\)")

Api = Callable[[str], tuple[int, Any]]


def gh_api(endpoint: str) -> tuple[int, Any]:
    result = subprocess.run(["gh", "api", endpoint], capture_output=True, text=True, check=False)
    if result.returncode == 0:
        return 200, json.loads(result.stdout) if result.stdout.strip() else None
    match = HTTP_STATUS.search(result.stderr)
    return int(match.group(1)) if match else 0, None


def finding(control: str, status: str, observed: Any, required: str) -> dict[str, Any]:
    return {"control": control, "status": status, "observed": observed, "required": required}


def evaluate(api: Api, source: str, feed: str | None) -> list[dict[str, Any]]:
    findings: list[dict[str, Any]] = []

    def read(endpoint: str) -> tuple[str, Any]:
        status, value = api(endpoint)
        if status == 200:
            return "ok", value
        if status == 404:
            return "absent", None
        return "unreadable", status

    # Source repository and main.
    state, protection = read(f"repos/{source}/branches/main/protection")
    if state == "ok":
        reviews = protection.get("required_pull_request_reviews") or {}
        checks = (protection.get("required_status_checks") or {}).get("contexts") or []
        findings += [
            finding("main: force pushes and deletion blocked",
                    "met" if not protection["allow_force_pushes"]["enabled"] and not protection["allow_deletions"]["enabled"] else "gap",
                    {"forcePushes": protection["allow_force_pushes"]["enabled"], "deletions": protection["allow_deletions"]["enabled"]},
                    "neither allowed"),
            finding("main: changes reviewed before merge",
                    "met" if reviews.get("required_approving_review_count", 0) >= 1 else "gap",
                    {"requiredApprovingReviews": reviews.get("required_approving_review_count", 0)},
                    "at least one approving review"),
            finding("main: administrators cannot bypass protection",
                    "met" if protection["enforce_admins"]["enabled"] else "gap",
                    {"enforceAdmins": protection["enforce_admins"]["enabled"]}, "enforced for administrators"),
            finding("main: verification checks required",
                    "met" if {"checks", "windows-verify"} <= set(checks) else "gap",
                    {"requiredChecks": sorted(checks)}, "includes checks and windows-verify"),
            # License lock's job. Until it is required, a pull request that fails the dependency
            # review, the workflow policy or the release fixtures can still be merged.
            finding("main: supply-chain gate required",
                    "met" if "supply-chain" in checks else "gap",
                    {"requiredChecks": sorted(checks)}, "includes supply-chain"),
        ]
    else:
        findings.append(finding("main: branch protection", "gap" if state == "absent" else "unreadable", protection,
                                "protected (reading it needs repository administration read)"))

    state, rulesets = read(f"repos/{source}/rulesets")
    findings.append(finding("source rulesets", "info" if state == "ok" else "unreadable",
                            [item.get("name") for item in rulesets or []] if state == "ok" else rulesets,
                            "recorded for the release record; branch protection above is the evaluated control"))

    state, workflow = read(f"repos/{source}/actions/permissions/workflow")
    findings.append(finding("default GITHUB_TOKEN permission is read-only",
                            ("met" if workflow.get("default_workflow_permissions") == "read" else "gap") if state == "ok" else "unreadable",
                            workflow.get("default_workflow_permissions") if state == "ok" else workflow, "read"))

    state, actions = read(f"repos/{source}/actions/permissions")
    findings.append(finding("actions must be pinned to full commit SHAs",
                            ("met" if actions.get("sha_pinning_required") else "gap") if state == "ok" else "unreadable",
                            {"shaPinningRequired": actions.get("sha_pinning_required"), "allowedActions": actions.get("allowed_actions")}
                            if state == "ok" else actions,
                            "required (enable only after every workflow is pinned, including #279's)"))

    state, repository = read(f"repos/{source}")
    if state == "ok":
        analysis = repository.get("security_and_analysis") or {}
        scanning = (analysis.get("secret_scanning") or {}).get("status")
        push = (analysis.get("secret_scanning_push_protection") or {}).get("status")
        findings.append(finding("secret scanning and push protection", "met" if scanning == push == "enabled" else "gap",
                                {"secretScanning": scanning, "pushProtection": push}, "both enabled"))

    state, _ = read(f"repos/{source}/dependency-graph/sbom")
    findings.append(finding("dependency graph enabled, so dependency review can run",
                            {"ok": "met", "absent": "gap"}.get(state, "unreadable"), state, "enabled"))
    state, _ = read(f"repos/{source}/vulnerability-alerts")
    findings.append(finding("Dependabot vulnerability alerts",
                            {"ok": "met", "absent": "gap"}.get(state, "unreadable"), state, "enabled"))

    # Release environments.
    for ring in RINGS:
        name = f"v2-{ring}-release"
        state, environment = read(f"repos/{source}/environments/{name}")
        if state != "ok":
            findings.append(finding(f"environment {name}", "gap" if state == "absent" else "unreadable", environment, "exists"))
            continue
        rules = environment.get("protection_rules") or []
        reviewers = [rule for rule in rules if rule.get("type") == "required_reviewers"]
        policy = environment.get("deployment_branch_policy") or {}
        branch_state, branches = read(f"repos/{source}/environments/{name}/deployment-branch-policies")
        names = sorted(item.get("name") for item in (branches or {}).get("branch_policies", [])) if branch_state == "ok" else None
        main_only = policy.get("protected_branches") is True or (policy.get("custom_branch_policies") is True and names == ["main"])
        findings.append(finding(f"environment {name}: deployments only from main",
                                "met" if main_only else "gap", {"policy": policy, "branches": names}, "protected branches or exactly main"))
        if ring != "canary":
            prevents_self = any(rule.get("prevent_self_review") for rule in reviewers)
            findings.append(finding(f"environment {name}: required reviewer who is not the dispatcher",
                                    "met" if reviewers and prevents_self else "gap",
                                    {"reviewerRules": len(reviewers), "preventSelfReview": prevents_self},
                                    "required reviewers with self-review prevented"))
        secret_state, secrets = read(f"repos/{source}/environments/{name}/secrets")
        variable_state, variables = read(f"repos/{source}/environments/{name}/variables")
        secret_names = sorted(item["name"] for item in (secrets or {}).get("secrets", [])) if secret_state == "ok" else None
        variable_names = sorted(item["name"] for item in (variables or {}).get("variables", [])) if variable_state == "ok" else None
        findings.append(finding(f"environment {name}: feed credential and repository configured",
                                "unreadable" if secret_names is None or variable_names is None else
                                ("met" if "V2_RELEASE_TOKEN" in secret_names and "V2_RELEASE_REPOSITORY" in variable_names else "gap"),
                                {"secrets": secret_names, "variables": variable_names}, "V2_RELEASE_TOKEN secret and V2_RELEASE_REPOSITORY variable"))

    # The private feed.
    if not feed:
        findings.append(finding("private release feed repository", "gap", None, "named, private or internal, and initialised"))
        return findings
    state, repository = read(f"repos/{feed}")
    if state != "ok":
        findings.append(finding(f"feed {feed}", "gap" if state == "absent" else "unreadable", repository, "readable private repository"))
        return findings
    findings.append(finding(f"feed {feed}: private or internal", "met" if repository.get("visibility") in ("private", "internal") else "gap",
                            repository.get("visibility"), "private or internal"))
    state, immutable = read(f"repos/{feed}/immutable-releases")
    findings.append(finding(f"feed {feed}: published builds are immutable",
                            ("met" if immutable.get("enabled") else "gap") if state == "ok" else "unreadable",
                            immutable if state == "ok" else immutable, "immutable releases enabled"))
    return findings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source-repository", default="smartpbx/tarkov-companion")
    parser.add_argument("--feed-repository")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--require", action="store_true", help="exit non-zero unless every evaluated control is met")
    args = parser.parse_args()

    status, user = gh_api("user")
    findings = evaluate(gh_api, args.source_repository, args.feed_repository)
    record = {
        "schemaVersion": 1,
        "capturedUtc": datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        "capturedBy": (user or {}).get("login") if status == 200 else None,
        "sourceRepository": args.source_repository,
        "feedRepository": args.feed_repository,
        "findings": findings,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    for item in findings:
        print(f"{item['status']:>10}  {item['control']}")
    unmet = [item for item in findings if item["status"] in ("gap", "unreadable")]
    print(f"\n{len(findings) - len(unmet)} of {len(findings)} controls met or informational; evidence written to {args.output}")
    return 1 if args.require and unmet else 0


if __name__ == "__main__":
    raise SystemExit(main())
