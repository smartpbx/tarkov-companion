"""Fixture tests for deploy/group-server/tarkov-group-update.sh.

The updater runs as root on the relay host with systemd, cosign and the private feed. Here each
of those is a small fake on PATH, so every failure path after the swap can be forced and the
state it leaves behind inspected: the tree, the private stamps, the status the panel reads, the
refusal record, the units and the updater itself.

The relay's own state directory is treated as hostile throughout. The relay runs as an
unprivileged user that owns it, so anything planted there - a journal, a link where a lock or a
stamp used to be - must change nothing outside it.
"""

from __future__ import annotations

import base64
import hashlib
import io
import json
import os
import shutil
import stat
import subprocess
import tarfile
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from sigstore_fixture import bundle_for, bundle_text, hostile_bundles, install_fake_cosign  # noqa: E402


ROOT = Path(__file__).resolve().parents[3]
UPDATER = ROOT / "deploy/group-server/tarkov-group-update.sh"
FEED = "example/tarkov-feed"
OLD_COMMIT = "a" * 40
OLD_SHA = "0" * 64
STAMPS = ("INSTALLED_SHA256", "INSTALLED_VERSION", "INSTALLED_COMMIT", "INSTALLED_RING", "INSTALLED_GENERATION")
STATUS_STAMPS = ("INSTALLED_SHA256", "INSTALLED_VERSION", "PUBLISHED_SHA256", "PUBLISHED_VERSION", "REFUSED_SHA256")
SIGNED_AT = "2026-09-15T00:00:00Z"

FAKE_SYSTEMCTL = """#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "${FAKE_ROOT}/systemctl.log"
env | grep -E '^(GH_TOKEN|GITHUB_TOKEN)=' >> "${FAKE_ROOT}/leaked-token.log" || true
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
# Delivered to the updater while this command runs, so bash acts on it the moment this returns:
# the same point a unit timeout's SIGTERM would land between two commands.
for signal in ${FAKE_SYSTEMCTL_TERM:-}; do
    if [[ "${signal}" == "${verb}#${count}" ]]; then
        kill -TERM "${PPID}"
    fi
done
"""

FAKE_WGET = """#!/usr/bin/env bash
set -euo pipefail
env | grep -E '^(GH_TOKEN|GITHUB_TOKEN)=' >> "${FAKE_ROOT}/leaked-token.log" || true
output=''
while (($#)); do
    if [[ "$1" == -O ]]; then output="$2"; shift 2; else shift; fi
done
[[ -f "${FAKE_ROOT}/running" && -s "${TARKOV_UPDATE_INSTALL}/health.json" ]]
cp "${TARKOV_UPDATE_INSTALL}/health.json" "${output}"
"""

# Delegates to the real mv, except for the rename a test fails or the exact destination after
# which a test kills the updater. SIGKILL models power loss: no EXIT trap gets a chance to make
# the filesystem prettier before the next invocation has to recover it.
FAKE_MV = """#!/usr/bin/env bash
set -euo pipefail
kill_after=0
destination="${!#}"
for argument in "$@"; do
    if [[ -n "${FAKE_MV_FAIL:-}" && "${argument}" == *"${FAKE_MV_FAIL}" ]]; then
        echo "fake mv: refused ${argument}" >&2
        exit 1
    fi
done
[[ -n "${FAKE_MV_KILL_AFTER:-}" && "${destination}" == "${FAKE_MV_KILL_AFTER}" ]] && kill_after=1
export PATH="${FAKE_REAL_PATH}"
mv "$@"
if ((kill_after)); then
    kill -KILL "${PPID}"
fi
"""

