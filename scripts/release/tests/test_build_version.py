from __future__ import annotations

import re
import subprocess
import unittest
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[3]
SCRIPT = REPOSITORY / "scripts" / "build-version.sh"
PRODUCT = (REPOSITORY / "PRODUCT_VERSION").read_text(encoding="utf-8").strip()


def decide(*arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(["bash", str(SCRIPT), *arguments], capture_output=True, text=True, check=False)


class BuildVersionTests(unittest.TestCase):
    def test_a_ci_build_is_the_product_version_and_the_run_number(self) -> None:
        result = decide("1140")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, f"{PRODUCT}.1140\n")
        self.assertRegex(result.stdout.strip(), r"^\d+\.\d+\.\d+$")

    def test_this_is_the_second_generation_with_no_minor_yet(self) -> None:
        # docs/RELEASES.md says what the parts mean. 2 is the V2 workspace; nothing has earned a minor.
        self.assertEqual(PRODUCT, "2.0")

    def test_a_build_with_no_number_says_it_is_a_development_build(self) -> None:
        result = decide()

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, f"{PRODUCT}.0-dev\n")

    def test_a_build_number_that_is_not_a_plain_number_is_refused(self) -> None:
        # 65535 and above do not fit an assembly version part, where the build number also goes.
        for bad in ("", "abc", "01", "-1", "1.5", "12 34", "65535", "100000"):
            with self.subTest(bad=bad):
                result = decide(bad)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, "")
        self.assertEqual(decide("65534").stdout, f"{PRODUCT}.65534\n")
        self.assertEqual(decide("0").stdout, f"{PRODUCT}.0\n")

    def test_nothing_that_builds_or_packages_spells_a_version_of_its_own(self) -> None:
        """The old number survived because every copy of it agreed with every other copy."""
        spelt = re.compile(r"\d+\.\d+\.\$\{\{\s*github\.run_number|TarkovCompanion-v\d|TARKOV_BUILD_VERSION:-\d")
        for relative in (".github/workflows/windows-verify.yml", "scripts/package-windows.sh"):
            text = (REPOSITORY / relative).read_text(encoding="utf-8")
            code = "\n".join(line for line in text.splitlines() if not line.lstrip().startswith("#"))
            with self.subTest(file=relative):
                self.assertIsNone(spelt.search(code))
                self.assertIn("TARKOV_BUILD_VERSION", code)

    def test_the_workflow_decides_the_version_once_per_job_and_msbuild_reads_the_same_file(self) -> None:
        workflow = (REPOSITORY / ".github/workflows/windows-verify.yml").read_text(encoding="utf-8")
        self.assertEqual(workflow.count('./scripts/build-version.sh "${{ github.run_number }}"'), 2)
        self.assertEqual(workflow.count("github.run_number"), 2)
        self.assertIn("PRODUCT_VERSION", (REPOSITORY / "Directory.Build.props").read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
