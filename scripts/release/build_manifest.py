#!/usr/bin/env python3
"""Collect verification artifacts, reconcile every version they claim, and write the manifest.

The manifest is the release unit. Everything a consumer is allowed to install is named in it
with its digest, it is signed once, and the signed ring index names the manifest by digest. So
this is where "the package, assembly, manifest, commit, schema and protocol versions agree"
stops being a sentence and becomes a refusal.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import re
import shutil
import stat
import sys
import tarfile
import zipfile
import xml.etree.ElementTree as ElementTree
from datetime import datetime
from pathlib import Path, PurePosixPath
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))

# One grammar for every version the chain handles. This accepted "1.0.0-01" while the ring
# policy refused it, so such a build would have been signed and attested and then never offered.
from release_policy import SEMVER, semver_key  # noqa: E402
from resource_limits import (  # noqa: E402
    MAX_ARCHIVE_BYTES,
    MAX_ARCHIVE_MEMBERS,
    MAX_ARTIFACT_COUNT,
    MAX_EXPANDED_BYTES,
    MAX_JSON_BYTES,
    MAX_MEMBER_BYTES,
    MIN_FREE_RESERVE_BYTES,
    ResourceLimitError,
    copy_stream,
    decode_json,
    read_bytes as bounded_read_bytes,
    read_json as bounded_read_json,
    sha256_file as bounded_sha256_file,
)


SCHEMA_VERSION = 1
HEX_40 = re.compile(r"^[0-9a-f]{40}$")
HEX_64 = re.compile(r"^[0-9a-f]{64}$")
SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._+-]*$")
DECIMAL_ID = re.compile(r"^[1-9][0-9]{0,19}$")
SOURCE_REPOSITORY = "smartpbx/tarkov-companion"
PACK_ID = "TarkovCompanionDesktop"
DESKTOP_ASSEMBLY = "TarkovCompanion.dll"
RELAY_EXECUTABLE = "TarkovCompanion.GroupServer"
RELAY_ASSEMBLY = "TarkovCompanion.GroupServer.dll"
RELAY_DEPLOYMENT = (
    "tarkov-group-update.sh",
    "tarkov-group-update.service",
    "tarkov-group-update.timer",
    "tarkov-group-update.path",
)
# Files the publisher adds beside what verification produced. Anything in the payload that is
# neither one of these nor named by a verification checksum file is refused, so nothing can be
# signed into a release without somebody having accounted for it.
PUBLISHER_FILES = (
    "THIRD_PARTY_NOTICES.md",
    "THIRD_PARTY_INVENTORY.json",
    "TarkovCompanion.spdx.json",
    "relay-package-health.json",
    "BINARY-SHA256SUMS.txt",
)
VERIFICATION_METADATA = (
    "update.json", "SHA256SUMS.txt", "GROUPSERVER-SHA256SUMS.txt", "VELOPACK-SHA256SUMS.txt",
    "COMPONENT-SHA256SUMS.txt",
)
NOT_ARTIFACTS = re.compile(r"^(release-manifest\.json|release-index.*|.*\.sigstore\.json)$")


class ManifestError(ValueError):
    """The verified payload is incomplete or internally inconsistent."""


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    return bounded_sha256_file(path, path.name, MAX_ARCHIVE_BYTES)


def read_json_bytes(value: bytes, label: str) -> Any:
    try:
        return decode_json(value, label)
    except ResourceLimitError as exception:
        raise ManifestError(f"{label} is not valid JSON: {exception}") from exception


def read_json(path: Path) -> Any:
    try:
        return bounded_read_json(path)
    except (OSError, ResourceLimitError) as exception:
        raise ManifestError(f"could not read {path}: {exception}") from exception


def archive_file(path: Path, label: str) -> None:
    if path.is_symlink() or not path.is_file():
        raise ManifestError(f"{label} is absent or redirected")
    size = path.stat().st_size
    if size <= 0 or size > MAX_ARCHIVE_BYTES:
        raise ManifestError(f"{label} is {size} bytes, outside the archive size limit")


def directory_entries(path: Path, label: str) -> list[Path]:
    """Enumerate a release directory without first allocating an attacker-sized list."""
    result: list[Path] = []
    try:
        for entry in path.iterdir():
            if len(result) >= MAX_ARTIFACT_COUNT * 4:
                raise ManifestError(f"{label} exceeds its directory-entry limit")
            result.append(entry)
    except OSError as exception:
        raise ManifestError(f"could not enumerate {label}: {exception}") from exception
    return result


def zip_members(archive: zipfile.ZipFile, label: str) -> list[zipfile.ZipInfo]:
    members = archive.infolist()
    if len(members) > MAX_ARCHIVE_MEMBERS:
        raise ManifestError(f"{label} has {len(members)} entries, above the {MAX_ARCHIVE_MEMBERS}-entry limit")
    expanded = 0
    for member in members:
        if member.file_size < 0 or member.file_size > MAX_MEMBER_BYTES:
            raise ManifestError(f"{label} entry {member.filename} exceeds the per-file size limit")
        expanded += member.file_size
        if expanded > MAX_EXPANDED_BYTES:
            raise ManifestError(f"{label} exceeds the {MAX_EXPANDED_BYTES}-byte expanded-size limit")
    return members


def read_zip_member(archive: zipfile.ZipFile, name: str, label: str) -> bytes:
    try:
        member = archive.getinfo(name)
    except KeyError as exception:
        raise ManifestError(f"{label} lacks {name}") from exception
    output = io.BytesIO()
    with archive.open(member) as source:
        actual = copy_stream(source, output, maximum=min(member.file_size, MAX_MEMBER_BYTES),
                             label=f"{label} entry {name}")
    if actual != member.file_size:
        raise ManifestError(f"{label} entry {name} expanded to {actual} bytes, not its declared {member.file_size}")
    return output.getvalue()


def safe_name(name: str) -> bool:
    return SAFE_NAME.fullmatch(name) is not None and ".." not in name


def collect(sources: list[Path], output: Path) -> list[str]:
    """Flatten downloaded artifacts into one directory of uniquely named regular files.

    upload-artifact keeps the directory a glob matched in, so the Velopack files arrive under
    velopack/ while their checksum file names them bare. The previous publisher only looked at
    the top level and would have signed a release with no installer in it.
    """
    output.mkdir(parents=True, exist_ok=True)
    collected: list[str] = []
    total_bytes = 0
    for source in sources:
        root = source.resolve()
        for path in sorted(root.rglob("*")):
            relative = path.relative_to(root)
            if path.is_symlink():
                raise ManifestError(f"verification artifact contains a link: {relative}")
            if path.is_dir():
                if len(relative.parts) > 1:
                    raise ManifestError(f"verification artifact is nested too deeply: {relative}")
                continue
            if not path.is_file() or len(relative.parts) > 2:
                raise ManifestError(f"unexpected verification artifact entry: {relative}")
            size = path.stat().st_size
            if size < 0 or size > MAX_MEMBER_BYTES:
                raise ManifestError(f"verification artifact file is outside the per-file size limit: {relative}")
            total_bytes += size
            if len(collected) >= MAX_ARTIFACT_COUNT or total_bytes > MAX_EXPANDED_BYTES:
                raise ManifestError("verification artifacts exceed the file-count or total-size limit")
            name = path.name
            if not safe_name(name):
                raise ManifestError(f"unsafe artifact file name: {relative}")
            target = output / name
            if target.exists():
                raise ManifestError(f"two verification artifacts are both named {name}")
            with path.open("rb") as source_stream, target.open("xb") as target_stream:
                copied = copy_stream(source_stream, target_stream, maximum=MAX_MEMBER_BYTES,
                                     label=f"verification artifact {relative}")
            if copied != size:
                raise ManifestError(f"verification artifact changed while being copied: {relative}")
            collected.append(name)
    if not collected:
        raise ManifestError("verification artifacts are empty")
    return sorted(collected)


def parse_sums(path: Path) -> dict[str, str]:
    """Read sha256sum output, including a Windows runner's `*C:/absolute/path` form."""
    result: dict[str, str] = {}
    try:
        lines = bounded_read_bytes(path, path.name, MAX_JSON_BYTES).decode("utf-8-sig").splitlines()
    except (OSError, ResourceLimitError, UnicodeDecodeError) as exception:
        raise ManifestError(f"could not read checksum file {path.name}: {exception}") from exception
    for line in lines:
        match = re.fullmatch(r"([0-9a-f]{64}) [ *](.+)", line.rstrip("\r"))
        if match is None:
            raise ManifestError(f"invalid checksum line in {path.name}: {line!r}")
        segments = match.group(2).replace("\\", "/").split("/")
        name = segments[-1]
        # A runner's absolute path is reduced to its file name; a relative climb is not a path
        # sha256sum writes, so it is refused rather than quietly reduced to something plausible.
        if ".." in segments or not safe_name(name):
            raise ManifestError(f"{path.name} names an unsafe file: {match.group(2)!r}")
        if name in result:
            raise ManifestError(f"duplicate checksum entry in {path.name}: {name}")
        result[name] = match.group(1)
    if not result:
        raise ManifestError(f"checksum file is empty: {path.name}")
    return result


