"""Fail-fast fixtures for hostile release inputs that are valid enough to reach parsers."""

from __future__ import annotations

import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

import sys

RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RELEASE_DIRECTORY))

import artifacts  # noqa: E402
import resource_limits  # noqa: E402
from artifacts import ArtifactError  # noqa: E402
from resource_limits import ResourceLimitError  # noqa: E402


class JsonLimitTests(unittest.TestCase):
    def test_byte_limit_is_applied_before_json_parsing(self) -> None:
        with self.assertRaisesRegex(ResourceLimitError, "byte JSON limit"):
            resource_limits.decode_json(b'{"value":"12345"}', "fixture", maximum=8)

    def test_nesting_limit_ignores_brackets_in_strings_but_refuses_real_depth(self) -> None:
        self.assertEqual({"text": "[[[["}, resource_limits.decode_json(b'{"text":"[[[["}', "fixture", maximum_depth=2))
        with self.assertRaisesRegex(ResourceLimitError, "nesting"):
            resource_limits.decode_json(b"[[[0]]]", "fixture", maximum_depth=2)


class ArchiveLimitTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-resource-limits-")
        self.root = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def write_zip(self, count: int) -> Path:
        path = self.root / "fixture.zip"
        with zipfile.ZipFile(path, "w") as archive:
            for index in range(count):
                archive.writestr(f"file-{index}.txt", b"x")
        return path

    def test_member_count_is_checked_before_extraction(self) -> None:
        archive = self.write_zip(3)
        with mock.patch.object(artifacts, "MAX_ARCHIVE_MEMBERS", 2):
            with self.assertRaisesRegex(ArtifactError, "entry limit"):
                artifacts.extract(archive, self.root / "out")
        self.assertFalse((self.root / "out").exists())

    def test_declared_expanded_size_is_checked_before_extraction(self) -> None:
        archive = self.write_zip(1)
        with mock.patch.object(artifacts, "MAX_EXPANDED_BYTES", 0):
            with self.assertRaisesRegex(ArtifactError, "expanded-size"):
                artifacts.extract(archive, self.root / "out-expanded")
        self.assertFalse((self.root / "out-expanded").exists())

    def test_a_redirected_bounded_input_is_refused(self) -> None:
        target = self.root / "target.json"
        target.write_text("{}", encoding="utf-8")
        link = self.root / "link.json"
        link.symlink_to(target)

        with self.assertRaisesRegex(ResourceLimitError, "plain file"):
            resource_limits.read_json(link)

    def test_bounded_hashing_refuses_redirects_and_files_above_its_consumption_limit(self) -> None:
        target = self.root / "artifact.bin"
        target.write_bytes(b"known bytes")
        link = self.root / "artifact-link.bin"
        link.symlink_to(target)

        self.assertEqual(
            "25cb6d61356e5cada4238d160f3a77522e550e27a69758da40cd281c7ef2c8dc",
            resource_limits.sha256_file(target, "artifact", 11),
        )
        with self.assertRaisesRegex(ResourceLimitError, "byte limit"):
            resource_limits.sha256_file(target, "artifact", 10)
        with self.assertRaisesRegex(ResourceLimitError, "plain file"):
            resource_limits.sha256_file(link, "artifact link", 11)

    def test_ring_selection_is_bounded_and_ignores_links(self) -> None:
        (self.root / "release-index-g0000000001.json").write_text("{}", encoding="utf-8")
        (self.root / "release-index-g0000000002.json").symlink_to(
            self.root / "release-index-g0000000001.json")

        self.assertEqual(
            "release-index-g0000000001.json",
            resource_limits.select_ring_file(self.root, maximum_entries=2),
        )
        (self.root / "extra").write_text("x", encoding="utf-8")
        with self.assertRaisesRegex(ResourceLimitError, "directory limit"):
            resource_limits.select_ring_file(self.root, maximum_entries=2)


if __name__ == "__main__":
    unittest.main()
