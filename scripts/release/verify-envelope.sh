#!/usr/bin/env bash
set -euo pipefail

# Extracts the exact signed index bytes from a ring envelope and verifies them. The payload is
# written out only for reading after this succeeds; nothing may use it when this fails.
if (($# != 4)); then
    printf 'Usage: scripts/release/verify-envelope.sh ENVELOPE PAYLOAD-OUT BUNDLE-OUT TRUST-ROOT\n' >&2
    exit 2
fi

TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT

if ! python3 "${TASK_PROJECT_ROOT}/scripts/release/release_policy.py" unpack-envelope \
    --envelope "$1" --payload "$2" --bundle "$3"; then
    rm -f -- "$2" "$3"
    exit 1
fi
if ! "${TASK_PROJECT_ROOT}/scripts/release/verify-signed-file.sh" "$2" "$3" "$4"; then
    rm -f -- "$2" "$3"
    exit 1
fi