def verify_sums(payload: Path, name: str) -> dict[str, str]:
    sums = parse_sums(payload / name)
    for item, expected in sums.items():
        target = payload / item
        if not target.is_file():
            raise ManifestError(f"{name} names a missing file: {item}")
        actual = sha256_file(target)
        if actual != expected:
            raise ManifestError(f"{item} digest is {actual}, but {name} says {expected}")
    return sums


def safe_member(name: str) -> bool:
    path = PurePosixPath(name)
    return not path.is_absolute() and ".." not in path.parts and "\\" not in name


def parse_build_info(value: bytes) -> dict[str, str]:
    result: dict[str, str] = {}
    for line in value.decode("utf-8-sig").splitlines():
        if not line.strip():
            continue
        key, separator, item = line.rstrip("\r").partition("=")
        if not separator or key in result:
            raise ManifestError(f"BUILD_INFO.txt has a malformed or repeated line: {line!r}")
        result[key] = item
    if set(result) != {"version", "commit", "built_utc"}:
        raise ManifestError("BUILD_INFO.txt must contain exactly version, commit, and built_utc")
    return result


def normalize_text(value: bytes) -> bytes:
    # Windows runners check the repository out with CRLF, so the notices inside the package are
    # the same document with different line endings. Anything beyond that is a real difference.
    return value.replace(b"\r\n", b"\n")


