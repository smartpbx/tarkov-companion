#!/usr/bin/env bash
set -euo pipefail

# Proves scripts/release/verify-signed-file.sh against real Sigstore material, not a fake.
#
# A release signature cannot be minted from a pull request: the only identity it accepts belongs
# to the publisher on main. What can be proven here is that the verification command itself is
# right: a real keyless bundle made by a GitHub Actions release workflow verifies offline against
# a trust root file when its subject, repository and ref all match, and each of these is refused:
# the wrong subject, repository or ref, altered bytes, a trust root without the signing CA, and
# a legacy bundle carrying a bare public key (GHSA-fx35-mq7g-6g98) - by the wrapper before cosign
# runs, and by the pinned cosign itself.
#
# The material is sigstore/timestamp-authority v2.1.3's checksums file and the keyless bundle
# its release workflow published beside it, both pinned by digest.
readonly TASK_RELEASE="https://github.com/sigstore/timestamp-authority/releases/download/v2.1.3"
readonly TASK_SUBJECT_NAME="timestamp-authority_checksums.txt"
readonly TASK_SUBJECT_SHA256="e9de4c0847d9f03eb0d51d4bdcc4f1ab931451f04152303fe0a08b8b9ec458d8"
readonly TASK_BUNDLE_SHA256="59ef58395d8d299c5c16d05692009514ae84b19991d7ae856b268592b8a49c4b"
readonly TASK_IDENTITY="https://github.com/sigstore/timestamp-authority/.github/workflows/release.yaml@refs/tags/v2.1.3"
readonly TASK_ISSUER="https://token.actions.githubusercontent.com"
readonly TASK_REPOSITORY="sigstore/timestamp-authority"
readonly TASK_REF="refs/tags/v2.1.3"
readonly TASK_MAX_FIXTURE_BYTES=$((2 * 1024 * 1024))

TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT
readonly TASK_VERIFY="${TASK_PROJECT_ROOT}/scripts/release/verify-signed-file.sh"
TASK_WORK="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/tarkov-real-sigstore.XXXXXX")"
readonly TASK_WORK
trap 'rm -rf -- "${TASK_WORK}"' EXIT

# Refuses to run at all with anything but a pinned cosign: the point is to test that one.
TASK_COSIGN="$("${TASK_PROJECT_ROOT}/scripts/release/pinned-cosign.sh")"
readonly TASK_COSIGN
[[ "$("${TASK_COSIGN}" version 2>&1 | awk '/^GitVersion:/ {print $2}')" == "v3.1.3" ]] \
    || { printf 'The pinned cosign does not report v3.1.3\n' >&2; exit 1; }

fetch() {
    ( ulimit -f "$(((TASK_MAX_FIXTURE_BYTES + 1023) / 1024))"
      curl --fail --silent --show-error --location --max-filesize "${TASK_MAX_FIXTURE_BYTES}" \
          --output "${TASK_WORK}/$1" "${TASK_RELEASE}/$1" )
    [[ "$(stat -c %s -- "${TASK_WORK}/$1")" -le "${TASK_MAX_FIXTURE_BYTES}" ]] \
        || { printf 'The downloaded %s exceeds its fixture limit\n' "$1" >&2; exit 1; }
    [[ "$(sha256sum "${TASK_WORK}/$1" | awk '{print $1}')" == "$2" ]] \
        || { printf 'The downloaded %s is not the pinned one\n' "$1" >&2; exit 1; }
}
fetch "${TASK_SUBJECT_NAME}" "${TASK_SUBJECT_SHA256}"
fetch "${TASK_SUBJECT_NAME}-keyless.sigstore.json" "${TASK_BUNDLE_SHA256}"
readonly TASK_SUBJECT="${TASK_WORK}/${TASK_SUBJECT_NAME}"
readonly TASK_BUNDLE="${TASK_WORK}/${TASK_SUBJECT_NAME}-keyless.sigstore.json"
"${TASK_COSIGN}" trusted-root create --with-default-services --out "${TASK_WORK}/trusted-root.json"

