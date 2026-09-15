"""Fixture tests for deploy/group-server/tarkov-group-update.sh.

The updater runs as root on the relay host with systemd, cosign and the private feed. Here each
of those is a small fake on PATH, so every failure path after the swap can be forced and the
state it leaves behind inspected: the tree, the stamps the panel reads, the refusal record, the
units and the updater itself.
"""

from __future__ import annotations

import base64
import hashlib
import io
import json
import os
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
UPDATER = ROOT / "deploy/group-server/tarkov-group-update.sh"
FEED = "example/tarkov-feed"
OLD_COMMIT = "a" * 40
OLD_SHA = "0" * 64
STAMPS = ("INSTALLED_SHA256", "INSTALLED_VERSION", "INSTALLED_COMMIT", "INSTALLED_RING", "INSTALLED_GENERATION")

FAKE_SYSTEMCTL = """#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "${FAKE_ROOT}/systemctl.log"
verb="$1"
count_file="${FAKE_ROOT}/count-${verb}"
count=$(( $(cat "${count_file}" 2>/dev/null || echo 0) + 1 ))
echo "${count}" > "${count_file}"
for failure in ${FAKE_SYSTEMCTL_FAIL:-}; do
    if [[ "${failure}" == "${verb}" || "${failure}" == "${verb}#${count}" ]]; then
        exit 1
    fi
done
case "${verb}" in
    stop) rm -f "${FAKE_ROOT}/running" ;;
    start) touch "${FAKE_ROOT}/running" ;;
esac
"""

FAKE_WGET = """#!/usr/bin/env bash
set -euo pipefail
output=''
while (($#)); do
    if [[ "$1" == -O ]]; then output="$2"; shift 2; else shift; fi
done
[[ -f "${FAKE_ROOT}/running" && -s "${TARKOV_UPDATE_INSTALL}/health.json" ]]
cp "${TARKOV_UPDATE_INSTALL}/health.json" "${output}"
"""

FAKE_COSIGN = """#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "${FAKE_ROOT}/cosign.log"
subject="${@: -1}"
if [[ -n "${FAKE_COSIGN_REJECT:-}" && "$(basename "${subject}")" == *"${FAKE_COSIGN_REJECT}"* ]]; then
    echo "fake cosign: rejected ${subject}" >&2
    exit 1
fi
"""

FAKE_GH = """#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "${FAKE_ROOT}/gh.log"
[[ -n "${GH_TOKEN:-}" ]] || { echo "no token" >&2; exit 4; }
if [[ "$1" == api ]]; then
    shift
    accept=''
    jq_filter=''
    endpoint=''
    while (($#)); do
        case "$1" in
            -H) accept="$2"; shift 2 ;;
            --jq) jq_filter="$2"; shift 2 ;;
            *) endpoint="$1"; shift ;;
        esac
    done
    case "${endpoint}" in
        "repos/${FAKE_FEED}")
            printf '%s\\n' "${FAKE_GH_VISIBILITY:-private}"
            ;;
        "repos/${FAKE_FEED}/contents/rings/"*/*)
            cat "${FAKE_ROOT}/feed/${endpoint#repos/${FAKE_FEED}/contents/}"
            ;;
        "repos/${FAKE_FEED}/contents/rings/"*)
            directory="${FAKE_ROOT}/feed/${endpoint#repos/${FAKE_FEED}/contents/}"
            [[ -d "${directory}" ]] || { echo "gh: Not Found (HTTP 404)" >&2; exit 1; }
            listing="$(find "${directory}" -maxdepth 1 -type f -printf '%f\\n' | jq -R '{name: ., type: "file"}' | jq -s .)"
            jq -r "${jq_filter}" <<<"${listing}"
            ;;
        *) echo "gh: unexpected endpoint ${endpoint}" >&2; exit 1 ;;
    esac
elif [[ "$1" == release && "$2" == download ]]; then
    tag="$3"; shift 3
    pattern=''; directory=''
    while (($#)); do
        case "$1" in
            --pattern) pattern="$2"; shift 2 ;;
            --dir) directory="$2"; shift 2 ;;
            *) shift ;;
        esac
    done
    [[ -f "${FAKE_ROOT}/feed/releases/${tag}/${pattern}" ]] || { echo "no asset" >&2; exit 1; }
    cp "${FAKE_ROOT}/feed/releases/${tag}/${pattern}" "${directory}/${pattern}"
else
    echo "gh: unexpected command $*" >&2
    exit 1
fi
"""


def sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


class RelayUpdaterTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-relay-updater-")
        self.root = Path(self.temporary.name)
        self.install = self.root / "opt/tarkov-group"
        self.lkg = self.root / "opt/tarkov-group.lkg"
        self.state = self.root / "var/state"
        self.bundle = self.root / "bundle"
        self.units = self.root / "units"
        self.bin = self.root / "bin"
        self.feed = self.root / "feed"
        self.updater_copy = self.root / "opt/tarkov-group-update.sh"
        for path in (self.install, self.state, self.bundle, self.units, self.bin):
            path.mkdir(parents=True)
        (self.root / "trust.json").write_text('{"mediaType": "trusted-root"}\n', encoding="utf-8")
        (self.root / "token").write_text("feed-read-token\n", encoding="utf-8")
        (self.root / "running").touch()
        (self.install / "TarkovCompanion.GroupServer").write_text("old relay\n", encoding="utf-8")
        self.write_health(self.install, "1.0.0", OLD_COMMIT)
        self.write_stamps(OLD_SHA, "1.0.0", OLD_COMMIT, "stable", "0")
        (self.units / "tarkov-group-update.service").write_text("original service\n", encoding="utf-8")
        self.updater_copy.write_text("#!/bin/sh\n# original updater\n", encoding="utf-8")
        for name, body in (("systemctl", FAKE_SYSTEMCTL), ("wget", FAKE_WGET), ("cosign", FAKE_COSIGN), ("gh", FAKE_GH)):
            path = self.bin / name
            path.write_text(body, encoding="utf-8")
            path.chmod(0o755)
        self.releases: dict[int, dict[str, object]] = {}

    def tearDown(self) -> None:
        self.temporary.cleanup()

    # Fixtures ---------------------------------------------------------------------------------

    @staticmethod
    def write_health(directory: Path, version: str, commit: str, protocol: int = 1) -> None:
        (directory / "health.json").write_text(
            json.dumps({"status": "ok", "version": version, "commit": commit, "protocol": protocol}), encoding="utf-8"
        )

    def write_stamps(self, sha: str, version: str, commit: str, ring: str, generation: str) -> None:
        for name, value in zip(STAMPS, (sha, version, commit, ring, generation)):
            (self.state / name).write_text(value + "\n", encoding="utf-8")

    def stamps(self) -> dict[str, str | None]:
        return {
            name: (self.state / name).read_text(encoding="utf-8").strip() if (self.state / name).exists() else None
            for name in STAMPS
        }

    def running_version(self) -> str:
        return json.loads((self.install / "health.json").read_text(encoding="utf-8"))["version"]

    def release(
        self,
        version: str,
        commit: str,
        generation: int,
        action: str = "publish",
        *,
        ring: str = "stable",
        paused: bool = False,
        rollback: bool = False,
        health_commit: str | None = None,
        shipped: dict[str, bytes] | None = None,
        symlink: bool = False,
        feed: str = FEED,
        keep_bundle: bool = False,
        manifest_digest: str | None = None,
    ) -> str:
        """Writes a signed-looking release to the offline bundle and the fake online feed."""
        if not keep_bundle:
            shutil.rmtree(self.bundle)
            self.bundle.mkdir()
        archive_bytes = io.BytesIO()
        with tarfile.open(fileobj=archive_bytes, mode="w:gz") as archive:
            def add(name: str, value: bytes, mode: int = 0o644) -> None:
                info = tarfile.TarInfo(name)
                info.size = len(value)
                info.mode = mode
                archive.addfile(info, io.BytesIO(value))

            add("./TarkovCompanion.GroupServer", f"relay {version}\n".encode(), 0o755)
            add("./health.json", json.dumps({
                "status": "ok", "version": version, "commit": health_commit or commit, "protocol": 1,
            }).encode())
            for name, value in (shipped or {}).items():
                add(f"./deploy/{name}", value, 0o755 if name.endswith(".sh") else 0o644)
            if symlink:
                link = tarfile.TarInfo("./escape")
                link.type = tarfile.SYMTYPE
                link.linkname = "/etc/passwd"
                archive.addfile(link)
        archive_value = archive_bytes.getvalue()
        archive_name = "TarkovCompanion-GroupServer-linux-x64.tar.gz"
        manifest = {
            "schemaVersion": 1,
            "version": version,
            "commit": commit,
            "versions": {
                "package": version, "manifest": version, "commit": commit,
                "assemblyInformational": f"{version}+{commit}", "relayProtocol": 1,
            },
            "artifacts": [{
                "name": archive_name, "component": "relay", "role": "archive",
                "sha256": sha256(archive_value), "size": len(archive_value),
            }],
        }
        manifest_value = json.dumps(manifest).encode()
        index = {
            "schemaVersion": 1,
            "mediaType": "application/vnd.tarkov-companion.release-index.v1+json",
            "feedRepository": feed,
            "ring": ring,
            "generation": generation,
            "paused": paused,
            "release": {
                "version": version, "commit": commit, "buildTag": f"v2-build-{version}",
                "manifestName": "release-manifest.json",
                "manifestSha256": manifest_digest or sha256(manifest_value),
            },
            "highWaterVersion": version,
            "rollback": {"generation": generation, "from": {}} if rollback else None,
            "authorization": {"action": action, "previousGeneration": generation - 1},
        }
        envelope = json.dumps({
            "schemaVersion": 1,
            "mediaType": "application/vnd.tarkov-companion.signed-release-index.v1+json",
            "payloadBase64": base64.b64encode(json.dumps(index).encode()).decode(),
            "sigstoreBundle": {"fixture": True},
        }).encode()
        index_name = f"release-index-g{generation:010d}.json"
        build_files = {
            archive_name: archive_value,
            f"{archive_name}.sigstore.json": b"{}",
            "release-manifest.json": manifest_value,
            "release-manifest.json.sigstore.json": b"{}",
        }
        for directory, files in (
            (self.bundle, {**build_files, index_name: envelope}),
            (self.feed / "releases" / f"v2-build-{version}", build_files),
            (self.feed / "rings" / ring, {index_name: envelope}),
        ):
            directory.mkdir(parents=True, exist_ok=True)
            for name, value in files.items():
                (directory / name).write_bytes(value)
        return sha256(archive_value)

    def run_updater(self, *, online: bool = False, **environment: str) -> subprocess.CompletedProcess[str]:
        env = {
            "PATH": f"{self.bin}:{os.environ['PATH']}",
            "HOME": str(self.root),
            "FAKE_ROOT": str(self.root),
            "FAKE_FEED": FEED,
            "TARKOV_RELEASE_RING": "stable",
            "TARKOV_SIGSTORE_TRUST_ROOT": str(self.root / "trust.json"),
            "TARKOV_RELEASE_TOKEN_FILE": str(self.root / "token"),
            "TARKOV_UPDATE_INSTALL": str(self.install),
            "TARKOV_UPDATE_LKG": str(self.lkg),
            "TARKOV_UPDATE_STATE": str(self.state),
            "TARKOV_UPDATE_SELF": str(self.updater_copy),
            "TARKOV_UPDATE_UNITS": str(self.units),
            "TARKOV_UPDATE_HEALTH_ATTEMPTS": "1",
            "TARKOV_UPDATE_HEALTH_INTERVAL": "0",
        }
        if online:
            env["TARKOV_RELEASE_REPOSITORY"] = FEED
        else:
            env["TARKOV_RELEASE_BUNDLE_DIR"] = str(self.bundle)
        env.update(environment)
        for counter in self.root.glob("count-*"):
            counter.unlink()
        (self.root / "systemctl.log").unlink(missing_ok=True)
        return subprocess.run([str(UPDATER)], text=True, capture_output=True, env=env, check=False, timeout=60)

    def assert_no_leftovers(self) -> None:
        for path in (self.state / ".swap", Path(f"{self.install}.incoming"), Path(f"{self.install}.previous")):
            self.assertFalse(path.exists(), f"{path} was left behind")
        self.assertEqual([], list(self.state.glob(".update.*")))

    def systemctl_calls(self) -> list[str]:
        log = self.root / "systemctl.log"
        return log.read_text(encoding="utf-8").splitlines() if log.exists() else []

    # Success and truthfulness -----------------------------------------------------------------

    def test_success_commits_every_stamp_and_clears_refusal(self) -> None:
        expected = self.release("2.0.0", "b" * 40, 1)
        (self.state / "REFUSED_SHA256").write_text("f" * 64, encoding="utf-8")
        (self.state / "REFUSED_RELEASE.json").write_text('{"sha256": "' + "f" * 64 + '"}', encoding="utf-8")

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(
            {"INSTALLED_SHA256": expected, "INSTALLED_VERSION": "2.0.0", "INSTALLED_COMMIT": "b" * 40,
             "INSTALLED_RING": "stable", "INSTALLED_GENERATION": "1"},
            self.stamps(),
        )
        self.assertEqual(expected, (self.state / "PUBLISHED_SHA256").read_text().strip())
        self.assertFalse((self.state / "REFUSED_SHA256").exists())
        self.assertFalse((self.state / "REFUSED_RELEASE.json").exists())
        self.assertEqual("2.0.0", self.running_version())
        self.assertEqual("1.0.0", json.loads((self.lkg / "health.json").read_text())["version"])
        self.assert_no_leftovers()

    def test_an_installed_decision_is_not_reinstalled(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        self.assertEqual(0, self.run_updater().returncode)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("already running 2.0.0", result.stdout)
        self.assertEqual([], [call for call in self.systemctl_calls() if call.startswith(("stop", "start"))])

    def test_verification_uses_the_trust_root_and_publisher_identity(self) -> None:
        self.release("2.0.0", "b" * 40, 1)

        self.assertEqual(0, self.run_updater().returncode)

        calls = (self.root / "cosign.log").read_text(encoding="utf-8").splitlines()
        self.assertEqual(3, len(calls))
        for call in calls:
            self.assertIn(f"--trusted-root {self.root / 'trust.json'}", call)
            self.assertIn(
                "--certificate-identity https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main",
                call,
            )
            self.assertIn("--certificate-oidc-issuer https://token.actions.githubusercontent.com", call)

    # Failure after the swap -------------------------------------------------------------------

    def test_failed_health_restores_tree_and_stamps_and_records_refusal(self) -> None:
        before = self.stamps()
        refused = self.release("3.0.0", "c" * 40, 1, health_commit="e" * 40)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual(before, self.stamps())
        self.assertEqual(refused, (self.state / "REFUSED_SHA256").read_text().strip())
        record = json.loads((self.state / "REFUSED_RELEASE.json").read_text())
        self.assertEqual((refused, "stable", 1), (record["sha256"], record["ring"], record["generation"]))
        self.assertTrue((self.root / "running").exists(), "the previous relay was not started again")
        self.assert_no_leftovers()

    def test_the_next_tick_after_a_refusal_refuses_rather_than_reporting_already_on(self) -> None:
        # RISK-RELAY-UPDATE-STATE: the stamp used to be written before health, so the tick after
        # a rollback said "already on" the refused build and the panel named it as installed.
        self.release("3.0.0", "c" * 40, 1, health_commit="e" * 40)
        self.assertNotEqual(0, self.run_updater().returncode)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("was refused here before", result.stdout)
        self.assertNotIn("already running", result.stdout)
        self.assertEqual(OLD_SHA, self.stamps()["INSTALLED_SHA256"])
        self.assertEqual([], [call for call in self.systemctl_calls() if call.startswith(("stop", "start"))])

    def test_a_new_signed_decision_after_a_refusal_installs_and_clears_it(self) -> None:
        self.release("3.0.0", "c" * 40, 1, health_commit="e" * 40)
        self.assertNotEqual(0, self.run_updater().returncode)
        expected = self.release("3.0.1", "d" * 40, 2)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(expected, self.stamps()["INSTALLED_SHA256"])
        self.assertFalse((self.state / "REFUSED_SHA256").exists())
        self.assertFalse((self.state / "REFUSED_RELEASE.json").exists())

    def test_unit_installation_failure_restores_units_updater_tree_and_stamps(self) -> None:
        before = self.stamps()
        self.release("3.0.0", "c" * 40, 1, shipped={
            "tarkov-group-update.service": b"new service\n",
            "tarkov-group-update.timer": b"new timer\n",
            "tarkov-group-update.sh": b"#!/bin/sh\n# new updater\n",
        })

        result = self.run_updater(FAKE_SYSTEMCTL_FAIL="daemon-reload#1")

        self.assertNotEqual(0, result.returncode)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual(before, self.stamps())
        self.assertEqual("original service\n", (self.units / "tarkov-group-update.service").read_text())
        self.assertFalse((self.units / "tarkov-group-update.timer").exists())
        self.assertIn("original updater", self.updater_copy.read_text())
        self.assert_no_leftovers()

    def test_a_stop_that_fails_still_leaves_the_previous_relay_running(self) -> None:
        before = self.stamps()
        self.release("3.0.0", "c" * 40, 1)

        result = self.run_updater(FAKE_SYSTEMCTL_FAIL="stop#1")

        self.assertNotEqual(0, result.returncode)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual(before, self.stamps())
        self.assertTrue((self.root / "running").exists())
        self.assert_no_leftovers()

    def test_a_start_that_fails_is_rolled_back(self) -> None:
        before = self.stamps()
        self.release("3.0.0", "c" * 40, 1)

        result = self.run_updater(FAKE_SYSTEMCTL_FAIL="start#1")

        self.assertNotEqual(0, result.returncode)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual(before, self.stamps())
        self.assertTrue((self.root / "running").exists())

    def test_an_interrupted_swap_is_undone_by_the_next_run(self) -> None:
        # Power lost after the new stamps were written but before the commit point: the next run
        # must put back the tree and the stamps the journal holds, and refuse that build.
        before = self.stamps()
        target = self.release("3.0.0", "c" * 40, 1)
        previous = Path(f"{self.install}.previous")
        shutil.copytree(self.install, previous)
        journal = self.state / ".swap"
        (journal / "stamps").mkdir(parents=True)
        (journal / "deployment").mkdir()
        for name in STAMPS:
            shutil.copy2(self.state / name, journal / "stamps" / name)
        (journal / "deployment" / "tarkov-group-update.service").write_text("original service\n")
        (journal / "target.json").write_text(json.dumps(
            {"sha256": target, "version": "3.0.0", "commit": "c" * 40, "ring": "stable", "generation": 1}
        ))
        (journal / "complete").touch()
        self.write_health(self.install, "3.0.0", "c" * 40)
        self.write_stamps(target, "3.0.0", "c" * 40, "stable", "1")

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("interrupted during its swap", result.stdout)
        self.assertIn("was refused here before", result.stdout)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual(before, self.stamps())
        self.assert_no_leftovers()

    def test_an_incomplete_journal_changes_nothing(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        (self.state / ".swap/stamps").mkdir(parents=True)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertNotIn("interrupted", result.stdout)
        self.assertEqual("2.0.0", self.running_version())

    # Authentication before anything changes ----------------------------------------------------

    def test_a_signature_failure_changes_nothing(self) -> None:
        for rejected in ("index.json", "release-manifest.json", "GroupServer-linux-x64.tar.gz"):
            with self.subTest(rejected=rejected):
                before = self.stamps()
                self.release("2.0.0", "b" * 40, 1)

                result = self.run_updater(FAKE_COSIGN_REJECT=rejected)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("does not verify", result.stdout)
                self.assertEqual("1.0.0", self.running_version())
                self.assertEqual(before, self.stamps())
                self.assertEqual([], [c for c in self.systemctl_calls() if c.startswith("stop")])

    def test_a_manifest_the_index_does_not_name_is_refused(self) -> None:
        self.release("2.0.0", "b" * 40, 1, manifest_digest="9" * 64)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("not the one the signed ring index names", result.stdout)
        self.assertEqual("1.0.0", self.running_version())

    def test_an_archive_with_a_link_is_refused_before_the_swap(self) -> None:
        self.release("2.0.0", "b" * 40, 1, symlink=True)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("link or special file", result.stdout)
        self.assertEqual("1.0.0", self.running_version())
        self.assert_no_leftovers()

    # Ordering, replay and rollback -------------------------------------------------------------

    def test_a_normal_decision_cannot_downgrade(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        self.assertEqual(0, self.run_updater().returncode)
        self.release("1.5.0", "c" * 40, 2, action="promote")

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("without a signed rollback", result.stdout)
        self.assertEqual("2.0.0", self.running_version())
        self.assertEqual("1", self.stamps()["INSTALLED_GENERATION"])

    def test_a_prerelease_is_older_than_its_release(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        self.assertEqual(0, self.run_updater().returncode)
        self.release("2.0.0-rc.1", "c" * 40, 2, action="promote")

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("without a signed rollback", result.stdout)

    def test_a_signed_rollback_installs_the_older_release(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        self.assertEqual(0, self.run_updater().returncode)
        expected = self.release("1.5.0", "c" * 40, 2, action="rollback", rollback=True)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(expected, self.stamps()["INSTALLED_SHA256"])
        self.assertEqual("1.5.0", self.running_version())

    def test_a_paused_ring_holds_but_still_applies_a_signed_rollback(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        self.assertEqual(0, self.run_updater().returncode)

        self.release("2.1.0", "c" * 40, 2, action="pause", paused=True)
        held = self.run_updater()
        self.assertEqual(0, held.returncode, held.stdout + held.stderr)
        self.assertIn("is paused", held.stdout)
        self.assertEqual("2.0.0", self.running_version())

        self.release("1.5.0", "d" * 40, 3, action="rollback", paused=True, rollback=True)
        rolled_back = self.run_updater()
        self.assertEqual(0, rolled_back.returncode, rolled_back.stdout + rolled_back.stderr)
        self.assertEqual("1.5.0", self.running_version())

    def test_a_replayed_older_generation_is_refused(self) -> None:
        self.release("2.0.0", "b" * 40, 5)
        self.assertEqual(0, self.run_updater().returncode)
        self.release("2.5.0", "c" * 40, 4)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("already installed", result.stdout)
        self.assertEqual("5", (self.state / "PUBLISHED_GENERATION").read_text().strip())

    def test_two_different_decisions_at_one_generation_are_both_refused(self) -> None:
        self.release("2.0.0", "b" * 40, 1, action="pause", paused=True)
        self.assertEqual(0, self.run_updater().returncode)
        self.release("2.0.1", "c" * 40, 1)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("claim generation 1", result.stdout)
        self.assertEqual("1.0.0", self.running_version())

    def test_the_same_version_with_different_bytes_is_refused(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        self.assertEqual(0, self.run_updater().returncode)
        self.release("2.0.0", "b" * 40, 2, shipped={"extra.txt": b"different bytes\n"})

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("different relay archive", result.stdout)

    def test_changing_ring_starts_that_ring_generation_history(self) -> None:
        self.release("2.0.0", "b" * 40, 9)
        self.assertEqual(0, self.run_updater().returncode)
        expected = self.release("2.1.0", "c" * 40, 2, ring="beta")

        result = self.run_updater(TARKOV_RELEASE_RING="beta")

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("ring changed from stable to beta", result.stdout)
        self.assertEqual(expected, self.stamps()["INSTALLED_SHA256"])
        self.assertEqual("beta", self.stamps()["INSTALLED_RING"])

    def test_a_decision_for_another_ring_is_refused(self) -> None:
        self.release("2.0.0", "b" * 40, 1, ring="canary")
        shutil.copy2(self.feed / "rings/canary/release-index-g0000000001.json", self.bundle)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("not a valid stable decision", result.stdout)

    # Host migration and configuration ----------------------------------------------------------

    def test_legacy_stamp_and_unauthenticated_refusal_are_migrated(self) -> None:
        (self.state / "INSTALLED_SHA256").unlink()
        (self.install / "INSTALLED_SHA256").write_text(OLD_SHA, encoding="utf-8")
        (self.state / "REFUSED_SHA256").write_text("f" * 64, encoding="utf-8")
        self.release("2.0.0", "b" * 40, 1)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("legacy install stamp", result.stdout)
        self.assertIn("unauthenticated updater", result.stdout)
        self.assertEqual("2.0.0", self.stamps()["INSTALLED_VERSION"])

    def test_the_request_marker_is_removed_even_when_configuration_is_missing(self) -> None:
        (self.state / "UPDATE_NOW").touch()
        (self.root / "trust.json").unlink()

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("trust root", result.stdout)
        self.assertFalse((self.state / "UPDATE_NOW").exists())

    def test_a_held_lock_leaves_everything_to_the_running_update(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        with open(self.state / "UPDATE.lock", "w", encoding="utf-8") as lock:
            holder = subprocess.Popen(["flock", "-x", str(self.state / "UPDATE.lock"), "sleep", "30"])
            try:
                for _ in range(50):
                    probe = subprocess.run(["flock", "-n", str(self.state / "UPDATE.lock"), "true"], check=False)
                    if probe.returncode != 0:
                        break
                result = self.run_updater()
            finally:
                holder.kill()
                holder.wait()
            del lock

        self.assertEqual(0, result.returncode)
        self.assertIn("holds the lock", result.stdout)
        self.assertEqual("1.0.0", self.running_version())

    # Online feed ------------------------------------------------------------------------------

    def test_online_selects_the_newest_decision_and_ignores_stray_names(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        expected = self.release("2.1.0", "c" * 40, 2)
        (self.feed / "rings/stable/release-index-g99.json").write_text("{}", encoding="utf-8")
        (self.feed / "rings/stable/notes.txt").write_text("not a decision", encoding="utf-8")

        result = self.run_updater(online=True)

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(expected, self.stamps()["INSTALLED_SHA256"])
        self.assertIn("contents/rings/stable/release-index-g0000000002.json", (self.root / "gh.log").read_text())

    def test_online_refuses_a_public_feed_or_the_source_repository(self) -> None:
        self.release("2.0.0", "b" * 40, 1)

        public = self.run_updater(online=True, FAKE_GH_VISIBILITY="public")
        source = self.run_updater(online=True, TARKOV_RELEASE_REPOSITORY="smartpbx/tarkov-companion")

        self.assertNotEqual(0, public.returncode)
        self.assertIn("not private or internal", public.stdout)
        self.assertNotEqual(0, source.returncode)
        self.assertIn("public source repository", source.stdout)
        self.assertEqual("1.0.0", self.running_version())

    def test_online_refuses_a_decision_signed_for_another_feed(self) -> None:
        self.release("2.0.0", "b" * 40, 1, feed="example/other-feed")

        result = self.run_updater(online=True)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("for this feed", result.stdout)


if __name__ == "__main__":
    unittest.main()