def require_informational_version(value: bytes, identity: bytes, label: str) -> None:
    # InformationalVersion is stored in the assembly's custom-attribute blob as UTF-8. The SDK
    # appends the source revision again (1.0.N+SHA.SHA), so a prefix match is the honest check.
    if identity not in value:
        raise ManifestError(f"{label} does not carry informational version {identity.decode()}")


def inspect_desktop(archive_path: Path, source_root: Path, version: str, commit: str) -> tuple[dict[str, str], list[dict[str, Any]]]:
    identity = f"{version}+{commit}".encode()
    binaries: list[dict[str, Any]] = []
    archive_file(archive_path, "desktop archive")
    with zipfile.ZipFile(archive_path) as archive:
        members = zip_members(archive, "desktop archive")
        names = [member.filename for member in members]
        if len(names) != len(set(names)):
            raise ManifestError("desktop archive repeats an entry")
        if any(not safe_member(name) for name in names):
            raise ManifestError("desktop archive contains an unsafe path")
        for required in ("BUILD_INFO.txt", "TarkovCompanion.exe", DESKTOP_ASSEMBLY,
                         "THIRD_PARTY_NOTICES.md", "THIRD_PARTY_INVENTORY.json"):
            if required not in names:
                raise ManifestError(f"desktop archive lacks {required}")
        build_info = parse_build_info(read_zip_member(archive, "BUILD_INFO.txt", "desktop archive"))
        require_informational_version(read_zip_member(archive, DESKTOP_ASSEMBLY, "desktop archive"), identity,
                                      f"desktop {DESKTOP_ASSEMBLY}")
        notices = bounded_read_bytes(source_root / "docs/THIRD_PARTY_NOTICES.md", "source notices", MAX_JSON_BYTES)
        if normalize_text(read_zip_member(archive, "THIRD_PARTY_NOTICES.md", "desktop archive")) != normalize_text(notices):
            raise ManifestError("the desktop package ships different third-party notices from its source commit")
        inventory = read_json(source_root / "docs/THIRD_PARTY_INVENTORY.json")
        if read_json_bytes(read_zip_member(archive, "THIRD_PARTY_INVENTORY.json", "desktop archive"),
                           "packaged inventory") != inventory:
            raise ManifestError("the desktop package's dependency inventory differs from the locked inventory")
        for name in sorted(names):
            if name.lower().endswith((".exe", ".dll")):
                value = read_zip_member(archive, name, "desktop archive")
                binaries.append({"component": "desktop", "archive": archive_path.name, "path": name,
                                 "sha256": sha256_bytes(value), "size": len(value)})
    return build_info, binaries


def inspect_relay(archive_path: Path, source_root: Path, version: str, commit: str) -> list[dict[str, Any]]:
    identity = f"{version}+{commit}".encode()
    binaries: list[dict[str, Any]] = []
    seen: set[str] = set()
    executable = False
    archive_file(archive_path, "relay archive")
    expanded = 0
    count = 0
    with tarfile.open(archive_path, "r|gz") as archive:
        for member in archive:
            count += 1
            if count > MAX_ARCHIVE_MEMBERS:
                raise ManifestError(f"relay archive has more than {MAX_ARCHIVE_MEMBERS} entries")
            if not safe_member(member.name) or not (member.isfile() or member.isdir()):
                raise ManifestError(f"relay archive contains an unsafe entry: {member.name}")
            if member.size < 0 or member.size > MAX_MEMBER_BYTES:
                raise ManifestError(f"relay archive entry {member.name} exceeds the per-file size limit")
            expanded += member.size
            if expanded > MAX_EXPANDED_BYTES:
                raise ManifestError(f"relay archive exceeds the {MAX_EXPANDED_BYTES}-byte expanded-size limit")
            name = PurePosixPath(member.name).as_posix().removeprefix("./")
            if name in seen:
                raise ManifestError(f"relay archive repeats an entry: {name}")
            seen.add(name)
            if not member.isfile():
                continue
            if name == RELAY_EXECUTABLE:
                executable = bool(member.mode & stat.S_IXUSR)
            reviewed = name.startswith("deploy/") and name.removeprefix("deploy/") in RELAY_DEPLOYMENT
            if name in (RELAY_EXECUTABLE, RELAY_ASSEMBLY) or name.lower().endswith(".dll") or reviewed:
                stream = archive.extractfile(member)
                if stream is None:
                    raise ManifestError(f"could not read relay archive entry: {name}")
                output = io.BytesIO()
                actual = copy_stream(stream, output, maximum=member.size, label=f"relay archive entry {name}")
                if actual != member.size:
                    raise ManifestError(f"relay archive entry {name} did not match its declared size")
                value = output.getvalue()
                if name == RELAY_ASSEMBLY:
                    require_informational_version(value, identity, f"relay {RELAY_ASSEMBLY}")
                if reviewed:
                    # The updater installs these onto the host after the relay proves itself, so
                    # they must be the reviewed files from this exact commit and nothing else.
                    expected = bounded_read_bytes(
                        source_root / "deploy/group-server" / name.removeprefix("deploy/"),
                        f"source deployment file {name}", MAX_MEMBER_BYTES)
                    if value != expected:
                        raise ManifestError(f"relay archive ships a different {name} from its source commit")
                    continue
                binaries.append({"component": "relay", "archive": archive_path.name, "path": name,
                                 "sha256": sha256_bytes(value), "size": len(value)})
    if not executable:
        raise ManifestError(f"relay archive lacks an executable {RELAY_EXECUTABLE}")
    if RELAY_ASSEMBLY not in seen:
        raise ManifestError(f"relay archive lacks {RELAY_ASSEMBLY}")
    missing = [name for name in RELAY_DEPLOYMENT if f"deploy/{name}" not in seen]
    if missing:
        raise ManifestError(f"relay archive lacks its deployment files: {', '.join(missing)}")
    return binaries


