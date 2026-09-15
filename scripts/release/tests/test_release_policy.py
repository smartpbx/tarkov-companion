from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path


RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RELEASE_DIRECTORY))

import release_policy  # noqa: E402
from release_policy import PolicyError, PolicySuperseded  # noqa: E402


FEED = "example/tarkov-feed"
NOW = datetime(2026, 9, 15, 12, 0, tzinfo=timezone.utc)


class ReleasePolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-release-policy-")
        self.root = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def manifest(self, version: str, commit: str) -> Path:
        path = self.root / f"manifest-{version}.json"
        path.write_text(json.dumps({"schemaVersion": 1, "version": version, "commit": commit}) + "\n", encoding="utf-8")
        return path

    def transition(self, ring: str, action: str, current=None, **extra):
        return release_policy.next_index(
            ring=ring,
            action=action,
            actor="release-operator",
            feed_repository=FEED,
            workflow_run_id="4242",
            current=current,
            current_generation=current["generation"] if current else 0,
            now=NOW,
            **extra,
        )

    def publish(self, version: str, current=None, commit: str = "a" * 40):
        return self.transition(
            "canary", "publish", current, manifest_path=self.manifest(version, commit), verification_run_id="777"
        )

    def promote(self, ring: str, source, current=None):
        return self.transition(ring, "promote", current, source=source, source_generation=source["generation"])

    # Entering and advancing -------------------------------------------------------------------

    def test_a_verified_build_enters_canary_without_an_invented_last_known_good(self) -> None:
        index = self.publish("1.0.608")

        self.assertEqual(1, index["generation"])
        self.assertEqual("v2-build-1.0.608", index["release"]["buildTag"])
        self.assertIsNone(index["lastKnownGood"])
        self.assertIsNone(index["previous"])
        self.assertEqual("1.0.608", index["highWaterVersion"])
        self.assertEqual(FEED, index["feedRepository"])
        self.assertEqual({"action": "publish", "actor": "release-operator", "reason": "", "workflowRunId": "4242",
                          "verificationRunId": "777", "sourceRing": None, "sourceGeneration": None,
                          "previousGeneration": 0}, index["authorization"])
        release_policy.validate_index(index, ring="canary", feed_repository=FEED, generation=1)

    def test_the_second_publish_keeps_the_serving_build_as_last_known_good(self) -> None:
        first = self.publish("1.0.608")
        second = self.publish("1.0.609", first, commit="b" * 40)

        self.assertEqual(first["release"], second["previous"])
        self.assertEqual(first["release"], second["lastKnownGood"])
        self.assertEqual(2, second["generation"])

    def test_an_out_of_order_or_repeated_build_is_superseded_not_published(self) -> None:
        current = self.publish("1.0.609")

        for version in ("1.0.609", "1.0.608"):
            with self.subTest(version=version), self.assertRaises(PolicySuperseded):
                self.publish(version, current)

    def test_builds_enter_only_canary_and_canary_is_never_promoted_into(self) -> None:
        with self.assertRaises(PolicyError):
            self.transition("beta", "publish", manifest_path=self.manifest("1.0.1", "a" * 40), verification_run_id="1")
        canary = self.publish("1.0.1")
        with self.assertRaises(PolicyError):
            self.promote("canary", canary)

    def test_promotion_follows_ring_order_and_records_its_source(self) -> None:
        canary = self.publish("1.0.608")
        beta = self.promote("beta", canary)
        stable = self.promote("stable", beta)

        self.assertEqual(canary["release"], beta["release"])
        self.assertEqual(("canary", 1), (beta["authorization"]["sourceRing"], beta["authorization"]["sourceGeneration"]))
        self.assertEqual(beta["release"], stable["release"])
        with self.assertRaises(PolicyError):
            # Stable takes only beta's decision; a canary index is the wrong source.
            self.promote("stable", canary)

    def test_promotion_from_a_paused_source_or_into_a_paused_ring_is_refused(self) -> None:
        canary = self.publish("1.0.608")
        paused_canary = self.transition("canary", "pause", canary)
        with self.assertRaises(PolicyError):
            self.promote("beta", paused_canary)

        beta = self.promote("beta", canary)
        paused_beta = self.transition("beta", "pause", beta)
        newer = self.publish("1.0.609", canary)
        with self.assertRaises(PolicyError):
            self.promote("beta", newer, paused_beta)

    def test_a_paused_canary_accepts_no_new_build(self) -> None:
        paused = self.transition("canary", "pause", self.publish("1.0.608"))

        with self.assertRaises(PolicyError) as raised:
            self.publish("1.0.609", paused)
        self.assertNotIsInstance(raised.exception, PolicySuperseded)

    def test_promotion_must_advance_the_target_ring(self) -> None:
        canary = self.publish("1.0.608")
        beta = self.promote("beta", canary)

        with self.assertRaises(PolicyError):
            self.promote("beta", canary, beta)

    # Pause, last-known-good and rollback -------------------------------------------------------

    def test_pause_resume_and_mark_lkg_change_no_release_and_refuse_no_ops(self) -> None:
        first = self.publish("1.0.608")
        second = self.publish("1.0.609", first)
        paused = self.transition("canary", "pause", second)
        resumed = self.transition("canary", "resume", paused)
        marked = self.transition("canary", "mark-lkg", resumed)

        self.assertTrue(paused["paused"])
        self.assertFalse(resumed["paused"])
        for index in (paused, resumed, marked):
            self.assertEqual(second["release"], index["release"])
        self.assertEqual(second["release"], marked["lastKnownGood"])
        self.assertEqual([3, 4, 5], [paused["generation"], resumed["generation"], marked["generation"]])
        for action, current in (("pause", paused), ("resume", resumed), ("mark-lkg", marked)):
            with self.subTest(action=action), self.assertRaises(PolicyError):
                self.transition("canary", action, current)

    def test_rollback_selects_last_known_good_and_authorizes_the_downgrade(self) -> None:
        first = self.publish("1.0.608")
        second = self.publish("1.0.609", first, commit="b" * 40)

        rollback = self.transition("canary", "rollback", second, reason="relay crash loop")

        self.assertEqual(first["release"], rollback["release"])
        self.assertEqual(second["release"], rollback["previous"])
        self.assertEqual({"generation": 3, "from": second["release"]}, rollback["rollback"])
        self.assertEqual("1.0.609", rollback["highWaterVersion"])
        self.assertEqual("relay crash loop", rollback["authorization"]["reason"])
        with self.assertRaises(PolicyError):
            self.transition("canary", "rollback", rollback)

    def test_a_rollback_keeps_a_pause_and_blocks_late_builds_below_the_high_water_mark(self) -> None:
        first = self.publish("1.0.608")
        bad = self.publish("1.0.610", first)
        paused = self.transition("canary", "pause", bad)
        rolled_back = self.transition("canary", "rollback", paused)
        self.assertTrue(rolled_back["paused"])

        resumed = self.transition("canary", "resume", rolled_back)
        self.assertIsNotNone(resumed["rollback"], "a resume must not withdraw the rollback authorization")
        with self.assertRaises(PolicySuperseded):
            # 1.0.609 finished verifying late. It is newer than the rolled-back-to build but not
            # newer than the build that was rolled away from, so it must not re-enter.
            self.publish("1.0.609", resumed)
        forward = self.publish("1.0.611", resumed)
        self.assertIsNone(forward["rollback"])

    def test_empty_rings_support_nothing_but_their_entry_transition(self) -> None:
        for action in ("pause", "resume", "mark-lkg", "rollback"):
            with self.subTest(action=action), self.assertRaises(PolicyError):
                self.transition("stable", action)

    def test_a_ring_without_last_known_good_cannot_roll_back(self) -> None:
        with self.assertRaises(PolicyError):
            self.transition("canary", "rollback", self.publish("1.0.608"))

    # Integrity of the chain -------------------------------------------------------------------

    def test_a_current_index_from_another_feed_ring_or_generation_is_refused(self) -> None:
        current = self.publish("1.0.608")
        cases = {
            "feed": dict(current, feedRepository="example/other"),
            "ring": dict(current, ring="beta"),
            "chain": dict(current, authorization=dict(current["authorization"], previousGeneration=5)),
            "high-water": dict(current, highWaterVersion="1.0.1"),
        }
        for label, index in cases.items():
            with self.subTest(label=label), self.assertRaises(PolicyError):
                self.transition("canary", "pause", index)
        with self.assertRaises(PolicyError):
            release_policy.next_index(
                ring="canary", action="pause", actor="operator", feed_repository=FEED, workflow_run_id="1",
                current=current, current_generation=2, now=NOW,
            )

    def test_transition_metadata_and_rollback_authority_are_fail_closed(self) -> None:
        first = self.publish("1.0.608")
        second = self.publish("1.0.609", first, commit="b" * 40)
        rollback = self.transition("canary", "rollback", second)
        cases = {
            "unknown root field": {**first, "unexpected": True},
            "publish without producer": {
                **first,
                "authorization": {**first["authorization"], "verificationRunId": None},
            },
            "promote without source": {
                **self.promote("beta", first),
                "authorization": {
                    **self.promote("beta", first)["authorization"],
                    "sourceRing": None,
                    "sourceGeneration": None,
                },
            },
            "rollback from unrelated release": {
                **rollback,
                "previous": first["release"],
            },
            "rollback action without grant": {
                **first,
                "authorization": {
                    **first["authorization"],
                    "action": "rollback",
                    "verificationRunId": None,
                },
            },
        }
        for label, value in cases.items():
            with self.subTest(label=label), self.assertRaises(PolicyError):
                release_policy.validate_index(
                    value,
                    ring=value["ring"],
                    feed_repository=FEED,
                    generation=value["generation"],
                )

    def test_semantic_versions_order_by_precedence(self) -> None:
        ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0", "1.0.608", "1.1.0"]
        keys = [release_policy.semver_key(value) for value in ordered]
        self.assertEqual(keys, sorted(keys))
        for invalid in ("1.0", "01.0.0", "1.0.0-", "1.0.0-01", "v1.0.0"):
            with self.subTest(invalid=invalid), self.assertRaises(PolicyError):
                release_policy.semver_key(invalid)

    def test_selection_ignores_names_that_only_resemble_a_decision(self) -> None:
        names = ["release-index-g0000000002.json", "release-index-g0000000010.json", "release-index-g11.json",
                 "release-index-g0000000099.json.sigstore.json", "README.md"]

        self.assertEqual((10, "release-index-g0000000010.json"), release_policy.select_current(names))
        self.assertEqual((0, None), release_policy.select_current([]))
        self.assertEqual("release-index-g0000000011.json", release_policy.index_name(11))

    def test_envelope_round_trip_preserves_the_signed_bytes(self) -> None:
        payload = self.root / "index.json"
        bundle = self.root / "bundle.json"
        envelope = self.root / "envelope.json"
        payload.write_bytes(b'{"schemaVersion":1,  "ring":"canary"}\n')
        bundle.write_text('{"verificationMaterial": {}}\n', encoding="utf-8")

        release_policy.pack_envelope(payload, bundle, envelope)
        release_policy.unpack_envelope(envelope, self.root / "out.json", self.root / "out-bundle.json")

        self.assertEqual(payload.read_bytes(), (self.root / "out.json").read_bytes())

    def test_malformed_envelopes_are_refused(self) -> None:
        envelope = self.root / "envelope.json"
        for body in (
            {"schemaVersion": 1, "mediaType": release_policy.ENVELOPE_MEDIA_TYPE, "payloadBase64": "not base64!", "sigstoreBundle": {}},
            {"schemaVersion": 1, "mediaType": "text/plain", "payloadBase64": "e30=", "sigstoreBundle": {}},
            {"schemaVersion": 1, "mediaType": release_policy.ENVELOPE_MEDIA_TYPE, "payloadBase64": "W10=", "sigstoreBundle": {}},
        ):
            envelope.write_text(json.dumps(body), encoding="utf-8")
            with self.subTest(body=body), self.assertRaises(PolicyError):
                release_policy.unpack_envelope(envelope, self.root / "payload", self.root / "bundle")

    def test_the_command_line_reports_supersession_distinctly(self) -> None:
        current = self.publish("1.0.609")
        (self.root / "current.json").write_text(json.dumps(current), encoding="utf-8")
        arguments = [
            sys.executable, str(RELEASE_DIRECTORY / "release_policy.py"), "next",
            "--ring", "canary", "--action", "publish", "--actor", "github-actions", "--feed", FEED,
            "--workflow-run-id", "1", "--current", str(self.root / "current.json"), "--current-generation", "1",
            "--verification-run-id", "2", "--output", str(self.root / "next.json"),
        ]

        older = subprocess.run([*arguments, "--manifest", str(self.manifest("1.0.608", "c" * 40))], capture_output=True, text=True, check=False)
        newer = subprocess.run([*arguments, "--manifest", str(self.manifest("1.0.610", "d" * 40))], capture_output=True, text=True, check=False)

        self.assertEqual(3, older.returncode, older.stderr)
        self.assertEqual(0, newer.returncode, newer.stderr)
        self.assertEqual("release-index-g0000000002.json", newer.stdout.strip())


if __name__ == "__main__":
    unittest.main()
