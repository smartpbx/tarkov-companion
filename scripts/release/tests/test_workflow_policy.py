from __future__ import annotations

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

import check_workflow_policy  # noqa: E402


PIN = "actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09 # v5.1.0"

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


class WorkflowPolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-workflow-policy-")
        self.workflows = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def check(self, files: dict[str, str], *enforced: str) -> subprocess.CompletedProcess[str]:
        for name, body in files.items():
            (self.workflows / name).write_text(textwrap.dedent(body).lstrip(), encoding="utf-8")
        arguments = [sys.executable, str(CHECKER), "--workflows", str(self.workflows)]
        for name in enforced:
            arguments += ["--enforce", name]
        return subprocess.run(arguments, capture_output=True, text=True, check=False)

    def test_a_compliant_publisher_passes(self) -> None:
        result = self.check({"publish.yml": GOOD_PUBLISHER}, "publish.yml")

        self.assertEqual(0, result.returncode, result.stdout)

    def test_publisher_violations_fail(self) -> None:
        cases = {
            "pull request trigger": GOOD_PUBLISHER.replace("  workflow_dispatch:", "  workflow_dispatch:\n  pull_request:"),
            "signing outside an environment": GOOD_PUBLISHER.replace("    environment:\n      name: v2-canary-release\n", ""),
            "environment not limited to main": GOOD_PUBLISHER.replace("    if: github.ref == 'refs/heads/main'\n", ""),
            "workflow-wide write": GOOD_PUBLISHER.replace("permissions: {}", "permissions:\n  contents: write"),
            "mutable tag": GOOD_PUBLISHER.replace(PIN, "actions/checkout@v5", 1),
            "pin without reviewed tag": GOOD_PUBLISHER.replace(PIN, PIN.split(" #")[0], 1),
            "secret in unprotected job": GOOD_PUBLISHER.replace("      - run: ./scripts/scan-secrets.sh",
                                                                "      - run: echo ${{ secrets.V2_RELEASE_TOKEN }}"),
        }
        for label, body in cases.items():
            with self.subTest(label=label):
                result = self.check({"publish.yml": body}, "publish.yml")
                self.assertEqual(1, result.returncode, result.stdout)

    def test_pull_request_target_fails_even_in_a_workflow_owned_elsewhere(self) -> None:
        result = self.check({"other.yml": "on:\n  pull_request_target:\njobs: {}\n"})

        self.assertEqual(1, result.returncode, result.stdout)

    def test_other_owners_workflows_are_reported_not_failed(self) -> None:
        result = self.check({
            "publish.yml": GOOD_PUBLISHER,
            "ci.yml": "on:\n  pull_request:\njobs:\n  build:\n    permissions:\n      contents: write\n    steps:\n      - uses: actions/checkout@v5\n",
        }, "publish.yml")

        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("ci.yml:8 actions/checkout@v5 is not pinned", result.stdout)
        self.assertIn("runs for pull requests and grants write scopes: contents", result.stdout)

    def test_an_enforced_workflow_that_does_not_exist_fails(self) -> None:
        self.assertEqual(1, self.check({"publish.yml": GOOD_PUBLISHER}, "publish.yml", "license-lock.yml").returncode)

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

    def test_the_repository_release_workflows_pass(self) -> None:
        result = subprocess.run(
            [sys.executable, str(CHECKER), "--workflows", str(REPOSITORY_ROOT / ".github/workflows"),
             "--enforce", "publish.yml", "--enforce", "license-lock.yml"],
            capture_output=True, text=True, check=False,
        )

        self.assertEqual(0, result.returncode, result.stdout)


if __name__ == "__main__":
    unittest.main()
