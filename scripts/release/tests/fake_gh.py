#!/usr/bin/env python3
"""A file-backed stand-in for the gh invocations the release scripts make.

State lives in $FAKE_GH_STATE so separate processes (transition.sh calling feed.py calling gh)
share one feed. It keeps the GitHub behaviours the release design depends on: a contents path
that already exists cannot be created again, drafts appear only in the release list, and each
uploaded asset carries a server-computed sha256 digest. Two one-shot hooks model failure:
race.json pre-empts the next contents create with another writer's file, and fail-put makes the
next contents create fail as a server error. Immutable releases are on unless mutable-releases
exists, and reading that setting fails as a permission error if immutable-unreadable exists.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import sys
from pathlib import Path


STATE = Path(os.environ["FAKE_GH_STATE"])
REPOSITORY = os.environ.get("FAKE_GH_REPOSITORY", "example/tarkov-feed")


def fail(message: str, status: int) -> None:
    print(f"gh: {message} (HTTP {status})", file=sys.stderr)
    raise SystemExit(1)


def load_releases() -> list[dict]:
    path = STATE / "releases.json"
    return json.loads(path.read_text()) if path.exists() else []


def save_releases(releases: list[dict]) -> None:
    (STATE / "releases.json").write_text(json.dumps(releases))


def api(arguments: list[str]) -> None:
    method, raw, endpoint, fields = "GET", False, None, []
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
    if endpoint is None or not endpoint.startswith(prefix):
        fail(f"unexpected endpoint {endpoint}", 404)
    path = endpoint[len(prefix):]
    contents = STATE / "contents"

    if path == "":
        print((STATE / "visibility").read_text().strip() if (STATE / "visibility").exists() else "private")
    elif path == "/immutable-releases":
        if (STATE / "immutable-unreadable").exists():
            fail("Resource not accessible by personal access token", 403)
        if (STATE / "mutable-releases").exists():
            fail("Not Found", 404)
        print(json.dumps({"enabled": True, "enforced_by_owner": False}))
    elif path.startswith("/commits?path="):
        ring = path.split("path=", 1)[1].split("&", 1)[0]
        history = json.loads((STATE / "history.json").read_text()) if (STATE / "history.json").exists() else []
        print(json.dumps([{"sha": "1"}] if ring in history else []))
    elif path.startswith("/contents/"):
        relative = path[len("/contents/"):]
        target = contents / relative
        if method == "PUT":
            if (STATE / "fail-put").exists():
                (STATE / "fail-put").unlink()
                fail("Server Error", 500)
            if (STATE / "race.json").exists():
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes((STATE / "race.json").read_bytes())
                (STATE / "race.json").unlink()
            if target.exists():
                fail('Invalid request. "sha" wasn\'t supplied.', 422)
            body = json.loads(sys.stdin.buffer.read())
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(base64.b64decode(body["content"]))
            history_path = STATE / "history.json"
            history = set(json.loads(history_path.read_text())) if history_path.exists() else set()
            history.add(relative.rsplit("/", 1)[0])
            history_path.write_text(json.dumps(sorted(history)))
            print(json.dumps({"content": {"path": relative}}))
        elif method == "DELETE":
            if not target.is_file():
                fail("Not Found", 404)
            target.unlink()
            print("{}")
        elif target.is_file():
            sys.stdout.buffer.write(target.read_bytes())
        elif target.is_dir() and any(target.iterdir()):
            print(json.dumps([
                {"name": item.name, "path": f"{relative}/{item.name}", "sha": hashlib.sha1(item.read_bytes()).hexdigest(), "type": "file"}
                for item in sorted(target.iterdir()) if item.is_file()
            ]))
        else:
            fail("Not Found", 404)
    elif path.startswith("/releases?"):
        for release in load_releases():
            immutable = release["draft"] is False and not (STATE / "mutable-releases").exists()
            print(json.dumps({"id": release["id"], "tag_name": release["tag"], "draft": release["draft"], "immutable": immutable}))
    elif path.startswith("/releases/"):
        release_id = int(path.split("/")[2].split("?")[0])
        releases = load_releases()
        release = next((item for item in releases if item["id"] == release_id), None)
        if release is None:
            fail("Not Found", 404)
        if "/assets" in path:
            for asset in release["assets"].values():
                print(json.dumps({key: asset[key] for key in ("name", "size", "digest", "state")}))
        elif method == "DELETE":
            releases.remove(release)
            save_releases(releases)
        elif method == "PATCH":
            release["draft"] = False
            save_releases(releases)
            print(json.dumps({"draft": False, "tag_name": release["tag"], "immutable": not (STATE / "mutable-releases").exists()}))
    else:
        fail(f"unexpected endpoint {endpoint}", 404)


def release_create(arguments: list[str]) -> None:
    tag = arguments[0]
    releases = load_releases()
    counter = STATE / "next-release-id"
    release_id = int(counter.read_text()) if counter.exists() else 100
    counter.write_text(str(release_id + 1))
    assets = {}
    storage = STATE / "assets" / str(release_id)
    storage.mkdir(parents=True, exist_ok=True)
    for item in arguments[1:]:
        if not item.startswith("/"):
            continue
        source = Path(item)
        value = source.read_bytes()
        (storage / source.name).write_bytes(value)
        assets[source.name] = {"name": source.name, "size": len(value), "digest": f"sha256:{hashlib.sha256(value).hexdigest()}",
                               "state": "uploaded", "file": str(storage / source.name)}
    releases.append({"id": release_id, "tag": tag, "draft": "--draft" in arguments, "assets": assets})
    save_releases(releases)


def release_download(arguments: list[str]) -> None:
    tag = arguments[0]
    pattern = arguments[arguments.index("--pattern") + 1]
    directory = Path(arguments[arguments.index("--dir") + 1])
    release = next((item for item in load_releases() if item["tag"] == tag and not item["draft"]), None)
    if release is None or pattern not in release["assets"]:
        fail("release asset not found", 404)
    directory.mkdir(parents=True, exist_ok=True)
    (directory / pattern).write_bytes(Path(release["assets"][pattern]["file"]).read_bytes())


def main() -> None:
    arguments = sys.argv[1:]
    with (STATE / "calls.log").open("a") as log:
        log.write(" ".join(arguments[:4]) + "\n")
    if not os.environ.get("GH_TOKEN"):
        fail("no token", 401)
    if arguments[0] == "api":
        api(arguments[1:])
    elif arguments[:2] == ["release", "create"]:
        release_create(arguments[2:])
    elif arguments[:2] == ["release", "download"]:
        release_download(arguments[2:])
    else:
        fail(f"unexpected command {arguments}", 400)


if __name__ == "__main__":
    main()
