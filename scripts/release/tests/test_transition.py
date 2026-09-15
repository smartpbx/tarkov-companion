"""End-to-end fixture tests for scripts/release/transition.sh.

Everything real except GitHub and Sigstore: the transition script, the policy, the feed client
and the verification wrappers run as they do in the release job. The fake cosign binds a bundle
to the exact bytes and identity it signed, so a decision altered after signing fails the same
way a real one would.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RELEASE_DIRECTORY))
sys.path.insert(0, str(RELEASE_DIRECTORY / "tests"))

import release_policy  # noqa: E402
from sigstore_fixture import install_fake_cosign  # noqa: E402


TRANSITION = RELEASE_DIRECTORY / "transition.sh"
SIGN = RELEASE_DIRECTORY / "sign-files.sh"
FEED = "example/tarkov-feed"


class TransitionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-transition-")
        self.root = Path(self.temporary.name)
        self.state = self.root / "github"
        self.state.mkdir()
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.cosign_sha256 = install_fake_cosign(self.bin)
        (self.bin / "gh").write_text(f'#!/usr/bin/env bash\nexec {sys.executable} {RELEASE_DIRECTORY / "tests/fake_gh.py"} "$@"\n', encoding="utf-8")
        (self.bin / "gh").chmod(0o755)
        self.trust = self.root / "trusted-root.json"
        self.trust.write_text('{"mediaType": "trusted-root"}', encoding="utf-8")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def environment(self, state: Path | None = None, **extra: str) -> dict[str, str]:
        return {
            **os.environ,
            "PATH": f"{self.bin}:{os.environ['PATH']}",
            "TMPDIR": str(self.root),
            "FAKE_GH_STATE": str(state or self.state),
            "FAKE_GH_REPOSITORY": FEED,
            "GH_TOKEN": "feed-token",
            "FEED_REPOSITORY": FEED,
            "SOURCE_REPOSITORY": "smartpbx/tarkov-companion",
            "WORKFLOW_RUN_ID": "4242",
            "ACTOR": "release-operator",
            "REASON": "fixture",
            "TARKOV_SIGSTORE_TRUST_ROOT": str(self.trust),
            "TARKOV_COSIGN_SHA256": self.cosign_sha256,
            **extra,
        }

    def signed_release(self, version: str) -> Path:
        directory = self.root / f"release-{version}"
        directory.mkdir()
        artifacts = []
        for name, value in (("TarkovCompanion-GroupServer-linux-x64.tar.gz", f"relay {version}".encode()),
                            ("TarkovCompanionDesktop-win-Setup.exe", f"setup {version}".encode())):
            (directory / name).write_bytes(value)
            artifacts.append({"name": name, "component": "desktop", "role": "installer",
                              "sha256": hashlib.sha256(value).hexdigest(), "size": len(value)})
        (directory / "release-manifest.json").write_text(json.dumps({
            "schemaVersion": 1, "version": version, "commit": "c" * 40, "builtUtc": "2026-09-15T00:00:31Z",
            "versions": {"package": version}, "source": {"verificationRunId": "777"}, "artifacts": artifacts,
        }), encoding="utf-8")
        files = [str(directory / item["name"]) for item in artifacts] + [str(directory / "release-manifest.json")]
        subprocess.run([str(SIGN), *files], env=self.environment(), check=True, capture_output=True)
        return directory

    def transition(self, action: str, ring: str, *, release: Path | None = None, state: Path | None = None,
                   **extra: str) -> subprocess.CompletedProcess[str]:
        environment = self.environment(state, ACTION=action, RING=ring, **extra)
        if release is not None:
            environment.update(RELEASE_DIR=str(release), VERIFICATION_RUN_ID="777")
        return subprocess.run([str(TRANSITION)], env=environment, capture_output=True, text=True, check=False, timeout=120)

    def ring(self, ring: str, state: Path | None = None) -> dict[int, dict]:
        directory = (state or self.state) / "contents/rings" / ring
        result = {}
        for path in sorted(directory.glob("release-index-g*.json")) if directory.exists() else []:
            envelope = json.loads(path.read_text())
            result[release_policy.generation_of(path.name)] = json.loads(base64.b64decode(envelope["payloadBase64"]))
        return result

    def releases(self) -> list[dict]:
        path = self.state / "releases.json"
        return json.loads(path.read_text()) if path.exists() else []

    def assertOk(self, result: subprocess.CompletedProcess[str]) -> None:
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    # Lifecycle --------------------------------------------------------------------------------

    def test_publish_promote_pause_and_rollback_through_the_real_scripts(self) -> None:
        self.assertOk(self.transition("publish", "canary", release=self.signed_release("1.0.608")))
        self.assertOk(self.transition("publish", "canary", release=self.signed_release("1.0.609")))
        self.assertOk(self.transition("promote", "beta"))
        self.assertOk(self.transition("pause", "canary"))

        paused_publish = self.transition("publish", "canary", release=self.signed_release("1.0.610"))
        self.assertNotEqual(0, paused_publish.returncode)
        self.assertNotIn("v2-build-1.0.610", {release["tag"] for release in self.releases()})

        self.assertOk(self.transition("rollback", "canary"))

        canary = self.ring("canary")
        beta = self.ring("beta")
        self.assertEqual([1, 2, 3, 4], sorted(canary))
        for generation, index in canary.items():
            release_policy.validate_index(index, ring="canary", feed_repository=FEED, generation=generation)
        self.assertEqual("1.0.609", beta[1]["release"]["version"])
        self.assertEqual(("canary", 2), (beta[1]["authorization"]["sourceRing"], beta[1]["authorization"]["sourceGeneration"]))
        self.assertTrue(canary[3]["paused"])
        self.assertEqual("1.0.608", canary[4]["release"]["version"])
        self.assertTrue(canary[4]["paused"])
        self.assertEqual("1.0.609", canary[4]["rollback"]["from"]["version"])
        self.assertEqual({"v2-build-1.0.608", "v2-build-1.0.609"}, {r["tag"] for r in self.releases() if not r["draft"]})
        published = next(r for r in self.releases() if r["tag"] == "v2-build-1.0.609")
        self.assertEqual(6, len(published["assets"]))
        manifest = Path(published["assets"]["release-manifest.json"]["file"]).read_bytes()
        self.assertEqual(hashlib.sha256(manifest).hexdigest(), canary[2]["release"]["manifestSha256"])

    def test_a_superseded_build_changes_nothing(self) -> None:
        self.assertOk(self.transition("publish", "canary", release=self.signed_release("1.0.609")))

        result = self.transition("publish", "canary", release=self.signed_release("1.0.608"))

        self.assertOk(result)
        self.assertIn("superseded", result.stdout)
        self.assertEqual([1], sorted(self.ring("canary")))
        self.assertEqual(["v2-build-1.0.609"], [release["tag"] for release in self.releases()])

    # Refusals ---------------------------------------------------------------------------------

    def test_a_decision_altered_after_signing_stops_every_transition(self) -> None:
        self.assertOk(self.transition("publish", "canary", release=self.signed_release("1.0.608")))
        path = self.state / "contents/rings/canary/release-index-g0000000001.json"
        envelope = json.loads(path.read_text())
        payload = json.loads(base64.b64decode(envelope["payloadBase64"]))
        payload["paused"] = True
        envelope["payloadBase64"] = base64.b64encode(json.dumps(payload).encode()).decode()
        path.write_text(json.dumps(envelope))

        result = self.transition("resume", "canary")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("signs different bytes", result.stderr)
        self.assertEqual([1], sorted(self.ring("canary")))

    def test_a_public_feed_is_refused_before_anything_is_written(self) -> None:
        (self.state / "visibility").write_text("public")

        result = self.transition("publish", "canary", release=self.signed_release("1.0.608"))

        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.releases())
        self.assertEqual({}, self.ring("canary"))

    def test_a_candidate_manifest_changed_after_signing_is_refused_before_publication(self) -> None:
        release = self.signed_release("1.0.608")
        manifest = release / "release-manifest.json"
        value = json.loads(manifest.read_text(encoding="utf-8"))
        value["commit"] = "d" * 40
        manifest.write_text(json.dumps(value), encoding="utf-8")

        result = self.transition("publish", "canary", release=release)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("signs different bytes", result.stderr)
        self.assertEqual([], self.releases())
        self.assertEqual({}, self.ring("canary"))

    def test_transition_retry_count_is_bounded_before_feed_access(self) -> None:
        for value in ("0", "01", "11", "999999999999999999999999"):
            with self.subTest(value=value):
                result = self.transition("pause", "canary", TRANSITION_ATTEMPTS=value)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("TRANSITION_ATTEMPTS", result.stderr)
                self.assertEqual({}, self.ring("canary"))

    def test_transition_retry_count_accepts_its_upper_boundary(self) -> None:
        result = self.transition(
            "publish",
            "canary",
            release=self.signed_release("1.0.608"),
            TRANSITION_ATTEMPTS="10",
        )

        self.assertOk(result)
        self.assertEqual([1], sorted(self.ring("canary")))

    # Races and interruptions ------------------------------------------------------------------

    def test_a_lost_race_recomputes_from_the_winner_without_overwriting_it(self) -> None:
        self.assertOk(self.transition("publish", "canary", release=self.signed_release("1.0.608")))
        rival = self.root / "rival"
        shutil.copytree(self.state, rival)
        self.assertOk(self.transition("pause", "canary", state=rival))
        winner = (rival / "contents/rings/canary/release-index-g0000000002.json").read_bytes()
        (self.state / "race.json").write_bytes(winner)

        result = self.transition("mark-lkg", "canary")

        self.assertOk(result)
        self.assertIn("recomputing", result.stdout + result.stderr)
        self.assertEqual(winner, (self.state / "contents/rings/canary/release-index-g0000000002.json").read_bytes())
        canary = self.ring("canary")
        self.assertEqual("mark-lkg", canary[3]["authorization"]["action"])
        self.assertTrue(canary[3]["paused"], "the recomputed decision must build on the winner's pause")

    def test_a_run_that_published_its_build_but_not_its_decision_is_completed_by_a_rerun(self) -> None:
        release = self.signed_release("1.0.608")
        (self.state / "fail-put").touch()
        interrupted = self.transition("publish", "canary", release=release)
        self.assertNotEqual(0, interrupted.returncode)
        self.assertEqual({}, self.ring("canary"))
        self.assertEqual(1, len(self.releases()))

        # The re-run finds its build already public and adopts it rather than publishing it twice.
        rerun = self.transition("publish", "canary", release=release)

        self.assertOk(rerun)
        self.assertIn("already published and verified", rerun.stdout)
        self.assertEqual(1, len(self.releases()))
        self.assertEqual("1.0.608", self.ring("canary")[1]["release"]["version"])


if __name__ == "__main__":
    unittest.main()
