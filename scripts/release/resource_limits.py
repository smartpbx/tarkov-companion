#!/usr/bin/env python3
"""Shared, fail-closed size and nesting limits for release inputs.

Signatures establish authority, not safety. A feed response or archive must be bounded before a
parser or extractor commits memory and disk to it, including when the bytes later fail signature
verification.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import stat
from pathlib import Path
from typing import Any, BinaryIO


MAX_JSON_BYTES = 16 * 1024 * 1024
MAX_JSON_DEPTH = 32
MAX_SIGNATURE_BYTES = 2 * 1024 * 1024
MAX_ARCHIVE_BYTES = 512 * 1024 * 1024
MAX_ARCHIVE_MEMBERS = 8192
MAX_MEMBER_BYTES = 512 * 1024 * 1024
MAX_EXPANDED_BYTES = 1024 * 1024 * 1024
MIN_FREE_RESERVE_BYTES = 256 * 1024 * 1024
MAX_ARTIFACT_COUNT = 4096
MAX_DIRECTORY_ENTRIES = MAX_ARTIFACT_COUNT * 4
CHUNK_BYTES = 1024 * 1024


class ResourceLimitError(ValueError):
    """An input exceeded a resource limit before it could be trusted or consumed."""


def require_json_shape(value: bytes, label: str, *, maximum: int = MAX_JSON_BYTES,
                       maximum_depth: int = MAX_JSON_DEPTH) -> None:
    if len(value) > maximum:
        raise ResourceLimitError(f"{label} is {len(value)} bytes, above the {maximum}-byte JSON limit")
    depth = 0
    in_string = False
    escaped = False
    for byte in value:
        character = chr(byte)
        if in_string:
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == '"':
                in_string = False
            continue
        if character == '"':
            in_string = True
        elif character in "[{":
            depth += 1
            if depth > maximum_depth:
                raise ResourceLimitError(f"{label} exceeds the JSON nesting limit of {maximum_depth}")
        elif character in "]}":
            depth -= 1
            if depth < 0:
                raise ResourceLimitError(f"{label} has unbalanced JSON delimiters")
    if in_string or depth != 0:
        raise ResourceLimitError(f"{label} has unbalanced JSON strings or delimiters")


def decode_json(value: bytes, label: str, *, maximum: int = MAX_JSON_BYTES,
                maximum_depth: int = MAX_JSON_DEPTH) -> Any:
    require_json_shape(value, label, maximum=maximum, maximum_depth=maximum_depth)
    try:
        return json.loads(value.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError) as exception:
        raise ResourceLimitError(f"{label} is not valid bounded JSON: {exception}") from exception


def read_bytes(path: Path, label: str, maximum: int) -> bytes:
    if maximum < 0:
        raise ResourceLimitError(f"{label} has an invalid byte limit")
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags)
    except OSError as exception:
        raise ResourceLimitError(f"could not open plain file {label}: {exception}") from exception
    with os.fdopen(descriptor, "rb") as stream:
        metadata = os.fstat(stream.fileno())
        if not stat.S_ISREG(metadata.st_mode):
            raise ResourceLimitError(f"{label} is not a plain file")
        if metadata.st_size < 0 or metadata.st_size > maximum:
            raise ResourceLimitError(
                f"{label} is {metadata.st_size} bytes, above the {maximum}-byte limit")
        value = stream.read(maximum + 1)
        if len(value) > maximum or stream.read(1):
            raise ResourceLimitError(f"{label} exceeded the {maximum}-byte limit while being read")
    if len(value) != metadata.st_size:
        raise ResourceLimitError(f"{label} changed while it was being read")
    return value


def read_json(path: Path, label: str | None = None, *, maximum: int = MAX_JSON_BYTES) -> Any:
    name = label or path.name
    return decode_json(read_bytes(path, name, maximum), name, maximum=maximum)


def sha256_file(path: Path, label: str, maximum: int) -> str:
    """Hash one plain file through the opened descriptor, within the same bound used to consume it."""
    if maximum < 0:
        raise ResourceLimitError(f"{label} has an invalid byte limit")
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags)
    except OSError as exception:
        raise ResourceLimitError(f"could not open plain file {label}: {exception}") from exception
    hasher = hashlib.sha256()
    with os.fdopen(descriptor, "rb") as stream:
        metadata = os.fstat(stream.fileno())
        if not stat.S_ISREG(metadata.st_mode):
            raise ResourceLimitError(f"{label} is not a plain file")
        if metadata.st_size < 0 or metadata.st_size > maximum:
            raise ResourceLimitError(
                f"{label} is {metadata.st_size} bytes, above the {maximum}-byte limit")
        total = 0
        while True:
            chunk = stream.read(min(CHUNK_BYTES, maximum - total + 1))
            if not chunk:
                break
            total += len(chunk)
            if total > maximum:
                raise ResourceLimitError(f"{label} exceeded the {maximum}-byte limit while being hashed")
            hasher.update(chunk)
    if total != metadata.st_size:
        raise ResourceLimitError(f"{label} changed while it was being hashed")
    return hasher.hexdigest()


def copy_stream(source: BinaryIO, destination: BinaryIO, *, maximum: int, label: str) -> int:
    total = 0
    while True:
        chunk = source.read(min(CHUNK_BYTES, maximum - total + 1))
        if not chunk:
            return total
        total += len(chunk)
        if total > maximum:
            raise ResourceLimitError(f"{label} exceeded the {maximum}-byte limit while being read")
        destination.write(chunk)


def select_ring_file(directory: Path, *, maximum_entries: int = MAX_DIRECTORY_ENTRIES) -> str:
    """Select the newest plain ring decision without allocating an unbounded directory list."""
    if directory.is_symlink() or not directory.is_dir():
        raise ResourceLimitError(f"offline bundle {directory} is not a plain directory")
    pattern = re.compile(r"^release-index-g([0-9]{10})\.json$")
    selected: tuple[int, str] | None = None
    try:
        with os.scandir(directory) as entries:
            for count, entry in enumerate(entries, start=1):
                if count > maximum_entries:
                    raise ResourceLimitError(
                        f"offline bundle exceeds the {maximum_entries}-entry directory limit")
                match = pattern.fullmatch(entry.name)
                if match is None or not entry.is_file(follow_symlinks=False):
                    continue
                candidate = (int(match.group(1)), entry.name)
                if candidate[0] > 0 and (selected is None or candidate > selected):
                    selected = candidate
    except OSError as exception:
        raise ResourceLimitError(f"could not enumerate offline bundle {directory}: {exception}") from exception
    if selected is None:
        raise ResourceLimitError("offline bundle holds no plain signed ring decision")
    return selected[1]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    validate = subparsers.add_parser("validate-json")
    validate.add_argument("path", type=Path)
    validate.add_argument("--maximum", type=int, default=MAX_JSON_BYTES)
    select_ring = subparsers.add_parser("select-ring")
    select_ring.add_argument("directory", type=Path)
    args = parser.parse_args()
    try:
        if args.command == "validate-json":
            read_json(args.path, maximum=args.maximum)
        else:
            print(select_ring_file(args.directory))
    except (OSError, ResourceLimitError) as exception:
        print(f"release input refused: {exception}")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
