#!/usr/bin/env python3
"""Stage and check the rough update channel: the folder that is copied onto the relay.

The rough channel is unsigned. What this checks is that every package is the package the feed
describes, by SHA256 and size, which is the same check the desktop makes before it installs.
It says nothing about who built the package; docs/RELEASES.md says what that leaves open.

    rough_channel.py stage --velopack dist/velopack --output dist/rough-channel
    rough_channel.py verify rough-channel
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import sys
from pathlib import Path

FEED_NAME = "releases.win.json"
SUMS_NAME = "SHA256SUMS.txt"
ORDER_NAME = "COPY-ORDER.txt"
SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,199}$")
SHA256_HEX = re.compile(r"^[0-9A-Fa-f]{64}$")
MAX_FEED_BYTES = 1024 * 1024


class ChannelError(Exception):
    """The folder is not a channel that should be published."""


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def read_feed(directory: Path) -> list[dict]:
    """The feed's packages, each with the fields the desktop requires."""
    feed_path = directory / FEED_NAME
    if not feed_path.is_file():
        raise ChannelError(f"{feed_path} is missing: there is no feed to publish")
    if feed_path.stat().st_size > MAX_FEED_BYTES:
        raise ChannelError(f"{feed_path} is larger than a feed can be")
    try:
        document = json.loads(feed_path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise ChannelError(f"{feed_path} is not JSON: {error}") from error
    assets = document.get("Assets") if isinstance(document, dict) else None
    if not isinstance(assets, list) or not assets:
        raise ChannelError(f"{feed_path} lists no packages")
    for asset in assets:
        if not isinstance(asset, dict):
            raise ChannelError(f"{feed_path} lists something that is not a package")
        name = asset.get("FileName")
        if not isinstance(name, str) or not SAFE_NAME.match(name) or ".." in name:
            raise ChannelError(f"{feed_path} names a file that is not a plain file name: {name!r}")
        if not isinstance(asset.get("SHA256"), str) or not SHA256_HEX.match(asset["SHA256"]):
            raise ChannelError(f"{feed_path} gives no SHA256 for {name}; the desktop would refuse this feed")
        if not isinstance(asset.get("Size"), int) or asset["Size"] <= 0:
            raise ChannelError(f"{feed_path} gives no size for {name}")
        if not asset.get("Version") or not asset.get("PackageId"):
            raise ChannelError(f"{feed_path} gives no version or package id for {name}")
    if not any(asset.get("Type") == "Full" for asset in assets):
        raise ChannelError(f"{feed_path} lists no full package, so nothing could install from it")
    return assets


def check_packages(directory: Path, assets: list[dict]) -> None:
    for asset in assets:
        path = directory / asset["FileName"]
        if not path.is_file():
            raise ChannelError(f"{asset['FileName']} is in the feed but not in {directory}")
        size = path.stat().st_size
        if size != asset["Size"]:
            raise ChannelError(f"{asset['FileName']} is {size} bytes and the feed says {asset['Size']}")
        actual = sha256_file(path)
        if actual != asset["SHA256"].upper():
            raise ChannelError(
                f"{asset['FileName']} does not match the feed: expected SHA256 {asset['SHA256'].upper()}, found {actual}"
            )


def installers(directory: Path) -> list[Path]:
    return sorted(path for path in directory.glob("*-Setup.exe") if path.is_file())


def stage(velopack: Path, output: Path) -> list[Path]:
    """Copies the feed, its packages and the installer, and writes the sums and the copy order."""
    assets = read_feed(velopack)
    check_packages(velopack, assets)
    setups = installers(velopack)
    if len(setups) != 1:
        raise ChannelError(f"expected exactly one installer in {velopack}, found {len(setups)}")
    if output.exists() and any(output.iterdir()):
        raise ChannelError(f"{output} is not empty; stage into a fresh folder")
    output.mkdir(parents=True, exist_ok=True)

    # The order a publisher copies in. The feed goes last: a client that reads a feed naming a
    # package that has not arrived yet fails its download, and one that reads the old feed
    # simply sees nothing new for another minute.
    ordered = [velopack / asset["FileName"] for asset in assets] + setups
    names = [path.name for path in ordered]
    for path in ordered:
        shutil.copyfile(path, output / path.name)
    sums = "".join(f"{sha256_file(output / name).lower()}  {name}\n" for name in names)
    shutil.copyfile(velopack / FEED_NAME, output / FEED_NAME)
    sums += f"{sha256_file(output / FEED_NAME).lower()}  {FEED_NAME}\n"
    (output / SUMS_NAME).write_text(sums, encoding="utf-8", newline="\n")
    (output / ORDER_NAME).write_text("\n".join(names + [FEED_NAME]) + "\n", encoding="utf-8", newline="\n")
    verify(output)
    return [output / name for name in names + [FEED_NAME]]


def verify(directory: Path) -> list[dict]:
    """Checks a staged folder the way the desktop will, plus the sums written when it was staged."""
    assets = read_feed(directory)
    check_packages(directory, assets)
    sums_path = directory / SUMS_NAME
    if not sums_path.is_file():
        raise ChannelError(f"{sums_path} is missing")
    listed = set()
    for line in sums_path.read_text(encoding="utf-8").splitlines():
        digest, _, name = line.partition("  ")
        if not SHA256_HEX.match(digest) or not SAFE_NAME.match(name):
            raise ChannelError(f"{sums_path} has a line that is not a checksum: {line!r}")
        path = directory / name
        if not path.is_file() or sha256_file(path) != digest.upper():
            raise ChannelError(f"{name} does not match {SUMS_NAME}")
        listed.add(name)
    expected = {asset["FileName"] for asset in assets} | {FEED_NAME}
    if not expected <= listed:
        raise ChannelError(f"{SUMS_NAME} does not cover: {', '.join(sorted(expected - listed))}")
    if len(installers(directory)) != 1:
        raise ChannelError(f"{directory} has no installer, so a first install has nothing to run")
    return assets


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    stage_parser = commands.add_parser("stage", help="build the folder to publish from the packaging tool's output")
    stage_parser.add_argument("--velopack", type=Path, required=True)
    stage_parser.add_argument("--output", type=Path, required=True)
    verify_parser = commands.add_parser("verify", help="check a staged folder before copying it to the relay")
    verify_parser.add_argument("directory", type=Path)
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "stage":
            for path in stage(arguments.velopack, arguments.output):
                print(f"staged {path.name}")
        else:
            for asset in verify(arguments.directory):
                print(f"ok {asset['FileName']} {asset['Version']} sha256 {asset['SHA256'].upper()}")
    except ChannelError as error:
        print(f"rough channel refused: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
