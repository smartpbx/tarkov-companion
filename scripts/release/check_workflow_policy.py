#!/usr/bin/env python3
"""Static policy for the workflows that can touch releases, read as YAML rather than as lines.

Enforced files must pin every action and reusable workflow to a full commit, with the reviewed
tag in a comment. They must declare their token permissions, and must never grant a write scope
to a run a pull request can start. The publisher must be unreachable from pull requests and must
hold its signing identity and secrets only inside a main-only protected environment. The release
gate uses ``--enforce-all`` so a newly added workflow cannot silently reintroduce a mutable
producer. `pull_request_target` fails everywhere.

The first version matched lines with regular expressions, and valid YAML walked straight past it:
`permissions: write-all`, flow mappings such as `permissions: {contents: write}` and
`- {uses: actions/checkout@v5}`, quoted keys, and `on: [push, pull_request_target]`. This parses
each file the way the runner does, and fails closed where the two could differ: a duplicate key,
a merge key, a YAML 1.1 boolean where the runner reads a string, or a permission value it does
not recognise. Comments are the one thing a parser discards, so a pin's reviewed tag is still
read from the source line the parser says the pin is on.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

try:
    import yaml
except ImportError:  # pragma: no cover - exercised by a test that hides the module
    print("workflow policy failed: PyYAML is required (pip install --require-hashes -r scripts/release/policy-requirements.txt)",
          file=sys.stderr)
    raise SystemExit(2)


PINNED = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_./-]+)?@[0-9a-f]{40}$")
DOCKER_PINNED = re.compile(r"^docker://[^@\s]+@sha256:[0-9a-f]{64}$")
TAG_COMMENT = re.compile(r"#\s*(v?[0-9]+(?:\.[0-9]+){1,2})\b")
PERMISSION_LEVELS = {"read", "write", "none"}
# Every scope GitHub documents for GITHUB_TOKEN; an unknown name is refused rather than ignored.
PERMISSION_SCOPES = {
    "actions", "attestations", "checks", "contents", "deployments", "discussions", "id-token", "issues",
    "models", "packages", "pages", "pull-requests", "repository-projects", "security-events", "statuses",
}
PULL_REQUEST_EVENTS = {"pull_request", "pull_request_target", "pull_request_review", "pull_request_review_comment"}
PUBLISHER_TRIGGERS = {"workflow_run", "workflow_dispatch"}


@dataclass
class Report:
    errors: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)
    pins: list[tuple[str, str, str, str]] = field(default_factory=list)


class PolicyParseError(ValueError):
    """The file is not YAML this checker can read the same way the runner does."""


class WorkflowLoader(yaml.SafeLoader):
    """SafeLoader that refuses what could make this checker and the runner read different files."""

    def construct_mapping(self, node: yaml.MappingNode, deep: bool = False) -> dict[Any, Any]:
        seen: set[Any] = set()
        for key_node, _ in node.value:
            if key_node.tag == "tag:yaml.org,2002:merge":
                raise PolicyParseError(f"line {key_node.start_mark.line + 1}: merge keys are not accepted")
            key = self.construct_object(key_node, deep=True)
            if not isinstance(key, str):
                raise PolicyParseError(f"line {key_node.start_mark.line + 1}: mapping key {key!r} is not a string")
            if key in seen:
                raise PolicyParseError(f"line {key_node.start_mark.line + 1}: duplicate key {key!r}")
            seen.add(key)
        return super().construct_mapping(node, deep=deep)

    def flatten_mapping(self, node: yaml.MappingNode) -> None:
        for key_node, _ in node.value:
            if key_node.tag == "tag:yaml.org,2002:merge":
                raise PolicyParseError(f"line {key_node.start_mark.line + 1}: merge keys are not accepted")
        super().flatten_mapping(node)


# YAML 1.1 reads on/off/yes/no/y/n as booleans; the runner reads them as strings. Only true and
# false are booleans here, so `on:` is the key "on" and not True.
WorkflowLoader.yaml_implicit_resolvers = {
    first: [(tag, pattern) for tag, pattern in resolvers if tag != "tag:yaml.org,2002:bool"]
    for first, resolvers in yaml.SafeLoader.yaml_implicit_resolvers.items()
}
WorkflowLoader.add_implicit_resolver(
    "tag:yaml.org,2002:bool", re.compile(r"^(?:true|True|TRUE|false|False|FALSE)$"), list("tTfF"))


def load(text: str) -> tuple[dict[str, Any], yaml.Node]:
    try:
        node = yaml.compose(text, Loader=WorkflowLoader)
        document = yaml.load(text, Loader=WorkflowLoader)
    except PolicyParseError:
        raise
    except yaml.YAMLError as exception:
        raise PolicyParseError(f"not valid YAML: {exception}") from exception
    if not isinstance(document, dict) or node is None:
        raise PolicyParseError("the workflow is not a mapping")
    return document, node


def events(value: Any) -> set[str]:
    if isinstance(value, str):
        return {value}
    if isinstance(value, list) and all(isinstance(item, str) for item in value):
        return set(value)
    if isinstance(value, dict):
        return set(value)
    raise PolicyParseError(f"unrecognised `on` value: {value!r}")


def write_scopes(value: Any, where: str) -> set[str]:
    """The scopes a permissions value grants write to; `*` for write-all."""
    if value is None:
        return set()
    if isinstance(value, str):
        if value == "write-all":
            return {"*"}
        if value == "read-all":
            return set()
        raise PolicyParseError(f"{where}: unrecognised permissions value {value!r}")
    if isinstance(value, dict):
        granted = set()
        for scope, level in value.items():
            if scope not in PERMISSION_SCOPES or level not in PERMISSION_LEVELS:
                raise PolicyParseError(f"{where}: unrecognised permission {scope!r}: {level!r}")
            if level == "write":
                granted.add(scope)
        return granted
    raise PolicyParseError(f"{where}: unrecognised permissions value {value!r}")


def uses_nodes(node: yaml.Node) -> list[tuple[int, str]]:
    """Every scalar under a `uses` key, with its line, however the mapping around it is written."""
    found: list[tuple[int, str]] = []
    seen: set[int] = set()
    stack = [node]
    while stack:
        current = stack.pop()
        if id(current) in seen:
            continue
        seen.add(id(current))
        if isinstance(current, yaml.MappingNode):
            for key, value in current.value:
                if isinstance(key, yaml.ScalarNode) and key.value == "uses":
                    if not isinstance(value, yaml.ScalarNode):
                        raise PolicyParseError(f"line {value.start_mark.line + 1}: `uses` is not a string")
                    found.append((value.start_mark.line + 1, value.value))
                stack.append(value)
        elif isinstance(current, yaml.SequenceNode):
            stack.extend(current.value)
    return sorted(found)


def check_pinning(path: Path, lines: list[str], node: yaml.Node, report: Report, enforce: bool) -> None:
    for number, reference in uses_nodes(node):
        location = f"{path.name}:{number}"
        if reference.startswith("./") or DOCKER_PINNED.match(reference):
            continue
        if not PINNED.match(reference):
            message = f"{location} {reference} is not pinned to a full commit"
            if enforce:
                report.errors.append(message)
            else:
                report.notes.append(f"{message} (owner: not this workflow set)")
            continue
        if not enforce:
            continue
        tag = TAG_COMMENT.search(lines[number - 1].split(reference, 1)[-1]) if reference in lines[number - 1] else None
        if tag is None:
            report.errors.append(f"{location} pins {reference} without the reviewed tag in a comment on the same line")
            continue
        action, sha = reference.split("@", 1)
        report.pins.append((location, "/".join(action.split("/")[:2]), sha, tag.group(1)))


def job_items(document: dict[str, Any]) -> dict[str, dict[str, Any]]:
    jobs = document.get("jobs")
    if not isinstance(jobs, dict) or not all(isinstance(job, dict) for job in jobs.values()):
        raise PolicyParseError("`jobs` is not a mapping of job mappings")
    return jobs


def check_workflow(path: Path, report: Report, enforce: bool) -> None:
    text = path.read_text(encoding="utf-8")
    try:
        document, node = load(text)
        triggers = events(document.get("on"))
        jobs = job_items(document)
        check_pinning(path, text.splitlines(), node, report, enforce)
        workflow_writes = write_scopes(document.get("permissions"), f"{path.name} permissions")
        job_writes = {
            name: write_scopes(job["permissions"], f"{path.name} job {name} permissions") if "permissions" in job else workflow_writes
            for name, job in jobs.items()
        }
    except PolicyParseError as exception:
        # Unreadable is a failure for anything enforced, and for anything else is reported, never
        # quietly treated as compliant.
        (report.errors if enforce else report.notes).append(f"{path.name}: {exception}")
        if "pull_request_target" in text:
            report.errors.append(f"{path.name} mentions pull_request_target and could not be read to rule it out")
        return

    if "pull_request_target" in triggers:
        report.errors.append(f"{path.name} uses pull_request_target, which runs pull-request code with repository secrets")

    if enforce and "permissions" not in document and any("permissions" not in job for job in jobs.values()):
        report.errors.append(f"{path.name} leaves token permissions to the repository default; declare them")

    granted = sorted({scope for scopes in job_writes.values() for scope in scopes} | workflow_writes)
    if triggers & PULL_REQUEST_EVENTS and granted:
        message = f"{path.name} runs for pull requests and grants write scopes: " + ", ".join(granted)
        (report.errors if enforce else report.notes).append(message)

    if path.name == "publish.yml":
        check_publisher(triggers, workflow_writes, jobs, job_writes, report)


def environment_name(job: dict[str, Any]) -> str | None:
    value = job.get("environment")
    if isinstance(value, str):
        return value
    if isinstance(value, dict) and isinstance(value.get("name"), str):
        return value["name"]
    return None


def top_level_conjuncts(expression: str) -> list[str]:
    """Split a GitHub expression on top-level ``&&`` without trusting substring presence.

    A guard of ``main || true`` contains the right words and is not a main-only guard. Requiring
    the canonical comparison as its own top-level conjunct keeps other job conditions flexible
    while making that bypass impossible. Invalid quoting or parentheses yield no conjuncts and
    therefore fail closed.
    """
    value = expression.strip()
    if value.startswith("${{") and value.endswith("}}"):
        value = value[3:-2].strip()
    parts: list[str] = []
    start = 0
    depth = 0
    quote: str | None = None
    index = 0
    while index < len(value):
        character = value[index]
        if quote is not None:
            if character == quote:
                # GitHub expressions escape a single quote by doubling it.
                if quote == "'" and index + 1 < len(value) and value[index + 1] == "'":
                    index += 2
                    continue
                quote = None
        elif character in ("'", '"'):
            quote = character
        elif character == "(":
            depth += 1
        elif character == ")":
            depth -= 1
            if depth < 0:
                return []
        elif value.startswith("&&", index) and depth == 0:
            parts.append(value[start:index].strip())
            index += 2
            start = index
            continue
        index += 1
    if quote is not None or depth != 0:
        return []
    parts.append(value[start:].strip())
    return [part for part in parts if part]


def reads_secret(value: Any) -> bool:
    """Recognise secret-context reads inside GitHub expressions in any scalar."""
    if isinstance(value, dict):
        return any(reads_secret(key) or reads_secret(item) for key, item in value.items())
    if isinstance(value, list):
        return any(reads_secret(item) for item in value)
    if not isinstance(value, str):
        return False
    expressions = re.findall(r"\$\{\{(?:(?!\}\}).)*\}\}", value, flags=re.DOTALL)
    return any(re.search(r"(?<![A-Za-z0-9_])secrets\s*(?:\.|\[)", expression) for expression in expressions)


def check_publisher(triggers: set[str], workflow_writes: set[str], jobs: dict[str, dict[str, Any]],
                    job_writes: dict[str, set[str]], report: Report) -> None:
    if not triggers or not triggers <= PUBLISHER_TRIGGERS:
        report.errors.append(f"publish.yml may be triggered only by workflow_run or workflow_dispatch, not {sorted(triggers)}")
    if workflow_writes:
        report.errors.append(f"publish.yml grants write scopes to every job: {sorted(workflow_writes)}")
    for name, job in jobs.items():
        environment = environment_name(job)
        condition = re.sub(r"\s+", " ", str(job.get("if", "")))
        if "uses" in job:
            report.errors.append(f"publish.yml job {name} calls a reusable workflow, which would hold its authority elsewhere")
        if job_writes[name] and environment is None:
            report.errors.append(f"publish.yml job {name} holds {sorted(job_writes[name])} outside a protected environment")
        if environment is not None and "github.ref == 'refs/heads/main'" not in top_level_conjuncts(condition):
            report.errors.append(f"publish.yml job {name} requests an environment without an explicit main-only condition")
        if reads_secret(job) and environment is None:
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
    parser.add_argument("--enforce-all", action="store_true", help="hold every workflow, including future files, to the full policy")
    parser.add_argument("--summary", type=Path, help="append a Markdown report here")
    parser.add_argument("--verify-tags", action="store_true", help="resolve each pinned tag through the GitHub API")
    args = parser.parse_args()

    report = Report()
    files = sorted(args.workflows.glob("*.yml")) + sorted(args.workflows.glob("*.yaml"))
    missing = sorted(set(args.enforce) - {path.name for path in files})
    report.errors.extend(f"enforced workflow {name} does not exist" for name in missing)
    for path in files:
        check_workflow(path, report, args.enforce_all or path.name in args.enforce)
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
