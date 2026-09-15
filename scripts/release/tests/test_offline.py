"""Offline recovery: verify-offline.sh everywhere, install-offline.ps1 wherever PowerShell exists.

GitHub's Ubuntu runners have pwsh, and the release gate sets TARKOV_RELEASE_REQUIRE_TOOLS so the
PowerShell cases fail rather than skip there. A development host without pwsh skips them.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from sigstore_fixture import bundle_for, bundle_text, hostile_bundles, install_fake_cosign  # noqa: E402


ROOT = Path(__file__).resolve().parents[3]
VERIFY_OFFLINE = ROOT / "scripts/release/verify-offline.sh"
INSTALL_OFFLINE = ROOT / "scripts/release/install-offline.ps1"
PWSH = shutil.which("pwsh")
FEED = "example/tarkov-feed"
INSTALLER = "TarkovCompanionDesktop-win-Setup.exe"
RELAY = "TarkovCompanion-GroupServer-linux-x64.tar.gz"

# Stands in for Velopack: records its private path and installs the requested build identity.
FAKE_INSTALLER = """#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$0" > "${FAKE_INSTALL_ROOT}/ran-from"
case "${FAKE_INSTALL_MODE:-install}" in
  noop) exit 0 ;;
  wrong) version=0.0.1; commit=dddddddddddddddddddddddddddddddddddddddd ;;
  install) version="${FAKE_INSTALL_VERSION}"; commit="${FAKE_INSTALL_COMMIT}" ;;
  *) exit 9 ;;
