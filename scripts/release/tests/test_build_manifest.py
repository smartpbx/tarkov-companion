"""Fixture tests for scripts/release/build_manifest.py.

The fixture payload copies the shape of a real protected-main verification run (run 608,
commit cbaf3df): Velopack files under velopack/, a portable checksum written by a Windows runner
as `*/d/a/.../dist/<zip>`, CRLF notices inside the package, and an informational version the SDK
has suffixed with the commit a second time.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock


RELEASE_DIRECTORY = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = RELEASE_DIRECTORY.parents[1]
sys.path.insert(0, str(RELEASE_DIRECTORY))

import build_manifest  # noqa: E402
import build_components  # noqa: E402
from build_manifest import ManifestError  # noqa: E402


VERSION = "1.0.608"
COMMIT = "c" * 40
RUN_ID = "34910997075"
BUILT = "2026-09-15T00:00:31Z"
DESKTOP = "TarkovCompanion-v1.0.0-win-x64.zip"
RELAY = "TarkovCompanion-GroupServer-linux-x64.tar.gz"
NOTICES = b"# Third-party notices\n\nExample notice.\n"
INVENTORY = {"schemaVersion": 1, "packages": [{"id": "Example", "version": "1.0.0"}]}
DEPLOY = {
    "tarkov-group-update.sh": b"#!/usr/bin/env bash\necho updater\n",
    "tarkov-group-update.service": b"[Service]\n",
    "tarkov-group-update.timer": b"[Timer]\n",
    "tarkov-group-update.path": b"[Path]\n",
}


def sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


class BuildManifestTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="tarkov-build-manifest-")
        self.root = Path(self.temporary.name)
        self.source = self.root / "source"
        self.artifacts = self.root / "artifacts"
        self.payload = self.root / "payload"
        self.write_source()
        self.write_artifacts()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    # Fixture ----------------------------------------------------------------------------------

    def write(self, path: Path, value: bytes | str) -> Path:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(value.encode() if isinstance(value, str) else value)
        return path

    def write_source(self) -> None:
        self.write(self.source / "src/TarkovCompanion.Infrastructure/Persistence/Migrations/0001_initial.sql", "")
        self.write(self.source / "src/TarkovCompanion.Infrastructure/Persistence/Migrations/0010_latest.sql", "")
        self.write(self.source / "src/TarkovCompanion.GroupServer/GroupContracts.cs",
                   "public static class GroupProtocol\n{\n    public const int Version = 1;\n}\n")
        self.write(self.source / "src/TarkovCompanion.Core/Abstractions/V2/V2ContractVersion.cs",
                   "public static V2ContractVersion Current { get; } = new(2, 0);\n")
        self.write(self.source / "src/TarkovCompanion.Core/Domain/Quests/QuestExchange.cs",
                   "public const int Version = 2;\n")
        self.write(self.source / "src/TarkovCompanion.Infrastructure/Recognition/Tessdata/eng.traineddata",
                   b"reviewed model bytes")
        self.write(self.source / "docs/THIRD_PARTY_NOTICES.md", NOTICES)
        self.write(self.source / "docs/THIRD_PARTY_INVENTORY.json", json.dumps(INVENTORY, indent=2) + "\n")
        for name, value in DEPLOY.items():
            self.write(self.source / "deploy/group-server" / name, value)

    def desktop_zip(self, **overrides: bytes) -> bytes:
        members = {
            "TarkovCompanion.exe": b"apphost",
            "TarkovCompanion.dll": b"\x00\x01" + f"{VERSION}+{COMMIT}.{COMMIT}".encode() + b"\x00",
            "Avalonia.dll": b"library",
            "BUILD_INFO.txt": f"version={VERSION}\ncommit={COMMIT}\nbuilt_utc={BUILT}\n".encode(),
            "THIRD_PARTY_NOTICES.md": NOTICES.replace(b"\n", b"\r\n"),
            "THIRD_PARTY_INVENTORY.json": json.dumps(INVENTORY).encode(),
            **overrides,
        }
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            for name, value in members.items():
                archive.writestr(name, value)
        return buffer.getvalue()

    def relay_tar(self, *, assembly: bytes | None = None, deploy: dict[str, bytes] | None = None,
                  executable_mode: int = 0o755, link: bool = False) -> bytes:
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w:gz") as archive:
            directory = tarfile.TarInfo(".")
            directory.type = tarfile.DIRTYPE
            archive.addfile(directory)

            def add(name: str, value: bytes, mode: int = 0o644) -> None:
                info = tarfile.TarInfo(f"./{name}")
                info.size = len(value)
                info.mode = mode
                archive.addfile(info, io.BytesIO(value))

            add("TarkovCompanion.GroupServer", b"elf", executable_mode)
            add("TarkovCompanion.GroupServer.dll",
                assembly if assembly is not None else f"{VERSION}+{COMMIT}.{COMMIT}".encode())
            for name, value in (deploy or DEPLOY).items():
                add(f"deploy/{name}", value)
            if link:
                info = tarfile.TarInfo("./escape")
                info.type = tarfile.SYMTYPE
                info.linkname = "/etc/shadow"
                archive.addfile(info)
        return buffer.getvalue()

    @staticmethod
    def nupkg(version: str = VERSION, package_id: str = "TarkovCompanionDesktop") -> bytes:
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr(
                "TarkovCompanionDesktop.nuspec",
                '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">'
                f"<metadata><id>{package_id}</id><version>{version}</version></metadata></package>",
            )
        return buffer.getvalue()

    def write_artifacts(self, *, desktop: bytes | None = None, relay: bytes | None = None,
                        nupkg: bytes | None = None, feed_sha: str | None = None) -> None:
        windows = self.artifacts / "windows"
        velopack = windows / "velopack"
        relay_directory = self.artifacts / "relay"
        desktop = desktop or self.desktop_zip()
        relay = relay or self.relay_tar()
        full = f"TarkovCompanionDesktop-{VERSION}-full.nupkg"
        nupkg = nupkg or self.nupkg()
        files = {
            full: nupkg,
            "TarkovCompanionDesktop-win-Setup.exe": b"setup",
            "TarkovCompanionDesktop-win-Portable.zip": b"portable",
            "releases.win.json": json.dumps({"Assets": [{
                "PackageId": "TarkovCompanionDesktop", "Version": VERSION, "Type": "Full", "FileName": full,
                "SHA256": (feed_sha or sha256(nupkg)).upper(), "Size": len(nupkg),
            }]}).encode(),
            "assets.win.json": json.dumps([
                {"RelativeFileName": full, "Type": "Full"},
                {"RelativeFileName": "TarkovCompanionDesktop-win-Setup.exe", "Type": "Installer"},
            ]).encode(),
            "RELEASES": f"\ufeff{'A' * 40} {full} {len(nupkg)}".encode(),
        }
        for name, value in files.items():
            self.write(velopack / name, value)
        self.write(windows / "VELOPACK-SHA256SUMS.txt",
                   "".join(f"{sha256(value)} *{name}\n" for name, value in sorted(files.items())))
        self.write(windows / DESKTOP, desktop)
        self.write(windows / "SHA256SUMS.txt", f"{sha256(desktop)} */d/a/tarkov-companion/tarkov-companion/dist/{DESKTOP}\n")
        self.write(windows / "update.json", json.dumps({
            "version": VERSION, "commit": COMMIT, "builtUtc": BUILT, "asset": DESKTOP,
            "sha256": sha256(desktop), "branch": "main", "run": RUN_ID,
        }, indent=2))
        component_data = json.dumps({
            "schemaVersion": 1, "version": VERSION, "commit": COMMIT, "builtUtc": BUILT,
            "contracts": {"databaseSchema": "0010", "relayProtocol": 1, "v2Contract": "2.0", "questExchange": 2},
        }, indent=2, sort_keys=True).encode() + b"\n"
        component_model = (self.source / "src/TarkovCompanion.Infrastructure/Recognition/Tessdata/eng.traineddata").read_bytes()
        component_files = {
            f"TarkovCompanion-data-{VERSION}.json": component_data,
            f"TarkovCompanion-model-eng-{VERSION}.traineddata": component_model,
        }
        for name, value in component_files.items():
            self.write(windows / name, value)
        self.write(windows / "COMPONENT-SHA256SUMS.txt",
                   "".join(f"{sha256(value)}  {name}\n" for name, value in sorted(component_files.items())))
        self.write(relay_directory / RELAY, relay)
        self.write(relay_directory / "GROUPSERVER-SHA256SUMS.txt", f"{sha256(relay)}  {RELAY}\n")

    def collect(self) -> None:
        build_manifest.collect([self.artifacts / "windows", self.artifacts / "relay"], self.payload)

    def reconcile(self) -> dict:
        self.collect()
        return build_manifest.reconcile(self.payload, self.source, COMMIT, RUN_ID)

    def add_publisher_files(self, *, health: dict | None = None, sbom: dict | None = None) -> None:
        self.write(self.payload / "THIRD_PARTY_NOTICES.md", (self.source / "docs/THIRD_PARTY_NOTICES.md").read_bytes())
        self.write(self.payload / "THIRD_PARTY_INVENTORY.json", (self.source / "docs/THIRD_PARTY_INVENTORY.json").read_bytes())
        self.write(self.payload / "TarkovCompanion.spdx.json", json.dumps(
            sbom if sbom is not None else {"spdxVersion": "SPDX-2.3", "packages": [{"name": "Avalonia", "externalRefs": [
                {"referenceCategory": "PACKAGE-MANAGER", "referenceType": "purl", "referenceLocator": "pkg:nuget/Avalonia@12.1.2"}]}]}
        ))
        relay_sha = sha256((self.payload / RELAY).read_bytes())
        self.write(self.payload / "relay-package-health.json", json.dumps(health if health is not None else {
            "schemaVersion": 1, "status": "ok", "version": VERSION, "commit": COMMIT, "protocol": 1,
            "archiveSha256": relay_sha,
        }))

    def artifact_record(self, **run_overrides: object) -> Path:
        """What artifacts.py writes after fetching both archives by their recorded digests."""
        artifacts = []
        for name, directory in (("windows-release-payload", self.artifacts / "windows"), ("group-server-release", self.artifacts / "relay")):
            files = [
                {"path": path.relative_to(directory).as_posix(), "sha256": sha256(path.read_bytes()), "size": path.stat().st_size}
                for path in sorted(directory.rglob("*")) if path.is_file()
            ]
            artifacts.append({"name": name, "id": len(artifacts) + 1, "sha256": "a" * 64, "size": 10, "files": files})
        run = {"id": int(RUN_ID), "attempt": 2, "workflowPath": ".github/workflows/windows-verify.yml", "event": "push",
               "headBranch": "main", "headSha": COMMIT, **run_overrides}
        record = self.root / f"artifact-record-{len(list(self.root.glob('artifact-record-*.json')))}.json"
        record.write_text(json.dumps({"schemaVersion": 1, "repository": "smartpbx/tarkov-companion",
                                      "verificationRun": run, "artifacts": artifacts}), encoding="utf-8")
        return record

    def manifest(self, record: Path | None = None) -> dict:
        return build_manifest.create_manifest(argparse.Namespace(
            payload=self.payload, source_root=self.source, commit=COMMIT, verification_run_id=RUN_ID,
            repository="smartpbx/tarkov-companion", artifact_record=record or self.artifact_record(),
        ))

    # Collection and checksums -----------------------------------------------------------------

    def test_collection_flattens_velopack_and_refuses_ambiguity(self) -> None:
        self.collect()
        self.assertTrue((self.payload / "TarkovCompanionDesktop-win-Setup.exe").is_file())

        self.write(self.artifacts / "relay/update.json", b"{}")
        with self.assertRaises(ManifestError):
            build_manifest.collect([self.artifacts / "windows", self.artifacts / "relay"], self.root / "again")

    def test_collection_refuses_links_and_deep_nesting(self) -> None:
        (self.artifacts / "relay/link").symlink_to(self.artifacts / "relay" / RELAY)
        with self.assertRaises(ManifestError):
            build_manifest.collect([self.artifacts / "relay"], self.root / "links")
        os.unlink(self.artifacts / "relay/link")

        self.write(self.artifacts / "relay/a/b/deep.txt", b"deep")
        with self.assertRaises(ManifestError):
            build_manifest.collect([self.artifacts / "relay"], self.root / "deep")

    def test_release_directory_enumeration_stops_at_its_bound(self) -> None:
        directory = self.root / "too-many"
        directory.mkdir()
        for index in range(5):
            self.write(directory / f"{index}.txt", b"x")

        with mock.patch.object(build_manifest, "MAX_ARTIFACT_COUNT", 1):
            with self.assertRaisesRegex(ManifestError, "directory-entry limit"):
                build_manifest.directory_entries(directory, "fixture")

    def test_checksum_names_are_reduced_to_safe_file_names(self) -> None:
        sums = self.write(self.root / "sums.txt", f"{'1' * 64} */d/a/x/dist/{DESKTOP}\n{'2' * 64} *RELEASES\n{'3' * 64}  C:\\\\dist\\\\{RELAY}\n")
        self.assertEqual({DESKTOP: "1" * 64, "RELEASES": "2" * 64, RELAY: "3" * 64}, build_manifest.parse_sums(sums))

        for bad in (f"{'1' * 64}  ../escape\n", f"{'1' * 64}  a\n{'2' * 64}  a\n", "nonsense\n", ""):
            self.write(sums, bad)
            with self.subTest(bad=bad), self.assertRaises(ManifestError):
                build_manifest.parse_sums(sums)

    # Reconciliation --------------------------------------------------------------------------

    def test_component_builder_versions_and_copies_reviewed_inputs(self) -> None:
        output = self.root / "components"

        paths = build_components.build(self.source, output, self.artifacts / "windows/update.json")

        self.assertEqual(3, len(paths))
        self.assertEqual(
            (self.source / "src/TarkovCompanion.Infrastructure/Recognition/Tessdata/eng.traineddata").read_bytes(),
            (output / f"TarkovCompanion-model-eng-{VERSION}.traineddata").read_bytes(),
        )
        data = json.loads((output / f"TarkovCompanion-data-{VERSION}.json").read_text())
        self.assertEqual(COMMIT, data["commit"])
        self.assertEqual("0010", data["contracts"]["databaseSchema"])

    def test_component_builder_refuses_a_version_that_could_escape_its_directory(self) -> None:
        update = json.loads((self.artifacts / "windows/update.json").read_text())
        update["version"] = "../../outside"
        path = self.write(self.root / "hostile-update.json", json.dumps(update))

        with self.assertRaises(ValueError):
            build_components.build(self.source, self.root / "hostile-components", path)

        self.assertFalse((self.root / "hostile-components").exists())

    def test_a_coherent_payload_reconciles_every_version(self) -> None:
        identity = self.reconcile()

        self.assertEqual(VERSION, identity["version"])
        self.assertEqual(f"{VERSION}+{COMMIT}", identity["informationalVersion"])
        self.assertEqual({"databaseSchema": "0010", "relayProtocol": 1, "v2Contract": "2.0", "questExchange": 2},
                         identity["sources"])
        self.assertEqual({"desktop", "relay"}, {item["component"] for item in identity["binaries"]})
        self.assertNotIn("deploy/tarkov-group-update.sh", {item["path"] for item in identity["binaries"]})

    def test_the_manifest_names_every_file_with_its_role(self) -> None:
        self.reconcile()
        self.add_publisher_files()

        manifest = self.manifest()

        roles = {item["name"]: (item["component"], item["role"]) for item in manifest["artifacts"]}
        self.assertEqual(("relay", "archive"), roles[RELAY])
        self.assertEqual(("desktop", "installer"), roles["TarkovCompanionDesktop-win-Setup.exe"])
        self.assertEqual(("desktop", "full-package"), roles[f"TarkovCompanionDesktop-{VERSION}-full.nupkg"])
        self.assertEqual(("release", "sbom"), roles["TarkovCompanion.spdx.json"])
        self.assertIn("BINARY-SHA256SUMS.txt", roles)
        self.assertEqual(f"{VERSION}+{COMMIT}", manifest["versions"]["assemblyInformational"])
        self.assertEqual([f"TarkovCompanion-data-{VERSION}.json"], manifest["feeds"]["data"]["artifacts"])
        self.assertEqual([f"TarkovCompanion-model-eng-{VERSION}.traineddata"], manifest["feeds"]["model"]["artifacts"])
        self.assertNotIn(f"TarkovCompanion-data-{VERSION}.json", manifest["feeds"]["binary"]["artifacts"])
        for item in manifest["artifacts"]:
            self.assertEqual(sha256((self.payload / item["name"]).read_bytes()), item["sha256"])
        self.assertEqual(2, manifest["source"]["verificationRunAttempt"])
        self.assertEqual(["windows-release-payload", "group-server-release"],
                         [item["name"] for item in manifest["source"]["verificationArtifacts"]])

    def test_the_manifest_binds_every_produced_file_to_the_uploaded_artifacts(self) -> None:
        self.reconcile()
        self.add_publisher_files()
        record = self.artifact_record()
        honest = json.loads(record.read_text())

        cases = {
            "another commit": self.artifact_record(headSha="d" * 40),
            "another run": self.artifact_record(id=1),
            "a pull request run": self.artifact_record(event="pull_request"),
        }
        altered = json.loads(json.dumps(honest))
        altered["artifacts"][1]["files"][0]["sha256"] = "0" * 64
        cases["a file the upload did not contain"] = self.root / "altered.json"
        cases["a file the upload did not contain"].write_text(json.dumps(altered))
        undigested = json.loads(json.dumps(honest))
        undigested["artifacts"][0]["sha256"] = None
        cases["an artifact without a recorded digest"] = self.root / "undigested.json"
        cases["an artifact without a recorded digest"].write_text(json.dumps(undigested))

        for label, path in cases.items():
            with self.subTest(label=label), self.assertRaises(ManifestError):
                self.manifest(path)

    def test_identity_disagreements_are_refused(self) -> None:
        cases = {
            "tampered archive": lambda: self.write(self.artifacts / "windows" / DESKTOP, self.desktop_zip(**{"extra.dll": b"x"})),
            "build info": lambda: self.write_artifacts(desktop=self.desktop_zip(**{
                "BUILD_INFO.txt": f"version=1.0.607\ncommit={COMMIT}\nbuilt_utc={BUILT}\n".encode()})),
            "desktop assembly": lambda: self.write_artifacts(desktop=self.desktop_zip(**{"TarkovCompanion.dll": b"1.0.607+" + COMMIT.encode()})),
            "relay assembly": lambda: self.write_artifacts(relay=self.relay_tar(assembly=b"1.0.0+" + COMMIT.encode())),
            "notices": lambda: self.write_artifacts(desktop=self.desktop_zip(**{"THIRD_PARTY_NOTICES.md": b"# Different\r\n"})),
            "inventory": lambda: self.write_artifacts(desktop=self.desktop_zip(**{"THIRD_PARTY_INVENTORY.json": b'{"packages": []}'})),
            "shipped updater": lambda: self.write_artifacts(relay=self.relay_tar(deploy={**DEPLOY, "tarkov-group-update.sh": b"#!/bin/sh\n"})),
            "relay link": lambda: self.write_artifacts(relay=self.relay_tar(link=True)),
            "relay not executable": lambda: self.write_artifacts(relay=self.relay_tar(executable_mode=0o644)),
            "package version": lambda: self.write_artifacts(nupkg=self.nupkg("1.0.607")),
            "package id": lambda: self.write_artifacts(nupkg=self.nupkg(package_id="SomethingElse")),
            "feed digest": lambda: self.write_artifacts(feed_sha="0" * 64),
            "extra file": lambda: self.write(self.artifacts / "relay/unaccounted.bin", b"?"),
        }
        for label, mutate in cases.items():
            with self.subTest(label=label):
                self.tearDown()
                self.setUp()
                mutate()
                with self.assertRaises(ManifestError):
                    self.reconcile()

    def test_the_verification_run_must_be_protected_main_at_the_selected_commit(self) -> None:
        self.collect()
        for label, arguments in {
            "commit": (self.payload, self.source, "d" * 40, RUN_ID),
            "run": (self.payload, self.source, COMMIT, "1"),
        }.items():
            with self.subTest(label=label), self.assertRaises(ManifestError):
                build_manifest.reconcile(*arguments)
        update = json.loads((self.payload / "update.json").read_text())
        (self.payload / "update.json").write_text(json.dumps(dict(update, branch="feature")), encoding="utf-8")
        with self.assertRaises(ManifestError):
            build_manifest.reconcile(self.payload, self.source, COMMIT, RUN_ID)

    def test_the_manifest_requires_functional_relay_evidence_and_a_real_sbom(self) -> None:
        cases = {
            "protocol": {"health": {"schemaVersion": 1, "status": "ok", "version": VERSION, "commit": COMMIT, "protocol": 2,
                                    "archiveSha256": None}},
            "archive": {"health": {"schemaVersion": 1, "status": "ok", "version": VERSION, "commit": COMMIT, "protocol": 1,
                                   "archiveSha256": "0" * 64}},
            "empty sbom": {"sbom": {"spdxVersion": "SPDX-2.3", "packages": []}},
            "sbom of packed archives": {"sbom": {"spdxVersion": "SPDX-2.3", "packages": [{"name": "packed"}]}},
        }
        for label, overrides in cases.items():
            with self.subTest(label=label):
                self.tearDown()
                self.setUp()
                self.reconcile()
                self.add_publisher_files(**overrides)
                record = self.artifact_record()
                with self.assertRaises(ManifestError):
                    self.manifest(record)

    def test_sbom_input_unpacks_both_archives(self) -> None:
        identity = self.reconcile()

        build_manifest.extract_sbom_input(self.payload, self.root / "sbom", identity)

        self.assertTrue((self.root / "sbom/desktop/TarkovCompanion.dll").is_file())
        self.assertTrue((self.root / "sbom/relay/TarkovCompanion.GroupServer.dll").is_file())

    def test_the_real_source_tree_still_exposes_every_versioned_contract(self) -> None:
        # A ratchet on the parser, not on the values: renaming GroupProtocol or moving the v2
        # contract must fail here, on the pull request, rather than at the first release.
        versions = build_manifest.source_versions(REPOSITORY_ROOT)

        self.assertRegex(versions["databaseSchema"], r"^[0-9]{4}$")
        self.assertIsInstance(versions["relayProtocol"], int)
        self.assertRegex(versions["v2Contract"], r"^[0-9]+\.[0-9]+$")
        self.assertIsInstance(versions["questExchange"], int)


if __name__ == "__main__":
    unittest.main()