# Exercise the desktop's production C# boundary against the same real bundle, not only its
# deterministic process double. The exact filtered test also alters the subject and requires the
# verifier to refuse it before reporting success.
env TARKOV_REAL_SIGSTORE_CSHARP=1 \
    TARKOV_REAL_SIGSTORE_SUBJECT="${TASK_SUBJECT}" \
    TARKOV_REAL_SIGSTORE_BUNDLE="${TASK_BUNDLE}" \
    TARKOV_REAL_SIGSTORE_COSIGN="${TASK_COSIGN}" \
    TARKOV_REAL_SIGSTORE_COSIGN_SHA256="$(sha256sum "${TASK_COSIGN}" | awk '{print $1}')" \
    TARKOV_REAL_SIGSTORE_TRUST_ROOT="${TASK_WORK}/trusted-root.json" \
    TARKOV_REAL_SIGSTORE_IDENTITY="${TASK_IDENTITY}" \
    TARKOV_REAL_SIGSTORE_ISSUER="${TASK_ISSUER}" \
    TARKOV_REAL_SIGSTORE_REPOSITORY="${TASK_REPOSITORY}" \
    TARKOV_REAL_SIGSTORE_REF="${TASK_REF}" \
    dotnet test "${TASK_PROJECT_ROOT}/tests/TarkovCompanion.UnitTests/TarkovCompanion.UnitTests.csproj" \
        --no-restore --filter \
        'FullyQualifiedName=TarkovCompanion.UnitTests.CosignReleaseSignatureVerifierTests.ARealV03BundleIsAcceptedAndAlteredBytesAreRefusedByTheProductionProcessBoundary'

expect() {
    local expected="$1" label="$2" pattern="$3" status=0 wrong=0
    shift 3
    "$@" >"${TASK_WORK}/out.txt" 2>&1 || status=$?
    if [[ "${expected}" == pass ]]; then
        ((status == 0)) || wrong=1
    else
        ((status != 0)) || wrong=1
    fi
    if [[ -n "${pattern}" ]] && ! grep -qE -- "${pattern}" "${TASK_WORK}/out.txt"; then
        wrong=1
    fi
    if ((wrong)); then
        cat "${TASK_WORK}/out.txt" >&2
        printf 'FAIL: %s (exit %s)\n' "${label}" "${status}" >&2
        exit 1
    fi
    printf 'ok: %s\n' "${label}"
}

as_signer() {
    env TARKOV_RELEASE_SIGNER_IDENTITY="${TASK_IDENTITY}" TARKOV_RELEASE_SIGNER_ISSUER="${TASK_ISSUER}" \
        TARKOV_RELEASE_SIGNER_REPOSITORY="${TASK_REPOSITORY}" TARKOV_RELEASE_SIGNER_REF="${TASK_REF}" "$@"
}

expect pass "a real GitHub Actions keyless bundle verifies against a trust root file" "" \
    as_signer "${TASK_VERIFY}" "${TASK_SUBJECT}" "${TASK_BUNDLE}" "${TASK_WORK}/trusted-root.json"

expect fail "the default rule refuses anything but this repository's publisher on main" "" \
    "${TASK_VERIFY}" "${TASK_SUBJECT}" "${TASK_BUNDLE}" "${TASK_WORK}/trusted-root.json"

expect fail "the right subject from another repository is refused" "GithubWorkflowRepository" \
    as_signer env TARKOV_RELEASE_SIGNER_REPOSITORY=smartpbx/tarkov-companion \
    "${TASK_VERIFY}" "${TASK_SUBJECT}" "${TASK_BUNDLE}" "${TASK_WORK}/trusted-root.json"

expect fail "the right subject at another ref is refused" "GithubWorkflowRef" \
    as_signer env TARKOV_RELEASE_SIGNER_REF=refs/heads/main \
    "${TASK_VERIFY}" "${TASK_SUBJECT}" "${TASK_BUNDLE}" "${TASK_WORK}/trusted-root.json"

