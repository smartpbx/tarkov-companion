#!/usr/bin/env bash
set -euo pipefail

# Extracts the relay archive exactly as the host updater would and asks it who it is.
#
# Signing and checksums prove the bytes are the ones verification produced; they say nothing
# about whether those bytes start. This runs in a job with no signing identity and no feed
# credential, under an emptied environment, because it executes the thing being released.
if (($# != 6)); then
    printf 'Usage: scripts/release/verify-relay-package.sh ARCHIVE SHA256 VERSION COMMIT PROTOCOL OUTPUT\n' >&2
    exit 2
fi

readonly TASK_ARCHIVE="$1"
readonly TASK_SHA256="$2"
readonly TASK_VERSION="$3"
readonly TASK_COMMIT="$4"
readonly TASK_PROTOCOL="$5"
readonly TASK_OUTPUT="$6"
readonly TASK_ATTEMPTS="${TARKOV_RELAY_HEALTH_ATTEMPTS:-30}"
readonly TASK_MAX_ARCHIVE_BYTES=$((512 * 1024 * 1024))
readonly TASK_MAX_MEMBERS=8192
readonly TASK_MAX_MEMBER_BYTES=$((512 * 1024 * 1024))
readonly TASK_MAX_EXPANDED_BYTES=$((1024 * 1024 * 1024))
readonly TASK_FREE_RESERVE_BYTES=$((256 * 1024 * 1024))
readonly TASK_RESOURCE_LIMITS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/resource_limits.py"
TASK_WORK="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/tarkov-relay-package.XXXXXX")"
readonly TASK_WORK
TASK_PID=""

stop_relay() {
    if [[ -n "${TASK_PID}" ]]; then
        kill -TERM -- "-${TASK_PID}" 2>/dev/null || kill -TERM "${TASK_PID}" 2>/dev/null || true
        wait "${TASK_PID}" 2>/dev/null || true
        TASK_PID=""
    fi
}

cleanup() {
    local status=$?
    trap - EXIT INT TERM
    stop_relay
    rm -rf -- "${TASK_WORK}"
    exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

fail() {
    printf 'Relay package verification failed: %s\n' "$1" >&2
    exit 1
}

[[ "${TASK_SHA256}" =~ ^[0-9a-f]{64}$ && "${TASK_COMMIT}" =~ ^[0-9a-f]{40}$ && "${TASK_PROTOCOL}" =~ ^[0-9]+$ ]] \
    || fail "expected digest, commit, or protocol is malformed"
[[ "$(sha256sum "${TASK_ARCHIVE}" | awk '{print $1}')" == "${TASK_SHA256}" ]] \
    || fail "archive digest is not the reconciled relay archive"
archive_size="$(stat -c %s -- "${TASK_ARCHIVE}")"
((archive_size > 0 && archive_size <= TASK_MAX_ARCHIVE_BYTES)) \
    || fail "archive is outside the compressed-size limit"

mkdir -p "${TASK_WORK}/app" "${TASK_WORK}/state" "${TASK_WORK}/home"
python3 - "${TASK_ARCHIVE}" "${TASK_WORK}/app" "${TASK_MAX_MEMBERS}" "${TASK_MAX_MEMBER_BYTES}" \
    "${TASK_MAX_EXPANDED_BYTES}" "${TASK_FREE_RESERVE_BYTES}" <<'PY'
import os
from pathlib import Path, PurePosixPath
import shutil
import sys
import tarfile

source, destination = Path(sys.argv[1]), Path(sys.argv[2])
member_limit, file_limit, expanded_limit, reserve = map(int, sys.argv[3:])
seen = set()
expanded = count = 0
with tarfile.open(source, "r|gz") as archive:
    for member in archive:
        count += 1
        if count > member_limit:
            raise SystemExit(f"relay package has more than {member_limit} entries")
        path = PurePosixPath(member.name)
        if path.is_absolute() or ".." in path.parts or "\\" in member.name or not (member.isfile() or member.isdir()):
            raise SystemExit(f"relay package has an unsafe entry: {member.name}")
        parts = tuple(part for part in path.parts if part not in ("", "."))
        if not parts:
            if member.isdir():
                continue
            raise SystemExit("relay package has an empty file name")
        name = "/".join(parts)
        if name in seen:
            raise SystemExit(f"relay package repeats {name}")
        seen.add(name)
        target = destination.joinpath(*parts)
        if member.isdir():
            target.mkdir(parents=True, exist_ok=True, mode=0o755)
            target.chmod(0o755)
            continue
        if member.size < 0 or member.size > file_limit:
            raise SystemExit(f"relay package entry {name} exceeds the per-file limit")
        expanded += member.size
        if expanded > expanded_limit:
            raise SystemExit("relay package exceeds the expanded-size limit")
        if shutil.disk_usage(destination).free < member.size + reserve:
            raise SystemExit("not enough free space remains for bounded extraction")
        target.parent.mkdir(parents=True, exist_ok=True)
        stream = archive.extractfile(member)
        if stream is None:
            raise SystemExit(f"relay package cannot read {name}")
        flags = os.O_CREAT | os.O_EXCL | os.O_WRONLY | getattr(os, "O_NOFOLLOW", 0)
        mode = 0o755 if member.mode & 0o111 else 0o644
        descriptor = os.open(target, flags, mode)
        written = 0
        with stream, os.fdopen(descriptor, "wb") as output:
            while True:
                value = stream.read(min(1024 * 1024, member.size - written + 1))
                if not value:
                    break
                written += len(value)
                if written > member.size:
                    raise SystemExit(f"relay package entry {name} expanded past its declared size")
                output.write(value)
        if written != member.size:
            raise SystemExit(f"relay package entry {name} is truncated")
PY
[[ -f "${TASK_WORK}/app/TarkovCompanion.GroupServer" && -x "${TASK_WORK}/app/TarkovCompanion.GroupServer" ]] \
    || fail "executable is absent or not executable"

TASK_PORT="$(python3 -c 'import socket; s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()')"
readonly TASK_PORT

# setsid gives the relay its own process group, so stopping it also stops anything it started.
setsid env -i \
    PATH=/usr/local/bin:/usr/bin:/bin \
    HOME="${TASK_WORK}/home" \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_URLS="http://127.0.0.1:${TASK_PORT}" \
    TARKOV_GROUP_STATE="${TASK_WORK}/state" \
    "${TASK_WORK}/app/TarkovCompanion.GroupServer" \
    >"${TASK_WORK}/stdout.log" 2>"${TASK_WORK}/stderr.log" &
TASK_PID=$!

TASK_HEALTH="${TASK_WORK}/health.json"
for ((attempt = 1; attempt <= TASK_ATTEMPTS; attempt++)); do
    if ! kill -0 "${TASK_PID}" 2>/dev/null; then
        sed -n '1,60p' "${TASK_WORK}/stderr.log" >&2
        fail "the relay exited before answering /health"
    fi
    if ( ulimit -f 1024; wget -q --timeout=2 -O "${TASK_HEALTH}" "http://127.0.0.1:${TASK_PORT}/health" 2>/dev/null ) \
        && python3 "${TASK_RESOURCE_LIMITS}" validate-json --maximum 1048576 "${TASK_HEALTH}" >/dev/null; then
        break
    fi
    rm -f -- "${TASK_HEALTH}"
    sleep 1
done
stop_relay
[[ -s "${TASK_HEALTH}" ]] || fail "the health endpoint did not answer with bounded JSON"

if ! jq -e \
    --arg version "${TASK_VERSION}" \
    --arg commit "${TASK_COMMIT}" \
    --argjson protocol "${TASK_PROTOCOL}" \
    '.status == "ok" and .version == $version and .commit == $commit and .protocol == $protocol' \
    "${TASK_HEALTH}" >/dev/null; then
    jq -c '{status, version, commit, protocol}' "${TASK_HEALTH}" >&2 || true
    fail "the running relay reported a different identity from the release"
fi

# Recorded from what the relay said, not from what it was expected to say.
mkdir -p "$(dirname "${TASK_OUTPUT}")"
jq -S \
    --arg archiveSha256 "${TASK_SHA256}" \
    '{schemaVersion: 1, status, version, commit, protocol, archiveSha256: $archiveSha256}' \
    "${TASK_HEALTH}" > "${TASK_OUTPUT}"
printf 'Relay package answered as version %s, commit %s, protocol %s\n' \
    "${TASK_VERSION}" "${TASK_COMMIT}" "${TASK_PROTOCOL}"
