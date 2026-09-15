"""One version grammar and one ordering, in every language the chain speaks.

A version is decided by the ring policy (Python), checked again by the relay updater (bash) and
the offline installer (PowerShell). When the manifest builder accepted "1.0.0-01" and the policy
did not, such a build would have been signed and attested and then never offered; when the
updater compared prerelease labels in the host's collation, a German locale and the publisher
disagreed about which build was newer. The same vectors go through all three here.
"""

from __future__ import annotations

import functools
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


VALID = ["0.0.0", "1.0.608", "10.20.30", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-0.3.7", "1.0.0-x.7.z.92",
         "1.0.0-x-y-z.--", "1.0.0-rc.1", "1.0.0-B", "1.0.0-a"]
INVALID = ["1.0", "01.0.0", "1.00.0", "1.0.0-01", "1.0.0-rc..1", "1.0.0-", "1.0.0+build", "v1.0.0", "1.0.0 ", "",
           "1.0.0-rc.01"]
# Pairs in ascending precedence, per SemVer 2.0 section 11, plus the ordinal "B" before "a".
ORDER = ["1.0.0-0", "1.0.0-B", "1.0.0-a", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
         "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0", "10.0.0"]


@functools.lru_cache(maxsize=None)
def updater_functions() -> str:
    source = (ROOT / "deploy/group-server/tarkov-group-update.sh").read_text(encoding="utf-8")
    pattern = "\n".join(line for line in source.splitlines()
                        if line.startswith(("readonly PRERELEASE_IDENTIFIER=", "readonly VERSION_PATTERN=")))
    function = re.search(r"^semver_less\(\) \{.*?^\}\n", source, re.S | re.M).group(0)
    return f"export LC_ALL=C\n{pattern}\n{function}"


def bash(script: str, *arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["bash", "-c", updater_functions() + script, "bash", *arguments],
                          capture_output=True, text=True, check=False, env={**os.environ, "LC_ALL": "en_US.UTF-8"})


class VersionGrammarTests(unittest.TestCase):
    def test_python_accepts_exactly_the_grammar(self) -> None:
        for version in VALID:
            self.assertIsNotNone(release_policy.SEMVER.fullmatch(version), version)
        for version in INVALID:
            self.assertIsNone(release_policy.SEMVER.fullmatch(version), version)
        self.assertIs(release_policy.SEMVER, build_manifest.SEMVER)

    def test_the_relay_updater_accepts_exactly_the_same_versions(self) -> None:
        for version in VALID + INVALID:
            with self.subTest(version=version):
                result = bash('[[ "$1" =~ ${VERSION_PATTERN} ]]', version)
                self.assertEqual(release_policy.SEMVER.fullmatch(version) is not None, result.returncode == 0)

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
        declarations = "\n".join(line for line in source.splitlines() if line.startswith(("$Identifier =", "$VersionPattern =")))
        function = re.search(r"^function Compare-ReleaseVersion.*?^\}\n", source, re.S | re.M).group(0)
        vectors = "@(" + ",".join(f"'{version}'" for version in VALID + INVALID) + ")"
        order = "@(" + ",".join(f"'{version}'" for version in ORDER) + ")"
        script = f"""
{declarations}
{function}
foreach ($Version in {vectors}) {{ "$Version|$($Version -cmatch $VersionPattern)" }}
$Order = {order}
for ($i = 0; $i -lt $Order.Count; $i++) {{
  for ($j = $i + 1; $j -lt $Order.Count; $j++) {{
    "order|$($Order[$i])|$($Order[$j])|$(Compare-ReleaseVersion $Order[$i] $Order[$j])|$(Compare-ReleaseVersion $Order[$j] $Order[$i])"
  }}
}}
"""
        result = subprocess.run([pwsh, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
                                capture_output=True, text=True, check=False, timeout=120)
        self.assertEqual(0, result.returncode, result.stderr)
        lines = result.stdout.splitlines()
        matches = dict(line.rsplit("|", 1) for line in lines if not line.startswith("order|"))
        for version in VALID + INVALID:
            with self.subTest(version=version):
                self.assertEqual(str(release_policy.SEMVER.fullmatch(version) is not None), matches[version])
        for line in (line for line in lines if line.startswith("order|")):
            _, left, right, forward, backward = line.split("|")
            with self.subTest(left=left, right=right):
                self.assertEqual(("-1", "1"), (forward, backward))


if __name__ == "__main__":
    unittest.main()