def nuspec_identity(path: Path) -> tuple[str, str]:
    try:
        archive_file(path, path.name)
        with zipfile.ZipFile(path) as archive:
            members = zip_members(archive, path.name)
            nuspecs = [member.filename for member in members if member.filename.lower().endswith(".nuspec")]
            if len(nuspecs) != 1:
                raise ManifestError(f"{path.name} must contain exactly one NuGet specification")
            document = ElementTree.fromstring(read_zip_member(archive, nuspecs[0], path.name))
    except (zipfile.BadZipFile, ElementTree.ParseError) as exception:
        raise ManifestError(f"could not read package identity from {path.name}: {exception}") from exception
    values: dict[str, list[str]] = {"id": [], "version": []}
    for element in document.iter():
        tag = element.tag.rsplit("}", 1)[-1]
        if tag in values and element.text and element.text.strip():
            values[tag].append(element.text.strip())
    if len(values["id"]) != 1 or len(values["version"]) != 1:
        raise ManifestError(f"{path.name} must declare exactly one package id and version")
    return values["id"][0], values["version"][0]


def inspect_velopack(payload: Path, sums: dict[str, str], version: str) -> None:
    installers = [name for name in sums if name.endswith("-Setup.exe")]
    if installers != [f"{PACK_ID}-win-Setup.exe"]:
        raise ManifestError("Velopack checksums must name exactly the TarkovCompanionDesktop installer")
    full = f"{PACK_ID}-{version}-full.nupkg"
    if full not in sums:
        raise ManifestError(f"Velopack checksums do not name the full package {full}")
    for name in (item for item in sums if item.endswith(".nupkg")):
        package_id, package_version = nuspec_identity(payload / name)
        if package_id != PACK_ID:
            raise ManifestError(f"{name} is package {package_id}, not {PACK_ID}")
        # A delta names the version it produces, like the full package does.
        if package_version != version:
            raise ManifestError(f"{name} carries version {package_version}, not {version}")
    if "releases.win.json" not in sums:
        raise ManifestError("Velopack checksums do not name releases.win.json")
    feed = read_json(payload / "releases.win.json")
    assets = feed.get("Assets") if isinstance(feed, dict) else None
    if not isinstance(assets, list) or not assets:
        raise ManifestError("releases.win.json lists no packages")
    for asset in assets:
        name = asset.get("FileName") if isinstance(asset, dict) else None
        if name not in sums or asset.get("PackageId") != PACK_ID or asset.get("Version") != version:
            raise ManifestError(f"releases.win.json names a package outside this release: {name!r}")
        if str(asset.get("SHA256", "")).lower() != sums[name] or asset.get("Size") != (payload / name).stat().st_size:
            raise ManifestError(f"releases.win.json disagrees with the digest or size of {name}")
    if "assets.win.json" in sums:
        listed = read_json(payload / "assets.win.json")
        if not isinstance(listed, list) or any(
            not isinstance(item, dict) or item.get("RelativeFileName") not in sums for item in listed
        ):
            raise ManifestError("assets.win.json names a file outside the verified Velopack output")
    if "RELEASES" in sums:
        try:
            lines = bounded_read_bytes(
                payload / "RELEASES", "Velopack RELEASES metadata", MAX_JSON_BYTES
            ).decode("utf-8-sig").splitlines()
        except (ResourceLimitError, UnicodeDecodeError) as exception:
            raise ManifestError(f"RELEASES is not bounded UTF-8 metadata: {exception}") from exception
        for line in lines:
            parts = line.split()
            if len(parts) != 3 or parts[1] not in sums:
                raise ManifestError(f"RELEASES names a file outside the verified Velopack output: {line!r}")


