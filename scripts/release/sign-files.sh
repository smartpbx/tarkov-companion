#!/usr/bin/env bash
set -euo pipefail

# Keyless-signs each file and then verifies the bundle it just wrote with the same rule every
# consumer uses. Signing that is never read back is how a release ships signatures nobody can
# verify: the wrong identity, the wrong bundle format, or a trust root that does not cover it.
if (($# == 0)); then
    printf 'Usage: TARKOV_SIGSTORE_TRUST_ROOT=FILE scripts/release/sign-files.sh FILE...\n' >&2
    exit 2
fi

TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT
readonly TASK_TRUST_ROOT="${TARKOV_SIGSTORE_TRUST_ROOT:-}"

# The same pinned build that verifies, so a signature is never made by a cosign nobody reviewed.
TASK_COSIGN="$("${TASK_PROJECT_ROOT}/scripts/release/pinned-cosign.sh")" || exit 1
readonly TASK_COSIGN
if [[ -z "${TASK_TRUST_ROOT}" || ! -s "${TASK_TRUST_ROOT}" ]]; then
    printf 'Release signing failed: the trust root to verify against is missing\n' >&2
    exit 1
fi

for file in "$@"; do
    if [[ ! -f "${file}" || ! -s "${file}" ]]; then
        printf 'Release signing failed: missing or empty subject: %s\n' "${file}" >&2
        exit 1
    fi
    rm -f -- "${file}.sigstore.json"
    "${TASK_COSIGN}" sign-blob --yes --bundle "${file}.sigstore.json" "${file}" >/dev/null
    "${TASK_PROJECT_ROOT}/scripts/release/verify-signed-file.sh" \
        "${file}" "${file}.sigstore.json" "${TASK_TRUST_ROOT}"
done
