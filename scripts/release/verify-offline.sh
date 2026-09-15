#!/usr/bin/env bash
set -euo pipefail

# Verifies an offline release bundle without the feed: the recovery path when the private feed
# is unreachable, or when media is being prepared for a machine that has no network at all.
#
# A bundle is the files of one immutable build release (release-manifest.json, every artifact
# the manifest names, and a .sigstore.json bundle beside each), optionally with the signed ring
# decision that selected it. Offline media is a delivery route, not an authorization: nothing is
# accepted here that the online path would not accept.
if (($# != 2)); then
    printf 'Usage: scripts/release/verify-offline.sh BUNDLE-DIRECTORY TRUST-ROOT\n' >&2
    exit 2
fi

TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT
readonly TASK_VERIFY="${TASK_PROJECT_ROOT}/scripts/release/verify-signed-file.sh"
TASK_BUNDLE="$(cd "$1" && pwd)"
readonly TASK_BUNDLE
readonly TASK_TRUST_ROOT="$2"
readonly TASK_MANIFEST="${TASK_BUNDLE}/release-manifest.json"
TASK_WORK="$(mktemp -d "${TMPDIR:-/tmp}/tarkov-offline.XXXXXX")"
readonly TASK_WORK
trap 'rm -rf -- "${TASK_WORK}"' EXIT

fail() {
    printf 'Offline verification failed: %s\n' "$1" >&2
    exit 1
}

for command_name in cosign jq sha256sum python3; do
    command -v "${command_name}" >/dev/null 2>&1 || fail "required command is unavailable: ${command_name}"
done

"${TASK_VERIFY}" "${TASK_MANIFEST}" "${TASK_MANIFEST}.sigstore.json" "${TASK_TRUST_ROOT}"
jq -e '.schemaVersion == 1
       and (.version | type == "string") and (.commit | type == "string" and test("^[0-9a-f]{40}$"))
       and (.artifacts | type == "array" and length > 0)
       and ([.artifacts[].name] | length == (unique | length))' \
    "${TASK_MANIFEST}" >/dev/null || fail "the signed manifest is malformed"

count=0
while IFS=$'\t' read -r name expected size; do
    if [[ ! "${name}" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ || "${name}" == *..* || ! "${expected}" =~ ^[0-9a-f]{64}$ ]]; then
        fail "the manifest names an unsafe artifact: ${name}"
    fi
    file="${TASK_BUNDLE}/${name}"
    [[ -f "${file}" ]] || fail "the bundle is missing ${name}"
    [[ "$(sha256sum "${file}" | awk '{print $1}')" == "${expected}" && "$(stat -c %s "${file}")" == "${size}" ]] \
        || fail "${name} does not match the signed manifest"
    "${TASK_VERIFY}" "${file}" "${file}.sigstore.json" "${TASK_TRUST_ROOT}"
    count=$((count + 1))
done < <(jq -r '.artifacts[] | [.name, .sha256, (.size | tostring)] | @tsv' "${TASK_MANIFEST}")

manifest_sha="$(sha256sum "${TASK_MANIFEST}" | awk '{print $1}')"
index="$(find "${TASK_BUNDLE}" -maxdepth 1 -type f -name 'release-index-g[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9].json' -printf '%f\n' | sort | tail -n 1)"
decision="no signed ring decision included"
if [[ -n "${index}" ]]; then
    "${TASK_PROJECT_ROOT}/scripts/release/verify-envelope.sh" \
        "${TASK_BUNDLE}/${index}" "${TASK_WORK}/index.json" "${TASK_WORK}/index.bundle.json" "${TASK_TRUST_ROOT}"
    jq -e --arg sha "${manifest_sha}" '.release.manifestSha256 == $sha' "${TASK_WORK}/index.json" >/dev/null \
        || fail "${index} selects a different manifest from the one in this bundle"
    decision="$(jq -r '"\(.ring) generation \(.generation) (\(.authorization.action)\(if .paused then ", paused" else "" end))"' "${TASK_WORK}/index.json")"
fi

printf 'Offline bundle verified: version %s, commit %s, %s artifacts, %s\n' \
    "$(jq -r .version "${TASK_MANIFEST}")" "$(jq -r .commit "${TASK_MANIFEST}")" "${count}" "${decision}"
