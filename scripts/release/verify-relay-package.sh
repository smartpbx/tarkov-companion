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

mkdir -p "${TASK_WORK}/app" "${TASK_WORK}/state" "${TASK_WORK}/home"
python3 - "${TASK_ARCHIVE}" <<'PY'
from pathlib import PurePosixPath
import sys
import tarfile

with tarfile.open(sys.argv[1], "r:gz") as archive:
    for member in archive.getmembers():
        path = PurePosixPath(member.name)
        if path.is_absolute() or ".." in path.parts or not (member.isfile() or member.isdir()):
            raise SystemExit(f"relay package has an unsafe entry: {member.name}")
PY
tar --no-same-owner -C "${TASK_WORK}/app" -xzf "${TASK_ARCHIVE}"
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

TASK_HEALTH=""
for ((attempt = 1; attempt <= TASK_ATTEMPTS; attempt++)); do
    if ! kill -0 "${TASK_PID}" 2>/dev/null; then
        sed -n '1,60p' "${TASK_WORK}/stderr.log" >&2
        fail "the relay exited before answering /health"
    fi
    if TASK_HEALTH="$(wget -q --timeout=2 -O - "http://127.0.0.1:${TASK_PORT}/health" 2>/dev/null)"; then
        break
    fi
    TASK_HEALTH=""
    sleep 1
done
stop_relay
[[ -n "${TASK_HEALTH}" ]] || fail "the health endpoint did not answer"

if ! jq -e \
    --arg version "${TASK_VERSION}" \
    --arg commit "${TASK_COMMIT}" \
    --argjson protocol "${TASK_PROTOCOL}" \
    '.status == "ok" and .version == $version and .commit == $commit and .protocol == $protocol' \
    <<<"${TASK_HEALTH}" >/dev/null; then
    jq -c '{status, version, commit, protocol}' <<<"${TASK_HEALTH}" >&2 || true
    fail "the running relay reported a different identity from the release"
fi

# Recorded from what the relay said, not from what it was expected to say.
mkdir -p "$(dirname "${TASK_OUTPUT}")"
jq -S \
    --arg archiveSha256 "${TASK_SHA256}" \
    '{schemaVersion: 1, status, version, commit, protocol, archiveSha256: $archiveSha256}' \
    <<<"${TASK_HEALTH}" > "${TASK_OUTPUT}"
printf 'Relay package answered as version %s, commit %s, protocol %s\n' \
    "${TASK_VERSION}" "${TASK_COMMIT}" "${TASK_PROTOCOL}"
