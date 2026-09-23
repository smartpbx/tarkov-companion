#!/usr/bin/env python3
"""Build the embedded, player-facing changelog from merged pull-request titles."""

from __future__ import annotations

import argparse
import json
import pathlib
import re
import subprocess


ROOT = pathlib.Path(__file__).resolve().parents[1]
DEFAULT_OUTPUT = ROOT / "docs" / "CHANGELOG-player.json"
SKIP_PREFIXES = ("build", "chore", "ci", "docs", "release", "test")


def git(*arguments: str) -> str:
    return subprocess.run(
        ["git", "-C", str(ROOT), *arguments],
        check=True,
        capture_output=True,
        text=True,
    ).stdout.strip()


def previous_tag(to_ref: str) -> str:
    try:
        return git("describe", "--tags", "--abbrev=0", f"{to_ref}^")
    except subprocess.CalledProcessError:
        return git("rev-list", "--max-parents=0", to_ref).splitlines()[0]


def title_for(message: str) -> str | None:
    lines = [line.strip() for line in message.splitlines() if line.strip()]
    if not lines:
        return None
    title = lines[1] if lines[0].startswith("Merge pull request #") and len(lines) > 1 else lines[0]
    title = re.sub(r"^(feat|fix|perf|refactor)(\([^)]*\))?!?:\s*", "", title, flags=re.IGNORECASE)
    title = re.sub(r"\s*\(#\d+\)\s*$", "", title).strip().rstrip(".")
    if not title or title.lower().startswith(SKIP_PREFIXES):
        return None
    title = title[0].upper() + title[1:]
    if len(title) > 116:
        title = title[:115].rstrip() + "…"
    return title + "."


def merged_titles(from_ref: str, to_ref: str) -> list[str]:
    raw = git("log", "--first-parent", "--format=%B%x00", f"{from_ref}..{to_ref}")
    changes: list[str] = []
    for message in raw.split("\x00"):
        title = title_for(message)
        if title and title not in changes:
            changes.append(title)
        if len(changes) == 8:
            break
    return changes


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--from-ref")
    parser.add_argument("--to-ref", default="HEAD")
    parser.add_argument("--output", type=pathlib.Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()

    from_ref = args.from_ref or previous_tag(args.to_ref)
    changes = merged_titles(from_ref, args.to_ref)
    if not changes:
        raise SystemExit(f"No player-visible pull-request titles found between {from_ref} and {args.to_ref}.")

    document = {
        "schemaVersion": 1,
        "releases": [{"version": args.version, "changes": changes}],
    }
    output = args.output.resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {len(changes)} player changes for {args.version} from {from_ref}..{args.to_ref} to {output}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
