"""Verification artifacts are fetched by recorded digest, and provenance is written from them.

The fakes return the shapes GitHub returns for a real Windows verification run (34924256898):
an artifact listing whose entries carry `digest: sha256:...` and their workflow run, and an
archive endpoint whose bytes hash to that digest.
"""

from __future__ import annotations

import hashlib
import io
import json
import stat
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from typing import Sequence


RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(RELEASE_DIRECTORY))

import artifacts  # noqa: E402
import provenance  # noqa: E402
from artifacts import ArtifactError  # noqa: E402
from provenance import ProvenanceError  # noqa: E402


REPOSITORY = "smartpbx/tarkov-companion"
RUN_ID = "34924256898"
SHA = "c" * 40


def archive(entries: dict[str, bytes], links: tuple[str, ...] = ()) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as bundle:
        for name, value in entries.items():
            info = zipfile.ZipInfo(name)
            info.external_attr = (stat.S_IFREG | 0o644) << 16
            bundle.writestr(info, value)
        for name in links:
            info = zipfile.ZipInfo(name)
            info.external_attr = (stat.S_IFLNK | 0o777) << 16
            bundle.writestr(info, "/etc/passwd")
    return buffer.getvalue()


class FakeGitHub:
    def __init__(self) -> None:
        self.run = {
            "id": int(RUN_ID), "path": ".github/workflows/windows-verify.yml", "event": "push", "head_branch": "main",
            "status": "completed", "conclusion": "success", "head_sha": SHA, "run_number": 650, "run_attempt": 1,
            "run_started_at": "2026-09-15T03:15:00Z", "updated_at": "2026-09-15T03:40:00Z",
            "repository": {"full_name": REPOSITORY}, "head_repository": {"full_name": REPOSITORY},
        }
        self.archives = {
            "windows-release-payload": archive({"update.json": b"{}", "velopack/RELEASES": b"releases"}),
            "group-server-release": archive({"TarkovCompanion-GroupServer-linux-x64.tar.gz": b"relay", "GROUPSERVER-SHA256SUMS.txt": b"sums"}),
        }
        self.listing_overrides: dict[str, dict] = {}
        self.served: dict[str, bytes] = {}
        self.extra: list[dict] = []

    def listing(self) -> dict:
        items = []
        for index, (name, value) in enumerate(self.archives.items(), start=1):
            item = {"id": index, "name": name, "size_in_bytes": len(value), "digest": f"sha256:{hashlib.sha256(value).hexdigest()}",
                    "expired": False, "workflow_run": {"id": int(RUN_ID), "head_sha": SHA}}
            item.update(self.listing_overrides.get(name, {}))
            items.append(item)
        return {"total_count": len(items) + len(self.extra), "artifacts": items + self.extra}

    def __call__(self, arguments: Sequence[str], output: Path | None) -> subprocess.CompletedProcess:
        endpoint = arguments[2]
        if endpoint == f"repos/{REPOSITORY}/actions/runs/{RUN_ID}":
            return subprocess.CompletedProcess(arguments, 0, json.dumps(self.run).encode(), b"")
        if endpoint.startswith(f"repos/{REPOSITORY}/actions/runs/{RUN_ID}/artifacts"):
            return subprocess.CompletedProcess(arguments, 0, json.dumps(self.listing()).encode(), b"")
        if endpoint.startswith(f"repos/{REPOSITORY}/actions/artifacts/") and endpoint.endswith("/zip"):
            artifact_id = int(endpoint.split("/")[5])
            name = list(self.archives)[artifact_id - 1]
            assert output is not None
            output.write_bytes(self.served.get(name, self.archives[name]))
            return subprocess.CompletedProcess(arguments, 0, b"", b"")
        return subprocess.CompletedProcess(arguments, 1, b"", b"gh: Not Found (HTTP 404)")


class ArtifactFetchTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-artifacts-")
        self.root = Path(self.temporary.name)
        self.github = FakeGitHub()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def fetch(self, output: str = "out") -> dict:
        return artifacts.fetch(self.github, REPOSITORY, RUN_ID, ["windows-release-payload", "group-server-release"], self.root / output)

    def test_artifacts_whose_bytes_match_the_recorded_digests_are_extracted_and_recorded(self) -> None:
        record = self.fetch()

        self.assertEqual(SHA, record["verificationRun"]["headSha"])
        self.assertEqual(f"https://github.com/{REPOSITORY}/actions/runs/{RUN_ID}/attempts/1", record["verificationRun"]["url"])
        payload = next(item for item in record["artifacts"] if item["name"] == "windows-release-payload")
        self.assertEqual(hashlib.sha256(self.github.archives["windows-release-payload"]).hexdigest(), payload["sha256"])
        self.assertEqual(["update.json", "velopack/RELEASES"], [item["path"] for item in payload["files"]])
        self.assertEqual(b"releases", (self.root / "out/windows-release-payload/velopack/RELEASES").read_bytes())
        self.assertEqual([], list((self.root / "out").glob(".*.zip")))

    def test_bytes_that_are_not_what_was_uploaded_are_refused(self) -> None:
        self.github.served["group-server-release"] = archive({"TarkovCompanion-GroupServer-linux-x64.tar.gz": b"swapped"})

        with self.assertRaisesRegex(ArtifactError, "but the verification run uploaded"):
            self.fetch()

    def test_an_artifact_that_cannot_be_bound_to_its_upload_is_refused(self) -> None:
        cases = {
            "no digest": {"digest": None},
            "not sha256": {"digest": "md5:abc"},
            "expired": {"expired": True},
            "another run": {"workflow_run": {"id": 1, "head_sha": SHA}},
            "another commit": {"workflow_run": {"id": int(RUN_ID), "head_sha": "d" * 40}},
        }
        for label, override in cases.items():
            with self.subTest(label=label):
                self.github.listing_overrides = {"group-server-release": override}
                with self.assertRaises(ArtifactError):
                    self.fetch(output=label.replace(" ", "-"))

    def test_an_ambiguous_artifact_name_is_refused(self) -> None:
        self.github.extra = [{"id": 9, "name": "group-server-release", "digest": "sha256:" + "0" * 64, "size_in_bytes": 1,
                              "expired": False, "workflow_run": {"id": int(RUN_ID), "head_sha": SHA}}]

        with self.assertRaisesRegex(ArtifactError, "not exactly one"):
            self.fetch()

    def test_an_archive_with_a_link_or_a_climb_is_refused(self) -> None:
        for label, value in (("link", archive({"a.txt": b"a"}, links=("escape",))), ("climb", archive({"../escape": b"x"}))):
            with self.subTest(label=label):
                self.github.archives["group-server-release"] = value
                with self.assertRaises(ArtifactError):
                    self.fetch(output=label)

    def test_only_a_successful_push_to_main_verification_run_is_fetched(self) -> None:
        for key, value in (("event", "pull_request"), ("head_branch", "feature"), ("conclusion", "failure")):
            with self.subTest(key=key):
                original = self.github.run[key]
                self.github.run[key] = value
                with self.assertRaises(ArtifactError):
                    self.fetch(output=key)
                self.github.run[key] = original


class ProvenanceTests(unittest.TestCase):
    def record(self, **run: object) -> dict:
        return {
            "schemaVersion": 1, "repository": REPOSITORY,
            "verificationRun": {"id": int(RUN_ID), "attempt": 2, "number": 650, "workflowPath": ".github/workflows/windows-verify.yml",
                                "event": "push", "headBranch": "main", "headSha": SHA,
                                "url": f"https://github.com/{REPOSITORY}/actions/runs/{RUN_ID}/attempts/2",
                                "startedAt": "2026-09-15T03:15:00Z", "updatedAt": "2026-09-15T03:40:00Z", **run},
            "artifacts": [{"name": "windows-release-payload", "id": 7, "sha256": "a" * 64, "size": 10, "files": []},
                          {"name": "group-server-release", "id": 8, "sha256": "b" * 64, "size": 10, "files": []}],
        }

    MANIFEST = {"version": "1.0.650", "commit": SHA, "versions": {"package": "1.0.650"},
                "source": {"verificationRunId": RUN_ID}}
    PUBLISHER = {"repository": REPOSITORY, "workflowRef": f"{REPOSITORY}/.github/workflows/publish.yml@refs/heads/main",
                 "runId": "99", "runAttempt": "1"}

    def test_the_builder_source_invocation_and_inputs_are_the_verification_runs(self) -> None:
        value = provenance.predicate(self.record(), self.MANIFEST, self.PUBLISHER)

        self.assertEqual(f"https://github.com/{REPOSITORY}/.github/workflows/windows-verify.yml@refs/heads/main",
                         value["runDetails"]["builder"]["id"])
        self.assertEqual(f"https://github.com/{REPOSITORY}/actions/runs/{RUN_ID}/attempts/2", value["runDetails"]["metadata"]["invocationId"])
        dependencies = value["buildDefinition"]["resolvedDependencies"]
        self.assertEqual({"gitCommit": SHA}, dependencies[0]["digest"])
        self.assertEqual([{"sha256": "a" * 64}, {"sha256": "b" * 64}], [item["digest"] for item in dependencies[1:]])
        byproduct = value["runDetails"]["byproducts"][0]
        self.assertEqual("attested-by", byproduct["name"])
        self.assertIn("/actions/runs/99/attempts/1", byproduct["uri"])
        self.assertNotIn("publish.yml", value["runDetails"]["builder"]["id"])

    def test_a_record_that_is_not_this_manifests_protected_main_build_is_refused(self) -> None:
        cases = {
            "pull request": (self.record(event="pull_request"), self.MANIFEST),
            "another branch": (self.record(headBranch="feature"), self.MANIFEST),
            "another commit": (self.record(), {**self.MANIFEST, "commit": "d" * 40}),
            "another run": (self.record(), {**self.MANIFEST, "source": {"verificationRunId": "1"}}),
            "no digests": ({**self.record(), "artifacts": []}, self.MANIFEST),
        }
        for label, (record, manifest) in cases.items():
            with self.subTest(label=label), self.assertRaises(ProvenanceError):
                provenance.predicate(record, manifest, self.PUBLISHER)


if __name__ == "__main__":
    unittest.main()