FAKE_GH = """#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "${FAKE_ROOT}/gh.log"
[[ "${GH_TOKEN:-}" == "feed-read-token" ]] || { echo "no token" >&2; exit 4; }
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


class UpdaterFixture(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-relay-updater-")
        self.root = Path(self.temporary.name)
        self.install = self.root / "opt/tarkov-group"
        self.lkg = self.root / "opt/tarkov-group.lkg"
        self.state = self.root / "var/lib/tarkov-group-update"
        self.status = self.root / "var/lib/tarkov-group-update-status"
        self.relay_state = self.root / "var/lib/tarkov-group"
        self.bundle = self.root / "bundle"
        self.units = self.root / "units"
        self.bin = self.root / "bin"
        self.feed = self.root / "feed"
        self.updater_copy = self.root / "opt/tarkov-group-update.sh"
        for path in (self.install, self.relay_state, self.bundle, self.units, self.bin):
            path.mkdir(parents=True)
        self.state.mkdir(mode=0o700)
        self.state.chmod(0o700)
        (self.root / "trust.json").write_text('{"mediaType": "trusted-root"}\n', encoding="utf-8")
        token = self.root / "token"
        token.write_text("feed-read-token\n", encoding="utf-8")
        token.chmod(0o600)
        (self.root / "running").touch()
        (self.install / "TarkovCompanion.GroupServer").write_text("old relay\n", encoding="utf-8")
        self.write_health(self.install, "1.0.0", OLD_COMMIT)
        self.write_stamps(OLD_SHA, "1.0.0", OLD_COMMIT, "stable", "0")
        (self.units / "tarkov-group-update.service").write_text("original service\n", encoding="utf-8")
        self.updater_copy.write_text("#!/bin/sh\n# original updater\n", encoding="utf-8")
        for name, body in (("systemctl", FAKE_SYSTEMCTL), ("wget", FAKE_WGET), ("gh", FAKE_GH), ("mv", FAKE_MV)):
            path = self.bin / name
            path.write_text(body, encoding="utf-8")
            path.chmod(0o755)
        self.cosign_sha256 = install_fake_cosign(self.bin)
        # Something that must never change, whatever is planted in the relay's directory.
        self.victim = self.root / "etc/victim"
        self.victim.parent.mkdir(parents=True)
        self.victim.write_text("untouched\n", encoding="utf-8")

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

    def clear_stamps(self) -> None:
        for name in STAMPS:
            (self.state / name).unlink(missing_ok=True)

    def stamps(self) -> dict[str, str | None]:
        return {
            name: (self.state / name).read_text(encoding="utf-8").strip() if (self.state / name).exists() else None
            for name in STAMPS
        }

    def status_stamps(self) -> dict[str, str | None]:
        return {
            name: (self.status / name).read_text(encoding="utf-8").strip() if (self.status / name).exists() else None
            for name in STATUS_STAMPS
        }

    def running_version(self) -> str:
        return json.loads((self.install / "health.json").read_text(encoding="utf-8"))["version"]

    def release(
        self,
        version: str,
        commit: str,
        generation: int,
        action: str | None = None,
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
        signed_at: str = SIGNED_AT,
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
        effective_action = action or ("publish" if ring == "canary" else "promote")
        release_identity = {
            "version": version, "commit": commit, "buildTag": f"v2-build-{version}",
            "manifestName": "release-manifest.json",
            "manifestSha256": manifest_digest or sha256(manifest_value),
        }
        rollback_from = {
            "version": "9.9.9", "commit": "f" * 40, "buildTag": "v2-build-9.9.9",
            "manifestName": "release-manifest.json", "manifestSha256": "f" * 64,
        } if rollback else None
        source_ring = ({"stable": "beta", "beta": "canary"}.get(ring)
                       if effective_action == "promote" else None)
        index = {
            "schemaVersion": 1,
            "mediaType": "application/vnd.tarkov-companion.release-index.v1+json",
            "feedRepository": feed,
            "ring": ring,
            "generation": generation,
            "updatedUtc": signed_at,
            "paused": paused,
            "release": release_identity,
            "previous": rollback_from,
            "lastKnownGood": release_identity if rollback else None,
            "highWaterVersion": "9.9.9" if rollback else version,
            "rollback": {"generation": generation, "from": rollback_from} if rollback else None,
            "authorization": {
                "action": effective_action,
                "actor": "release-operator",
                "reason": "fixture",
                "workflowRunId": "43",
                "verificationRunId": "42" if effective_action == "publish" else None,
                "sourceRing": source_ring,
                "sourceGeneration": generation if source_ring is not None else None,
                "previousGeneration": generation - 1,
            },
        }
        index_value = json.dumps(index).encode()
        envelope = json.dumps({
            "schemaVersion": 1,
            "mediaType": "application/vnd.tarkov-companion.signed-release-index.v1+json",
            "payloadBase64": base64.b64encode(index_value).decode(),
            "sigstoreBundle": bundle_for(index_value),
        }).encode()
        index_name = f"release-index-g{generation:010d}.json"
        build_files = {
            archive_name: archive_value,
            f"{archive_name}.sigstore.json": bundle_text(archive_value).encode(),
            "release-manifest.json": manifest_value,
            "release-manifest.json.sigstore.json": bundle_text(manifest_value).encode(),
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

    def mutate_offline_index(self, mutate) -> None:
        path = max(self.bundle.glob("release-index-g*.json"))
        envelope = json.loads(path.read_text(encoding="utf-8"))
        payload = json.loads(base64.b64decode(envelope["payloadBase64"]))
        mutate(payload)
        payload_bytes = json.dumps(payload).encode()
        envelope["payloadBase64"] = base64.b64encode(payload_bytes).decode()
        envelope["sigstoreBundle"] = bundle_for(payload_bytes)
        path.write_text(json.dumps(envelope), encoding="utf-8")

    def run_updater(
        self,
        *,
        online: bool = False,
        validate_paths: bool = False,
        **environment: str,
    ) -> subprocess.CompletedProcess[str]:
        env = {
            "PATH": f"{self.bin}:{os.environ['PATH']}",
            "FAKE_REAL_PATH": os.environ["PATH"],
            "HOME": str(self.root),
            "FAKE_ROOT": str(self.root),
            "FAKE_FEED": FEED,
            "TARKOV_RELEASE_REPOSITORY": FEED,
            "TARKOV_RELEASE_RING": "stable",
            "TARKOV_SIGSTORE_TRUST_ROOT": str(self.root / "trust.json"),
            "TARKOV_RELEASE_TOKEN_FILE": str(self.root / "token"),
            "TARKOV_UPDATE_PATH_ROOT": str(self.root),
            "TARKOV_UPDATE_INSTALL": str(self.install),
            "TARKOV_UPDATE_LKG": str(self.lkg),
            "TARKOV_UPDATE_STATE": str(self.state),
            "TARKOV_UPDATE_STATUS": str(self.status),
            "TARKOV_RELAY_STATE": str(self.relay_state),
            "TARKOV_UPDATE_SELF": str(self.updater_copy),
            "TARKOV_UPDATE_UNITS": str(self.units),
            "TARKOV_UPDATE_HEALTH_ATTEMPTS": "1",
            "TARKOV_UPDATE_HEALTH_INTERVAL": "0",
            "TARKOV_COSIGN_SHA256": self.cosign_sha256,
            "FAKE_COSIGN_LOG": str(self.root / "cosign.log"),
        }
        if not online:
            env["TARKOV_RELEASE_BUNDLE_DIR"] = str(self.bundle)
        env.update(environment)
        for counter in self.root.glob("count-*"):
            counter.unlink()
        (self.root / "systemctl.log").unlink(missing_ok=True)
        command = [str(UPDATER)]
        if validate_paths:
            command.append("--validate-paths")
        return subprocess.run(command, text=True, capture_output=True, env=env, check=False, timeout=60)

    def assert_no_leftovers(self) -> None:
        for path in (self.state / "swap", self.state / "swap.new", self.state / "swap.committed",
                     Path(f"{self.install}.incoming"), Path(f"{self.install}.previous"),
                     Path(f"{self.lkg}.incoming"), Path(f"{self.lkg}.previous"),
                     Path(f"{self.updater_copy}.incoming")):
            self.assertFalse(path.exists(), f"{path} was left behind")
        self.assertEqual([], list(self.state.glob("work.*")))

    def assert_relay_directory_untouched(self, expected: set[str]) -> None:
        self.assertEqual(expected, {path.name for path in self.relay_state.iterdir()})
        self.assertEqual("untouched\n", self.victim.read_text(encoding="utf-8"))

    def systemctl_calls(self) -> list[str]:
        log = self.root / "systemctl.log"
        return log.read_text(encoding="utf-8").splitlines() if log.exists() else []


class RelayUpdaterTests(UpdaterFixture):
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

    def test_the_status_directory_mirrors_what_was_decided(self) -> None:
        expected = self.release("2.0.0", "b" * 40, 1)

        self.assertEqual(0, self.run_updater().returncode)

        self.assertEqual(
            {"INSTALLED_SHA256": expected, "INSTALLED_VERSION": "2.0.0", "PUBLISHED_SHA256": expected,
             "PUBLISHED_VERSION": "2.0.0", "REFUSED_SHA256": None},
            self.status_stamps(),
        )
        self.assertEqual(0o755, stat.S_IMODE(self.status.stat().st_mode))
        self.assertEqual(0o700, stat.S_IMODE(self.state.stat().st_mode))

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
            self.assertIn("--certificate-github-workflow-repository smartpbx/tarkov-companion", call)
            self.assertIn("--certificate-github-workflow-ref refs/heads/main", call)

    def test_a_signature_from_another_repository_calling_the_workflow_is_refused(self) -> None:
        # Same certificate subject, different repository claim: what a reusable-workflow caller
        # elsewhere would present. The fake binds the claims into the bundle it checks.
        self.release("2.0.0", "b" * 40, 1)
        for name in ("TarkovCompanion-GroupServer-linux-x64.tar.gz", "release-manifest.json"):
            value = (self.bundle / name).read_bytes()
            (self.bundle / f"{name}.sigstore.json").write_text(bundle_text(value, repository="attacker/fork"))

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("does not verify", result.stdout)
        self.assertEqual("1.0.0", self.running_version())

    # The verifier itself -----------------------------------------------------------------------

    def test_a_cosign_that_is_not_a_pinned_build_is_refused_before_anything_is_read(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        for environment in ({"TARKOV_COSIGN_SHA256": "0" * 64}, {"TARKOV_COSIGN_SHA256": ""}):
            with self.subTest(environment=environment):
                result = self.run_updater(**environment)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("is not a pinned cosign", result.stdout)
                self.assertFalse((self.root / "cosign.log").exists())
                self.assertEqual("1.0.0", self.running_version())

    def test_a_cosign_other_users_can_replace_is_refused(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        (self.bin / "cosign").chmod(0o777)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("writable by other users", result.stdout)

    def test_the_updater_pins_the_same_cosign_builds_as_the_repository(self) -> None:
        pins = {line.split()[0] for line in (ROOT / "scripts/release/cosign.sha256").read_text().splitlines()
                if line.split()[1].startswith("cosign-linux-")}
        source = UPDATER.read_text(encoding="utf-8")
        block = source[source.index("readonly COSIGN_PINS=("):]
        embedded = {line.strip() for line in block[:block.index(")")].splitlines()[1:] if line.strip()}

        self.assertEqual(pins, embedded)

    def test_every_hostile_bundle_is_refused_before_cosign_runs(self) -> None:
        for target in ("index", "release-manifest.json", "TarkovCompanion-GroupServer-linux-x64.tar.gz"):
            for label, hostile in hostile_bundles(b"placeholder").items():
                with self.subTest(target=target, bundle=label):
                    # Each case is a host that has never seen this generation, so a refusal is the
                    # bundle's and not a replay check remembering the previous case's manifest.
                    for published in self.state.glob("PUBLISHED_*"):
                        published.unlink()
                    self.release("2.0.0", "b" * 40, 1)
                    self.rewrite_bundle(target, label)
                    (self.root / "cosign.log").unlink(missing_ok=True)

                    result = self.run_updater()

                    self.assertNotEqual(0, result.returncode, result.stdout)
                    self.assertRegex(
                        result.stdout,
                        "not a standardized v0.3 Sigstore bundle|signs different bytes|ring envelope is malformed",
                    )
                    self.assertFalse((self.root / "cosign.log").exists() and target in (self.root / "cosign.log").read_text())
                    self.assertEqual("1.0.0", self.running_version())
                    self.assertEqual([], [c for c in self.systemctl_calls() if c.startswith("stop")])

    def rewrite_bundle(self, target: str, label: str) -> None:
        """Replaces one signed object's bundle with the named hostile bundle for its bytes."""
        if target == "index":
            index_file = next(self.bundle.glob("release-index-g*.json"))
            envelope = json.loads(index_file.read_text())
            payload = base64.b64decode(envelope["payloadBase64"])
            envelope["sigstoreBundle"] = hostile_bundles(payload)[label]
            index_file.write_text(json.dumps(envelope))
            return
        value = (self.bundle / target).read_bytes()
        (self.bundle / f"{target}.sigstore.json").write_text(json.dumps(hostile_bundles(value)[label]))

    # Failure after the swap -------------------------------------------------------------------

    def test_failed_health_restores_tree_and_stamps_and_records_refusal(self) -> None:
        before = self.stamps()
        refused = self.release("3.0.0", "c" * 40, 1, health_commit="e" * 40)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual(before, self.stamps())
        self.assertEqual(refused, (self.state / "REFUSED_SHA256").read_text().strip())
        self.assertEqual(refused, self.status_stamps()["REFUSED_SHA256"])
        self.assertEqual(OLD_SHA, self.status_stamps()["INSTALLED_SHA256"])
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
        self.assertIsNone(self.status_stamps()["REFUSED_SHA256"])

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

    def test_rollback_removes_an_updater_that_was_absent_before_the_swap(self) -> None:
        self.updater_copy.unlink()
        self.release("3.0.0", "c" * 40, 1, shipped={
            "tarkov-group-update.service": b"new service\n",
            "tarkov-group-update.sh": b"#!/bin/sh\n# first updater\n",
        })

        result = self.run_updater(FAKE_SYSTEMCTL_FAIL="daemon-reload#1")

        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.updater_copy.exists())
        self.assertEqual("1.0.0", self.running_version())
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

    def test_sigterm_after_the_swap_is_rolled_back(self) -> None:
        # The unit's TimeoutStartSec sends SIGTERM. Landing just after the new relay starts, it
        # must be undone like any other failure, not leave the new tree under the old stamps.
        before = self.stamps()
        target = self.release("3.0.0", "c" * 40, 1)

        result = self.run_updater(FAKE_SYSTEMCTL_TERM="start#1")

        self.assertEqual(143, result.returncode, result.stdout + result.stderr)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual(before, self.stamps())
        self.assertEqual(target, (self.state / "REFUSED_SHA256").read_text().strip())
        self.assert_no_leftovers()

    def test_a_commit_rename_that_fails_restores_tree_and_stamps_together(self) -> None:
        # The stamps are already written when the journal is renamed to commit. If that rename
        # does not happen the swap did not commit, and the old stamps come back with the old tree.
        before = self.stamps()
        self.release("3.0.0", "c" * 40, 1)

        result = self.run_updater(FAKE_MV_FAIL="swap.committed")

        self.assertNotEqual(0, result.returncode)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual(before, self.stamps())
        self.assertFalse((self.state / "swap").exists())

    def test_an_interrupted_swap_is_undone_by_the_next_run(self) -> None:
        # Power lost after the new stamps were written but before the commit point: the next run
        # must put back the tree and the stamps the journal holds, and refuse that build.
        before = self.stamps()
        target = self.release("3.0.0", "c" * 40, 1)
        previous = Path(f"{self.install}.previous")
        shutil.copytree(self.install, previous)
        journal = self.state / "swap"
        (journal / "stamps").mkdir(parents=True)
        (journal / "deployment").mkdir()
        (journal / "lkg.absent").touch()
        for name in STAMPS:
            shutil.copy2(self.state / name, journal / "stamps" / name)
        (journal / "deployment" / "tarkov-group-update.service").write_text("original service\n")
        (journal / "target.json").write_text(json.dumps(
            {"sha256": target, "version": "3.0.0", "commit": "c" * 40, "ring": "stable", "generation": 1}
        ))
        self.write_health(self.install, "3.0.0", "c" * 40)
        self.write_stamps(target, "3.0.0", "c" * 40, "stable", "1")

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("interrupted during its swap", result.stdout)
        self.assertIn("was refused here before", result.stdout)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual(before, self.stamps())
        self.assert_no_leftovers()

    def test_power_loss_immediately_after_journaling_keeps_current_and_prior_lkg(self) -> None:
        prior_lkg = self.root / "prior-lkg"
        shutil.copytree(self.install, prior_lkg)
        self.write_health(prior_lkg, "0.9.0", "9" * 40)
        shutil.copytree(prior_lkg, self.lkg)
        before = self.stamps()
        self.release("3.0.0", "c" * 40, 1)

        killed = self.run_updater(FAKE_MV_KILL_AFTER=str(self.state / "swap"))

        self.assertEqual(-9, killed.returncode, killed.stdout + killed.stderr)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual("0.9.0", json.loads((self.lkg / "health.json").read_text())["version"])
        self.assertTrue((self.state / "swap").is_dir())

        recovered = self.run_updater()

        self.assertEqual(0, recovered.returncode, recovered.stdout + recovered.stderr)
        self.assertIn("interrupted during its swap", recovered.stdout)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual("0.9.0", json.loads((self.lkg / "health.json").read_text())["version"])
        self.assertEqual(before, self.stamps())
        self.assert_no_leftovers()

    def test_power_loss_after_preserving_lkg_restores_it_on_the_next_run(self) -> None:
        shutil.copytree(self.install, self.lkg)
        self.write_health(self.lkg, "0.9.0", "9" * 40)
        self.release("3.0.0", "c" * 40, 1)

        killed = self.run_updater(FAKE_MV_KILL_AFTER=str(Path(f"{self.lkg}.previous")))

        self.assertEqual(-9, killed.returncode, killed.stdout + killed.stderr)
        self.assertFalse(self.lkg.exists())
        self.assertTrue(Path(f"{self.lkg}.previous").is_dir())
        self.assertTrue(Path(f"{self.lkg}.incoming").is_dir())

        recovered = self.run_updater()

        self.assertEqual(0, recovered.returncode, recovered.stdout + recovered.stderr)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual("0.9.0", json.loads((self.lkg / "health.json").read_text())["version"])
        self.assert_no_leftovers()

    def test_power_loss_after_publishing_replacement_lkg_restores_the_prior_one(self) -> None:
        shutil.copytree(self.install, self.lkg)
        self.write_health(self.lkg, "0.9.0", "9" * 40)
        self.release("3.0.0", "c" * 40, 1)

        killed = self.run_updater(FAKE_MV_KILL_AFTER=str(self.lkg))

        self.assertEqual(-9, killed.returncode, killed.stdout + killed.stderr)
        self.assertEqual("1.0.0", json.loads((self.lkg / "health.json").read_text())["version"])
        self.assertTrue(Path(f"{self.lkg}.previous").is_dir())

        recovered = self.run_updater()

        self.assertEqual(0, recovered.returncode, recovered.stdout + recovered.stderr)
        self.assertEqual("1.0.0", self.running_version())
        self.assertEqual("0.9.0", json.loads((self.lkg / "health.json").read_text())["version"])
        self.assert_no_leftovers()

    def test_a_journal_that_was_never_renamed_into_place_changes_nothing(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        (self.state / "swap.new/stamps").mkdir(parents=True)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertNotIn("interrupted", result.stdout)
        self.assertEqual("2.0.0", self.running_version())
        self.assert_no_leftovers()

    def test_a_committed_journal_left_behind_is_not_undone(self) -> None:
        # Killed after the commit rename and before the delete: the new build is the installed
        # one, and the next run must not roll it back.
        expected = self.release("2.0.0", "b" * 40, 1)
        self.assertEqual(0, self.run_updater().returncode)
        (self.state / "swap.committed/stamps").mkdir(parents=True)
        (self.state / "swap.committed/target.json").write_text("{}")

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("already running 2.0.0", result.stdout)
        self.assertEqual(expected, self.stamps()["INSTALLED_SHA256"])
        self.assert_no_leftovers()

    # The relay's directory is hostile ----------------------------------------------------------

    def test_a_journal_planted_in_the_relay_directory_is_never_restored(self) -> None:
        # The audit's exploit: the relay user writes a complete-looking journal holding its own
        # "updater" and units, then asks for an update. Root must install none of it.
        for name in (".swap", "swap"):
            journal = self.relay_state / name
            (journal / "deployment").mkdir(parents=True)
            (journal / "stamps").mkdir()
            (journal / "complete").touch()
            (journal / "deployment/updater").write_text("#!/bin/sh\n# attacker\n")
            (journal / "deployment/tarkov-group-update.service").write_text("attacker service\n")
            (journal / "stamps/INSTALLED_SHA256").write_text("f" * 64)
            (journal / "target.json").write_text(json.dumps({"sha256": "f" * 64, "version": "9.9.9"}))
        (self.relay_state / "UPDATE_NOW").touch()
        self.release("2.0.0", "b" * 40, 1)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertNotIn("interrupted", result.stdout)
        self.assertIn("original updater", self.updater_copy.read_text())
        self.assertEqual("original service\n", (self.units / "tarkov-group-update.service").read_text())
        self.assertEqual("2.0.0", self.stamps()["INSTALLED_VERSION"])
        self.assert_relay_directory_untouched({".swap", "swap"})

    def test_links_planted_in_the_relay_directory_change_nothing_outside_it(self) -> None:
        names = ("UPDATE.lock", "update.lock", "INSTALLED_SHA256", "INSTALLED_VERSION", "PUBLISHED_SHA256",
                 "REFUSED_SHA256", "REFUSED_RELEASE.json", "PUBLISHED_GENERATION", ".update.link", "work.link")
        for name in names:
            (self.relay_state / name).symlink_to(self.victim)
        self.release("3.0.0", "c" * 40, 1, health_commit="e" * 40)
        self.assertNotEqual(0, self.run_updater().returncode)
        self.release("3.0.1", "d" * 40, 2)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assert_relay_directory_untouched(set(names))
        for name in names:
            self.assertTrue((self.relay_state / name).is_symlink())

    def test_stamps_in_the_relay_directory_are_not_believed(self) -> None:
        # The checksum updater kept its stamps where the relay could write them. A relay that
        # names the target as installed there must not stop the signed build being installed.
        self.clear_stamps()
        target = self.release("2.0.0", "b" * 40, 1)
        (self.relay_state / "INSTALLED_SHA256").write_text(target)
        (self.relay_state / "INSTALLED_VERSION").write_text("2.0.0")

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertNotIn("already running", result.stdout)
        self.assertIn("start", " ".join(self.systemctl_calls()))
        self.assertEqual(target, self.stamps()["INSTALLED_SHA256"])

    def test_a_request_marker_that_cannot_be_unlinked_does_not_stop_the_update(self) -> None:
        (self.relay_state / "UPDATE_NOW").mkdir()
        self.release("2.0.0", "b" * 40, 1)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("could not remove the request marker", result.stdout)
        self.assertEqual("2.0.0", self.running_version())

    def test_the_request_marker_is_removed_even_when_configuration_is_missing(self) -> None:
        (self.relay_state / "UPDATE_NOW").touch()
        (self.root / "trust.json").unlink()

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("trust root", result.stdout)
        self.assertFalse((self.relay_state / "UPDATE_NOW").exists())

    def test_a_state_directory_that_is_a_link_is_refused(self) -> None:
        real = self.root / "elsewhere"
        shutil.move(str(self.state), real)
        self.state.symlink_to(real)
        self.release("2.0.0", "b" * 40, 1)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertRegex(result.stdout, "safe absolute directories|symbolic link")
        self.assertEqual("1.0.0", self.running_version())

    def test_a_destructive_path_spelled_through_a_parent_alias_is_refused(self) -> None:
        # The updater removes the install, previous and incoming trees during recovery. A path
        # that is only lexically below a safe root must not be able to resolve to a broad target.
        aliased = self.root / "missing" / ".." / "install"

        result = self.run_updater(TARKOV_UPDATE_INSTALL=str(aliased))

        self.assertNotEqual(0, result.returncode)
        self.assertIn("safe absolute directories", result.stdout)
        self.assertEqual("1.0.0", self.running_version())

    def test_default_path_preflight_reflects_the_hosts_real_ownership_and_mode(self) -> None:
        result = self.run_updater(
            validate_paths=True,
            TARKOV_UPDATE_PATH_ROOT="",
            TARKOV_UPDATE_INSTALL="/opt/tarkov-group",
            TARKOV_UPDATE_LKG="/opt/tarkov-group.lkg",
            TARKOV_UPDATE_STATE="/var/lib/tarkov-group-update",
            TARKOV_UPDATE_STATUS="/var/lib/tarkov-group-update-status",
            TARKOV_RELAY_STATE="/var/lib/tarkov-group",
            TARKOV_UPDATE_SELF="/opt/tarkov-group-update.sh",
            TARKOV_UPDATE_UNITS="/etc/systemd/system",
        )

        opt = Path("/opt").stat()
        etc_systemd = Path("/etc/systemd/system").stat()
        trusted = (opt.st_uid in (0, os.getuid()) and stat.S_IMODE(opt.st_mode) & 0o022 == 0 and
                   etc_systemd.st_uid in (0, os.getuid()) and stat.S_IMODE(etc_systemd.st_mode) & 0o022 == 0)
        if trusted:
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("path preflight passed", result.stdout)
        else:
            self.assertNotEqual(0, result.returncode)
            self.assertRegex(result.stdout, "belongs to uid|writable by other users")

    def test_broad_install_roots_are_refused_without_mutating_them(self) -> None:
        safe_defaults = {
            "TARKOV_UPDATE_PATH_ROOT": "",
            "TARKOV_UPDATE_LKG": "/opt/tarkov-group.lkg",
            "TARKOV_UPDATE_STATE": "/var/lib/tarkov-group-update",
            "TARKOV_UPDATE_STATUS": "/var/lib/tarkov-group-update-status",
            "TARKOV_RELAY_STATE": "/var/lib/tarkov-group",
            "TARKOV_UPDATE_SELF": "/opt/tarkov-group-update.sh",
            "TARKOV_UPDATE_UNITS": "/etc/systemd/system",
        }
        for broad in ("/", "/opt", "/var", "/var/lib", "/etc", "/usr", "/home", "/root", "/run", "/tmp"):
            with self.subTest(path=broad):
                result = self.run_updater(
                    validate_paths=True,
                    TARKOV_UPDATE_INSTALL=broad,
                    **safe_defaults,
                )

                self.assertNotEqual(0, result.returncode)
                self.assertRegex(result.stdout, "safe absolute directories|must be below /opt")

    def test_custom_path_root_contains_every_mutable_tree(self) -> None:
        outside = self.root.parent / f"{self.root.name}-outside"

        result = self.run_updater(
            validate_paths=True,
            TARKOV_UPDATE_INSTALL=str(outside),
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("must be below TARKOV_UPDATE_PATH_ROOT", result.stdout)
        self.assertFalse(outside.exists())

    def test_custom_path_root_cannot_be_a_top_level_directory(self) -> None:
        result = self.run_updater(
            validate_paths=True,
            TARKOV_UPDATE_PATH_ROOT="/tmp",
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("must be a safe absolute directory below a top-level directory", result.stdout)

    def test_updater_executable_cannot_claim_an_install_swap_path(self) -> None:
        claimed = Path(f"{self.install}.incoming")

        result = self.run_updater(
            validate_paths=True,
            TARKOV_UPDATE_SELF=str(claimed),
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("update paths overlap", result.stdout)
        self.assertTrue(self.install.is_dir())

    def test_systemd_unit_target_cannot_be_a_link(self) -> None:
        target = self.units / "tarkov-group-update.service"
        target.unlink()
        target.symlink_to(self.victim)

        result = self.run_updater(validate_paths=True)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("systemd unit", result.stdout)
        self.assertRegex(result.stdout, "resolves somewhere else|not a plain file")
        self.assertEqual("untouched\n", self.victim.read_text(encoding="utf-8"))

    def test_updater_executable_cannot_be_a_link(self) -> None:
        self.updater_copy.unlink()
        self.updater_copy.symlink_to(self.victim)

        result = self.run_updater(validate_paths=True)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("updater executable", result.stdout)
        self.assertEqual("untouched\n", self.victim.read_text(encoding="utf-8"))

    def test_configured_directories_cannot_claim_an_install_swap_path(self) -> None:
        result = self.run_updater(TARKOV_UPDATE_LKG=f"{self.install}.incoming")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("paths overlap", result.stdout)
        self.assertEqual("1.0.0", self.running_version())

    def test_the_install_cannot_claim_the_last_known_good_swap_path(self) -> None:
        claimed = Path(f"{self.lkg}.incoming")
        shutil.copytree(self.install, claimed)
        victim = claimed / "must-survive"
        victim.write_text("caller-owned", encoding="utf-8")

        result = self.run_updater(TARKOV_UPDATE_INSTALL=str(claimed))

        self.assertNotEqual(0, result.returncode)
        self.assertIn("paths overlap", result.stdout)
        self.assertEqual("caller-owned", victim.read_text(encoding="utf-8"))

    def test_a_loose_state_directory_is_tightened_before_use(self) -> None:
        self.state.chmod(0o777)
        self.release("2.0.0", "b" * 40, 1)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(0o700, stat.S_IMODE(self.state.stat().st_mode))

    @unittest.skipUnless(hasattr(os, "geteuid") and os.geteuid() == 0, "needs root to give a directory away")
    def test_a_state_directory_owned_by_another_user_is_refused(self) -> None:
        os.chown(self.state, 65534, 65534)
        self.release("2.0.0", "b" * 40, 1)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("not by the updater", result.stdout)
        self.assertEqual("1.0.0", self.running_version())

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

    def test_an_envelope_nested_past_the_parser_limit_is_refused_before_verification(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        index = next(self.bundle.glob("release-index-g*.json"))
        index.write_text("[" * 33 + "0" + "]" * 33, encoding="utf-8")

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("not bounded valid JSON", result.stdout)
        self.assertEqual("1.0.0", self.running_version())

    def test_an_envelope_with_unrecognized_authority_fields_is_refused(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        path = next(self.bundle.glob("release-index-g*.json"))
        envelope = json.loads(path.read_text(encoding="utf-8"))
        envelope["fallbackPublicKey"] = "attacker-controlled"
        path.write_text(json.dumps(envelope), encoding="utf-8")

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("ring envelope is malformed", result.stdout)
        self.assertFalse((self.root / "cosign.log").exists())
        self.assertEqual("1.0.0", self.running_version())

    def test_an_actor_with_edge_whitespace_is_not_a_canonical_authorization(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        self.mutate_offline_index(
            lambda index: index["authorization"].__setitem__("actor", " release-operator")
        )

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("not a valid stable decision", result.stdout)
        self.assertEqual("1.0.0", self.running_version())

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

    def test_prerelease_labels_order_ordinally_whatever_the_host_locale(self) -> None:
        # "B" orders before "a" by code point and after it in en_US collation. The publisher
        # compares ordinally, so a host with a different locale must not call this an upgrade.
        self.release("2.0.0-a", "b" * 40, 1)
        self.assertEqual(0, self.run_updater(LC_ALL="en_US.UTF-8", LANG="en_US.UTF-8").returncode)
        self.release("2.0.0-B", "c" * 40, 2, action="promote")

        result = self.run_updater(LC_ALL="en_US.UTF-8", LANG="en_US.UTF-8")

        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("without a signed rollback", result.stdout)

    def test_a_version_the_publisher_would_refuse_is_refused_here(self) -> None:
        for version in ("2.0.0-01", "02.0.0", "2.0.0+build", "2.0.0-rc..1",
                        "2147483648.0.0", "2.0.0-2147483648"):
            with self.subTest(version=version):
                self.release(version, "b" * 40, 1)

                result = self.run_updater()

                self.assertNotEqual(0, result.returncode)
                self.assertIn("unsupported bounded version", result.stdout)

    def test_a_signed_rollback_installs_the_older_release(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        self.assertEqual(0, self.run_updater().returncode)
        expected = self.release("1.5.0", "c" * 40, 2, action="rollback", rollback=True)

        result = self.run_updater()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(expected, self.stamps()["INSTALLED_SHA256"])
        self.assertEqual("1.5.0", self.running_version())

    def test_a_signed_but_inconsistent_rollback_is_not_downgrade_authority(self) -> None:
        self.release("1.5.0", "c" * 40, 2, action="rollback", rollback=True)
        self.mutate_offline_index(
            lambda index: index.__setitem__("previous", index["release"])
        )

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("not a valid stable decision", result.stdout)
        self.assertEqual("1.0.0", self.running_version())

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

    # Anchoring a host without history ----------------------------------------------------------

    def test_a_host_with_no_history_floor_or_running_relay_refuses_to_choose(self) -> None:
        self.clear_stamps()
        (self.root / "running").unlink()
        self.release("1.5.0", "b" * 40, 1)

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("no floor is configured", result.stdout)
        self.assertEqual([], [c for c in self.systemctl_calls() if c.startswith("stop")])

    def test_an_explicitly_unanchored_first_install_proceeds(self) -> None:
        self.clear_stamps()
        (self.root / "running").unlink()
        expected = self.release("1.5.0", "b" * 40, 1)

        result = self.run_updater(TARKOV_RELEASE_ALLOW_UNANCHORED_BOOTSTRAP="1")

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("without any anchor", result.stdout)
        self.assertEqual(expected, self.stamps()["INSTALLED_SHA256"])

    def test_a_fresh_host_refuses_a_decision_below_its_provisioned_floor(self) -> None:
        # Somebody deleted the newer decisions from the feed. The floor came from the publish
        # run's summary, not from the feed, so the feed cannot lower it.
        self.clear_stamps()
        (self.root / "running").unlink()
        self.release("1.5.0", "b" * 40, 1)

        result = self.run_updater(TARKOV_RELEASE_MINIMUM_VERSION="2.0.0")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("configured floor is 2.0.0", result.stdout)

    def test_a_fresh_host_refuses_a_generation_below_its_provisioned_floor(self) -> None:
        self.clear_stamps()
        (self.root / "running").unlink()
        self.release("2.5.0", "b" * 40, 3)

        result = self.run_updater(TARKOV_RELEASE_MINIMUM_GENERATION="4")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("configured floor is generation 4", result.stdout)

    def test_the_provisioned_floor_holds_against_a_signed_rollback(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        self.assertEqual(0, self.run_updater().returncode)
        self.release("1.5.0", "c" * 40, 2, action="rollback", rollback=True)

        result = self.run_updater(TARKOV_RELEASE_MINIMUM_VERSION="1.9.0")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("configured floor is 1.9.0", result.stdout)
        self.assertEqual("2.0.0", self.running_version())

    def test_a_host_moving_from_the_checksum_updater_keeps_the_running_version_as_floor(self) -> None:
        # No private history, but a relay answering as 2.0.0: an older signed build still needs a
        # signed rollback, and a newer one installs.
        self.clear_stamps()
        self.write_health(self.install, "2.0.0", OLD_COMMIT)
        self.release("1.5.0", "b" * 40, 1)

        refused = self.run_updater()

        self.assertNotEqual(0, refused.returncode)
        self.assertIn("running relay reports 2.0.0", refused.stdout)
        self.assertIn("without a signed rollback", refused.stdout)

        expected = self.release("2.1.0", "c" * 40, 2)
        installed = self.run_updater()
        self.assertEqual(0, installed.returncode, installed.stdout + installed.stderr)
        self.assertEqual(expected, self.stamps()["INSTALLED_SHA256"])

    def test_a_decision_older_than_the_configured_age_is_not_installed(self) -> None:
        self.release("2.0.0", "b" * 40, 1, signed_at="2020-01-01T00:00:00Z")

        result = self.run_updater(TARKOV_RELEASE_MAX_DECISION_AGE_DAYS="30")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("older than this host's 30-day limit", result.stdout)
        self.assertEqual("1.0.0", self.running_version())
        self.assertFalse((self.state / "PUBLISHED_VERSION").exists())

    def test_a_decision_implausibly_in_the_future_is_always_refused(self) -> None:
        self.release("2.0.0", "b" * 40, 1, signed_at="2099-01-01T00:00:00Z")

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("implausibly in the future", result.stdout)
        self.assertEqual("1.0.0", self.running_version())
        self.assertFalse((self.state / "PUBLISHED_VERSION").exists())

    def test_a_calendar_invalid_signed_timestamp_does_not_advance_published_state(self) -> None:
        self.release("2.0.0", "b" * 40, 1, signed_at="2026-99-99T00:00:00Z")

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("timestamp cannot be read", result.stdout)
        self.assertFalse((self.state / "PUBLISHED_VERSION").exists())
        self.assertEqual("1.0.0", self.running_version())

    def test_malformed_policy_and_health_configuration_is_refused(self) -> None:
        for name, value in (("TARKOV_RELEASE_MINIMUM_VERSION", "2.0"), ("TARKOV_RELEASE_MINIMUM_GENERATION", "0"),
                            ("TARKOV_RELEASE_MAX_DECISION_AGE_DAYS", "-1"),
                            ("TARKOV_RELEASE_ALLOW_UNANCHORED_BOOTSTRAP", "yes"),
                            ("TARKOV_UPDATE_HEALTH_ATTEMPTS", "0"),
                            ("TARKOV_UPDATE_HEALTH_ATTEMPTS", "100000000000000000000"),
                            ("TARKOV_UPDATE_HEALTH_INTERVAL", "-1"),
                            ("TARKOV_UPDATE_HEALTH_INTERVAL", "61")):
            with self.subTest(name=name):
                self.release("2.0.0", "b" * 40, 1)

                result = self.run_updater(**{name: value})

                self.assertNotEqual(0, result.returncode)
                self.assertIn(name, result.stdout)

    def test_health_configuration_accepts_its_upper_boundaries(self) -> None:
        self.release("2.0.0", "b" * 40, 1)

        result = self.run_updater(
            TARKOV_UPDATE_HEALTH_ATTEMPTS="60",
            TARKOV_UPDATE_HEALTH_INTERVAL="60",
        )

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual("2.0.0", self.running_version())

    # Host configuration and concurrency ---------------------------------------------------------

    def test_a_held_lock_leaves_everything_to_the_running_update(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        lock = self.state / "update.lock"
        holder = subprocess.Popen(["flock", "-x", str(lock), "sleep", "30"])
        try:
            for _ in range(50):
                probe = subprocess.run(["flock", "-n", str(lock), "true"], check=False)
                if probe.returncode != 0:
                    break
            result = self.run_updater()
        finally:
            holder.kill()
            holder.wait()

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

    def test_the_feed_token_reaches_gh_and_nothing_else(self) -> None:
        self.release("2.0.0", "b" * 40, 1)

        result = self.run_updater(online=True, GH_TOKEN="inherited-token", GITHUB_TOKEN="inherited-token")

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        leaked = self.root / "leaked-token.log"
        self.assertEqual("", leaked.read_text() if leaked.exists() else "")

    def test_a_feed_token_other_users_can_read_is_refused(self) -> None:
        (self.root / "token").chmod(0o644)
        self.release("2.0.0", "b" * 40, 1)

        result = self.run_updater(online=True)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("readable by other users", result.stdout)

    def test_a_redirected_feed_token_or_trust_root_is_refused(self) -> None:
        self.release("2.0.0", "b" * 40, 1)
        for name, online in (("token", True), ("trust.json", False)):
            with self.subTest(name=name):
                target = self.root / name
                saved = target.read_bytes()
                target.unlink()
                replacement = self.root / f"real-{name}"
                replacement.write_bytes(saved)
                replacement.chmod(0o600)
                target.symlink_to(replacement)

                result = self.run_updater(online=online)

                self.assertNotEqual(0, result.returncode)
                self.assertRegex(result.stdout, "plain file|resolves somewhere else")
                target.unlink()
                target.write_bytes(saved)
                target.chmod(0o600)

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

    def test_offline_also_refuses_a_decision_signed_for_another_feed(self) -> None:
        self.release("2.0.0", "b" * 40, 1, feed="example/other-feed")

        result = self.run_updater()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("for this feed", result.stdout)


if __name__ == "__main__":
    unittest.main()
