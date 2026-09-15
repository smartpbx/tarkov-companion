#!/usr/bin/env bash
# The upper-case names documented below are this script's interface, set by the caller.
# shellcheck disable=SC2153
set -euo pipefail
# Without this a failure inside $(...) does not stop the substitution, and a function that
# verifies and then prints could hand back its output after the verification failed.
shopt -s inherit_errexit

# Performs one release-ring transition against the private feed.
#
# Order is the whole point. The current decision is read and its signature verified, the policy
# computes the next one, that is signed and read back, an immutable build (for publish) is made
# public and verified asset by asset, and only then is the new decision created. The decision is
# created with a name the previous generation implies, so a writer that lost a race is refused
# by GitHub rather than overwriting the winner; this recomputes from the new state and tries
# again, a bounded number of times.
#
# Environment:
#   ACTION RING FEED_REPOSITORY SOURCE_REPOSITORY WORKFLOW_RUN_ID ACTOR REASON GH_TOKEN
#   TARKOV_SIGSTORE_TRUST_ROOT, and for publish: RELEASE_DIR VERIFICATION_RUN_ID
#   Optional: TRANSITION_ATTEMPTS (default 3), GITHUB_OUTPUT, GITHUB_STEP_SUMMARY

TASK_PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly TASK_PROJECT_ROOT
readonly TASK_RELEASE="${TASK_PROJECT_ROOT}/scripts/release"
readonly TASK_ATTEMPTS="${TRANSITION_ATTEMPTS:-3}"
TASK_WORK="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/tarkov-transition.XXXXXX")"
readonly TASK_WORK
trap 'rm -rf -- "${TASK_WORK}"' EXIT

fail() {
    printf 'Release transition failed: %s\n' "$1" >&2
    exit 1
}

if [[ ! "${TASK_ATTEMPTS}" =~ ^[1-9][0-9]?$ ]] || ((TASK_ATTEMPTS > 10)); then
    fail "TRANSITION_ATTEMPTS must be a canonical integer from 1 through 10"
fi

summary() {
    if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
        printf '%s\n' "$@" >> "${GITHUB_STEP_SUMMARY}"
    fi
}

for name in ACTION RING FEED_REPOSITORY SOURCE_REPOSITORY WORKFLOW_RUN_ID ACTOR GH_TOKEN TARKOV_SIGSTORE_TRUST_ROOT; do
    [[ -n "${!name:-}" ]] || fail "${name} is not set"
done
[[ "${RING}" =~ ^(canary|beta|stable)$ ]] || fail "unknown ring ${RING}"
[[ "${ACTION}" =~ ^(publish|promote|pause|resume|mark-lkg|rollback)$ ]] || fail "unknown action ${ACTION}"
if [[ "${ACTION}" == "publish" ]]; then
    [[ -n "${RELEASE_DIR:-}" && -n "${VERIFICATION_RUN_ID:-}" ]] || fail "publish needs RELEASE_DIR and VERIFICATION_RUN_ID"
    [[ "${RING}" == "canary" ]] || fail "a verified build can only enter canary"
fi

feed() {
    python3 "${TASK_RELEASE}/feed.py" --repository "${FEED_REPOSITORY}" "$@"
}

# Reads a ring, verifies its newest decision and checks it belongs to this feed and ring.
# Leaves state.json, a verified generation file and, for an existing ring, payload.json. It is
# called directly rather than inside $(...), so any failure here stops the transition.
read_verified_ring() {
    local ring="$1" directory="$2" generation
    feed ring-read --ring "${ring}" --output-dir "${directory}" >/dev/null
    generation="$(jq -er '.generation' "${directory}/state.json")"
    if ((generation > 0)); then
        "${TASK_RELEASE}/verify-envelope.sh" \
            "${directory}/envelope.json" "${directory}/payload.json" "${directory}/bundle.json" \
            "${TARKOV_SIGSTORE_TRUST_ROOT}"
        python3 "${TASK_RELEASE}/release_policy.py" inspect \
            --payload "${directory}/payload.json" --ring "${ring}" \
            --feed "${FEED_REPOSITORY}" --generation "${generation}" >/dev/null
    fi
    printf '%s\n' "${generation}" > "${directory}/verified-generation"
}

feed check-feed --source-repository "${SOURCE_REPOSITORY}" >/dev/null

