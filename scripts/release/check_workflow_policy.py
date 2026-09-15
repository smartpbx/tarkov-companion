#!/usr/bin/env python3
"""Static policy for the workflows that can touch releases.

Enforced files must pin every action to a full commit with the reviewed tag in a comment, and
the publisher must be unreachable from pull requests and hold its signing identity only inside
a protected environment. Every other workflow is reported rather than failed: they belong to
other owners, and a report that says exactly what is still mutable is more useful than a pass
that hides it or a failure nobody here may fix.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable


USES = re.compile(r"^\s*(?:-\s+)?uses:\s*(?P<reference>[^\s#]+)\s*(?:#\s*(?P<comment>.*))?$")
PINNED = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_./-]+)?@[0-9a-f]{40}$")
DOCKER_PINNED = re.compile(r"^docker://[^@\s]+@sha256:[0-9a-f]{64}$")
TAG_COMMENT = re.compile(r"^v?[0-9]+(?:\.[0-9]+){1,2}\b")
WRITE_SCOPE = re.compile(r"^\s*(?P<scope>[a-z-]+)\s*:\s*write\s*(?:#.*)?$")
PR_TRIGGER = re.compile(r"^\s*(pull_request|pull_request_target)\s*:|^on:\s*\[[^\]]*\bpull_request")


@dataclass
class Report:
    errors: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    pins: list[tuple[str, str, str, str]] = field(default_factory=list)


def top_level_block(lines: list[str], key: str) -> list[tuple[int, str]]:
    block: list[tuple[int, str]] = []
    inside = False
    for number, line in enumerate(lines, 1):
        if re.match(rf"^{re.escape(key)}:", line):
            inside = True
            continue
        if inside and line and not line.startswith((" ", "#")):
            break
        # Comments are prose, and prose about permissions must not read as a permission.
        if inside and not line.lstrip().startswith("#"):
            block.append((number, line))
    return block


def job_blocks(lines: list[str]) -> dict[str, list[tuple[int, str]]]:
    jobs: dict[str, list[tuple[int, str]]] = {}
    current: str | None = None
    for number, line in top_level_block(lines, "jobs"):
        match = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if match:
            current = match.group(1)
            jobs[current] = []
        elif current is not None:
            jobs[current].append((number, line))
    return jobs


def check_pinning(path: Path, lines: list[str], report: Report, enforce: bool) -> None:
    unpinned = []
    for number, line in enumerate(lines, 1):
        match = USES.match(line)
        if not match:
            continue
        reference = match.group("reference").strip("'\"")
        if reference.startswith("./") or DOCKER_PINNED.match(reference):
            continue
        if not PINNED.match(reference):
            unpinned.append(f"{path.name}:{number} {reference}")
        elif enforce:
            comment = (match.group("comment") or "").strip()
            tag = TAG_COMMENT.match(comment)
            if not tag:
                report.errors.append(f"{path.name}:{number} pins {reference} without the reviewed tag in a comment")
            else:
                action, sha = reference.split("@", 1)
                report.pins.append((f"{path.name}:{number}", "/".join(action.split("/")[:2]), sha, tag.group(0)))
    if enforce:
        report.errors.extend(f"{item} is not pinned to a full commit" for item in unpinned)
    elif unpinned:
        report.notes.extend(f"{item} is not pinned to a full commit (owner: not this workflow set)" for item in unpinned)


def check_workflow(path: Path, report: Report, enforce: bool) -> None:
    text = path.read_text(encoding="utf-8")
    lines = text.splitlines()
    check_pinning(path, lines, report, enforce)

    if any(re.match(r"^\s*pull_request_target\s*:", line) for line in lines) or "pull_request_target" in "".join(
        line for _, line in top_level_block(lines, "on")
    ):
        report.errors.append(f"{path.name} uses pull_request_target, which runs pull-request code with repository secrets")

    pull_request = any(PR_TRIGGER.match(line) for _, line in top_level_block(lines, "on")) or bool(
        re.search(r"^on:.*pull_request", text, re.M)
    )
    writes = [(number, WRITE_SCOPE.match(line).group("scope")) for number, line in enumerate(lines, 1) if WRITE_SCOPE.match(line)]
    if pull_request and writes:
        message = f"{path.name} runs for pull requests and grants write scopes: " + ", ".join(
            f"{scope} (line {number})" for number, scope in writes
        )
        (report.errors if enforce else report.notes).append(message)

    if path.name == "publish.yml":
        check_publisher(lines, report)


def check_publisher(lines: list[str], report: Report) -> None:
    triggers = {
        match.group(1)
        for _, line in top_level_block(lines, "on")
        if (match := re.match(r"^  ([a-z_]+):", line))
    }
    if not triggers or not triggers <= {"workflow_run", "workflow_dispatch"}:
        report.errors.append(f"publish.yml may be triggered only by workflow_run or workflow_dispatch, not {sorted(triggers)}")

    top_permissions = [line for _, line in top_level_block(lines, "permissions")]
    top_inline = [line for line in lines if re.match(r"^permissions:\s*[^\s#]", line)]
    if any(WRITE_SCOPE.match(line) for line in top_permissions) or any(
        re.match(r"^permissions:\s*write-all", line) for line in top_inline
    ):
        report.errors.append("publish.yml grants a write scope to every job")

    for name, block in job_blocks(lines).items():
        body = [line for _, line in block]
        scopes = {match.group("scope") for line in body if (match := WRITE_SCOPE.match(line))}
        has_environment = any(re.match(r"^    environment:", line) for line in body)
        main_only = any(re.match(r"^    if:.*github\.ref\s*==\s*'refs/heads/main'", line) for line in body)
        if scopes & {"id-token", "attestations", "contents", "packages"} and not has_environment:
            report.errors.append(f"publish.yml job {name} holds {sorted(scopes)} outside a protected environment")
        if has_environment and not main_only:
            report.errors.append(f"publish.yml job {name} requests an environment without an explicit main-only condition")
        if any(re.search(r"\$\{\{\s*secrets\.", line) for line in body) and not has_environment:
            report.errors.append(f"publish.yml job {name} reads a secret outside a protected environment")


def resolve_tag(repository: str, tag: str) -> str:
    """The commit a tag names, following an annotated tag object to its commit."""
    def api(endpoint: str) -> dict:
        result = subprocess.run(["gh", "api", endpoint], capture_output=True, text=True, check=False)
        if result.returncode != 0:
            raise LookupError(result.stderr.strip() or f"could not read {endpoint}")
        return json.loads(result.stdout)

    target = api(f"repos/{repository}/git/ref/tags/{tag}")["object"]
    while target["type"] == "tag":
        target = api(f"repos/{repository}/git/tags/{target['sha']}")["object"]
    return target["sha"]


def verify_tags(report: Report, resolver: Callable[[str, str], str] = resolve_tag) -> None:
    # A comment is only a claim. The draft of this workflow labelled checkout v5.1.0 as v5.0.0,
    # which nobody reading the file could see and a resolver sees immediately.
    for location, repository, sha, tag in report.pins:
        try:
            actual = resolver(repository, tag)
        except (LookupError, KeyError, ValueError) as exception:
            report.errors.append(f"{location} could not resolve {repository} {tag}: {exception}")
            continue
        if actual != sha:
            report.errors.append(f"{location} pins {repository}@{sha} but {tag} is {actual}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--workflows", type=Path, default=Path(".github/workflows"))
    parser.add_argument("--enforce", action="append", default=[], help="workflow file name held to the full policy")
    parser.add_argument("--summary", type=Path, help="append a Markdown report here")
    parser.add_argument("--verify-tags", action="store_true", help="resolve each pinned tag through the GitHub API")
    args = parser.parse_args()

    report = Report()
    files = sorted(args.workflows.glob("*.yml")) + sorted(args.workflows.glob("*.yaml"))
    missing = sorted(set(args.enforce) - {path.name for path in files})
    report.errors.extend(f"enforced workflow {name} does not exist" for name in missing)
    for path in files:
        check_workflow(path, report, path.name in args.enforce)
    if args.verify_tags:
        verify_tags(report)

    lines = ["### Workflow supply-chain policy", ""]
    lines += [f"- FAIL: {item}" for item in report.errors] or ["- Enforced workflows pass."]
    if report.notes:
        lines += ["", "Reported, not enforced here:"] + [f"- {item}" for item in report.notes]
    output = "\n".join(lines) + "\n"
    print(output)
    if args.summary:
        with args.summary.open("a", encoding="utf-8") as stream:
            stream.write(output)
    return 1 if report.errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
