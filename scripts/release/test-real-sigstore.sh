#!/usr/bin/env bash
set -euo pipefail

# Proves scripts/release/verify-signed-file.sh against real Sigstore material, not a fake.
#
# A release signature cannot be minted from a pull request: the only identity it accepts belongs
# to the publisher on main. What can be proven here is that the verification command itself is
# right - that a real keyless bundle verifies offline against a trust root file, and that the
# wrong identity, altered bytes, or a trust root without the signing CA are each refused. The
# material is cosign's own release: the installed binary and the bundle Sigstore published for it.
readonly TASK_COSIGN_VERSION="v3.1.3"
readonly TASK_BUNDLE_URL="https://github.com/sigstore/cosign/releases/download/${TASK_COSIGN_VERSION}/cosign-linux-amd64.sigstore.json"
readonly TASK_BUNDLE_SHA256="e16547fbee348eb23bd7e5a4d542b540395faea2e7bb1d18da01bbc3cc74d57d"
readonly TASK_IDENTITY="keyless@projectsigstore.iam.gserviceaccount.com"
readonly TASK_ISSUER="https://accounts.google.com"

TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT
readonly TASK_VERIFY="${TASK_PROJECT_ROOT}/scripts/release/verify-signed-file.sh"
TASK_WORK="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/tarkov-real-sigstore.XXXXXX")"
readonly TASK_WORK
trap 'rm -rf -- "${TASK_WORK}"' EXIT

TASK_COSIGN="$(command -v cosign)"
readonly TASK_COSIGN
[[ "$(cosign version 2>&1 | awk '/^GitVersion:/ {print $2}')" == "${TASK_COSIGN_VERSION}" ]] \
    || { printf 'The installed cosign is not %s\n' "${TASK_COSIGN_VERSION}" >&2; exit 1; }

curl --fail --silent --show-error --location --output "${TASK_WORK}/cosign.sigstore.json" "${TASK_BUNDLE_URL}"
[[ "$(sha256sum "${TASK_WORK}/cosign.sigstore.json" | awk '{print $1}')" == "${TASK_BUNDLE_SHA256}" ]] \
    || { printf 'The downloaded bundle is not the pinned one\n' >&2; exit 1; }
cp -- "${TASK_COSIGN}" "${TASK_WORK}/cosign-linux-amd64"
cosign trusted-root create --with-default-services --out "${TASK_WORK}/trusted-root.json"

expect() {
    local expected="$1" label="$2" status=0
    shift 2
    "$@" >"${TASK_WORK}/out.txt" 2>&1 || status=$?
    if [[ "${expected}" == pass && "${status}" -ne 0 ]] || [[ "${expected}" == fail && "${status}" -eq 0 ]]; then
        cat "${TASK_WORK}/out.txt" >&2
        printf 'FAIL: %s (exit %s)\n' "${label}" "${status}" >&2
        exit 1
    fi
    printf 'ok: %s\n' "${label}"
}

expect pass "a real keyless bundle verifies against a trust root file" \
    env TARKOV_RELEASE_SIGNER_IDENTITY="${TASK_IDENTITY}" TARKOV_RELEASE_SIGNER_ISSUER="${TASK_ISSUER}" \
    "${TASK_VERIFY}" "${TASK_WORK}/cosign-linux-amd64" "${TASK_WORK}/cosign.sigstore.json" "${TASK_WORK}/trusted-root.json"

expect fail "the default identity refuses anything but the main-branch publisher" \
    "${TASK_VERIFY}" "${TASK_WORK}/cosign-linux-amd64" "${TASK_WORK}/cosign.sigstore.json" "${TASK_WORK}/trusted-root.json"

cp -- "${TASK_WORK}/cosign-linux-amd64" "${TASK_WORK}/altered"
printf '\0' >> "${TASK_WORK}/altered"
expect fail "altered bytes are refused" \
    env TARKOV_RELEASE_SIGNER_IDENTITY="${TASK_IDENTITY}" TARKOV_RELEASE_SIGNER_ISSUER="${TASK_ISSUER}" \
    "${TASK_VERIFY}" "${TASK_WORK}/altered" "${TASK_WORK}/cosign.sigstore.json" "${TASK_WORK}/trusted-root.json"

jq '.certificateAuthorities = []' "${TASK_WORK}/trusted-root.json" > "${TASK_WORK}/no-ca.json"
expect fail "a trust root without the signing certificate authority is refused" \
    env TARKOV_RELEASE_SIGNER_IDENTITY="${TASK_IDENTITY}" TARKOV_RELEASE_SIGNER_ISSUER="${TASK_ISSUER}" \
    "${TASK_VERIFY}" "${TASK_WORK}/cosign-linux-amd64" "${TASK_WORK}/cosign.sigstore.json" "${TASK_WORK}/no-ca.json"