cp -- "${TASK_SUBJECT}" "${TASK_WORK}/altered"
printf '\0' >> "${TASK_WORK}/altered"
expect fail "altered bytes are refused before cosign runs" "signs different bytes" \
    as_signer "${TASK_VERIFY}" "${TASK_WORK}/altered" "${TASK_BUNDLE}" "${TASK_WORK}/trusted-root.json"

# The digest check in the wrapper would stop the altered file above before cosign saw it. This
# gives cosign itself the altered bytes with a bundle claiming them, so its signature check is the
# one that must refuse.
jq --arg digest "$(openssl dgst -sha256 -binary "${TASK_WORK}/altered" | base64)" \
    '.messageSignature.messageDigest.digest = $digest' "${TASK_BUNDLE}" > "${TASK_WORK}/altered.sigstore.json"
expect fail "a bundle re-pointed at altered bytes fails cosign's own signature check" "" \
    as_signer "${TASK_VERIFY}" "${TASK_WORK}/altered" "${TASK_WORK}/altered.sigstore.json" "${TASK_WORK}/trusted-root.json"

jq '.certificateAuthorities = []' "${TASK_WORK}/trusted-root.json" > "${TASK_WORK}/no-ca.json"
expect fail "a trust root without the signing certificate authority is refused" "" \
    as_signer "${TASK_VERIFY}" "${TASK_SUBJECT}" "${TASK_BUNDLE}" "${TASK_WORK}/no-ca.json"

# GHSA-fx35-mq7g-6g98: a legacy bundle whose `cert` is a bare public key, with a signature that
# key really made. cosign before v3.1.3 accepted this with the identity flags ignored. v3.1.3
# refuses it with a trust root because a trust root needs the standardized format, and without one
# because it no longer falls back from a certificate that does not parse to a bare key.
openssl ecparam -name prime256v1 -genkey -noout -out "${TASK_WORK}/attacker.key" 2>/dev/null
openssl ec -in "${TASK_WORK}/attacker.key" -pubout -out "${TASK_WORK}/attacker.pub" 2>/dev/null
printf 'attacker payload\n' > "${TASK_WORK}/attacker.bin"
openssl dgst -sha256 -sign "${TASK_WORK}/attacker.key" -out "${TASK_WORK}/attacker.sig" "${TASK_WORK}/attacker.bin"
jq -n --arg sig "$(base64 -w0 "${TASK_WORK}/attacker.sig")" --arg cert "$(base64 -w0 "${TASK_WORK}/attacker.pub")" \
    '{base64Signature: $sig, cert: $cert}' > "${TASK_WORK}/attacker.legacy.json"

expect fail "a legacy bundle carrying a bare public key is refused by the wrapper" "not a standardized v0.3" \
    "${TASK_VERIFY}" "${TASK_WORK}/attacker.bin" "${TASK_WORK}/attacker.legacy.json" "${TASK_WORK}/trusted-root.json"

expect fail "the pinned cosign refuses the same legacy bundle on its own, given a trust root" "only supported with --new-bundle-format" \
    "${TASK_COSIGN}" verify-blob --bundle "${TASK_WORK}/attacker.legacy.json" \
    --trusted-root "${TASK_WORK}/trusted-root.json" \
    --certificate-identity "https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main" \
    --certificate-oidc-issuer "${TASK_ISSUER}" "${TASK_WORK}/attacker.bin"
expect fail "the pinned cosign refuses the legacy bundle without a trust root too" "loading verifier certificate from bundle" \
    "${TASK_COSIGN}" verify-blob --bundle "${TASK_WORK}/attacker.legacy.json" \
    --certificate-identity "https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main" \
    --certificate-oidc-issuer "${TASK_ISSUER}" "${TASK_WORK}/attacker.bin"