esac
mkdir -p "${FAKE_INSTALL_ROOT}/current"
touch "${FAKE_INSTALL_ROOT}/current/TarkovCompanion.exe"
printf 'version=%s\\ncommit=%s\\n' "$version" "$commit" > "${FAKE_INSTALL_ROOT}/current/BUILD_INFO.txt"
"""


def sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def plain(result: subprocess.CompletedProcess[str]) -> str:
    # PowerShell's error view colours and wraps a message to the console width; compare words.
    text = re.sub(r"\x1b\[[0-9;]*m", "", result.stdout + result.stderr)
    return " ".join(part for part in re.split(r"[\s|]+", text) if part)


class OfflineFixture(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-offline-")
        self.root = Path(self.temporary.name)
        self.bundle = self.root / "media"
        self.bundle.mkdir()
        self.bin = self.root / "bin"
        self.cosign_sha256 = install_fake_cosign(self.bin)
        self.trust = self.root / "trusted-root.json"
        self.trust.write_text('{"mediaType": "trusted-root"}', encoding="utf-8")
        self.install_root = self.root / "install"
        self.write_bundle("1.0.608")
        self.write_index()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def write_bundle(self, version: str) -> None:
        artifacts = []
        for name, component, role, value in (
            (INSTALLER, "desktop", "installer", FAKE_INSTALLER.encode()),
            (RELAY, "relay", "archive", b"relay bytes"),
        ):
            (self.bundle / name).write_bytes(value)
            (self.bundle / name).chmod(0o755)
            (self.bundle / f"{name}.sigstore.json").write_text(bundle_text(value), encoding="utf-8")
            artifacts.append({"name": name, "component": component, "role": role, "sha256": sha256(value), "size": len(value)})
        manifest = json.dumps({"schemaVersion": 1, "version": version, "commit": "c" * 40, "artifacts": artifacts}).encode()
        (self.bundle / "release-manifest.json").write_bytes(manifest)
        (self.bundle / "release-manifest.json.sigstore.json").write_text(bundle_text(manifest), encoding="utf-8")

    def manifest_sha(self) -> str:
        return sha256((self.bundle / "release-manifest.json").read_bytes())

    def write_index(self, *, generation: int = 7, manifest_sha: str | None = None, ring: str = "stable",
                    paused: bool = False, rollback: bool = False, feed: str = FEED) -> Path:
        for stale in self.bundle.glob("release-index-g*.json"):
            stale.unlink()
        manifest = json.loads((self.bundle / "release-manifest.json").read_text())
        release = {
            "version": manifest["version"], "commit": manifest["commit"], "buildTag": f"v2-build-{manifest['version']}",
            "manifestName": "release-manifest.json", "manifestSha256": manifest_sha or self.manifest_sha(),
        }
        rollback_from = {
            **release,
            "version": "9.9.9",
            "commit": "f" * 40,
            "buildTag": "v2-build-9.9.9",
            "manifestSha256": "f" * 64,
        } if rollback else None
        payload = json.dumps({
            "schemaVersion": 1,
            "mediaType": "application/vnd.tarkov-companion.release-index.v1+json",
            "feedRepository": feed,
            "ring": ring,
            "generation": generation,
            "updatedUtc": "2026-09-15T00:00:00Z",
            "paused": paused,
            "release": release,
            "previous": rollback_from,
            "lastKnownGood": release,
            "highWaterVersion": manifest["version"] if not rollback else "9.9.9",
            "rollback": {"generation": generation, "from": rollback_from} if rollback else None,
            "authorization": {"action": "rollback" if rollback else "pause" if paused else "promote",
                              "actor": "operator", "reason": "fixture",
                              "workflowRunId": "1", "verificationRunId": None,
                              "sourceRing": None if rollback or paused else "beta",
                              "sourceGeneration": None if rollback or paused else generation,
                              "previousGeneration": generation - 1},
        }).encode()
        path = self.bundle / f"release-index-g{generation:010d}.json"
        path.write_text(json.dumps({
            "schemaVersion": 1,
            "mediaType": "application/vnd.tarkov-companion.signed-release-index.v1+json",
            "payloadBase64": base64.b64encode(payload).decode(),
            "sigstoreBundle": bundle_for(payload),
        }), encoding="utf-8")
        return path

    def environment(self, **extra: str) -> dict[str, str]:
        return {
            **os.environ,
            "PATH": f"{self.bin}:{os.environ['PATH']}",
            "TMPDIR": str(self.root),
            "TARKOV_COSIGN_SHA256": self.cosign_sha256,
            "FAKE_INSTALL_ROOT": str(self.install_root),
            **extra,
        }


class VerifyOfflineTests(OfflineFixture):
    def verify(self, *options: str, **extra: str) -> subprocess.CompletedProcess[str]:
        arguments = list(options) if options else ["--ring", "stable", "--feed", FEED]
        return subprocess.run([str(VERIFY_OFFLINE), *arguments, str(self.bundle), str(self.trust)], capture_output=True,
                              text=True, env=self.environment(**extra), check=False, timeout=60)

    def test_a_complete_bundle_selected_by_its_ring_verifies(self) -> None:
        result = self.verify()

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("version 1.0.608", result.stdout)
        self.assertIn("2 artifacts", result.stdout)
        self.assertIn("stable generation 7 (promote)", result.stdout)

    def test_the_ring_is_required_unless_breaking_glass(self) -> None:
        no_ring = self.verify("--feed", FEED)
        self.assertNotEqual(0, no_ring.returncode)
        self.assertIn("--ring", no_ring.stderr)

        (next(self.bundle.glob("release-index-g*.json"))).unlink()
        no_decision = self.verify()
        self.assertNotEqual(0, no_decision.returncode)
        self.assertIn("bounded plain signed ring decision", no_decision.stderr)

        glass = self.verify("--break-glass")
        self.assertEqual(0, glass.returncode, glass.stderr)
        self.assertIn("BREAK-GLASS", glass.stdout)
        self.assertIn("NOT applied", glass.stderr)

    def test_the_decision_must_select_this_manifest_for_this_ring_and_feed(self) -> None:
        cases = {
            "different manifest": dict(manifest_sha="0" * 64),
            "another ring": dict(ring="beta"),
            "another feed": dict(feed="example/other-feed"),
        }
        for label, index in cases.items():
            with self.subTest(label=label):
                self.write_index(**index)

                result = self.verify()

                self.assertNotEqual(0, result.returncode, label)

    def test_a_paused_ring_holds_unless_the_decision_is_a_signed_rollback(self) -> None:
        self.write_index(paused=True)
        self.assertIn("is paused", self.verify().stderr)

        self.write_index(paused=True, rollback=True)
        result = self.verify()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("signed rollback", result.stdout)

    def test_a_generation_below_the_required_floor_is_refused(self) -> None:
        result = self.verify("--ring", "stable", "--feed", FEED, "--minimum-generation", "8")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("below the required generation 8", result.stderr)

    def test_altered_missing_linked_or_unsigned_files_are_refused(self) -> None:
        cases = {
            "altered": lambda: (self.bundle / INSTALLER).write_bytes(b"other"),
            "missing": lambda: (self.bundle / RELAY).unlink(),
            "unsigned": lambda: (self.bundle / f"{RELAY}.sigstore.json").unlink(),
            "linked": lambda: ((self.bundle / RELAY).unlink(), (self.bundle / RELAY).symlink_to(self.root / "elsewhere")),
        }
        (self.root / "elsewhere").write_bytes(b"relay bytes")
        for label, mutate in cases.items():
            with self.subTest(label=label):
                self.write_bundle("1.0.608")
                self.write_index()
                mutate()
                self.assertNotEqual(0, self.verify().returncode)
        self.write_bundle("1.0.608")
        self.write_index()
        self.assertNotEqual(0, self.verify(FAKE_COSIGN_REJECT="release-manifest.json").returncode)

    def test_every_hostile_bundle_is_refused(self) -> None:
        for label in hostile_bundles(b"").keys():
            with self.subTest(bundle=label):
                self.write_bundle("1.0.608")
                self.write_index()
                value = (self.bundle / INSTALLER).read_bytes()
                (self.bundle / f"{INSTALLER}.sigstore.json").write_text(json.dumps(hostile_bundles(value)[label]))

                result = self.verify()

                self.assertNotEqual(0, result.returncode)
                self.assertRegex(result.stderr, "not a standardized v0.3 Sigstore message-signature bundle|signs different bytes")

    def test_a_cosign_that_is_not_pinned_is_refused(self) -> None:
        result = self.verify(TARKOV_COSIGN_SHA256="")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("is not a pinned cosign", result.stderr)

    def test_an_unsafe_manifest_name_is_refused(self) -> None:
        manifest = json.loads((self.bundle / "release-manifest.json").read_text())
        manifest["artifacts"][0]["name"] = "../outside.exe"
        value = json.dumps(manifest).encode()
        (self.bundle / "release-manifest.json").write_bytes(value)
        (self.bundle / "release-manifest.json.sigstore.json").write_text(bundle_text(value))

        result = self.verify()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("unsafe artifact", result.stderr)

    def test_the_output_is_the_verified_copy_not_the_media(self) -> None:
        output = self.root / "verified"

        result = self.verify("--ring", "stable", "--feed", FEED, "--output", str(output))
        (self.bundle / INSTALLER).write_bytes(b"swapped after verification")

        self.assertEqual(0, result.returncode, result.stderr)
        manifest = json.loads((output / "release-manifest.json").read_text())
        installer = next(item for item in manifest["artifacts"] if item["name"] == INSTALLER)
        self.assertEqual(installer["sha256"], sha256((output / INSTALLER).read_bytes()))


class InstallOfflineTests(OfflineFixture):
    def install(self, *extra: str, ring: bool = True, **environment: str) -> subprocess.CompletedProcess[str]:
        if PWSH is None:
            if os.environ.get("TARKOV_RELEASE_REQUIRE_TOOLS") == "1":
                self.fail("pwsh is required by the release gate but is not installed")
            self.skipTest("pwsh is not installed on this host")
        arguments = [PWSH, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(INSTALL_OFFLINE),
                     "-BundleDirectory", str(self.bundle), "-TrustedRoot", str(self.trust),
                     "-CosignPath", str(self.bin / "cosign"), "-InstallRoot", str(self.install_root), "-Headless"]
        if "-CosignSha256" not in extra:
            arguments += ["-CosignSha256", self.cosign_sha256]
        if ring:
            arguments += ["-Ring", "stable", "-FeedRepository", FEED]
        manifest = json.loads((self.bundle / "release-manifest.json").read_text())
        return subprocess.run([*arguments, *extra], capture_output=True, text=True,
                              env=self.environment(FAKE_INSTALL_VERSION=manifest["version"],
                                                   FAKE_INSTALL_COMMIT=manifest["commit"], **environment),
                              check=False, timeout=120)

    def installed(self, version: str | None) -> None:
        (self.install_root / "current").mkdir(parents=True, exist_ok=True)
        if version is not None:
            (self.install_root / "current/BUILD_INFO.txt").write_text(f"version={version}\ncommit={'a' * 40}\n", encoding="utf-8")

    def test_the_installer_verifies_everything_before_what_if_stops_it(self) -> None:
        result = self.install("-WhatIf")

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("Verified signed Tarkov Companion 1.0.608", result.stdout)
        self.assertIn("selected by stable generation 7", plain(result))
        self.assertIn("What if", result.stdout)
        self.assertFalse((self.install_root / "ran-from").exists())

    @unittest.skipIf(os.name == "nt", "Unix executable-mode fixture")
    def test_linux_executes_the_private_verifier_and_honors_its_native_exit_code(self) -> None:
        executable_log = self.root / "cosign-executables.log"

        verified = self.install("-WhatIf", FAKE_COSIGN_EXECUTABLE_LOG=str(executable_log))
        executed = [Path(line) for line in executable_log.read_text(encoding="utf-8").splitlines()]
        rejected = self.install("-WhatIf", FAKE_COSIGN_REJECT=INSTALLER)

        self.assertEqual(0, verified.returncode, verified.stdout + verified.stderr)
        self.assertEqual(3, len(executed))
        self.assertNotIn((self.bin / "cosign").resolve(), executed)
        self.assertTrue(all(not path.exists() for path in executed), "a staged verifier survived cleanup")
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn("does not verify", plain(rejected))
        self.assertNotIn("LASTEXITCODE", plain(rejected))

    def test_the_installer_runs_the_verified_private_copy_not_the_media(self) -> None:
        result = self.install()

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        ran_from = Path((self.install_root / "ran-from").read_text().strip())
        self.assertNotEqual(self.bundle, ran_from.parent)
        self.assertTrue(ran_from.name == INSTALLER)
        self.assertFalse(ran_from.exists(), "the private copy was not removed afterwards")
        identity = (self.install_root / "current/BUILD_INFO.txt").read_text()
        self.assertIn("version=1.0.608", identity)
        self.assertIn(f"commit={'c' * 40}", identity)

    def test_an_exit_zero_noop_or_wrong_build_is_not_reported_as_installed(self) -> None:
        for mode in ("noop", "wrong"):
            with self.subTest(mode=mode):
                shutil.rmtree(self.install_root, ignore_errors=True)
                self.installed("1.0.500")
                (self.install_root / "current/TarkovCompanion.exe").touch()

                result = self.install(FAKE_INSTALL_MODE=mode)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("installed identity", plain(result))

    def test_the_installer_needs_a_ring_decision_unless_breaking_glass(self) -> None:
        no_ring = self.install("-WhatIf", ring=False)
        self.assertNotEqual(0, no_ring.returncode)
        self.assertIn("-BreakGlass", plain(no_ring))

        next(self.bundle.glob("release-index-g*.json")).unlink()
        no_decision = self.install("-WhatIf")
        self.assertNotEqual(0, no_decision.returncode)
        self.assertIn("no signed ring decision", plain(no_decision))

        glass = self.install("-WhatIf", "-BreakGlass", ring=False)
        self.assertEqual(0, glass.returncode, glass.stdout + glass.stderr)
        self.assertIn("BREAK-GLASS", plain(glass))

    def test_the_installer_refuses_a_decision_for_another_build_ring_or_feed_or_a_paused_ring(self) -> None:
        for label, index in {"manifest": dict(manifest_sha="0" * 64), "ring": dict(ring="beta"),
                             "feed": dict(feed="example/other"), "paused": dict(paused=True)}.items():
            with self.subTest(label=label):
                self.write_index(**index)

                self.assertNotEqual(0, self.install("-WhatIf").returncode, label)

    def test_the_installer_refuses_unverified_or_mismatched_bytes(self) -> None:
        (self.bundle / INSTALLER).write_bytes(b"swapped")
        mismatched = self.install("-WhatIf")
        self.assertNotEqual(0, mismatched.returncode)
        self.assertIn("does not match the signed manifest", plain(mismatched))

        self.write_bundle("1.0.608")
        self.write_index()
        rejected = self.install("-WhatIf", FAKE_COSIGN_REJECT=INSTALLER)
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn("does not verify", plain(rejected))

    def test_the_installer_refuses_every_hostile_bundle(self) -> None:
        for label in hostile_bundles(b"").keys():
            with self.subTest(bundle=label):
                self.write_bundle("1.0.608")
                self.write_index()
                value = (self.bundle / INSTALLER).read_bytes()
                (self.bundle / f"{INSTALLER}.sigstore.json").write_text(json.dumps(hostile_bundles(value)[label]))

                result = self.install("-WhatIf")

                self.assertNotEqual(0, result.returncode)
                self.assertRegex(plain(result), "not a standardized v0.3 Sigstore bundle|signs different bytes")

    def test_the_installer_refuses_a_cosign_that_is_not_pinned(self) -> None:
        result = self.install("-WhatIf", "-CosignSha256", "0" * 64)

        self.assertNotEqual(0, result.returncode)
        self.assertIn("is not the cosign -CosignSha256 names", plain(result))

    def test_the_installer_refuses_a_downgrade_without_a_signed_rollback_or_permission(self) -> None:
        self.installed("1.0.700")

        refused = self.install("-WhatIf")
        allowed = self.install("-WhatIf", "-AllowDowngrade")
        self.write_index(rollback=True)
        rolled_back = self.install("-WhatIf")

        self.assertNotEqual(0, refused.returncode)
        self.assertIn("installed 1.0.700 without a signed rollback or -AllowDowngrade", plain(refused))
        self.assertEqual(0, allowed.returncode, allowed.stdout + allowed.stderr)
        self.assertEqual(0, rolled_back.returncode, rolled_back.stdout + rolled_back.stderr)

    def test_an_installed_version_that_cannot_be_read_is_not_treated_as_older(self) -> None:
        for label, version in (("missing", None), ("unparsable", "one point oh")):
            with self.subTest(label=label):
                shutil.rmtree(self.install_root, ignore_errors=True)
                self.installed(version)

                refused = self.install("-WhatIf")

                self.assertNotEqual(0, refused.returncode)
                self.assertIn("cannot be read", plain(refused))
                self.assertEqual(0, self.install("-WhatIf", "-AllowDowngrade").returncode)

    def test_the_installer_orders_prereleases_below_their_release(self) -> None:
        self.installed("1.0.608")
        self.write_bundle("1.0.608-rc.2")
        self.write_index()

        self.assertNotEqual(0, self.install("-WhatIf").returncode)


if __name__ == "__main__":
    unittest.main()
