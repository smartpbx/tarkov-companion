#!/usr/bin/env bash
set -euo pipefail

readonly TASK_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly TASK_TEMP="$(mktemp -d "${TMPDIR:-/tmp}/tarkov-relay-watch.XXXXXX")"
trap 'rm -rf -- "${TASK_TEMP}"' EXIT

if bash "${TASK_ROOT}/relay-watch.sh" \
    --health-file "${TASK_ROOT}/fixtures/stale-health.json" \
    --expected-commit 0123456789abcdef0123456789abcdef01234567 \
    --commit-utc 1800000000 \
    --now-utc 1800005461 \
    --max-age-minutes 90 \
    >"${TASK_TEMP}/output.txt" 2>&1; then
    printf 'relay-watch fixture failed: a stale deployment was accepted\n' >&2
    exit 1
fi

if ! grep -Fxq 'status=stale' "${TASK_TEMP}/output.txt" ||
   ! grep -Fxq 'reason=relay deployment exceeds the configured update-age limit' "${TASK_TEMP}/output.txt"; then
    printf 'relay-watch fixture failed: stale deployment did not produce the expected alert\n' >&2
    sed -n '1,80p' "${TASK_TEMP}/output.txt" >&2
    exit 1
fi

printf 'relay-watch stale deployment fixture passed\n'
