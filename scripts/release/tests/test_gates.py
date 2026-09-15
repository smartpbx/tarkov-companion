from __future__ import annotations

import json
import subprocess
import sys
import unittest
from pathlib import Path
from typing import Sequence


RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RELEASE_DIRECTORY))

import gates  # noqa: E402
from gates import GateError  # noqa: E402


REPOSITORY = "smartpbx/tarkov-companion"
SHA = "c" * 40


def verification_run(**overrides):
    # The shape of GET /actions/runs/{id} for Windows verification run 34910997075.
    run = {
        "name": "Windows verification", "path": ".github/workflows/windows-verify.yml", "event": "push",
        "head_branch": "main", "status": "completed", "conclusion": "success", "head_sha": SHA,
        "run_number": 608, "run_attempt": 1,
        "repository": {"full_name": REPOSITORY}, "head_repository": {"full_name": REPOSITORY},
    }
    run.update(overrides)
    return run


class FakeApi:
    def __init__(self, responses: dict[str, object]) -> None:
        self.responses = responses
        self.calls: list[str] = []

    def __call__(self, arguments: Sequence[str]) -> subprocess.CompletedProcess:
        endpoint = arguments[2]
        self.calls.append(endpoint)
        for prefix, response in self.responses.items():
            if endpoint.startswith(prefix):
                value = response.pop(0) if isinstance(response, list) else response
                return subprocess.CompletedProcess(arguments, 0, json.dumps(value), "")
        return subprocess.CompletedProcess(arguments, 1, "", "gh: Not Found (HTTP 404)")


class VerificationRunTests(unittest.TestCase):
    def test_a_successful_push_to_main_in_this_repository_is_accepted(self) -> None:
        api = FakeApi({
            f"repos/{REPOSITORY}/actions/runs/34910997075": verification_run(),
            f"repos/{REPOSITORY}/compare/{SHA}...main": {"status": "identical"},
        })

        result = gates.verification_run(api, REPOSITORY, "34910997075")

        self.assertEqual(SHA, result["sha"])

    def test_anything_that_only_resembles_protected_main_is_refused(self) -> None:
        cases = {
            "pull request": verification_run(event="pull_request"),
            "fork branch named main": verification_run(head_repository={"full_name": "someone/tarkov-companion"}),
            "other workflow": verification_run(path=".github/workflows/ci.yml"),
            "failed": verification_run(conclusion="failure"),
            "in progress": verification_run(status="in_progress", conclusion=None),
            "short sha": verification_run(head_sha="abc123"),
        }
        for label, run in cases.items():
            with self.subTest(label=label), self.assertRaises(GateError):
                gates.check_verification_run(run, REPOSITORY)

    def test_a_commit_no_longer_on_main_is_refused(self) -> None:
        api = FakeApi({
            f"repos/{REPOSITORY}/actions/runs/1": verification_run(),
            f"repos/{REPOSITORY}/compare/": {"status": "diverged"},
        })

        with self.assertRaises(GateError):
            gates.verification_run(api, REPOSITORY, "1")
        with self.assertRaises(GateError):
            gates.verification_run(api, REPOSITORY, "1; rm -rf /")