def inspect_components(payload: Path, source_root: Path, identity: dict[str, Any], sums: dict[str, str]) -> None:
    version = identity["version"]
    data_name = f"TarkovCompanion-data-{version}.json"
    model_name = f"TarkovCompanion-model-eng-{version}.traineddata"
    if set(sums) != {data_name, model_name}:
        raise ManifestError("component checksums must name exactly one versioned data payload and English OCR model")
    data = read_json(payload / data_name)
    expected_data = {
        "schemaVersion": 1,
        "version": version,
        "commit": identity["commit"],
        "builtUtc": identity["builtUtc"],
        "contracts": source_versions(source_root),
    }
    if data != expected_data:
        raise ManifestError("the data component does not describe this release's exact contracts")
    model_source = source_root / "src/TarkovCompanion.Infrastructure/Recognition/Tessdata/eng.traineddata"
    archive_file(payload / model_name, "model component")
    archive_file(model_source, "reviewed model source")
    if sha256_file(payload / model_name) != sha256_file(model_source):
        raise ManifestError("the model component is not the reviewed model from the verified commit")


def source_versions(source_root: Path) -> dict[str, Any]:
    try:
        migrations = sorted(
            (source_root / "src/TarkovCompanion.Infrastructure/Persistence/Migrations").glob("[0-9][0-9][0-9][0-9]_*.sql")
        )
        group_contracts = (source_root / "src/TarkovCompanion.GroupServer/GroupContracts.cs").read_text(encoding="utf-8")
        v2_contract = (source_root / "src/TarkovCompanion.Core/Abstractions/V2/V2ContractVersion.cs").read_text(encoding="utf-8")
        quest_exchange = (source_root / "src/TarkovCompanion.Core/Domain/Quests/QuestExchange.cs").read_text(encoding="utf-8")
    except OSError as exception:
        raise ManifestError(f"could not read a versioned source contract: {exception}") from exception
    protocol = re.search(r"class\s+GroupProtocol\b.*?public\s+const\s+int\s+Version\s*=\s*([0-9]+)\s*;", group_contracts, re.S)
    v2 = re.search(r"Current\s*\{\s*get;\s*\}\s*=\s*new\(\s*([0-9]+)\s*,\s*([0-9]+)\s*\)", v2_contract)
    quest = re.search(r"public\s+const\s+int\s+Version\s*=\s*([0-9]+)\s*;", quest_exchange)
    if not migrations or protocol is None or v2 is None or quest is None:
        raise ManifestError("could not determine the schema, relay protocol, v2 contract, or quest exchange version")
    return {
        "databaseSchema": migrations[-1].stem.split("_", 1)[0],
        "relayProtocol": int(protocol.group(1)),
        "v2Contract": f"{v2.group(1)}.{v2.group(2)}",
        "questExchange": int(quest.group(1)),
    }


def reconcile(payload: Path, source_root: Path, commit: str, verification_run_id: str) -> dict[str, Any]:
    payload = payload.resolve()
    source_root = source_root.resolve()
    if HEX_40.fullmatch(commit) is None:
        raise ManifestError("the verified commit must be a full lowercase Git commit")
    if DECIMAL_ID.fullmatch(verification_run_id) is None:
        raise ManifestError("the verification run id must be a bounded positive decimal identifier")

    update = read_json(payload / "update.json")
    if not isinstance(update, dict):
        raise ManifestError("update.json must be an object")
    version = update.get("version")
    if not isinstance(version, str) or SEMVER.fullmatch(version) is None:
        raise ManifestError(f"update.json has an invalid version: {version!r}")
    try:
        semver_key(version)
    except ValueError as exception:
        raise ManifestError(f"update.json has an unsupported bounded version: {version!r}") from exception
    if update.get("commit") != commit:
        raise ManifestError("update.json commit is not the verified main commit")
    if str(update.get("run")) != verification_run_id:
        raise ManifestError("update.json was not produced by the selected verification run")
    if update.get("branch") != "main":
        raise ManifestError("update.json is not a protected-main package")
    built_utc = update.get("builtUtc")
    try:
        datetime.strptime(str(built_utc), "%Y-%m-%dT%H:%M:%SZ")
    except ValueError as exception:
        raise ManifestError("update.json has no canonical UTC build timestamp") from exception

    portable = verify_sums(payload, "SHA256SUMS.txt")
    relay = verify_sums(payload, "GROUPSERVER-SHA256SUMS.txt")
    velopack = verify_sums(payload, "VELOPACK-SHA256SUMS.txt")
    components = verify_sums(payload, "COMPONENT-SHA256SUMS.txt")
    if len(portable) != 1 or len(relay) != 1:
        raise ManifestError("the portable and relay checksum files must each name exactly one archive")
    desktop_name, desktop_sha = next(iter(portable.items()))
    relay_name, relay_sha = next(iter(relay.items()))
    if update.get("asset") != desktop_name or update.get("sha256") != desktop_sha:
        raise ManifestError("update.json does not identify the verified portable archive")

    accounted = (set(VERIFICATION_METADATA) | set(PUBLISHER_FILES) | set(portable) | set(relay)
                 | set(velopack) | set(components))
    entries = directory_entries(payload, "release payload")
    redirected = sorted(path.name for path in entries if path.is_symlink())
    if redirected:
        raise ManifestError(f"payload holds redirected entries: {', '.join(redirected)}")
    unexpected = sorted(path.name for path in entries if path.name not in accounted and not NOT_ARTIFACTS.match(path.name))
    if unexpected:
        raise ManifestError(f"payload holds files no verification checksum accounts for: {', '.join(unexpected)}")

    build_info, desktop_binaries = inspect_desktop(payload / desktop_name, source_root, version, commit)
    if build_info != {"version": version, "commit": commit, "built_utc": built_utc}:
        raise ManifestError("desktop BUILD_INFO.txt does not agree with update.json")
    relay_binaries = inspect_relay(payload / relay_name, source_root, version, commit)
    inspect_velopack(payload, velopack, version)

    component_identity = {
        "version": version,
        "commit": commit,
        "builtUtc": built_utc,
    }
    inspect_components(payload, source_root, component_identity, components)

    return {
        "version": version,
        "commit": commit,
        "builtUtc": built_utc,
        "verificationRunId": verification_run_id,
        "informationalVersion": f"{version}+{commit}",
        "desktopArchive": desktop_name,
        "relayArchive": relay_name,
        "relayArchiveSha256": relay_sha,
        "sources": source_versions(source_root),
        "binaries": sorted(desktop_binaries + relay_binaries, key=lambda item: (item["archive"], item["path"])),
    }


