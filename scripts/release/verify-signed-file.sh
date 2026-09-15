#!/usr/bin/env bash
set -euo pipefail

# The one place a release signature is checked, so the identity rule cannot drift between the
# publisher, the offline verifier and the tests.
#
# The identity is the reviewed publishing workflow on main, not "anything GitHub signed". A
# workflow dispatched from another branch gets a certificate naming that branch, and a feed
# credential can replace bytes and bundles together but cannot mint this certificate. The
# certificate's GitHub repository and ref claims are required as well as its subject, so a
# workflow in some other repository that calls ours cannot present the same subject. The trust
# root is a file provisioned separately from the feed, so verification does not depend on the
# network, or on anything the feed serves.
#
# Two things are refused before cosign sees anything:
#
# - A cosign that is not byte-for-byte a pinned build. Whatever `cosign` happens to be first on
#   PATH is not a verifier anybody reviewed, and cosign before v3.1.3 accepted a legacy bundle
#   whose certificate was a bare public key without checking the identity at all
#   (GHSA-fx35-mq7g-6g98).
# - Any bundle but a standardized v0.3 message-signature bundle for exactly these bytes: one
#   Fulcio certificate, one transparency-log entry, a SHA2_256 digest equal to the file's. The
#   legacy format, DSSE envelopes, bare keys and certificate chains are not something this
#   publisher produces, so there is no reason to let a verifier's format detection decide them.
if (($# != 3)); then
    printf 'Usage: scripts/release/verify-signed-file.sh FILE BUNDLE TRUST-ROOT\n' >&2
    exit 2
fi

TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT
readonly TASK_FILE="$1"
readonly TASK_BUNDLE="$2"
readonly TASK_TRUST_ROOT="$3"
readonly TASK_IDENTITY="${TARKOV_RELEASE_SIGNER_IDENTITY:-https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main}"
readonly TASK_ISSUER="${TARKOV_RELEASE_SIGNER_ISSUER:-https://token.actions.githubusercontent.com}"
readonly TASK_REPOSITORY="${TARKOV_RELEASE_SIGNER_REPOSITORY:-smartpbx/tarkov-companion}"
readonly TASK_REF="${TARKOV_RELEASE_SIGNER_REF:-refs/heads/main}"
readonly TASK_BUNDLE_MEDIA_TYPE="application/vnd.dev.sigstore.bundle.v0.3+json"

fail() {
    printf 'Signature verification failed: %s\n' "$1" >&2
    exit 1
}

for required in "${TASK_FILE}" "${TASK_BUNDLE}" "${TASK_TRUST_ROOT}"; do
    if [[ ! -f "${required}" || ! -s "${required}" ]]; then
        fail "missing or empty input: ${required}"
    fi
done
[[ -n "${TASK_IDENTITY}" && -n "${TASK_ISSUER}" && -n "${TASK_REPOSITORY}" && -n "${TASK_REF}" ]] \
    || fail "the signer identity, issuer, repository and ref must all be set"

TASK_COSIGN="$("${TASK_PROJECT_ROOT}/scripts/release/pinned-cosign.sh")" || exit 1
readonly TASK_COSIGN

file_sha="$(sha256sum -- "${TASK_FILE}" | awk '{print $1}')"
if ! bundle_sha="$(jq -r \
    --arg mediaType "${TASK_BUNDLE_MEDIA_TYPE}" \
    'if type == "object"
        and (keys - ["mediaType", "verificationMaterial", "messageSignature"] | length == 0)
        and .mediaType == $mediaType
        and (.verificationMaterial | type == "object")
        and (.verificationMaterial | keys - ["certificate", "tlogEntries", "timestampVerificationData"] | length == 0)
        and (.verificationMaterial.certificate | type == "object")
        and (.verificationMaterial.certificate.rawBytes | type == "string" and length > 0)
        and (.verificationMaterial.tlogEntries | type == "array" and length == 1)
        and (.messageSignature | type == "object")
        and (.messageSignature | keys - ["messageDigest", "signature"] | length == 0)
        and (.messageSignature.signature | type == "string" and length > 0)
        and .messageSignature.messageDigest.algorithm == "SHA2_256"
        and (.messageSignature.messageDigest.digest | type == "string" and test("^[A-Za-z0-9+/]{43}=$"))
     then .messageSignature.messageDigest.digest else error("not a standardized bundle") end' \
    "${TASK_BUNDLE}" 2>/dev/null)"; then
    fail "$(basename "${TASK_BUNDLE}") is not a standardized v0.3 Sigstore message-signature bundle"
fi
bundle_sha="$(base64 --decode <<<"${bundle_sha}" | od -An -v -tx1 | tr -d ' \n')"
[[ "${bundle_sha}" == "${file_sha}" ]] || fail "$(basename "${TASK_BUNDLE}") signs different bytes from $(basename "${TASK_FILE}")"

if ! TASK_RESULT="$("${TASK_COSIGN}" verify-blob \
    --bundle "${TASK_BUNDLE}" \
    --trusted-root "${TASK_TRUST_ROOT}" \
    --certificate-identity "${TASK_IDENTITY}" \
    --certificate-oidc-issuer "${TASK_ISSUER}" \
    --certificate-github-workflow-repository "${TASK_REPOSITORY}" \
    --certificate-github-workflow-ref "${TASK_REF}" \
    "${TASK_FILE}" 2>&1)"; then
    printf 'Signature verification failed for %s:\n%s\n' "$(basename "${TASK_FILE}")" "${TASK_RESULT}" >&2
    exit 1
fi