manifest=""
build_pending=0
if [[ "${ACTION}" == "publish" ]]; then
    manifest="${RELEASE_DIR}/release-manifest.json"
    [[ -s "${manifest}" && -s "${manifest}.sigstore.json" ]] || fail "the signed release manifest is missing"
    "${TASK_RELEASE}/verify-signed-file.sh" \
        "${manifest}" "${manifest}.sigstore.json" "${TARKOV_SIGSTORE_TRUST_ROOT}"
    version="$(jq -er '.version' "${manifest}")"
    tag="v2-build-${version}"
    feed build-inspect --tag "${tag}" --output-dir "${TASK_WORK}/existing" >/dev/null
    case "$(jq -er '.status' "${TASK_WORK}/existing/state.json")" in
        published)
            # An earlier attempt got as far as publishing the build and no further. Its manifest
            # is used only if it verifies and describes the same verified bytes as this one, and
            # only if GitHub reports the release itself immutable.
            jq -e '.immutable == true' "${TASK_WORK}/existing/state.json" >/dev/null \
                || fail "the published ${tag} is not immutable; no ring decision will name it"
            "${TASK_RELEASE}/verify-signed-file.sh" \
                "${TASK_WORK}/existing/release-manifest.json" \
                "${TASK_WORK}/existing/release-manifest.json.sigstore.json" \
                "${TARKOV_SIGSTORE_TRUST_ROOT}"
            feed build-adopt --tag "${tag}" --existing-dir "${TASK_WORK}/existing" --candidate-manifest "${manifest}" >/dev/null
            manifest="${TASK_WORK}/existing/release-manifest.json"
            printf 'Using the already published and verified build %s\n' "${tag}"
            ;;
        absent | draft)
            build_pending=1
            ;;
        *)
            fail "unexpected build state for ${tag}"
            ;;
    esac
fi

for ((attempt = 1; attempt <= TASK_ATTEMPTS; attempt++)); do
    round="${TASK_WORK}/attempt-${attempt}"
    mkdir -p "${round}"
    read_verified_ring "${RING}" "${round}/current"
    current_generation="$(<"${round}/current/verified-generation")"

    arguments=(
        next
        --ring "${RING}" --action "${ACTION}" --actor "${ACTOR}" --reason "${REASON:-}"
        --feed "${FEED_REPOSITORY}" --workflow-run-id "${WORKFLOW_RUN_ID}"
        --current-generation "${current_generation}"
        --output "${round}/next.json"
    )
    if ((current_generation > 0)); then
        arguments+=(--current "${round}/current/payload.json")
    fi
    if [[ "${ACTION}" == "publish" ]]; then
        arguments+=(--manifest "${manifest}" --verification-run-id "${VERIFICATION_RUN_ID}")
    elif [[ "${ACTION}" == "promote" ]]; then
        case "${RING}" in
            beta) source_ring=canary ;;
            stable) source_ring=beta ;;
            *) fail "canary receives verified builds; it is not promoted into" ;;
        esac
        read_verified_ring "${source_ring}" "${round}/source"
        source_generation="$(<"${round}/source/verified-generation")"
        ((source_generation > 0)) || fail "the ${source_ring} ring has no release to promote"
        arguments+=(--source "${round}/source/payload.json" --source-generation "${source_generation}")
    fi

    set +e
    name="$(python3 "${TASK_RELEASE}/release_policy.py" "${arguments[@]}")"
    policy_status=$?
    set -e
    if ((policy_status == 3)); then
        printf '::notice::%s %s was superseded; %s is unchanged.\n' "${ACTION}" "${version:-}" "${RING}"
        summary "- ${ACTION} was superseded by a newer ${RING} release; nothing was changed."
        exit 0
    fi
    ((policy_status == 0)) || fail "the release policy refused ${ACTION} on ${RING}"
    generation="$(jq -er '.generation' "${round}/next.json")"

    "${TASK_RELEASE}/sign-files.sh" "${round}/next.json"
    python3 "${TASK_RELEASE}/release_policy.py" pack-envelope \
        --payload "${round}/next.json" --bundle "${round}/next.json.sigstore.json" --output "${round}/${name}"
    "${TASK_RELEASE}/verify-envelope.sh" \
        "${round}/${name}" "${round}/readback.json" "${round}/readback.bundle.json" "${TARKOV_SIGSTORE_TRUST_ROOT}"
    cmp -s "${round}/next.json" "${round}/readback.json" || fail "the packed envelope does not carry the signed bytes"

    if ((build_pending)); then
        feed build-publish --tag "${tag}" --directory "${RELEASE_DIR}" --manifest "${manifest}"
        build_pending=0
    fi

    set +e
    feed ring-commit --ring "${RING}" --envelope "${round}/${name}" --generation "${generation}"
    commit_status=$?
    set -e
    if ((commit_status == 0)); then
        printf 'Committed %s generation %s (%s)\n' "${RING}" "${generation}" "${ACTION}"
        summary \
            "- Ring: ${RING}, action: ${ACTION}, signed generation: ${generation}" \
            "- Release: $(jq -r '.release.version + " (" + .release.commit + ")"' "${round}/next.json")" \
            "- Paused: $(jq -r '.paused' "${round}/next.json"); rollback authorized: $(jq -r '.rollback != null' "${round}/next.json")"
        if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
            {
                printf 'generation=%s\n' "${generation}"
                printf 'version=%s\n' "$(jq -r '.release.version' "${round}/next.json")"
            } >> "${GITHUB_OUTPUT}"
        fi
        exit 0
    fi
    ((commit_status == 3)) || fail "the feed refused ${RING} generation ${generation}"
    printf '::warning::%s changed while this transition was being prepared; recomputing (attempt %s of %s)\n' \
        "${RING}" "${attempt}" "${TASK_ATTEMPTS}"
done

fail "${RING} kept changing; nothing was overwritten, and the transition can be dispatched again"
