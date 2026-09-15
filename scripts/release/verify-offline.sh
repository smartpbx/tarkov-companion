#!/usr/bin/env bash
set -euo pipefail

# Verifies an offline release bundle without the feed: the recovery path when the private feed
# is unreachable, or when media is being prepared for a machine that has no network at all.
#
# A bundle is the files of one immutable build release (release-manifest.json, every artifact the
# manifest names, and a .sigstore.json bundle beside each) and the signed ring decision that
# selected it. Every file is copied into a private directory first and verified there, so media
# that changes after it is read changes nothing that was checked; --output keeps that verified
# copy for whatever installs from it.
#
# By default the ring's policy applies as it does online: the decision must be signed, name the
# given feed and ring, select exactly this manifest, not be paused (unless it is a signed
# rollback), and be at or above --minimum-generation. --break-glass verifies the manifest and
# every file without a ring decision. It says so, because that is the operator's authority
# replacing the ring's, not the ring's policy applied.
usage() {
    printf 'Usage: scripts/release/verify-offline.sh (--ring RING --feed OWNER/NAME [--minimum-generation N] | --break-glass) [--output DIRECTORY] BUNDLE-DIRECTORY TRUST-ROOT\n' >&2
    exit 2
}

TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT
readonly TASK_VERIFY="${TASK_PROJECT_ROOT}/scripts/release/verify-signed-file.sh"
readonly TASK_LIMITS="${TASK_PROJECT_ROOT}/scripts/release/resource_limits.py"
readonly TASK_MAX_JSON_BYTES=$((16 * 1024 * 1024))
readonly TASK_MAX_SIGNATURE_BYTES=$((2 * 1024 * 1024))
readonly TASK_MAX_ARTIFACT_BYTES=$((512 * 1024 * 1024))
readonly TASK_MAX_TOTAL_BYTES=$((1024 * 1024 * 1024))
readonly TASK_MAX_ARTIFACTS=4096
TASK_TAKEN_COUNT=0
TASK_TAKEN_BYTES=0

