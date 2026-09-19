from __future__ import annotations

import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path

RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RELEASE_DIRECTORY))

import rough_channel  # noqa: E402
from rough_channel import ChannelError  # noqa: E402

PACKAGE = "TarkovCompanionDesktop-1.0.1301-full.nupkg"
SETUP = "TarkovCompanionDesktop-win-Setup.exe"


def packaging_output(directory: Path, *, sha256: str | None = None, with_sha256: bool = True) -> Path:
    """What `vpk pack` leaves behind, in the parts this cares about."""
    directory.mkdir(parents=True, exist_ok=True)
    content = b"a whole build" * 1000
    (directory / PACKAGE).write_bytes(content)
    (directory / SETUP).write_bytes(b"an installer")
    (directory / "TarkovCompanionDesktop-win-Portable.zip").write_bytes(b"not published here")
    (directory / "RELEASES").write_text("legacy", encoding="utf-8")
    asset = {
        "PackageId": "TarkovCompanionDesktop",
        "Version": "1.0.1301",
        "Type": "Full",
        "FileName": PACKAGE,
        "SHA1": hashlib.sha1(content).hexdigest().upper(),
        "Size": len(content),
    }
    if with_sha256:
        asset["SHA256"] = sha256 or hashlib.sha256(content).hexdigest().upper()
    (directory / "releases.win.json").write_text(json.dumps({"Assets": [asset]}), encoding="utf-8")
    return directory


class RoughChannelTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.addCleanup(self._temporary.cleanup)

    def test_stage_publishes_the_feed_its_package_and_the_installer_and_nothing_else(self) -> None:
        staged = rough_channel.stage(packaging_output(self.root / "velopack"), self.root / "out")

        self.assertEqual(
            sorted(path.name for path in (self.root / "out").iterdir()),
            sorted([PACKAGE, SETUP, "releases.win.json", "SHA256SUMS.txt", "COPY-ORDER.txt"]),
        )
        # The feed is last, so no client ever reads a feed naming a package that is not there yet.
        self.assertEqual(staged[-1].name, "releases.win.json")
        order = (self.root / "out" / "COPY-ORDER.txt").read_text(encoding="utf-8").split()
        self.assertEqual(order, [PACKAGE, SETUP, "releases.win.json"])
        self.assertEqual(rough_channel.verify(self.root / "out")[0]["Version"], "1.0.1301")

    def test_a_package_that_does_not_match_the_feed_is_not_staged(self) -> None:
        source = packaging_output(self.root / "velopack", sha256="A" * 64)

        with self.assertRaisesRegex(ChannelError, "does not match the feed"):
            rough_channel.stage(source, self.root / "out")

    def test_a_feed_without_sha256_is_refused_because_the_desktop_would_refuse_it(self) -> None:
        source = packaging_output(self.root / "velopack", with_sha256=False)

        with self.assertRaisesRegex(ChannelError, "SHA256"):
            rough_channel.stage(source, self.root / "out")

    def test_a_staged_folder_changed_afterwards_fails_verification(self) -> None:
        rough_channel.stage(packaging_output(self.root / "velopack"), self.root / "out")
        (self.root / "out" / SETUP).write_bytes(b"a different installer")

        with self.assertRaisesRegex(ChannelError, "SHA256SUMS"):
            rough_channel.verify(self.root / "out")

    def test_a_missing_package_and_a_missing_installer_are_both_refused(self) -> None:
        source = packaging_output(self.root / "velopack")
        (source / SETUP).unlink()
        with self.assertRaisesRegex(ChannelError, "installer"):
            rough_channel.stage(source, self.root / "out")

        other = packaging_output(self.root / "velopack2")
        (other / PACKAGE).unlink()
        with self.assertRaisesRegex(ChannelError, "not in"):
            rough_channel.stage(other, self.root / "out2")

    def test_a_feed_offering_another_version_than_the_build_is_refused(self) -> None:
        source = packaging_output(self.root / "velopack")

        rough_channel.stage(source, self.root / "out", expected_version="1.0.1301")
        with self.assertRaisesRegex(ChannelError, "this build is 2.0.1301"):
            rough_channel.stage(source, self.root / "out2", expected_version="2.0.1301")
        # An empty expectation is a misspelt variable, not permission to skip the check.
        with self.assertRaisesRegex(ChannelError, "no expected version"):
            rough_channel.stage(source, self.root / "out3", expected_version="")
        self.assertEqual(
            rough_channel.main(["stage", "--velopack", str(source), "--output", str(self.root / "out4"),
                                "--expect-version", "2.0.1301"]), 1)

    def test_the_command_line_reports_a_refusal_as_a_failure(self) -> None:
        source = packaging_output(self.root / "velopack", sha256="B" * 64)

        self.assertEqual(rough_channel.main(["stage", "--velopack", str(source), "--output", str(self.root / "out")]), 1)
        good = packaging_output(self.root / "velopack-good")
        self.assertEqual(rough_channel.main(["stage", "--velopack", str(good), "--output", str(self.root / "out-good")]), 0)
        self.assertEqual(rough_channel.main(["verify", str(self.root / "out-good")]), 0)


if __name__ == "__main__":
    unittest.main()
