"""The workflow policy reads YAML the way the runner does, and fails closed where it cannot.

Every case in the "evasions" test is valid YAML that the line-matching checker this replaced let
through, or a form where it and the runner could disagree about what the file says.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path


RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = RELEASE_DIRECTORY.parents[1]
CHECKER = RELEASE_DIRECTORY / "check_workflow_policy.py"
sys.path.insert(0, str(RELEASE_DIRECTORY))

try:
    import yaml  # noqa: F401
except ImportError:  # pragma: no cover - depends on the host
    yaml = None
    if os.environ.get("TARKOV_RELEASE_REQUIRE_TOOLS") == "1":
        raise
else:
    import check_workflow_policy  # noqa: E402


PIN = "actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5.1.0"
PINNED_REFERENCE = PIN.split(" #")[0]

GOOD_PUBLISHER = f"""
name: Publish
on:
  workflow_run:
    workflows: ["Windows verification"]
    types: [completed]
  workflow_dispatch:
permissions: {{}}
jobs:
  check:
    runs-on: ubuntu-24.04
    permissions:
      contents: read
    steps:
      - uses: {PIN}
      - run: ./scripts/scan-secrets.sh
  release:
    if: github.ref == 'refs/heads/main'
    environment:
      name: v2-canary-release
    permissions:
      id-token: write
      attestations: write
    steps:
      - uses: {PIN}
      - env:
          GH_TOKEN: ${{{{ secrets.V2_RELEASE_TOKEN }}}}
        run: echo publish
"""

GOOD_GATE = f"""
name: License lock
on:
  pull_request:
    branches: [main]
permissions:
  contents: read
jobs:
  supply-chain:
    runs-on: ubuntu-24.04
    steps:
      - uses: {PIN}
