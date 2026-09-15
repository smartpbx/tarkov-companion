"""The cosign every release signature depends on is one reviewed build, wherever it is checked.

Three consumers hold the pins - the repository's scripts, the relay updater that runs alone on a
host, and the offline installer that runs alone on Windows - so the lists are compared here
rather than trusted to have been edited together.
"""

from __future__ import annotations

import os
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from sigstore_fixture import bundle_text, install_fake_cosign  # noqa: E402


ROOT = Path(__file__).resolve().parents[3]
RELEASE = ROOT / "scripts/release"


def pins() -> dict[str, str]:
    result = {}
    for line in (RELEASE / "cosign.sha256").read_text(encoding="utf-8").splitlines():
        digest, name = line.split()
        result[name] = digest
    return result


class CosignPinTests(unittest.TestCase):
    def test_the_pins_are_cosign_v3_1_3_for_every_platform_a_consumer_runs_on(self) -> None:
        self.assertEqual({"cosign-linux-amd64", "cosign-linux-arm64", "cosign-windows-amd64.exe"}, set(pins()))
        self.assertTrue(all(re.fullmatch(r"[0-9a-f]{64}", digest) for digest in pins().values()))
        self.assertIn('TASK_VERSION="v3.1.3"', (RELEASE / "install-cosign.sh").read_text(encoding="utf-8"))

    def test_the_offline_installer_pins_the_same_builds(self) -> None:
        source = (RELEASE / "install-offline.ps1").read_text(encoding="utf-8")
        block = source[source.index("$CosignPins = @("):]
        embedded = set(re.findall(r'"([0-9a-f]{64})"', block[:block.index(")")]))

        self.assertEqual(set(pins().values()), embedded)

    def test_the_relay_updater_pins_the_same_linux_builds(self) -> None:
        source = (ROOT / "deploy/group-server/tarkov-group-update.sh").read_text(encoding="utf-8")
        block = source[source.index("readonly COSIGN_PINS=("):]
        embedded = set(re.findall(r"[0-9a-f]{64}", block[:block.index(")")]))

        self.assertEqual({digest for name, digest in pins().items() if name.startswith("cosign-linux-")}, embedded)

    def test_no_owned_workflow_installs_cosign_by_tag(self) -> None:
        for workflow in ("publish.yml", "license-lock.yml"):
            text = (ROOT / ".github/workflows" / workflow).read_text(encoding="utf-8")
            with self.subTest(workflow=workflow):
                self.assertNotIn("cosign-installer", text)
                if "cosign" in text:
                    self.assertIn("scripts/release/install-cosign.sh", text)

    def test_velopack_cli_is_version_and_content_pinned(self) -> None:
        pin = (RELEASE / "vpk.sha256").read_text(encoding="utf-8").strip()
        source = (RELEASE / "install-vpk.ps1").read_text(encoding="utf-8")
        workflow = (ROOT / ".github/workflows/windows-verify.yml").read_text(encoding="utf-8")

        self.assertRegex(pin, r"^[0-9a-f]{64}  vpk\.1\.2\.0\.nupkg$")
        self.assertIn('$Version = "1.2.0"', source)
        self.assertIn("$ExpectedDigest", source)
        self.assertIn("--configfile $Config --no-cache", source)
        self.assertIn("scripts/release/install-vpk.ps1", workflow)
        self.assertNotIn("dotnet tool install --global vpk", workflow)


class PinnedCosignScriptTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-cosign-pins-")
        self.root = Path(self.temporary.name)
        self.bin = self.root / "bin"
        self.digest = install_fake_cosign(self.bin)
        self.trust = self.root / "trusted-root.json"
        self.trust.write_text("{}", encoding="utf-8")

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def run_script(self, *arguments: str, **environment: str) -> subprocess.CompletedProcess[str]:
        env = {**os.environ, "PATH": f"{self.bin}:{os.environ['PATH']}", "TARKOV_SIGSTORE_TRUST_ROOT": str(self.trust)}
        env.pop("TARKOV_COSIGN_SHA256", None)
        env.update(environment)
        return subprocess.run([*arguments], capture_output=True, text=True, env=env, check=False, timeout=60)

    def test_a_cosign_that_is_not_pinned_is_refused_everywhere_it_would_run(self) -> None:
        subject = self.root / "subject"
        subject.write_bytes(b"release bytes")
        (self.root / "subject.sigstore.json").write_text(bundle_text(b"release bytes"))

        for label, command in (
            ("resolve", [str(RELEASE / "pinned-cosign.sh")]),
            ("verify", [str(RELEASE / "verify-signed-file.sh"), str(subject), str(self.root / "subject.sigstore.json"), str(self.trust)]),
            ("sign", [str(RELEASE / "sign-files.sh"), str(subject)]),
        ):
            with self.subTest(label=label):
                result = self.run_script(*command)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("is not a pinned cosign", result.stderr)

    def test_a_named_digest_accepts_exactly_that_build(self) -> None:
        accepted = self.run_script(str(RELEASE / "pinned-cosign.sh"), TARKOV_COSIGN_SHA256=self.digest)
        wrong = self.run_script(str(RELEASE / "pinned-cosign.sh"), TARKOV_COSIGN_SHA256="0" * 64)
        malformed = self.run_script(str(RELEASE / "pinned-cosign.sh"), TARKOV_COSIGN_SHA256="not-a-digest")

        self.assertEqual(0, accepted.returncode, accepted.stderr)
        self.assertEqual(str((self.bin / "cosign").resolve()), accepted.stdout.strip())
        self.assertNotEqual(0, wrong.returncode)
        self.assertNotEqual(0, malformed.returncode)
        self.assertIn("is not a sha256", malformed.stderr)

    def test_a_link_is_resolved_before_it_is_hashed(self) -> None:
        link = self.root / "links/cosign"
        link.parent.mkdir()
        link.symlink_to(self.bin / "cosign")

        result = self.run_script(str(RELEASE / "pinned-cosign.sh"), TARKOV_COSIGN=str(link), TARKOV_COSIGN_SHA256=self.digest)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(str((self.bin / "cosign").resolve()), result.stdout.strip())

    def test_signing_writes_a_standardized_bundle_and_verifies_it_with_the_publisher_rule(self) -> None:
        subject = self.root / "subject"
        subject.write_bytes(b"release bytes")

        result = self.run_script(str(RELEASE / "sign-files.sh"), str(subject), TARKOV_COSIGN_SHA256=self.digest)

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("application/vnd.dev.sigstore.bundle.v0.3+json", (self.root / "subject.sigstore.json").read_text())


if __name__ == "__main__":
    unittest.main()
