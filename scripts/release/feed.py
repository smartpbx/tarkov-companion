#!/usr/bin/env python3
"""Read and write the private, authenticated v2 release feed.

The feed is a separate private repository. It holds two kinds of thing, deliberately stored
differently:

* An immutable build is a GitHub release tagged ``v2-build-<version>``. It is created as a
  draft, every asset is uploaded, GitHub's own digest for every asset is compared with the
  signed manifest, and only then is it published. Nothing names it until that has succeeded.
* A ring decision is a file, ``rings/<ring>/release-index-g<generation>.json``, created through
  the contents API without a parent blob. Creating a path that exists is refused by GitHub,
  so two writers that both read generation N cannot both write N+1, and no reader can ever see
  half a decision. Release assets were not usable for this: ``gh release upload --clobber``
  deletes the old asset before uploading the new one, which is exactly an empty feed for as
  long as the upload takes, or for ever if it fails.

Nothing here verifies a signature. The workflow and the updater do that with cosign against a
separately provisioned trust root; this module only moves bytes and refuses unsafe states.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path
from typing import Any, Callable, Sequence

sys.path.insert(0, str(Path(__file__).resolve().parent))

from release_policy import RINGS, SEMVER, generation_of, index_name, select_current  # noqa: E402


REPOSITORY = re.compile(r"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")
HTTP_STATUS = re.compile(r"\(HTTP ([0-9]{3})\)")
KEEP_GENERATIONS = 200
# Artifacts that may legitimately differ when the same verification run is published twice.
# The SBOM carries its own creation time and document namespace; everything else is derived
# deterministically from the verified bytes and must be identical.
REGENERATED_EVIDENCE = frozenset({"TarkovCompanion.spdx.json"})

Runner = Callable[[Sequence[str], bytes | None], subprocess.CompletedProcess]


class FeedError(RuntimeError):
    """The feed is unreachable, misconfigured, or in a state this publisher must not change."""

    def __init__(self, message: str, status: int | None = None) -> None:
        super().__init__(message)
        self.status = status


class FeedConflict(FeedError):
    """Another writer changed the ring first. Safe to recompute from the new state and retry."""


def run_process(arguments: Sequence[str], stdin: bytes | None) -> subprocess.CompletedProcess:
    return subprocess.run(list(arguments), input=stdin, capture_output=True, check=False)


def sha256_file(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exception:
        raise FeedError(f"could not read {path}: {exception}") from exception


class Feed:
    def __init__(self, repository: str, runner: Runner = run_process) -> None:
        if REPOSITORY.fullmatch(repository or "") is None:
            raise FeedError("the release repository must be owner/name")
        self.repository = repository
        self._run = runner

    def _gh(self, arguments: Sequence[str], stdin: bytes | None = None) -> bytes:
        result = self._run(["gh", *arguments], stdin)
        if result.returncode != 0:
            error = (result.stderr or b"").decode("utf-8", "replace").strip()
            match = HTTP_STATUS.search(error)
            raise FeedError(error or f"gh {arguments[0]} failed", int(match.group(1)) if match else None)
        return result.stdout or b""

    def _api_json(self, arguments: Sequence[str], stdin: bytes | None = None) -> Any:
        output = self._gh(["api", *arguments], stdin)
        try:
            return json.loads(output) if output.strip() else None
        except json.JSONDecodeError as exception:
            raise FeedError(f"GitHub returned unreadable JSON for {arguments[-1]}") from exception

    def _api_lines(self, arguments: Sequence[str]) -> list[Any]:
        output = self._gh(["api", "--paginate", *arguments])
        try:
            return [json.loads(line) for line in output.decode("utf-8").splitlines() if line.strip()]
        except json.JSONDecodeError as exception:
            raise FeedError(f"GitHub returned unreadable JSON for {arguments[0]}") from exception

    # Repository ------------------------------------------------------------------------------

    def require_private(self, source_repository: str) -> str:
        if self.repository.lower() == source_repository.lower():
            raise FeedError("the public source repository cannot be the authenticated v2 feed")
        value = self._gh(["api", f"repos/{self.repository}", "--jq", ".visibility"]).decode("utf-8").strip()
        if value not in ("private", "internal"):
            raise FeedError(f"the v2 feed repository is {value!r}, not private or internal")
        return value

    def require_immutable_releases(self) -> dict[str, Any]:
        """The feed's own setting, read live before anything is written, not assumed from a runbook.

        A published build is the thing a signed manifest names by digest; if its assets can be
        replaced, a consumer that already verified one download can be served different bytes on
        the next, and a rollback to it may no longer find what was signed. Reading this setting
        needs the token's Administration read permission on the feed. A token without it fails
        here, as does a feed with the setting off.
        """
        try:
            value = self._api_json([f"repos/{self.repository}/immutable-releases"])
        except FeedError as exception:
            if exception.status == 404:
                raise FeedError("immutable releases are not enabled on the v2 feed repository") from exception
            raise FeedError(f"could not read whether the v2 feed enforces immutable releases: {exception}") from exception
        if not isinstance(value, dict) or value.get("enabled") is not True:
            raise FeedError("immutable releases are not enabled on the v2 feed repository")
        return value

    # Rings ------------------------------------------------------------------------------------

    def _ring_path(self, ring: str) -> str:
        if ring not in RINGS:
            raise FeedError(f"unknown ring {ring!r}")
        return f"rings/{ring}"

    def ring_entries(self, ring: str) -> list[dict[str, Any]] | None:
        try:
            entries = self._api_json([f"repos/{self.repository}/contents/{self._ring_path(ring)}"])
        except FeedError as exception:
            if exception.status == 404:
                return None
            raise
        if not isinstance(entries, list):
            raise FeedError(f"rings/{ring} is not a directory")
        return [entry for entry in entries if isinstance(entry, dict) and entry.get("type") == "file"]

    def ring_state(self, ring: str) -> tuple[int, str | None]:
        entries = self.ring_entries(ring)
        if entries is None:
            # An absent directory is a new ring only if it never existed. A ring whose files were
            # deleted would otherwise restart at generation one with no high-water mark, and the
            # next promotion could quietly take it backwards.
            history = self._api_json([f"repos/{self.repository}/commits?path={self._ring_path(ring)}&per_page=1"])
            if history:
                raise FeedError(f"rings/{ring} has history but no files; refusing to start it again")
            return 0, None
        generation, name = select_current([str(entry.get("name")) for entry in entries])
        if generation == 0 and entries:
            raise FeedError(f"rings/{ring} holds files but no valid ring decision")
        return generation, name

    def read_ring(self, ring: str, output: Path) -> dict[str, Any]:
        generation, name = self.ring_state(ring)
        output.mkdir(parents=True, exist_ok=True)
        if name is not None:
            (output / "envelope.json").write_bytes(
                self._gh(["api", "-H", "Accept: application/vnd.github.raw+json",
                          f"repos/{self.repository}/contents/{self._ring_path(ring)}/{name}"])
            )
        state = {"ring": ring, "generation": generation, "name": name}
        (output / "state.json").write_text(json.dumps(state, sort_keys=True) + "\n", encoding="utf-8")
        return state

    def commit_ring(self, ring: str, envelope: Path, generation: int, keep: int = KEEP_GENERATIONS) -> dict[str, Any]:
        name = index_name(generation)
        path = f"{self._ring_path(ring)}/{name}"
        current, _ = self.ring_state(ring)
        if current != generation - 1:
            raise FeedConflict(f"{ring} is at generation {current}; generation {generation} was computed from {generation - 1}")
        body = json.dumps({
            "message": f"{ring}: signed release decision generation {generation}",
            "content": base64.b64encode(envelope.read_bytes()).decode("ascii"),
        }).encode("utf-8")
        try:
            self._api_json(["--method", "PUT", f"repos/{self.repository}/contents/{path}", "--input", "-"], body)
        except FeedError as exception:
            # 422 is the path already existing; 409 is the branch moving under the commit. Both
            # mean somebody else wrote first, and neither changed anything this run relied on.
            if exception.status in (409, 422):
                raise FeedConflict(f"another writer created {ring} generation {generation} first") from exception
            raise
        written = self._gh(["api", "-H", "Accept: application/vnd.github.raw+json",
                            f"repos/{self.repository}/contents/{path}"])
        if written != envelope.read_bytes():
            raise FeedError(f"{path} does not read back as the envelope that was written")
        pruned = self._prune(ring, generation, keep)
        return {"ring": ring, "generation": generation, "name": name, "pruned": pruned}

    def _prune(self, ring: str, generation: int, keep: int) -> list[str]:
        # Old decisions go only after the new one is readable, and never the newest `keep`. The
        # newest file is what readers select, so this cannot empty a ring or move it backwards;
        # it only keeps the directory under the contents API's listing limit. A failure is
        # reported and left for the next write rather than failing a release that succeeded.
        pruned: list[str] = []
        try:
            for entry in self.ring_entries(ring) or []:
                entry_generation = generation_of(str(entry.get("name")))
                if entry_generation is None or entry_generation > generation - keep:
                    continue
                body = json.dumps({"message": f"{ring}: prune generation {entry_generation}", "sha": entry["sha"]}).encode()
                self._api_json(["--method", "DELETE", f"repos/{self.repository}/contents/{entry['path']}", "--input", "-"], body)
                pruned.append(str(entry["name"]))
        except (FeedError, KeyError) as exception:
            print(f"::warning::pruning old {ring} decisions stopped: {exception}", file=sys.stderr)
        return pruned

    # Builds -----------------------------------------------------------------------------------

    def find_build(self, tag: str) -> dict[str, Any] | None:
        if not tag.startswith("v2-build-") or SEMVER.fullmatch(tag[len("v2-build-"):]) is None:
            raise FeedError(f"{tag!r} is not a build tag")
        matches = [
            release for release in self._api_lines([
                f"repos/{self.repository}/releases?per_page=100",
                "--jq", ".[] | {id, tag_name, draft, immutable} | @json",
            ])
            if isinstance(release, dict) and release.get("tag_name") == tag
        ]
        published = [release for release in matches if not release.get("draft")]
        if len(published) > 1:
            raise FeedError(f"more than one published release is tagged {tag}")
        if published:
            return {**published[0], "status": "published", "drafts": [item["id"] for item in matches if item.get("draft")]}
        if matches:
            return {"status": "draft", "drafts": [item["id"] for item in matches]}
        return None

    def assets(self, release_id: int) -> dict[str, dict[str, Any]]:
        listing = self._api_lines([
            f"repos/{self.repository}/releases/{release_id}/assets?per_page=100",
            "--jq", ".[] | {name, size, digest, state} | @json",
        ])
        result: dict[str, dict[str, Any]] = {}
        for asset in listing:
            if not isinstance(asset, dict) or asset.get("name") in result:
                raise FeedError(f"release {release_id} has a malformed or repeated asset")
            result[str(asset["name"])] = asset
        return result

    def download_build_files(self, tag: str, names: Sequence[str], output: Path) -> None:
        output.mkdir(parents=True, exist_ok=True)
        for name in names:
            self._gh(["release", "download", tag, "--repo", self.repository, "--pattern", name,
                      "--dir", str(output), "--clobber"])

    def verify_build_assets(self, release_id: int, expected: dict[str, tuple[str | None, int | None]]) -> None:
        assets = self.assets(release_id)
        if set(assets) != set(expected):
            missing = sorted(set(expected) - set(assets))
            extra = sorted(set(assets) - set(expected))
            raise FeedError(f"release {release_id} assets differ: missing {missing}, unexpected {extra}")
        for name, (digest, size) in expected.items():
            asset = assets[name]
            if asset.get("state") != "uploaded":
                raise FeedError(f"{name} is not fully uploaded")
            if digest is not None and asset.get("digest") != f"sha256:{digest}":
                raise FeedError(f"GitHub's digest for {name} is {asset.get('digest')!r}, not sha256:{digest}")
            if size is not None and asset.get("size") != size:
                raise FeedError(f"GitHub's size for {name} is {asset.get('size')!r}, not {size}")

    def publish_build(self, tag: str, directory: Path, manifest_path: Path) -> dict[str, Any]:
        manifest = read_json(manifest_path)
        if tag != f"v2-build-{manifest.get('version')}":
            raise FeedError("the build tag does not name the manifest version")
        expected = expected_build_assets(directory, manifest, manifest_path)
        existing = self.find_build(tag)
        if existing and existing["status"] == "published":
            raise FeedError(f"{tag} is already published; adopt or reject it instead of publishing again")
        for draft in (existing or {}).get("drafts", []):
            # A draft is what an interrupted earlier attempt leaves behind. It is invisible to
            # read-only feed credentials and nothing can name it, so replacing it is safe.
            self._gh(["api", "--method", "DELETE", f"repos/{self.repository}/releases/{draft}"])
        files = [str(directory / name) for name in sorted(expected)]
        self._gh(["release", "create", tag, "--repo", self.repository, "--draft",
                  "--title", f"Tarkov Companion {manifest['version']}",
                  "--notes", f"Immutable signed internal build of {manifest['commit']}. "
                             "Ring decisions under rings/ say which builds are offered.",
                  *files])
        created = self.find_build(tag)
        if not created or created["status"] != "draft" or len(created["drafts"]) != 1:
            raise FeedError(f"expected exactly one new draft for {tag}")
        release_id = created["drafts"][0]
        self.verify_build_assets(release_id, expected)
        published = self._api_json(["--method", "PATCH", f"repos/{self.repository}/releases/{release_id}",
                                    "-F", "draft=false"])
        if not isinstance(published, dict) or published.get("draft") is not False or published.get("tag_name") != tag:
            raise FeedError(f"GitHub did not publish {tag} as requested")
        # The repository setting was read before the upload; this is GitHub saying the release
        # that now exists is itself immutable. No ring decision may name a build that is not.
        if published.get("immutable") is not True:
            raise FeedError(f"GitHub published {tag} but does not report it immutable; no ring decision will name it")
        self.verify_build_assets(release_id, expected)
        return {"tag": tag, "releaseId": release_id, "immutable": published.get("immutable"), "assets": len(expected)}


def expected_build_assets(directory: Path, manifest: dict[str, Any], manifest_path: Path) -> dict[str, tuple[str | None, int | None]]:
    """Every file a build release must hold: each artifact, its bundle, the manifest and its bundle."""
    expected: dict[str, tuple[str | None, int | None]] = {}
    artifacts = manifest.get("artifacts")
    if not isinstance(artifacts, list) or not artifacts:
        raise FeedError("the manifest names no artifacts")
    for artifact in artifacts:
        name = artifact.get("name") if isinstance(artifact, dict) else None
        if not isinstance(name, str) or re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._+-]*", name) is None or ".." in name:
            raise FeedError(f"the manifest names an unsafe artifact: {name!r}")
        expected[name] = (artifact.get("sha256"), artifact.get("size"))
    expected[manifest_path.name] = (sha256_file(manifest_path), manifest_path.stat().st_size)
    for name in list(expected):
        expected[f"{name}.sigstore.json"] = (None, None)
    for name, (digest, size) in expected.items():
        path = directory / name
        if not path.is_file():
            raise FeedError(f"{name} is missing from the signed release directory")
        if digest is not None and sha256_file(path) != digest:
            raise FeedError(f"{name} no longer matches the signed manifest")
        expected[name] = (sha256_file(path), path.stat().st_size)
    return expected


def adoptable(existing: dict[str, Any], candidate: dict[str, Any]) -> None:
    """Whether an already-published build is this candidate, published by an earlier attempt."""
    for key in ("schemaVersion", "version", "commit", "builtUtc", "versions"):
        if existing.get(key) != candidate.get(key):
            raise FeedError(f"the published build disagrees with this candidate about {key}")
    if existing.get("source", {}).get("verificationRunId") != candidate.get("source", {}).get("verificationRunId"):
        raise FeedError("the published build came from a different verification run")

    def comparable(manifest: dict[str, Any]) -> dict[str, tuple[str, str, int]]:
        return {
            item["name"]: (item["component"], item["sha256"], item["size"])
            for item in manifest.get("artifacts", [])
            if item.get("name") not in REGENERATED_EVIDENCE
        }

    if comparable(existing) != comparable(candidate):
        raise FeedError("the published build's artifacts differ from this candidate's")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--repository", required=True)
    subparsers = parser.add_subparsers(dest="command", required=True)

    check = subparsers.add_parser("check-feed")
    check.add_argument("--source-repository", required=True)

    read_ring = subparsers.add_parser("ring-read")
    read_ring.add_argument("--ring", choices=RINGS, required=True)
    read_ring.add_argument("--output-dir", type=Path, required=True)

    commit = subparsers.add_parser("ring-commit")
    commit.add_argument("--ring", choices=RINGS, required=True)
    commit.add_argument("--envelope", type=Path, required=True)
    commit.add_argument("--generation", type=int, required=True)

    inspect = subparsers.add_parser("build-inspect")
    inspect.add_argument("--tag", required=True)
    inspect.add_argument("--output-dir", type=Path, required=True)

    adopt = subparsers.add_parser("build-adopt")
    adopt.add_argument("--tag", required=True)
    adopt.add_argument("--existing-dir", type=Path, required=True)
    adopt.add_argument("--candidate-manifest", type=Path, required=True)

    publish = subparsers.add_parser("build-publish")
    publish.add_argument("--tag", required=True)
    publish.add_argument("--directory", type=Path, required=True)
    publish.add_argument("--manifest", type=Path, required=True)

    args = parser.parse_args()
    try:
        feed = Feed(args.repository)
        if args.command == "check-feed":
            result: Any = {
                "visibility": feed.require_private(args.source_repository),
                "immutableReleases": feed.require_immutable_releases(),
            }
        elif args.command == "ring-read":
            result = feed.read_ring(args.ring, args.output_dir)
        elif args.command == "ring-commit":
            result = feed.commit_ring(args.ring, args.envelope, args.generation)
        elif args.command == "build-inspect":
            found = feed.find_build(args.tag)
            result = {"tag": args.tag, "status": found["status"] if found else "absent"}
            if found and found["status"] == "published":
                result["releaseId"] = found["id"]
                result["immutable"] = found.get("immutable")
                feed.download_build_files(
                    args.tag, ["release-manifest.json", "release-manifest.json.sigstore.json"], args.output_dir
                )
            args.output_dir.mkdir(parents=True, exist_ok=True)
            (args.output_dir / "state.json").write_text(json.dumps(result, sort_keys=True) + "\n", encoding="utf-8")
        elif args.command == "build-adopt":
            # Called only after the existing manifest's signature has been verified.
            existing_path = args.existing_dir / "release-manifest.json"
            existing = read_json(existing_path)
            adoptable(existing, read_json(args.candidate_manifest))
            state = read_json(args.existing_dir / "state.json")
            expected = {
                name: (digest, size)
                for name, (digest, size) in (
                    (item["name"], (item["sha256"], item["size"])) for item in existing["artifacts"]
                )
            }
            expected["release-manifest.json"] = (sha256_file(existing_path), existing_path.stat().st_size)
            for name in list(expected):
                expected[f"{name}.sigstore.json"] = (None, None)
            feed.verify_build_assets(int(state["releaseId"]), expected)
            result = {"tag": args.tag, "adopted": True}
        else:
            result = feed.publish_build(args.tag, args.directory, args.manifest)
    except FeedConflict as exception:
        print(f"release feed conflict: {exception}", file=sys.stderr)
        return 3
    except (FeedError, OSError, KeyError, ValueError) as exception:
        print(f"release feed refused: {exception}", file=sys.stderr)
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