def artifact_role(name: str, identity: dict[str, Any]) -> tuple[str, str]:
    if name == identity["relayArchive"]:
        return "relay", "archive"
    if name == identity["desktopArchive"]:
        return "desktop", "portable-archive"
    if name.endswith("-Setup.exe"):
        return "desktop", "installer"
    if name.endswith("-full.nupkg"):
        return "desktop", "full-package"
    if name.endswith("-delta.nupkg"):
        return "desktop", "delta-package"
    if name.endswith("-Portable.zip"):
        return "desktop", "velopack-portable"
    if name in ("releases.win.json", "assets.win.json", "RELEASES"):
        return "desktop", "velopack-feed"
    if name.startswith("TarkovCompanion-data-") and name.endswith(".json"):
        return "data", "full"
    if name.startswith("TarkovCompanion-model-eng-") and name.endswith(".traineddata"):
        return "model", "full"
    if name == "TarkovCompanion.spdx.json":
        return "release", "sbom"
    if name == "relay-package-health.json":
        return "relay", "functional-evidence"
    return "release", "metadata"


def verified_artifacts(record_path: Path, payload: Path, repository: str, commit: str, verification_run_id: str) -> dict[str, Any]:
    """The artifact record from artifacts.py, checked against this payload file by file.

    Every file verification produced must be one the record saw inside an artifact archive whose
    digest GitHub recorded at upload, with the same bytes. Only the files this publisher adds
    itself are exempt, and those are named in PUBLISHER_FILES.
    """
    record = read_json(record_path)
    run = record.get("verificationRun") if isinstance(record, dict) else None
    if (repository != SOURCE_REPOSITORY or DECIMAL_ID.fullmatch(verification_run_id) is None
            or not isinstance(run, dict) or record.get("schemaVersion") != 1 or record.get("repository") != repository
            or str(run.get("id")) != verification_run_id or run.get("headSha") != commit
            or run.get("workflowPath") != ".github/workflows/windows-verify.yml"
            or run.get("event") != "push" or run.get("headBranch") != "main"
            or not isinstance(run.get("attempt"), int) or isinstance(run.get("attempt"), bool)
            or run["attempt"] <= 0):
        raise ManifestError("the artifact record does not describe this repository's verification run at this commit")
    produced: dict[str, str] = {}
    artifacts = record.get("artifacts")
    if (not isinstance(artifacts, list) or len(artifacts) != 2
            or {item.get("name") for item in artifacts if isinstance(item, dict)}
            != {"windows-release-payload", "group-server-release"}):
        raise ManifestError("the artifact record must name exactly the two reviewed producer artifacts")
    for artifact in artifacts:
        if (not isinstance(artifact, dict) or not HEX_64.fullmatch(str(artifact.get("sha256")))
                or not isinstance(artifact.get("id"), int) or isinstance(artifact.get("id"), bool)
                or artifact["id"] <= 0
                or not isinstance(artifact.get("size"), int) or isinstance(artifact.get("size"), bool)
                or artifact["size"] <= 0 or artifact["size"] > MAX_ARCHIVE_BYTES):
            raise ManifestError("the artifact record has an artifact without a recorded digest")
        files = artifact.get("files") or []
        if not isinstance(files, list) or not files or len(files) > MAX_ARTIFACT_COUNT:
            raise ManifestError("the artifact record exceeds the per-artifact file-count limit")
        for item in files:
            path_text = item.get("path") if isinstance(item, dict) else None
            path = PurePosixPath(path_text) if isinstance(path_text, str) else None
            if (path is None or path.is_absolute() or ".." in path.parts or "\\" in path_text
                    or not 1 <= len(path.parts) <= 2 or not all(safe_name(part) for part in path.parts)
                    or not HEX_64.fullmatch(str(item.get("sha256")))
                    or not isinstance(item.get("size"), int) or isinstance(item.get("size"), bool)
                    or item["size"] <= 0 or item["size"] > MAX_MEMBER_BYTES):
                raise ManifestError("the artifact record contains a malformed produced file")
            name = path.name
            if name in produced:
                raise ManifestError(f"the artifact record holds two files named {name}")
            produced[name] = str(item.get("sha256"))
    payload_entries = directory_entries(payload, "collected release payload")
    if any(path.is_symlink() for path in payload_entries):
        raise ManifestError("the collected release payload contains a redirected entry")
    payload_files = {
        path.name: path
        for path in sorted(payload_entries)
        if path.is_file() and path.name not in PUBLISHER_FILES and not NOT_ARTIFACTS.match(path.name)
    }
    if set(produced) != set(payload_files):
        raise ManifestError("the artifact record and collected verification payload name different files")
    for path in payload_files.values():
        if produced.get(path.name) != sha256_file(path):
            raise ManifestError(f"{path.name} is not a file the verification run uploaded, byte for byte")
    return {
        "attempt": run.get("attempt"),
        "artifacts": [{"name": item["name"], "sha256": item["sha256"], "size": item.get("size")} for item in artifacts],
    }


