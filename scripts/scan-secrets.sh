#!/usr/bin/env bash
set -euo pipefail

readonly TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly TASK_SECRET_PATTERN='(ghp_|github_pat_|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|Authorization:[[:space:]]*Bearer[[:space:]]+[A-Za-z0-9._-]{16,}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----)'

if git -C "${TASK_PROJECT_ROOT}" grep -n -I -E "${TASK_SECRET_PATTERN}" -- . \
    ':(exclude)fixtures/**' \
    ':(exclude)docs/**' \
    ':(exclude)scripts/scan-secrets.sh'; then
    printf '%s\n' "Secret-pattern audit failed: review the matches above." >&2
    exit 1
fi

printf '%s\n' "Secret-pattern audit passed."