"""


@unittest.skipIf(yaml is None, "PyYAML is not installed on this host")
class WorkflowPolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-workflow-policy-")
        self.workflows = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def check(self, files: dict[str, str], *enforced: str, python: str = sys.executable,
              environment: dict[str, str] | None = None,
              enforce_all: bool = False) -> subprocess.CompletedProcess[str]:
        for name in list(self.workflows.glob("*.yml")):
            name.unlink()
        for name, body in files.items():
            (self.workflows / name).write_text(textwrap.dedent(body).lstrip(), encoding="utf-8")
        arguments = [python, str(CHECKER), "--workflows", str(self.workflows)]
        if enforce_all:
            arguments.append("--enforce-all")
        for name in enforced:
            arguments += ["--enforce", name]
        return subprocess.run(arguments, capture_output=True, text=True, check=False, env=environment)

    def test_a_compliant_publisher_and_gate_pass(self) -> None:
        result = self.check({"publish.yml": GOOD_PUBLISHER, "license-lock.yml": GOOD_GATE}, "publish.yml", "license-lock.yml")

        self.assertEqual(0, result.returncode, result.stdout)

    def test_secret_detection_only_reads_the_github_secrets_context(self) -> None:
        self.assertTrue(check_workflow_policy.reads_secret("${{ secrets.V2_RELEASE_TOKEN }}"))
        self.assertTrue(check_workflow_policy.reads_secret("${{ secrets['V2_RELEASE_TOKEN'] }}"))
        self.assertFalse(check_workflow_policy.reads_secret("./scripts/scan-secrets.sh"))
        self.assertFalse(check_workflow_policy.reads_secret("${{ github.token }}"))

    def test_publisher_violations_fail(self) -> None:
        cases = {
            "pull request trigger": GOOD_PUBLISHER.replace("  workflow_dispatch:", "  workflow_dispatch:\n  pull_request:"),
            "reusable-workflow trigger": GOOD_PUBLISHER.replace("  workflow_dispatch:", "  workflow_dispatch:\n  workflow_call:"),
            "signing outside an environment": GOOD_PUBLISHER.replace("    environment:\n      name: v2-canary-release\n", ""),
            "environment not limited to main": GOOD_PUBLISHER.replace("    if: github.ref == 'refs/heads/main'\n", ""),
            "workflow-wide write": GOOD_PUBLISHER.replace("permissions: {}", "permissions:\n  contents: write"),
            "mutable tag": GOOD_PUBLISHER.replace(PIN, "actions/checkout@v5", 1),
            "pin without reviewed tag": GOOD_PUBLISHER.replace(PIN, PINNED_REFERENCE, 1),
            "secret in unprotected job": GOOD_PUBLISHER.replace("      - run: ./scripts/scan-secrets.sh",
                                                                "      - run: echo ${{ secrets.V2_RELEASE_TOKEN }}"),
            "secret in an unprotected step input": GOOD_PUBLISHER.replace(
                "      - run: ./scripts/scan-secrets.sh",
                f"      - uses: {PIN}\n        with:\n          token: ${{{{secrets.V2_RELEASE_TOKEN}}}}"),
            "environment named as a string without main": GOOD_PUBLISHER.replace(
                "    if: github.ref == 'refs/heads/main'\n    environment:\n      name: v2-canary-release\n",
                "    environment: v2-canary-release\n"),
            "main words hidden inside a tautology": GOOD_PUBLISHER.replace(
                "    if: github.ref == 'refs/heads/main'\n",
                "    if: github.ref == 'refs/heads/main' || true\n"),
            "bracket secret in an unprotected job": GOOD_PUBLISHER.replace(
                "      - run: ./scripts/scan-secrets.sh",
                "      - run: echo ${{ secrets['V2_RELEASE_TOKEN'] }}"),
        }
        for label, body in cases.items():
            with self.subTest(label=label):
                result = self.check({"publish.yml": body}, "publish.yml")
                self.assertEqual(1, result.returncode, result.stdout)

    def test_valid_yaml_the_line_matcher_let_through_is_caught(self) -> None:
        publisher_cases = {
            "permissions: write-all at the top": GOOD_PUBLISHER.replace("permissions: {}", "permissions: write-all"),
            "permissions: write-all on the signing job": GOOD_PUBLISHER.replace(
                "    permissions:\n      contents: read\n", "    permissions: write-all\n"),
            "flow-mapping write outside the environment": GOOD_PUBLISHER.replace(
                "    permissions:\n      contents: read\n", "    permissions: {contents: write}\n"),
            "flow-mapping step with a mutable action": GOOD_PUBLISHER.replace(
                "      - run: ./scripts/scan-secrets.sh", "      - {uses: actions/checkout@v5, name: sneaky}"),
            "quoted permissions key": GOOD_PUBLISHER.replace(
                "    permissions:\n      contents: read\n", '    "permissions":\n      "contents": "write"\n'),
            "quoted uses key": GOOD_PUBLISHER.replace(f"      - uses: {PIN}", "      - 'uses': actions/checkout@v5", 1),
            "anchored mutable action reused by alias": GOOD_PUBLISHER.replace(
                f"      - uses: {PIN}\n      - run: ./scripts/scan-secrets.sh",
                "      - uses: &checkout actions/checkout@v5\n      - uses: *checkout"),
            "trigger list with pull_request_target": GOOD_PUBLISHER.replace(
                "on:\n  workflow_run:\n    workflows: [\"Windows verification\"]\n    types: [completed]\n  workflow_dispatch:\n",
                "on: [workflow_dispatch, pull_request_target]\n"),
            "scalar pull_request trigger": GOOD_PUBLISHER.replace(
                "on:\n  workflow_run:\n    workflows: [\"Windows verification\"]\n    types: [completed]\n  workflow_dispatch:\n",
                "on: pull_request\n"),
            "quoted on key": GOOD_PUBLISHER.replace("on:\n  workflow_run:", '"on":\n  pull_request_target:\n  workflow_run:'),
            "duplicate permissions key": GOOD_PUBLISHER.replace("permissions: {}", "permissions: {}\npermissions: write-all"),
            "merge key": GOOD_PUBLISHER.replace(
                "    permissions:\n      contents: read\n", "    permissions:\n      <<: {contents: write}\n"),
            "unknown permission level": GOOD_PUBLISHER.replace("permissions: {}", "permissions:\n  contents: admin"),
            "unknown permission value": GOOD_PUBLISHER.replace("permissions: {}", "permissions: write"),
            "reusable workflow at a branch": GOOD_PUBLISHER.replace(
                "  check:\n    runs-on: ubuntu-24.04\n    permissions:\n      contents: read\n    steps:\n"
                f"      - uses: {PIN}\n      - run: ./scripts/scan-secrets.sh\n",
                "  check:\n    uses: someone/else/.github/workflows/sign.yml@main\n"),
            "not YAML at all": "on: [unterminated\n",
        }
        for label, body in publisher_cases.items():
            with self.subTest(label=label):
                result = self.check({"publish.yml": body}, "publish.yml")
                self.assertEqual(1, result.returncode, f"{label}\n{result.stdout}")

        gate_cases = {
            "pull-request gate with write-all": GOOD_GATE.replace("permissions:\n  contents: read", "permissions: write-all"),
            "pull-request gate with a flow-mapping job write": GOOD_GATE.replace(
                "    runs-on: ubuntu-24.04\n", "    runs-on: ubuntu-24.04\n    permissions: {pull-requests: write}\n"),
            "gate relying on the repository default token": GOOD_GATE.replace("permissions:\n  contents: read\n", ""),
            "trigger list with pull_request and a write": GOOD_GATE.replace(
                "on:\n  pull_request:\n    branches: [main]\n", "on: [push, pull_request]\n").replace(
                "permissions:\n  contents: read", "permissions:\n  contents: write"),
        }
        for label, body in gate_cases.items():
            with self.subTest(label=label):
                result = self.check({"license-lock.yml": body}, "license-lock.yml")
                self.assertEqual(1, result.returncode, f"{label}\n{result.stdout}")

    def test_yaml_1_1_booleans_are_read_as_the_runner_reads_them(self) -> None:
        body = GOOD_GATE.replace("on:\n  pull_request:\n    branches: [main]\n",
                                 "on:\n  pull_request:\n    types: [opened]\n  push:\n    branches: [yes, on, off]\n")

        result = self.check({"license-lock.yml": body}, "license-lock.yml")

        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual({"pull_request", "push"}, check_workflow_policy.events(
            check_workflow_policy.load(textwrap.dedent(body).lstrip())[0]["on"]))

    def test_pull_request_target_fails_even_in_a_workflow_owned_elsewhere(self) -> None:
        for body in ("on:\n  pull_request_target:\njobs: {}\n", "on: [push, pull_request_target]\njobs: {}\n",
                     "'on': pull_request_target\njobs: {}\n", "on: [pull_request_target\n"):
            with self.subTest(body=body):
                result = self.check({"other.yml": body})
                self.assertEqual(1, result.returncode, result.stdout)

    def test_other_owners_workflows_are_reported_not_failed(self) -> None:
        result = self.check({
            "publish.yml": GOOD_PUBLISHER,
            "ci.yml": "on:\n  pull_request:\njobs:\n  build:\n    permissions:\n      contents: write\n    steps:\n      - uses: actions/checkout@v5\n",
        }, "publish.yml")

        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("ci.yml:8 actions/checkout@v5 is not pinned", result.stdout)
        self.assertIn("runs for pull requests and grants write scopes: contents", result.stdout)

    def test_enforce_all_turns_a_new_workflow_violation_into_a_failure(self) -> None:
        result = self.check({
            "publish.yml": GOOD_PUBLISHER,
            "future.yml": "on:\n  pull_request:\npermissions:\n  contents: read\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - uses: actions/checkout@v5\n",
        }, enforce_all=True)

        self.assertEqual(1, result.returncode, result.stdout)
        self.assertIn("future.yml:9 actions/checkout@v5 is not pinned", result.stdout)

    def test_an_enforced_workflow_that_does_not_exist_fails(self) -> None:
        self.assertEqual(1, self.check({"publish.yml": GOOD_PUBLISHER}, "publish.yml", "license-lock.yml").returncode)

    def test_without_a_yaml_parser_the_check_fails_rather_than_passing(self) -> None:
        shadow = Path(self.temporary.name) / "shadow"
        shadow.mkdir()
        (shadow / "yaml.py").write_text("raise ImportError('hidden for the test')\n", encoding="utf-8")

        result = self.check({"publish.yml": GOOD_PUBLISHER}, "publish.yml",
                            environment={**os.environ, "PYTHONPATH": str(shadow)})

        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn("PyYAML is required", result.stderr)

    def test_a_pin_whose_comment_names_a_different_tag_fails_tag_verification(self) -> None:
        report = check_workflow_policy.Report()
        path = self.workflows / "publish.yml"
        path.write_text(textwrap.dedent(GOOD_PUBLISHER).lstrip(), encoding="utf-8")
        check_workflow_policy.check_workflow(path, report, enforce=True)
        self.assertEqual({("actions/checkout", "v5.1.0")}, {(item[1], item[3]) for item in report.pins})

        check_workflow_policy.verify_tags(report, resolver=lambda repository, tag: "fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09")
        self.assertEqual([], report.errors)

        check_workflow_policy.verify_tags(report, resolver=lambda repository, tag: "08c6903cd8c0fde910a37f88322edcfb5dd907a8")
        self.assertTrue(report.errors and "but v5.1.0 is" in report.errors[0])

    def test_the_supply_chain_gate_watches_everything_that_produces_or_gates_signed_bytes(self) -> None:
        document, _ = check_workflow_policy.load((REPOSITORY_ROOT / ".github/workflows/license-lock.yml").read_text(encoding="utf-8"))
        push = document["on"]["push"]["paths"]
        pull_request = document["on"]["pull_request"]["paths"]

        self.assertEqual(push, pull_request)
        # Each of these produced or checked release bytes while the gate did not run for it.
        for producer in (".github/workflows/ci.yml", ".github/workflows/windows-verify.yml", ".github/workflows/publish.yml",
                         "scripts/package-windows.sh", "scripts/audit-licenses.sh", "scripts/scan-secrets.sh",
                         "scripts/release/build_manifest.py", "deploy/group-server/tarkov-group-update.sh",
                         "deploy/group-server/tarkov-group.service", "Directory.Packages.props", "Directory.Build.props",
                         "src/TarkovCompanion.GroupServer/TarkovCompanion.GroupServer.csproj",
                         "src/TarkovCompanion.Application/Services/Updates/SignedReleaseFeedConsumer.cs",
                         "src/TarkovCompanion.Infrastructure/Updates/AuthenticatedGitHubReleaseFeed.cs",
                         "tests/TarkovCompanion.UnitTests/SignedReleaseFeedConsumerTests.cs",
                         "docs/RELEASES.md", "licenses/dependency-license-map.json"):
            with self.subTest(producer=producer):
                self.assertTrue(any(self.glob_matches(pattern, producer) for pattern in push), producer)

    @staticmethod
    def glob_matches(pattern: str, path: str) -> bool:
        """GitHub's path filter glob, for the forms the gate uses: `**`, `*` and character classes."""
        import re
        expression = ""
        index = 0
        while index < len(pattern):
            if pattern.startswith("**/", index):
                expression += "(?:.*/)?"
                index += 3
            elif pattern.startswith("**", index):
                expression += ".*"
                index += 2
            elif pattern[index] == "*":
                expression += "[^/]*"
                index += 1
            elif pattern[index] == "[":
                end = pattern.index("]", index)
                expression += pattern[index:end + 1]
                index = end + 1
            else:
                expression += re.escape(pattern[index])
                index += 1
        return re.fullmatch(expression, path) is not None

    def test_the_repository_release_workflows_pass(self) -> None:
        result = subprocess.run(
            [sys.executable, str(CHECKER), "--workflows", str(REPOSITORY_ROOT / ".github/workflows"),
             "--enforce-all"],
            capture_output=True, text=True, check=False,
        )

        self.assertEqual(0, result.returncode, result.stdout)


if __name__ == "__main__":
    unittest.main()