class ContinuousIntegrationTests(unittest.TestCase):
    endpoint = f"repos/{REPOSITORY}/actions/workflows/ci.yml/runs"

    @staticmethod
    def ci_run(**overrides):
        run = {"id": 9, "head_sha": SHA, "event": "push", "head_branch": "main", "path": ".github/workflows/ci.yml",
               "status": "completed", "conclusion": "success", "created_at": "2026-09-14T23:55:35Z", "run_attempt": 1}
        run.update(overrides)
        return run

    def test_a_successful_run_for_the_commit_passes(self) -> None:
        api = FakeApi({self.endpoint: {"workflow_runs": [self.ci_run()]}})

        self.assertEqual({"id": 9, "conclusion": "success"}, gates.wait_for_ci(api, REPOSITORY, SHA, "ci.yml", 0))

    def test_the_latest_attempt_decides(self) -> None:
        api = FakeApi({self.endpoint: {"workflow_runs": [
            self.ci_run(id=1, conclusion="failure", run_attempt=1),
            self.ci_run(id=1, conclusion="success", run_attempt=2),
        ]}})

        self.assertEqual("success", gates.wait_for_ci(api, REPOSITORY, SHA, "ci.yml", 0)["conclusion"])

    def test_a_failed_or_absent_run_fails_and_a_running_one_is_waited_for(self) -> None:
        with self.assertRaises(GateError):
            gates.wait_for_ci(FakeApi({self.endpoint: {"workflow_runs": [self.ci_run(conclusion="failure")]}}), REPOSITORY, SHA, "ci.yml", 0)
        with self.assertRaises(GateError):
            gates.wait_for_ci(FakeApi({self.endpoint: {"workflow_runs": [self.ci_run(head_branch="feature")]}}), REPOSITORY, SHA, "ci.yml", 0)

        sleeps: list[float] = []
        api = FakeApi({self.endpoint: [
            {"workflow_runs": [self.ci_run(status="in_progress", conclusion=None)]},
            {"workflow_runs": [self.ci_run()]},
        ]})
        result = gates.wait_for_ci(api, REPOSITORY, SHA, "ci.yml", 120, interval_seconds=60, sleep=sleeps.append)
        self.assertEqual("success", result["conclusion"])
        self.assertEqual([60], sleeps)

        never = FakeApi({self.endpoint: {"workflow_runs": [self.ci_run(status="queued", conclusion=None)]}})
        with self.assertRaises(GateError):
            gates.wait_for_ci(never, REPOSITORY, SHA, "ci.yml", 90, interval_seconds=60, sleep=lambda _: None)


class VulnerabilityReportTests(unittest.TestCase):
    @staticmethod
    def report(**overrides):
        value = {
            "version": 1,
            "parameters": "--vulnerable --include-transitive",
            "sources": ["https://api.nuget.org/v3/index.json"],
            "projects": [{"path": "src/TarkovCompanion.App/TarkovCompanion.App.csproj", "frameworks": [
                {"framework": "net10.0", "topLevelPackages": [], "transitivePackages": []},
            ]}, {"path": "src/TarkovCompanion.Core/TarkovCompanion.Core.csproj"}],
        }
        value.update(overrides)
        return value

    def test_a_clean_audit_passes(self) -> None:
        self.assertEqual([], gates.vulnerable_packages(self.report()))

    def test_direct_and_transitive_vulnerabilities_are_listed(self) -> None:
        report = self.report()
        framework = report["projects"][0]["frameworks"][0]
        framework["topLevelPackages"] = [{"id": "Direct", "resolvedVersion": "1.0.0",
                                          "vulnerabilities": [{"severity": "High", "advisoryurl": "https://example.test/1"}]}]
        framework["transitivePackages"] = [{"id": "Deep", "resolvedVersion": "2.0.0",
                                            "vulnerabilities": [{"severity": "Low", "advisoryurl": "https://example.test/2"}]}]

        found = gates.vulnerable_packages(report)

        self.assertEqual(2, len(found))
        self.assertTrue(found[0].startswith("Deep 2.0.0 (Low"))

    def test_an_audit_that_could_not_look_is_not_a_clean_audit(self) -> None:
        cases = {
            "errors": self.report(problems=[{"level": "error", "text": "Unable to load the service index for source"}]),
            "no sources": self.report(sources=[]),
            "no projects": self.report(projects=[]),
            "unknown version": self.report(version=2),
        }
        for label, report in cases.items():
            with self.subTest(label=label), self.assertRaises(GateError):
                gates.vulnerable_packages(report)
        self.assertEqual([], gates.vulnerable_packages(self.report(problems=[{"level": "warning", "text": "deprecated"}])))


if __name__ == "__main__":
    unittest.main()
