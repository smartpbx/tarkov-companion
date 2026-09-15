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
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[3]
VERIFY_OFFLINE = ROOT / "scripts/release/verify-offline.sh"
INSTALL_OFFLINE = ROOT / "scripts/release/install-offline.ps1"
PWSH = shutil.which("pwsh")
IDENTITY = "https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main"

FAKE_COSIGN = f"""#!/usr/bin/env bash
set -euo pipefail
bundle=''
identity=''
root=''
while (($# > 1)); do
    case "$1" in
        --bundle) bundle="$2"; shift 2 ;;
        --certificate-identity) identity="$2"; shift 2 ;;
        --trusted-root) root="$2"; shift 2 ;;
        *) shift ;;
    esac
done
subject="$1"
[[ -s "${{bundle}}" && -s "${{root}}" ]] || {{ echo "missing bundle or root" >&2; exit 1; }}
[[ "${{identity}}" == "{IDENTITY}" ]] || {{ echo "wrong identity" >&2; exit 1; }}
if [[ -n "${{FAKE_COSIGN_REJECT:-}}" && "$(basename "${{subject}}")" == "${{FAKE_COSIGN_REJECT}}" ]]; then
    echo "rejected" >&2
    exit 1
fi
echo "Verified OK" >&2
"""


def sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def plain(result: subprocess.CompletedProcess[str]) -> str:
    # PowerShell's error view colours and wraps a message to the console width; compare words.
    text = re.sub(r"\x1b\[[0-9;]*m", "", result.stdout + result.stderr)
    return " ".join(part for part in re.split(r"[\s|]+", text) if part)


class OfflineBundleTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-offline-")
        self.root = Path(self.temporary.name)
        self.bundle = self.root / "media"
        self.bundle.mkdir()
        self.bin = self.root / "bin"
        self.bin.mkdir()
        cosign = self.bin / "cosign"
        cosign.write_text(FAKE_COSIGN, encoding="utf-8")
        cosign.chmod(0o755)
        self.trust = self.root / "trusted-root.json"
        self.trust.write_text('{"mediaType": "trusted-root"}', encoding="utf-8")
        self.write_bundle("1.0.608")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def write_bundle(self, version: str) -> None:
        artifacts = []
        for name, component, role, value in (
            ("TarkovCompanionDesktop-win-Setup.exe", "desktop", "installer", b"setup bytes"),
            ("TarkovCompanion-GroupServer-linux-x64.tar.gz", "relay", "archive", b"relay bytes"),
        ):
            (self.bundle / name).write_bytes(value)
            (self.bundle / f"{name}.sigstore.json").write_text("{}", encoding="utf-8")
            artifacts.append({"name": name, "component": component, "role": role, "sha256": sha256(value), "size": len(value)})
        manifest = self.bundle / "release-manifest.json"
        manifest.write_text(json.dumps({"schemaVersion": 1, "version": version, "commit": "c" * 40, "artifacts": artifacts}), encoding="utf-8")
        (self.bundle / "release-manifest.json.sigstore.json").write_text("{}", encoding="utf-8")

    def write_index(self, manifest_sha: str) -> None:
        payload = {"ring": "stable", "generation": 7, "paused": False, "authorization": {"action": "promote"},
                   "release": {"manifestSha256": manifest_sha}}
        (self.bundle / "release-index-g0000000007.json").write_text(json.dumps({
            "schemaVersion": 1,
            "mediaType": "application/vnd.tarkov-companion.signed-release-index.v1+json",
            "payloadBase64": base64.b64encode(json.dumps(payload).encode()).decode(),
            "sigstoreBundle": {"fixture": True},
        }), encoding="utf-8")

    def environment(self, **extra: str) -> dict[str, str]:
        return {**os.environ, "PATH": f"{self.bin}:{os.environ['PATH']}", "TMPDIR": str(self.root), **extra}

    def verify(self, **extra: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run([str(VERIFY_OFFLINE), str(self.bundle), str(self.trust)], capture_output=True,
                              text=True, env=self.environment(**extra), check=False, timeout=60)

    # verify-offline.sh ------------------------------------------------------------------------

    def test_a_complete_bundle_verifies(self) -> None:
        result = self.verify()

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("version 1.0.608", result.stdout)
        self.assertIn("2 artifacts", result.stdout)

    def test_a_ring_decision_on_the_media_must_select_this_manifest(self) -> None:
        self.write_index(sha256((self.bundle / "release-manifest.json").read_bytes()))
        good = self.verify()
        self.assertEqual(0, good.returncode, good.stderr)
        self.assertIn("stable generation 7 (promote)", good.stdout)

        self.write_index("0" * 64)
        bad = self.verify()
        self.assertNotEqual(0, bad.returncode)
        self.assertIn("selects a different manifest", bad.stderr)

    def test_altered_missing_or_unsigned_files_are_refused(self) -> None:
        cases = {
            "altered": lambda: (self.bundle / "TarkovCompanionDesktop-win-Setup.exe").write_bytes(b"other"),
            "missing": lambda: (self.bundle / "TarkovCompanion-GroupServer-linux-x64.tar.gz").unlink(),
            "unsigned": lambda: (self.bundle / "TarkovCompanion-GroupServer-linux-x64.tar.gz.sigstore.json").unlink(),
        }
        for label, mutate in cases.items():
            with self.subTest(label=label):
                self.write_bundle("1.0.608")
                mutate()
                self.assertNotEqual(0, self.verify().returncode)
        self.write_bundle("1.0.608")
        self.assertNotEqual(0, self.verify(FAKE_COSIGN_REJECT="release-manifest.json").returncode)

    def test_an_unsafe_manifest_name_is_refused(self) -> None:
        manifest = json.loads((self.bundle / "release-manifest.json").read_text())
        manifest["artifacts"][0]["name"] = "../outside.exe"
        (self.bundle / "release-manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

        result = self.verify()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("unsafe artifact", result.stderr)

    # install-offline.ps1 ----------------------------------------------------------------------

    def install(self, *extra: str, **environment: str) -> subprocess.CompletedProcess[str]:
        if PWSH is None:
            if os.environ.get("TARKOV_RELEASE_REQUIRE_TOOLS") == "1":
                self.fail("pwsh is required by the release gate but is not installed")
            self.skipTest("pwsh is not installed on this host")
        install_root = self.root / "install"
        return subprocess.run(
            [PWSH, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(INSTALL_OFFLINE),
             "-BundleDirectory", str(self.bundle), "-TrustedRoot", str(self.trust),
             "-CosignPath", str(self.bin / "cosign"), "-InstallRoot", str(install_root), "-Headless", *extra],
            capture_output=True, text=True, env=self.environment(**environment), check=False, timeout=120,
        )

    def installed(self, version: str) -> None:
        (self.root / "install/current").mkdir(parents=True, exist_ok=True)
        (self.root / "install/current/BUILD_INFO.txt").write_text(f"version={version}\ncommit={'a' * 40}\n", encoding="utf-8")

    def test_the_installer_verifies_everything_before_what_if_stops_it(self) -> None:
        result = self.install("-WhatIf")

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("Verified signed Tarkov Companion 1.0.608", result.stdout)
        self.assertIn("What if", result.stdout)

    def test_the_installer_refuses_unverified_or_mismatched_bytes(self) -> None:
        (self.bundle / "TarkovCompanionDesktop-win-Setup.exe").write_bytes(b"swapped")
        mismatched = self.install("-WhatIf")
        self.assertNotEqual(0, mismatched.returncode)
        self.assertIn("does not match the signed manifest", plain(mismatched))

        self.write_bundle("1.0.608")
        rejected = self.install("-WhatIf", FAKE_COSIGN_REJECT="TarkovCompanionDesktop-win-Setup.exe")
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn("does not verify", plain(rejected))

    def test_the_installer_refuses_a_downgrade_unless_asked(self) -> None:
        self.installed("1.0.700")

        refused = self.install("-WhatIf")
        allowed = self.install("-WhatIf", "-AllowDowngrade")

        self.assertNotEqual(0, refused.returncode)
        self.assertIn("installed 1.0.700 without -AllowDowngrade", plain(refused))
        self.assertEqual(0, allowed.returncode, allowed.stdout + allowed.stderr)

    def test_the_installer_orders_prereleases_below_their_release(self) -> None:
        self.installed("1.0.608")
        self.write_bundle("1.0.608-rc.2")

        self.assertNotEqual(0, self.install("-WhatIf").returncode)


if __name__ == "__main__":
    unittest.main()
