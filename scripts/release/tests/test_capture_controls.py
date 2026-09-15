from __future__ import annotations

import sys
import unittest
from pathlib import Path


RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RELEASE_DIRECTORY))

import capture_controls  # noqa: E402


SOURCE = "smartpbx/tarkov-companion"
FEED = "example/tarkov-feed"


def configured() -> dict[str, tuple[int, object]]:
    responses: dict[str, tuple[int, object]] = {
        f"repos/{SOURCE}/branches/main/protection": (200, {
            "allow_force_pushes": {"enabled": False}, "allow_deletions": {"enabled": False},
            "enforce_admins": {"enabled": True}, "required_pull_request_reviews": {"required_approving_review_count": 1},
            "required_status_checks": {"contexts": ["linux", "windows-build", "checks", "windows-verify", "supply-chain"]},
        }),
        f"repos/{SOURCE}/rulesets": (200, []),
        f"repos/{SOURCE}/actions/permissions/workflow": (200, {"default_workflow_permissions": "read"}),
        f"repos/{SOURCE}/actions/permissions": (200, {"sha_pinning_required": True, "allowed_actions": "selected"}),
        f"repos/{SOURCE}": (200, {"security_and_analysis": {"secret_scanning": {"status": "enabled"},
                                                            "secret_scanning_push_protection": {"status": "enabled"}}}),
        f"repos/{SOURCE}/dependency-graph/sbom": (200, {"sbom": {}}),
        f"repos/{SOURCE}/vulnerability-alerts": (200, None),
        f"repos/{FEED}": (200, {"visibility": "private"}),
        f"repos/{FEED}/immutable-releases": (200, {"enabled": True}),
    }
    for ring in capture_controls.RINGS:
        name = f"v2-{ring}-release"
        responses[f"repos/{SOURCE}/environments/{name}"] = (200, {
            "protection_rules": [{"type": "required_reviewers", "prevent_self_review": True}],
            "deployment_branch_policy": {"protected_branches": False, "custom_branch_policies": True},
        })
        responses[f"repos/{SOURCE}/environments/{name}/deployment-branch-policies"] = (200, {"branch_policies": [{"name": "main"}]})
        responses[f"repos/{SOURCE}/environments/{name}/secrets"] = (200, {"secrets": [{"name": "V2_RELEASE_TOKEN"}]})
        responses[f"repos/{SOURCE}/environments/{name}/variables"] = (200, {"variables": [{"name": "V2_RELEASE_REPOSITORY"}]})
    return responses


class CaptureControlsTests(unittest.TestCase):
    @staticmethod
    def evaluate(responses: dict[str, tuple[int, object]], feed: str | None = FEED):
        return {item["control"]: item["status"] for item in capture_controls.evaluate(
            lambda endpoint: responses.get(endpoint, (404, None)), SOURCE, feed
        )}

    def test_a_fully_configured_repository_meets_every_control(self) -> None:
        statuses = self.evaluate(configured())

        self.assertEqual({"met", "info"}, set(statuses.values()), statuses)

    def test_absent_environments_feed_and_dependency_graph_are_gaps(self) -> None:
        responses = configured()
        del responses[f"repos/{SOURCE}/environments/v2-stable-release"]
        del responses[f"repos/{SOURCE}/dependency-graph/sbom"]

        statuses = self.evaluate(responses, feed=None)

        self.assertEqual("gap", statuses["environment v2-stable-release"])
        self.assertEqual("gap", statuses["private release feed repository"])
        self.assertEqual("gap", statuses["dependency graph enabled, so dependency review can run"])

    def test_a_supply_chain_gate_that_is_not_required_is_a_gap(self) -> None:
        responses = configured()
        protection = responses[f"repos/{SOURCE}/branches/main/protection"][1]
        protection["required_status_checks"]["contexts"].remove("supply-chain")

        self.assertEqual("gap", self.evaluate(responses)["main: supply-chain gate required"])

    def test_what_the_token_cannot_read_is_unreadable_never_met(self) -> None:
        responses = configured()
        responses[f"repos/{SOURCE}/branches/main/protection"] = (403, None)
        responses[f"repos/{SOURCE}/environments/v2-beta-release/secrets"] = (403, None)

        statuses = self.evaluate(responses)

        self.assertEqual("unreadable", statuses["main: branch protection"])
        self.assertEqual("unreadable", statuses["environment v2-beta-release: feed credential and repository configured"])

    def test_an_environment_open_to_other_branches_or_self_approval_is_a_gap(self) -> None:
        responses = configured()
        responses[f"repos/{SOURCE}/environments/v2-stable-release/deployment-branch-policies"] = (
            200, {"branch_policies": [{"name": "main"}, {"name": "release/*"}]})
        responses[f"repos/{SOURCE}/environments/v2-beta-release"] = (200, {
            "protection_rules": [{"type": "required_reviewers", "prevent_self_review": False}],
            "deployment_branch_policy": {"protected_branches": True, "custom_branch_policies": False},
        })

        statuses = self.evaluate(responses)

        self.assertEqual("gap", statuses["environment v2-stable-release: deployments only from main"])
        self.assertEqual("met", statuses["environment v2-beta-release: deployments only from main"])
        self.assertEqual("gap", statuses["environment v2-beta-release: required reviewer who is not the dispatcher"])

    def test_a_public_or_mutable_feed_is_a_gap(self) -> None:
        responses = configured()
        responses[f"repos/{FEED}"] = (200, {"visibility": "public"})
        responses[f"repos/{FEED}/immutable-releases"] = (200, {"enabled": False})

        statuses = self.evaluate(responses)

        self.assertEqual("gap", statuses[f"feed {FEED}: private or internal"])
        self.assertEqual("gap", statuses[f"feed {FEED}: published builds are immutable"])


if __name__ == "__main__":
    unittest.main()