def create_manifest(args: argparse.Namespace) -> dict[str, Any]:
    payload = args.payload.resolve()
    identity = reconcile(payload, args.source_root, args.commit, args.verification_run_id)
    produced = verified_artifacts(args.artifact_record, payload, args.repository, args.commit, args.verification_run_id)
    sources = identity["sources"]

    for name, source in (("THIRD_PARTY_NOTICES.md", "docs/THIRD_PARTY_NOTICES.md"),
                         ("THIRD_PARTY_INVENTORY.json", "docs/THIRD_PARTY_INVENTORY.json")):
        if (not (payload / name).is_file()
                or bounded_read_bytes(payload / name, name, MAX_JSON_BYTES)
                != bounded_read_bytes(args.source_root / source, source, MAX_JSON_BYTES)):
            raise ManifestError(f"{name} is absent or is not the copy from the verified commit")
    sbom = payload / "TarkovCompanion.spdx.json"
    sbom_value = read_json(sbom) if sbom.is_file() else None
    if not isinstance(sbom_value, dict) or not str(sbom_value.get("spdxVersion", "")).startswith("SPDX-"):
        raise ManifestError("the SPDX SBOM is absent or malformed")
    # A scan of the packed archives lists only the scanned directory itself (run 608: one
    # package, no package URLs; unpacked, 86 NuGet package URLs). Count what was actually found.
    nuget = {
        reference.get("referenceLocator")
        for package in sbom_value.get("packages") or []
        for reference in package.get("externalRefs") or []
        if reference.get("referenceType") == "purl" and str(reference.get("referenceLocator", "")).startswith("pkg:nuget/")
    }
    if not nuget:
        raise ManifestError("the SPDX SBOM names no NuGet packages, so it did not see inside the release")

    health = read_json(payload / "relay-package-health.json") if (payload / "relay-package-health.json").is_file() else None
    expected_health = {
        "schemaVersion": SCHEMA_VERSION,
        "status": "ok",
        "version": identity["version"],
        "commit": identity["commit"],
        "protocol": sources["relayProtocol"],
        "archiveSha256": identity["relayArchiveSha256"],
    }
    if health != expected_health:
        raise ManifestError("the extracted relay package did not report this release's identity and protocol")

    (payload / "BINARY-SHA256SUMS.txt").write_text(
        "".join(f"{item['sha256']}  {item['archive']}!{item['path']}\n" for item in identity["binaries"]),
        encoding="utf-8",
    )

    artifacts = []
    payload_entries = directory_entries(payload, "release manifest input")
    if any(path.is_symlink() for path in payload_entries):
        raise ManifestError("release manifest input contains a redirected entry")
    for path in sorted(payload_entries):
        if not path.is_file() or NOT_ARTIFACTS.match(path.name):
            continue
        component, role = artifact_role(path.name, identity)
        artifacts.append({"name": path.name, "component": component, "role": role,
                          "sha256": sha256_file(path), "size": path.stat().st_size})
        if len(artifacts) > MAX_ARTIFACT_COUNT:
            raise ManifestError("release manifest exceeds the artifact-count limit")

    return {
        "schemaVersion": SCHEMA_VERSION,
        "version": identity["version"],
        "commit": identity["commit"],
        "builtUtc": identity["builtUtc"],
        "source": {
            "repository": args.repository,
            "branch": "main",
            "verificationWorkflow": ".github/workflows/windows-verify.yml",
            "verificationRunId": args.verification_run_id,
            "verificationRunAttempt": produced["attempt"],
            # The archives verification uploaded, by the digest GitHub recorded at upload.
            "verificationArtifacts": produced["artifacts"],
        },
        "versions": {
            "package": identity["version"],
            "assemblyInformational": identity["informationalVersion"],
            "manifest": identity["version"],
            "commit": identity["commit"],
            **sources,
        },
        "artifacts": artifacts,
        "binaries": identity["binaries"],
        # Binary, data and model artifacts share one signed manifest and one ring decision, so
        # pause, rollback and last-known-good cannot apply to one kind and not the others.
        "feeds": {
            component: {"authentication": "required", "artifacts": [
                item["name"] for item in artifacts if item["component"] in members
            ]}
            for component, members in {
                "binary": {"desktop", "relay"}, "data": {"data"}, "model": {"model"},
            }.items()
        },
    }


