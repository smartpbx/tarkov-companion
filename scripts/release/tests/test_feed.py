"""Fixture tests for scripts/release/feed.py against an in-memory GitHub.

The fake answers the exact gh invocations the feed client makes, including the GitHub
behaviours the design depends on: creating a contents path that already exists is refused, a
draft release is visible only through the release list, and every uploaded asset carries a
server-computed sha256 digest.
"""

from __future__ import annotations

import base64
import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Sequence


RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RELEASE_DIRECTORY))

import feed as feed_module  # noqa: E402
from feed import Feed, FeedConflict, FeedError  # noqa: E402


REPOSITORY = "example/tarkov-feed"


def completed(stdout: bytes = b"", stderr: bytes = b"", code: int = 0) -> subprocess.CompletedProcess:
    return subprocess.CompletedProcess([], code, stdout, stderr)


class FakeGitHub:
    def __init__(self) -> None:
        self.visibility = "private"
        # True, False (the setting is off: 404) or "unreadable" (the token lacks Administration read).
        self.immutable_setting: bool | str = True
        self.publish_immutable = True
        self.files: dict[str, bytes] = {}
        self.history: set[str] = set()
        self.releases: list[dict] = []
        self.next_id = 100
        self.corrupt_upload: str | None = None
        self.drop_upload: str | None = None
        self.readback_override: bytes | None = None
        self.calls: list[list[str]] = []

    def __call__(self, arguments: Sequence[str], stdin: bytes | None) -> subprocess.CompletedProcess:
        arguments = list(arguments)
        self.calls.append(arguments)
        assert arguments[0] == "gh"
        if arguments[1] == "api":
            return self.api(arguments[2:], stdin)
        if arguments[1:3] == ["release", "create"]:
            return self.release_create(arguments[3:])
        if arguments[1:3] == ["release", "download"]:
            return self.release_download(arguments[3:])
        raise AssertionError(f"unexpected gh call {arguments}")

    @staticmethod
    def not_found() -> subprocess.CompletedProcess:
        return completed(stderr=b"gh: Not Found (HTTP 404)", code=1)

    def api(self, arguments: list[str], stdin: bytes | None) -> subprocess.CompletedProcess:
        method = "GET"
        raw = False
        endpoint = None
        fields: list[str] = []
        index = 0
        while index < len(arguments):
            item = arguments[index]
            if item in ("--method", "-X"):
                method = arguments[index + 1]
                index += 2
            elif item == "-H":
                raw = "raw" in arguments[index + 1]
                index += 2
            elif item in ("--jq", "--input", "-F", "-f"):
                if item in ("-F", "-f"):
                    fields.append(arguments[index + 1])
                index += 2
            elif item == "--paginate":
                index += 1
            else:
                endpoint = item
                index += 1
        prefix = f"repos/{REPOSITORY}"
        assert endpoint is not None and endpoint.startswith(prefix), endpoint
        path = endpoint[len(prefix):]

        if path == "":
            return completed(f"{self.visibility}\n".encode())
        if path == "/immutable-releases":
            if self.immutable_setting == "unreadable":
                return completed(stderr=b"gh: Resource not accessible by personal access token (HTTP 403)", code=1)
            if self.immutable_setting is not True:
                return self.not_found()
            return completed(json.dumps({"enabled": True, "enforced_by_owner": False}).encode())
        if path.startswith("/commits?path="):
            ring_path = path.split("path=", 1)[1].split("&", 1)[0]
            return completed(json.dumps([{"sha": "1"}] if ring_path in self.history else []).encode())
        if path.startswith("/contents/"):
            content_path = path[len("/contents/"):]
            if method == "PUT":
                if content_path in self.files:
                    return completed(stderr=b'gh: Invalid request. "sha" wasn\'t supplied. (HTTP 422)', code=1)
                body = json.loads(stdin or b"{}")
                self.files[content_path] = base64.b64decode(body["content"])
                self.history.add(content_path.rsplit("/", 1)[0])
                return completed(json.dumps({"content": {"path": content_path}}).encode())
            if method == "DELETE":
                if content_path not in self.files:
                    return self.not_found()
                del self.files[content_path]
                return completed(b"{}")
            if content_path in self.files:
                if not raw:
                    raise AssertionError("files are read raw")
                return completed(self.readback_override if self.readback_override is not None else self.files[content_path])
            entries = [
                {"name": name.rsplit("/", 1)[1], "path": name, "sha": hashlib.sha1(value).hexdigest(), "type": "file"}
                for name, value in sorted(self.files.items())
                if name.rsplit("/", 1)[0] == content_path
            ]
            return completed(json.dumps(entries).encode()) if entries else self.not_found()
        if path.startswith("/releases?"):
            return completed("".join(
                json.dumps({"id": r["id"], "tag_name": r["tag"], "draft": r["draft"], "immutable": r["immutable"]}) + "\n"
                for r in self.releases
            ).encode())
        if path.startswith("/releases/"):
            release_id = int(path.split("/")[2].split("?")[0])
            release = next((r for r in self.releases if r["id"] == release_id), None)
            if release is None:
                return self.not_found()
            if "/assets" in path:
                return completed("".join(json.dumps(asset) + "\n" for asset in release["assets"].values()).encode())
            if method == "DELETE":
                assert release["draft"], "only drafts may be deleted"
                self.releases.remove(release)
                return completed(b"")
            if method == "PATCH":
                assert "draft=false" in fields
                release["draft"] = False
                release["immutable"] = self.publish_immutable
                return completed(json.dumps({"draft": False, "tag_name": release["tag"], "immutable": self.publish_immutable}).encode())
        raise AssertionError(f"unexpected api call {method} {endpoint}")

    def release_create(self, arguments: list[str]) -> subprocess.CompletedProcess:
        tag = arguments[0]
        assert "--draft" in arguments
        files = [Path(item) for item in arguments if item.startswith("/")]
        assets = {}
        for file in files:
            if file.name == self.drop_upload:
                continue
            value = file.read_bytes()
            if file.name == self.corrupt_upload:
                value += b"corrupted in transit"
            assets[file.name] = {"name": file.name, "size": len(value), "digest": f"sha256:{hashlib.sha256(value).hexdigest()}",
                                 "state": "uploaded", "content": base64.b64encode(value).decode()}
        self.releases.append({"id": self.next_id, "tag": tag, "draft": True, "immutable": False, "assets": assets})
        self.next_id += 1
        return completed()

    def release_download(self, arguments: list[str]) -> subprocess.CompletedProcess:
        tag = arguments[0]
        pattern = arguments[arguments.index("--pattern") + 1]
        directory = Path(arguments[arguments.index("--dir") + 1])
        release = next((r for r in self.releases if r["tag"] == tag and not r["draft"]), None)
        if release is None or pattern not in release["assets"]:
            return completed(stderr=b"release not found", code=1)
        directory.mkdir(parents=True, exist_ok=True)
        (directory / pattern).write_bytes(base64.b64decode(release["assets"][pattern]["content"]))
        return completed()


class FeedTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-feed-")
        self.root = Path(self.temporary.name)
        self.github = FakeGitHub()
        self.feed = Feed(REPOSITORY, runner=self.github)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def envelope(self, generation: int) -> Path:
        path = self.root / f"envelope-{generation}.json"
        path.write_text(json.dumps({"generation": generation}), encoding="utf-8")
        return path

    def build_directory(self, version: str = "1.0.608") -> tuple[Path, Path]:
        directory = self.root / f"build-{version}"
        directory.mkdir()
        artifacts = []
        for name, value in (("TarkovCompanion-GroupServer-linux-x64.tar.gz", b"relay"), ("TarkovCompanionDesktop-win-Setup.exe", b"setup")):
            (directory / name).write_bytes(value)
            (directory / f"{name}.sigstore.json").write_text("{}", encoding="utf-8")
            artifacts.append({"name": name, "component": "relay", "role": "archive",
                              "sha256": hashlib.sha256(value).hexdigest(), "size": len(value)})
        manifest = directory / "release-manifest.json"
        manifest.write_text(json.dumps({"schemaVersion": 1, "version": version, "commit": "c" * 40, "builtUtc": "x",
                                        "versions": {}, "source": {"verificationRunId": "1"}, "artifacts": artifacts}),
                            encoding="utf-8")
        (directory / "release-manifest.json.sigstore.json").write_text("{}", encoding="utf-8")
        return directory, manifest

    # Repository -------------------------------------------------------------------------------

    def test_only_a_private_or_internal_feed_other_than_the_source_is_accepted(self) -> None:
        self.assertEqual("private", self.feed.require_private("smartpbx/tarkov-companion"))
        self.github.visibility = "internal"
        self.assertEqual("internal", self.feed.require_private("smartpbx/tarkov-companion"))
        self.github.visibility = "public"
        with self.assertRaises(FeedError):
            self.feed.require_private("smartpbx/tarkov-companion")
        with self.assertRaises(FeedError):
            Feed("smartpbx/tarkov-companion", runner=self.github).require_private("smartpbx/tarkov-companion")

    def test_the_feed_must_enforce_immutable_releases_and_say_so_to_this_token(self) -> None:
        self.assertTrue(self.feed.require_immutable_releases()["enabled"])
        for setting, message in ((False, "not enabled"), ("unreadable", "could not read")):
            with self.subTest(setting=setting):
                self.github.immutable_setting = setting
                with self.assertRaisesRegex(FeedError, message):
                    self.feed.require_immutable_releases()

    def test_a_build_github_does_not_report_immutable_is_refused_after_publication(self) -> None:
        directory, manifest = self.build_directory()
        self.github.publish_immutable = False

        with self.assertRaisesRegex(FeedError, "does not report it immutable"):
            self.feed.publish_build("v2-build-1.0.608", directory, manifest)

    def test_a_build_tag_uses_the_ring_policys_version_grammar(self) -> None:
        for tag in ("v2-build-1.0.0-01", "v2-build-01.0.0", "v2-build-1.0.0+meta", "v2-build-1.0.0-rc..1"):
            with self.subTest(tag=tag), self.assertRaises(FeedError):
                self.feed.find_build(tag)

    # Rings ------------------------------------------------------------------------------------

    def test_a_ring_never_written_starts_at_generation_zero(self) -> None:
        state = self.feed.read_ring("canary", self.root / "ring")

        self.assertEqual({"ring": "canary", "generation": 0, "name": None}, state)
        self.assertFalse((self.root / "ring/envelope.json").exists())

    def test_a_ring_whose_files_were_deleted_is_not_started_again(self) -> None:
        self.github.history.add("rings/stable")

        with self.assertRaises(FeedError):
            self.feed.read_ring("stable", self.root / "ring")

    def test_reading_selects_the_newest_decision(self) -> None:
        for generation in (1, 2, 12):
            self.github.files[f"rings/beta/release-index-g{generation:010d}.json"] = f"g{generation}".encode()
        self.github.files["rings/beta/release-index-g0000000099.json.bak"] = b"stray"

        state = self.feed.read_ring("beta", self.root / "ring")

        self.assertEqual(12, state["generation"])
        self.assertEqual(b"g12", (self.root / "ring/envelope.json").read_bytes())

    def test_a_decision_is_created_once_and_read_back(self) -> None:
        result = self.feed.commit_ring("canary", self.envelope(1), 1)

        self.assertEqual("release-index-g0000000001.json", result["name"])
        self.assertEqual(self.envelope(1).read_bytes(), self.github.files["rings/canary/release-index-g0000000001.json"])

    def test_a_writer_that_computed_from_a_stale_generation_conflicts(self) -> None:
        self.feed.commit_ring("canary", self.envelope(1), 1)

        with self.assertRaises(FeedConflict):
            self.feed.commit_ring("canary", self.envelope(1), 1)
        with self.assertRaises(FeedConflict):
            self.feed.commit_ring("canary", self.envelope(3), 3)

    def test_losing_the_create_race_is_a_conflict_not_an_overwrite(self) -> None:
        # Both writers listed generation zero; the other one's create landed first.
        original_state = self.feed.ring_state

        def racing_state(ring: str):
            state = original_state(ring)
            self.github.files["rings/canary/release-index-g0000000001.json"] = b"the winner"
            return state

        self.feed.ring_state = racing_state  # type: ignore[method-assign]
        with self.assertRaises(FeedConflict):
            self.feed.commit_ring("canary", self.envelope(1), 1)
        self.assertEqual(b"the winner", self.github.files["rings/canary/release-index-g0000000001.json"])

    def test_a_decision_that_does_not_read_back_fails(self) -> None:
        self.github.readback_override = b"something else"

        with self.assertRaises(FeedError) as raised:
            self.feed.commit_ring("stable", self.envelope(1), 1)
        self.assertNotIsInstance(raised.exception, FeedConflict)

    def test_pruning_keeps_the_newest_decisions(self) -> None:
        for generation in range(1, 6):
            self.feed.commit_ring("canary", self.envelope(generation), generation, keep=3)

        names = sorted(name for name in self.github.files if name.startswith("rings/canary/"))
        self.assertEqual(
            [f"rings/canary/release-index-g{generation:010d}.json" for generation in (3, 4, 5)], names
        )

    # Builds -----------------------------------------------------------------------------------

    def test_a_build_is_published_only_after_every_digest_matches(self) -> None:
        directory, manifest = self.build_directory()

        result = self.feed.publish_build("v2-build-1.0.608", directory, manifest)

        release = self.github.releases[0]
        self.assertFalse(release["draft"])
        self.assertEqual(6, result["assets"])
        self.assertEqual({"TarkovCompanion-GroupServer-linux-x64.tar.gz", "TarkovCompanion-GroupServer-linux-x64.tar.gz.sigstore.json",
                          "TarkovCompanionDesktop-win-Setup.exe", "TarkovCompanionDesktop-win-Setup.exe.sigstore.json",
                          "release-manifest.json", "release-manifest.json.sigstore.json"}, set(release["assets"]))

    def test_a_corrupted_or_missing_upload_is_never_published(self) -> None:
        for field, name in (("corrupt_upload", "TarkovCompanionDesktop-win-Setup.exe"), ("drop_upload", "release-manifest.json.sigstore.json")):
            with self.subTest(field=field):
                self.github = FakeGitHub()
                setattr(self.github, field, name)
                self.feed = Feed(REPOSITORY, runner=self.github)
                directory, manifest = self.build_directory(version=f"1.0.{600 + len(field)}")

                with self.assertRaises(FeedError):
                    self.feed.publish_build(f"v2-build-1.0.{600 + len(field)}", directory, manifest)
                self.assertTrue(all(release["draft"] for release in self.github.releases))

    def test_a_stale_draft_is_replaced_and_a_published_build_is_never_republished(self) -> None:
        self.github.releases.append({"id": 7, "tag": "v2-build-1.0.608", "draft": True, "immutable": False, "assets": {}})
        directory, manifest = self.build_directory()

        self.feed.publish_build("v2-build-1.0.608", directory, manifest)

        self.assertEqual([False], [release["draft"] for release in self.github.releases])
        self.assertNotIn(7, [release["id"] for release in self.github.releases])
        with self.assertRaises(FeedError):
            self.feed.publish_build("v2-build-1.0.608", directory, manifest)

    def test_a_file_changed_after_signing_is_not_uploaded(self) -> None:
        directory, manifest = self.build_directory()
        (directory / "TarkovCompanionDesktop-win-Setup.exe").write_bytes(b"changed")

        with self.assertRaises(FeedError):
            self.feed.publish_build("v2-build-1.0.608", directory, manifest)
        self.assertEqual([], self.github.releases)

    def test_downloads_are_bound_to_githubs_recorded_size_and_digest(self) -> None:
        directory, manifest = self.build_directory()
        self.feed.publish_build("v2-build-1.0.608", directory, manifest)
        output = self.root / "download"

        self.feed.download_build_files(
            "v2-build-1.0.608", ["release-manifest.json", "release-manifest.json.sigstore.json"], output)

        self.assertEqual(manifest.read_bytes(), (output / "release-manifest.json").read_bytes())
        asset = self.github.releases[0]["assets"]["release-manifest.json"]
        asset["content"] = base64.b64encode(b"different").decode()
        with self.assertRaisesRegex(FeedError, "recorded size|recorded digest"):
            self.feed.download_build_files("v2-build-1.0.608", ["release-manifest.json"], self.root / "corrupt")

    def test_an_asset_claiming_an_unbounded_size_is_refused_before_download(self) -> None:
        directory, manifest = self.build_directory()
        self.feed.publish_build("v2-build-1.0.608", directory, manifest)
        self.github.releases[0]["assets"]["release-manifest.json"]["size"] = feed_module.MAX_JSON_BYTES + 1

        with self.assertRaisesRegex(FeedError, "size limit"):
            self.feed.download_build_files("v2-build-1.0.608", ["release-manifest.json"], self.root / "too-large")
        self.assertFalse((self.root / "too-large/release-manifest.json").exists())

    def test_an_earlier_attempts_build_is_adopted_only_if_it_is_the_same_build(self) -> None:
        existing = {"schemaVersion": 1, "version": "1.0.608", "commit": "c" * 40, "builtUtc": "t", "versions": {"package": "1.0.608"},
                    "source": {"verificationRunId": "1"},
                    "artifacts": [{"name": "a.tar.gz", "component": "relay", "sha256": "1" * 64, "size": 1},
                                  {"name": "TarkovCompanion.spdx.json", "component": "release", "sha256": "2" * 64, "size": 2}]}
        regenerated_sbom = json.loads(json.dumps(existing))
        regenerated_sbom["artifacts"][1]["sha256"] = "3" * 64
        feed_module.adoptable(existing, regenerated_sbom)

        different_bytes = json.loads(json.dumps(existing))
        different_bytes["artifacts"][0]["sha256"] = "4" * 64
        different_run = json.loads(json.dumps(existing))
        different_run["source"]["verificationRunId"] = "2"
        for candidate in (different_bytes, different_run):
            with self.subTest(candidate=candidate), self.assertRaises(FeedError):
                feed_module.adoptable(existing, candidate)


if __name__ == "__main__":
    unittest.main()
