#!/usr/bin/env bash
set -euo pipefail

# The one place a release signature is checked, so the identity rule cannot drift between the
# publisher, the offline verifier and the tests.
#
# The identity is the reviewed publishing workflow on main, not "anything GitHub signed". A
# workflow dispatched from another branch gets a certificate naming that branch, and a feed
# credential can replace bytes and bundles together but cannot mint this certificate. The trust
# root is a file provisioned separately from the feed, so verification does not depend on the
# network, or on anything the feed serves.
if (($# != 3)); then
    printf 'Usage: scripts/release/verify-signed-file.sh FILE BUNDLE TRUST-ROOT\n' >&2
    exit 2
fi

readonly TASK_FILE="$1"
readonly TASK_BUNDLE="$2"
readonly TASK_TRUST_ROOT="$3"
readonly TASK_IDENTITY="${TARKOV_RELEASE_SIGNER_IDENTITY:-https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main}"
readonly TASK_ISSUER="${TARKOV_RELEASE_SIGNER_ISSUER:-https://token.actions.githubusercontent.com}"

if ! command -v cosign >/dev/null 2>&1; then
    printf 'Signature verification failed: cosign is unavailable\n' >&2
    exit 1
fi
for required in "${TASK_FILE}" "${TASK_BUNDLE}" "${TASK_TRUST_ROOT}"; do
    if [[ ! -f "${required}" || ! -s "${required}" ]]; then
        printf 'Signature verification failed: missing or empty input: %s\n' "${required}" >&2
        exit 1
    fi
done

if ! TASK_RESULT="$(cosign verify-blob \
    --bundle "${TASK_BUNDLE}" \
    --trusted-root "${TASK_TRUST_ROOT}" \
    --certificate-identity "${TASK_IDENTITY}" \
    --certificate-oidc-issuer "${TASK_ISSUER}" \
    "${TASK_FILE}" 2>&1)"; then
    printf 'Signature verification failed for %s:\n%s\n' "$(basename "${TASK_FILE}")" "${TASK_RESULT}" >&2
    exit 1
fi
