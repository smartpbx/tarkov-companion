"""One version grammar and one ordering, in every language the chain speaks.

A version is decided by the ring policy (Python), checked again by the relay updater (bash) and
the offline installer (PowerShell). When the manifest builder accepted "1.0.0-01" and the policy
did not, such a build would have been signed and attested and then never offered; when the
updater compared prerelease labels in the host's collation, a German locale and the publisher
disagreed about which build was newer. The same vectors go through all three here.
"""

from __future__ import annotations

import base64
import functools
import json
import os
import re
import shutil
import subprocess
import sys
import unittest
from pathlib import Path


RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
ROOT = RELEASE_DIRECTORY.parents[1]
sys.path.insert(0, str(RELEASE_DIRECTORY))

import build_manifest  # noqa: E402
import release_policy  # noqa: E402


VECTORS = json.loads((ROOT / "fixtures/release/version-grammar.json").read_text(encoding="utf-8"))
VALID = VECTORS["valid"]
INVALID = VECTORS["invalid"]
# Pairs in ascending precedence, per SemVer 2.0 section 11, plus the ordinal "B" before "a".
ORDER = VECTORS["precedence"]


@functools.lru_cache(maxsize=None)
def updater_functions() -> str:
    source = (ROOT / "deploy/group-server/tarkov-group-update.sh").read_text(encoding="utf-8")
    pattern = "\n".join(line for line in source.splitlines()
                        if line.startswith(("readonly MAX_VERSION_LENGTH=", "readonly MAX_SEMVER_NUMBER=", "readonly PRERELEASE_IDENTIFIER=",
                                            "readonly VERSION_PATTERN=")))
    functions = "\n".join(
        re.search(rf"^{name}\(\) \{{.*?^\}}\n", source, re.S | re.M).group(0)
        for name in ("decimal_at_most", "valid_semver", "semver_less")
    )
    return f"export LC_ALL=C\n{pattern}\n{functions}"


def bash(script: str, *arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["bash", "-c", updater_functions() + script, "bash", *arguments],
                          capture_output=True, text=True, check=False, env={**os.environ, "LC_ALL": "en_US.UTF-8"})


class VersionGrammarTests(unittest.TestCase):
    def test_python_accepts_exactly_the_grammar(self) -> None:
        for version in VALID:
            self.assertIsNotNone(release_policy.SEMVER.fullmatch(version), version)
            release_policy.semver_key(version)
        for version in INVALID:
            self.assertIsNone(release_policy.SEMVER.fullmatch(version), version)
            with self.assertRaises(release_policy.PolicyError, msg=version):
                release_policy.semver_key(version)
        self.assertIs(release_policy.SEMVER, build_manifest.SEMVER)

    def test_the_relay_updater_accepts_exactly_the_same_versions(self) -> None:
        for version in VALID + INVALID:
            with self.subTest(version=version):
                result = bash('valid_semver "$1"', version)
                self.assertEqual(version in VALID, result.returncode == 0)

    def test_the_relay_updater_orders_versions_as_the_policy_does_in_any_locale(self) -> None:
        for index, left in enumerate(ORDER):
            for right in ORDER[index + 1:]:
                with self.subTest(left=left, right=right):
                    self.assertLess(release_policy.semver_key(left), release_policy.semver_key(right))
                    self.assertEqual(0, bash('semver_less "$1" "$2"', left, right).returncode)
                    self.assertEqual(1, bash('semver_less "$1" "$2"', right, left).returncode)

    @unittest.skipUnless(shutil.which("pwsh") or os.environ.get("TARKOV_RELEASE_REQUIRE_TOOLS") == "1", "pwsh is not installed")
    def test_the_offline_installer_accepts_and_orders_the_same_versions(self) -> None:
        pwsh = shutil.which("pwsh")
        self.assertIsNotNone(pwsh, "pwsh is required by the release gate")
        source = (RELEASE_DIRECTORY / "install-offline.ps1").read_text(encoding="utf-8")
        declarations = "\n".join(line for line in source.splitlines()
                                 if line.startswith(("$Identifier =", "$VersionPattern =", "$MaximumVersionLength =",
                                                     "$MaximumVersionNumber =")))
        functions = "\n".join(re.search(rf"^function {name}.*?^\}}\n", source, re.S | re.M).group(0)
                              for name in ("Assert-ReleaseVersion", "Compare-ReleaseVersion"))
        encoded_vectors = base64.b64encode(json.dumps(VECTORS).encode("utf-8")).decode("ascii")
        script = f"""
{declarations}
{functions}
$FixtureJson = [System.Text.Encoding]::UTF8.GetString(
  [System.Convert]::FromBase64String($env:TARKOV_VERSION_VECTORS))
$Fixture = ConvertFrom-Json $FixtureJson
foreach ($Version in @($Fixture.valid) + @($Fixture.invalid)) {{
  try {{ Assert-ReleaseVersion $Version; $Accepted = $true }} catch {{ $Accepted = $false }}
  $Encoded = [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($Version))
  "$Encoded|$Accepted"
}}
$Order = @($Fixture.precedence)
for ($i = 0; $i -lt $Order.Count; $i++) {{
  for ($j = $i + 1; $j -lt $Order.Count; $j++) {{
    "order|$($Order[$i])|$($Order[$j])|$(Compare-ReleaseVersion $Order[$i] $Order[$j])|$(Compare-ReleaseVersion $Order[$j] $Order[$i])"
  }}
}}
        """
        result = subprocess.run([pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
                                capture_output=True, text=True, check=False, timeout=120,
                                env={**os.environ, "TARKOV_VERSION_VECTORS": encoded_vectors})
        self.assertEqual(0, result.returncode, result.stderr)
        lines = result.stdout.splitlines()
        matches = {
            base64.b64decode(encoded).decode("utf-8"): accepted
            for encoded, accepted in (line.rsplit("|", 1) for line in lines if not line.startswith("order|"))
        }
        for version in VALID + INVALID:
            with self.subTest(version=version):
                self.assertEqual(str(version in VALID), matches[version])
        for line in (line for line in lines if line.startswith("order|")):
            _, left, right, forward, backward = line.split("|")
            with self.subTest(left=left, right=right):
                self.assertEqual(("-1", "1"), (forward, backward))


if __name__ == "__main__":
    unittest.main()
