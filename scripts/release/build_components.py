#!/usr/bin/env python3
"""Build the versioned data and model payloads that travel with a desktop release."""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from build_manifest import source_versions  # noqa: E402
from release_policy import semver_key  # noqa: E402
from resource_limits import (  # noqa: E402
    MAX_ARCHIVE_BYTES,
    MAX_JSON_BYTES,
    copy_stream,
    read_json,
    sha256_file,
)


MODEL_SOURCE = Path("src/TarkovCompanion.Infrastructure/Recognition/Tessdata/eng.traineddata")
HEX_40 = re.compile(r"^[0-9a-f]{40}$")


def sha256(path: Path) -> str:
    return sha256_file(path, path.name, MAX_ARCHIVE_BYTES)


def build(source: Path, output: Path, update_path: Path) -> list[Path]:
    update = read_json(update_path)
    if not isinstance(update, dict):
        raise ValueError("update metadata must be an object")
    version, commit, built = (update.get(name) for name in ("version", "commit", "builtUtc"))
    if not isinstance(version, str) or not isinstance(commit, str) or not isinstance(built, str):
        raise ValueError("update metadata has no version, commit, or timestamp")
    semver_key(version)
    if HEX_40.fullmatch(commit) is None:
        raise ValueError("update metadata commit is not a full lowercase Git commit")
    try:
        datetime.strptime(built, "%Y-%m-%dT%H:%M:%SZ")
    except ValueError as exception:
        raise ValueError("update metadata timestamp is not canonical UTC") from exception
    output.mkdir(parents=True, exist_ok=True)
    data = output / f"TarkovCompanion-data-{version}.json"
    with data.open("x", encoding="utf-8") as stream:
        stream.write(json.dumps({
            "schemaVersion": 1,
            "version": version,
            "commit": commit,
            "builtUtc": built,
            "contracts": source_versions(source),
        }, indent=2, sort_keys=True) + "\n")
    if data.stat().st_size > MAX_JSON_BYTES:
        raise ValueError("generated data component exceeds its JSON limit")

    model_source = source / MODEL_SOURCE
    if not model_source.is_file() or model_source.is_symlink():
        raise ValueError("the reviewed English OCR model is absent or redirected")
    if model_source.stat().st_size <= 0 or model_source.stat().st_size > MAX_ARCHIVE_BYTES:
        raise ValueError("the reviewed English OCR model is outside its byte limit")
    model = output / f"TarkovCompanion-model-eng-{version}.traineddata"
    with model_source.open("rb") as input_stream, model.open("xb") as output_stream:
        copy_stream(input_stream, output_stream, maximum=model_source.stat().st_size,
                    label="reviewed English OCR model")
    if model.stat().st_size != model_source.stat().st_size or sha256(model) != sha256(model_source):
        raise ValueError("the model changed while the release component was built")

    sums = output / "COMPONENT-SHA256SUMS.txt"
    artifacts = [data, model]
    with sums.open("x", encoding="utf-8") as stream:
        stream.write("".join(f"{sha256(path)}  {path.name}\n" for path in artifacts))
    return [*artifacts, sums]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--update", type=Path, required=True)
    args = parser.parse_args()
    try:
        for path in build(args.source.resolve(), args.output.resolve(), args.update.resolve()):
            print(path)
    except (OSError, ValueError) as exception:
        print(f"release components refused: {exception}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
