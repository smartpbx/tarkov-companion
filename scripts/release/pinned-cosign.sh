#!/usr/bin/env bash
set -euo pipefail

# Prints the path of the cosign to sign or verify with, and only if it is a pinned build.
#
# TARKOV_COSIGN names the binary (default: cosign on PATH). It is accepted when its sha256 is in
# scripts/release/cosign.sha256, or equal to TARKOV_COSIGN_SHA256 when that is set: a named
# digest, not a way to switch the check off. Links are resolved first, so what is hashed is what
# runs.
TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT

fail() {
    printf 'cosign refused: %s\n' "$1" >&2
    exit 1
}

cosign="${TARKOV_COSIGN:-$(command -v cosign || true)}"
[[ -n "${cosign}" && -f "${cosign}" && -x "${cosign}" ]] || fail "cosign is unavailable; install it with scripts/release/install-cosign.sh"
cosign="$(readlink -f -- "${cosign}")"
actual="$(sha256sum -- "${cosign}" | awk '{print $1}')"
if [[ -n "${TARKOV_COSIGN_SHA256:-}" ]]; then
    [[ "${TARKOV_COSIGN_SHA256}" =~ ^[0-9a-f]{64}$ ]] || fail "TARKOV_COSIGN_SHA256 is not a sha256"
    [[ "${actual}" == "${TARKOV_COSIGN_SHA256}" ]] || fail "${cosign} is not the cosign TARKOV_COSIGN_SHA256 names"
elif ! awk '{print $1}' "${TASK_PROJECT_ROOT}/scripts/release/cosign.sha256" | grep -qxF -- "${actual}"; then
    fail "${cosign} (sha256 ${actual}) is not a pinned cosign; install one with scripts/release/install-cosign.sh"
fi
printf '%s\n' "${cosign}"