def extract_sbom_input(payload: Path, output: Path, identity: dict[str, Any]) -> None:
    """Unpack both archives so the SBOM scanner sees .deps.json and assemblies, not two blobs."""
    desktop = output / "desktop"
    relay = output / "relay"
    for directory in (desktop, relay):
        directory.mkdir(parents=True, exist_ok=False)
    desktop_archive = payload / identity["desktopArchive"]
    archive_file(desktop_archive, "desktop archive")
    with zipfile.ZipFile(desktop_archive) as archive:
        members = zip_members(archive, "desktop archive")
        expanded = sum(member.file_size for member in members)
        if shutil.disk_usage(desktop).free < expanded + MIN_FREE_RESERVE_BYTES:
            raise ManifestError("insufficient free space to extract the bounded desktop archive")
        for member in members:
            if not safe_member(member.filename):
                raise ManifestError(f"desktop archive contains an unsafe path: {member.filename}")
            mode = (member.external_attr >> 16) & 0o170000
            if not member.is_dir() and mode not in (0, stat.S_IFREG):
                raise ManifestError(f"desktop archive contains a link or special file: {member.filename}")
            target = desktop.joinpath(*PurePosixPath(member.filename).parts)
            if member.is_dir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(member) as source, target.open("xb") as sink:
                actual = copy_stream(source, sink, maximum=member.file_size,
                                     label=f"desktop archive entry {member.filename}")
            if actual != member.file_size:
                raise ManifestError(f"desktop archive entry {member.filename} did not match its declared size")

    relay_archive = payload / identity["relayArchive"]
    archive_file(relay_archive, "relay archive")
    expanded = 0
    count = 0
    with tarfile.open(relay_archive, "r|gz") as archive:
        for member in archive:
            count += 1
            if count > MAX_ARCHIVE_MEMBERS:
                raise ManifestError(f"relay archive has more than {MAX_ARCHIVE_MEMBERS} entries")
            if not safe_member(member.name) or not (member.isfile() or member.isdir()):
                raise ManifestError(f"relay archive contains an unsafe entry: {member.name}")
            if member.size < 0 or member.size > MAX_MEMBER_BYTES:
                raise ManifestError(f"relay archive entry {member.name} exceeds the per-file size limit")
            expanded += member.size
            if expanded > MAX_EXPANDED_BYTES:
                raise ManifestError(f"relay archive exceeds the {MAX_EXPANDED_BYTES}-byte expanded-size limit")
            if shutil.disk_usage(relay).free < member.size + MIN_FREE_RESERVE_BYTES:
                raise ManifestError("insufficient free space to continue extracting the bounded relay archive")
            target = relay.joinpath(*PurePosixPath(member.name).parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            source = archive.extractfile(member)
            if source is None:
                raise ManifestError(f"could not read relay archive entry: {member.name}")
            with source, target.open("xb") as sink:
                actual = copy_stream(source, sink, maximum=member.size,
                                     label=f"relay archive entry {member.name}")
            if actual != member.size:
                raise ManifestError(f"relay archive entry {member.name} did not match its declared size")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    collect_parser = subparsers.add_parser("collect", help="flatten downloaded verification artifacts")
    collect_parser.add_argument("--source", type=Path, action="append", required=True)
    collect_parser.add_argument("--output", type=Path, required=True)

    for name in ("reconcile", "manifest", "sbom-input"):
        command = subparsers.add_parser(name)
        command.add_argument("--payload", type=Path, required=True)
        command.add_argument("--source-root", type=Path, required=True)
        command.add_argument("--commit", required=True)
        command.add_argument("--verification-run-id", required=True)
        command.add_argument("--output", type=Path, required=True)
        if name == "manifest":
            command.add_argument("--repository", required=True)
            command.add_argument("--artifact-record", type=Path, required=True)
    return parser


def main() -> int:
    args = build_parser().parse_args()
    try:
        if args.command == "collect":
            for name in collect(args.source, args.output):
                print(name)
        elif args.command == "reconcile":
            identity = reconcile(args.payload, args.source_root, args.commit, args.verification_run_id)
            identity.pop("binaries")
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(json.dumps(identity, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        elif args.command == "manifest":
            manifest = create_manifest(args)
            args.output.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        else:
            identity = reconcile(args.payload, args.source_root, args.commit, args.verification_run_id)
            extract_sbom_input(args.payload, args.output, identity)
    except (ManifestError, OSError, ResourceLimitError, tarfile.TarError, zipfile.BadZipFile) as exception:
        print(f"release manifest refused: {exception}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