ring=""
feed=""
minimum_generation=0
break_glass=0
output=""
while (($#)); do
    case "$1" in
        --ring) (($# >= 2)) || usage; ring="$2"; shift 2 ;;
        --feed) (($# >= 2)) || usage; feed="$2"; shift 2 ;;
        --minimum-generation) (($# >= 2)) || usage; minimum_generation="$2"; shift 2 ;;
        --break-glass) break_glass=1; shift ;;
        --output) (($# >= 2)) || usage; output="$2"; shift 2 ;;
        --) shift; break ;;
        -*) usage ;;
        *) break ;;
    esac
done
(($# == 2)) || usage
readonly TASK_SOURCE="$1"
readonly TASK_TRUST_ROOT="$2"

fail() {
    printf 'Offline verification failed: %s\n' "$1" >&2
    exit 1
}

for command_name in head jq sha256sum python3 stat; do
    command -v "${command_name}" >/dev/null 2>&1 || fail "required command is unavailable: ${command_name}"
done
if ((break_glass)); then
    [[ -z "${ring}" && -z "${feed}" ]] || fail "--break-glass checks no ring decision; do not combine it with --ring or --feed"
else
    [[ "${ring}" =~ ^(canary|beta|stable)$ ]] || fail "--ring canary|beta|stable is required, or --break-glass"
    [[ "${feed}" =~ ^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$ ]] || fail "--feed OWNER/NAME is required, or --break-glass"
fi
[[ "${minimum_generation}" =~ ^[0-9]{1,10}$ ]] || fail "--minimum-generation must be a whole number"
[[ -d "${TASK_SOURCE}" ]] || fail "the bundle directory does not exist"
[[ -f "${TASK_TRUST_ROOT}" && ! -L "${TASK_TRUST_ROOT}" && -s "${TASK_TRUST_ROOT}" ]] \
    || fail "the trust root is missing or is not a plain file"
python3 "${TASK_LIMITS}" validate-json --maximum "${TASK_MAX_JSON_BYTES}" "${TASK_TRUST_ROOT}" >/dev/null \
    || fail "the trust root is not bounded valid JSON"
[[ -z "${output}" || ! -e "${output}" ]] || fail "--output ${output} already exists"

TASK_WORK="$(mktemp -d "${TMPDIR:-/tmp}/tarkov-offline.XXXXXX")"
readonly TASK_WORK
chmod 0700 "${TASK_WORK}"
trap 'rm -rf -- "${TASK_WORK}"' EXIT
readonly TASK_BUNDLE="${TASK_WORK}/bundle"
mkdir -m 0700 "${TASK_BUNDLE}"

# One named, plain file off the media into the private copy.
take() {
    local name="$1" maximum="$2" source size copied
    if [[ ! "${name}" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ || "${name}" == *..* ]]; then
        fail "the manifest names an unsafe artifact: ${name}"
    fi
    source="${TASK_SOURCE}/${name}"
    [[ -f "${source}" && ! -L "${source}" ]] || fail "the bundle is missing ${name}, or it is not a plain file"
    size="$(stat -c %s -- "${source}")"
    ((size > 0 && size <= maximum)) || fail "${name} is ${size} bytes, outside its ${maximum}-byte limit"
    ((TASK_TAKEN_COUNT < TASK_MAX_ARTIFACTS * 2 + 4)) || fail "the bundle contains too many selected files"
    ((TASK_TAKEN_BYTES + size <= TASK_MAX_TOTAL_BYTES)) || fail "the selected bundle exceeds its total byte limit"
    head -c "$((maximum + 1))" -- "${source}" > "${TASK_BUNDLE}/${name}"
    copied="$(stat -c %s -- "${TASK_BUNDLE}/${name}")"
    ((copied == size && copied <= maximum)) || fail "${name} changed or exceeded its byte limit while copied"
    TASK_TAKEN_COUNT=$((TASK_TAKEN_COUNT + 1))
    TASK_TAKEN_BYTES=$((TASK_TAKEN_BYTES + copied))
}

readonly TASK_MANIFEST="${TASK_BUNDLE}/release-manifest.json"
take release-manifest.json "${TASK_MAX_JSON_BYTES}"
take release-manifest.json.sigstore.json "${TASK_MAX_SIGNATURE_BYTES}"
python3 "${TASK_LIMITS}" validate-json --maximum "${TASK_MAX_JSON_BYTES}" "${TASK_MANIFEST}" >/dev/null \
    || fail "the release manifest is not bounded valid JSON"
python3 "${TASK_LIMITS}" validate-json --maximum "${TASK_MAX_SIGNATURE_BYTES}" "${TASK_MANIFEST}.sigstore.json" >/dev/null \
    || fail "the manifest signature bundle is not bounded valid JSON"
"${TASK_VERIFY}" "${TASK_MANIFEST}" "${TASK_MANIFEST}.sigstore.json" "${TASK_TRUST_ROOT}"
jq -e '.schemaVersion == 1
       and (.version | type == "string") and (.commit | type == "string" and test("^[0-9a-f]{40}$"))
       and (.artifacts | type == "array" and length > 0 and length <= 4096)' \
    "${TASK_MANIFEST}" >/dev/null || fail "the signed manifest is malformed"
python3 - "${TASK_PROJECT_ROOT}/scripts/release" "$(jq -r .version "${TASK_MANIFEST}")" <<'PY' || fail "the signed manifest names an unsupported version"
import sys
sys.path.insert(0, sys.argv[1])
from release_policy import semver_key
semver_key(sys.argv[2])
PY

readonly TASK_ARTIFACT_TABLE="${TASK_WORK}/artifacts.tsv"
# A process substitution would hide jq's exit status from this shell. Materialize the signed
# table first so a malformed later row cannot turn verification of an artifact prefix into a
# successful verification of the whole manifest.
jq -er '
    if (all(.artifacts[];
            type == "object"
            and ((.name | type) == "string")
            and ((.sha256 | type) == "string")
            and (.sha256 | test("^[0-9a-f]{64}$"))
            and ((.size | type) == "number")
            and (.size == (.size | floor))
            and (.size > 0 and .size <= 536870912))
        and ([.artifacts[].name] | length == (unique | length))) then
        .artifacts[] | [.name, .sha256, (.size | tostring)] | @tsv
    else
        error("malformed artifact table")
    end' \
    "${TASK_MANIFEST}" > "${TASK_ARTIFACT_TABLE}" \
    || fail "the signed manifest artifact table is malformed"

count=0
while IFS=$'\t' read -r name expected size; do
    [[ "${expected}" =~ ^[0-9a-f]{64}$ && "${size}" =~ ^(0|[1-9][0-9]{0,9})$ ]] \
        || fail "the manifest entry for ${name} is malformed"
    ((size > 0 && size <= TASK_MAX_ARTIFACT_BYTES)) || fail "the manifest entry for ${name} exceeds the artifact limit"
    ((count < TASK_MAX_ARTIFACTS)) || fail "the manifest names too many artifacts"
    take "${name}" "${TASK_MAX_ARTIFACT_BYTES}"
    take "${name}.sigstore.json" "${TASK_MAX_SIGNATURE_BYTES}"
    file="${TASK_BUNDLE}/${name}"
    [[ "$(sha256sum "${file}" | awk '{print $1}')" == "${expected}" && "$(stat -c %s "${file}")" == "${size}" ]] \
        || fail "${name} does not match the signed manifest"
    "${TASK_VERIFY}" "${file}" "${file}.sigstore.json" "${TASK_TRUST_ROOT}"
    count=$((count + 1))
done < "${TASK_ARTIFACT_TABLE}"
((count == $(jq '.artifacts | length' "${TASK_MANIFEST}"))) \
    || fail "the signed manifest artifact table is incomplete"

manifest_sha="$(sha256sum "${TASK_MANIFEST}" | awk '{print $1}')"
if ((break_glass)); then
    decision="BREAK-GLASS: no ring decision was checked; ring, pause, rollback and generation policy were NOT applied"
    printf 'WARNING: %s\n' "${decision}" >&2
else
    if ! index="$(python3 "${TASK_LIMITS}" select-ring "${TASK_SOURCE}")"; then
        fail "the bundle holds no bounded plain signed ring decision; add the ring's release-index-g*.json, or use --break-glass"
    fi
    take "${index}" "${TASK_MAX_JSON_BYTES}"
    python3 "${TASK_LIMITS}" validate-json --maximum "${TASK_MAX_JSON_BYTES}" "${TASK_BUNDLE}/${index}" >/dev/null \
        || fail "the signed ring envelope is not bounded valid JSON"
    generation="${index#release-index-g}"
    generation="$((10#${generation%.json}))"
    "${TASK_PROJECT_ROOT}/scripts/release/verify-envelope.sh" \
        "${TASK_BUNDLE}/${index}" "${TASK_WORK}/index.json" "${TASK_WORK}/index.bundle.json" "${TASK_TRUST_ROOT}"
    python3 "${TASK_PROJECT_ROOT}/scripts/release/release_policy.py" inspect \
        --payload "${TASK_WORK}/index.json" --ring "${ring}" --feed "${feed}" --generation "${generation}" >/dev/null \
        || fail "${index} is not a valid ${ring} decision for ${feed}"
    jq -e --arg sha "${manifest_sha}" \
        --arg version "$(jq -r .version "${TASK_MANIFEST}")" --arg commit "$(jq -r .commit "${TASK_MANIFEST}")" \
        '.release.manifestSha256 == $sha and .release.version == $version and .release.commit == $commit' \
        "${TASK_WORK}/index.json" >/dev/null \
        || fail "${index} selects a different build from the manifest in this bundle"
    ((generation >= minimum_generation)) || fail "${ring} generation ${generation} is below the required generation ${minimum_generation}"
    if jq -e '.paused and .rollback == null' "${TASK_WORK}/index.json" >/dev/null; then
        fail "${ring} is paused at generation ${generation}; consumers hold where they are"
    fi
    decision="$(jq -r '"\(.ring) generation \(.generation) (\(.authorization.action)\(if .rollback != null then ", signed rollback" else "" end))"' "${TASK_WORK}/index.json")"
fi

if [[ -n "${output}" ]]; then
    mv -T -- "${TASK_BUNDLE}" "${output}"
fi
printf 'Offline bundle verified: version %s, commit %s, %s artifacts, %s\n' \
    "$(jq -r .version "${output:-${TASK_BUNDLE}}/release-manifest.json")" \
    "$(jq -r .commit "${output:-${TASK_BUNDLE}}/release-manifest.json")" "${count}" "${decision}"
