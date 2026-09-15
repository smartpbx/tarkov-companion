#!/usr/bin/env bash
#
# Updates the group relay to the build its signed release ring selects, unattended.
#
# The relay has no interface. Nobody can log into it and press a button, so without this it
# falls behind the desktop client, and the two have to speak the same protocol: the day the
# group key replaced a room and a server-side secret, a client that had updated could not talk
# to a server that had not.
#
# It used to follow the public `dev` release by checksum, and the checksum came from the same
# release as the archive. That detects a corrupt download and nothing else: whoever could change
# the release could change both, including to an older build, and this would install it. Now
# every decision is a signed ring index and every byte is named by a signed manifest, both
# verified against a trust root provisioned on this host and against the one workflow identity
# allowed to publish. The feed repository is transport, not authority.
#
# Everything here is still arranged so that a failure leaves the service running the build it
# was already running, and so that what the stamps say is what is actually installed.
set -euo pipefail
# A failure inside $(...) must stop the script too, or a verification that failed halfway
# through a substitution could still hand its partial output to the next line.
shopt -s inherit_errexit

readonly SOURCE_REPOSITORY="smartpbx/tarkov-companion"
readonly RELEASE_REPOSITORY="${TARKOV_RELEASE_REPOSITORY:-}"
readonly RELEASE_RING="${TARKOV_RELEASE_RING:-stable}"
readonly RELEASE_TOKEN_FILE="${TARKOV_RELEASE_TOKEN_FILE:-/etc/tarkov-group/release-feed.token}"
readonly TRUST_ROOT="${TARKOV_SIGSTORE_TRUST_ROOT:-/etc/tarkov-group/sigstore-trusted-root.json}"
readonly SIGNER_IDENTITY="${TARKOV_RELEASE_SIGNER_IDENTITY:-https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main}"
readonly SIGNER_ISSUER="${TARKOV_RELEASE_SIGNER_ISSUER:-https://token.actions.githubusercontent.com}"
# A directory holding a signed ring index, its manifest, the relay archive and their bundles.
# Set, it replaces the network entirely: the recovery path when the feed is down or unreachable.
readonly OFFLINE_BUNDLE="${TARKOV_RELEASE_BUNDLE_DIR:-}"
readonly INSTALL="${TARKOV_UPDATE_INSTALL:-/opt/tarkov-group}"
readonly LKG="${TARKOV_UPDATE_LKG:-/opt/tarkov-group.lkg}"
# Outside the tree, because the swap replaces the tree.
#
# The stamp once lived at ${INSTALL}/INSTALLED_SHA256 and the rollback restored the OLD stamp
# along with the old build, so a refused build looked brand new to the next tick: fetched,
# swapped and rolled back every thirty minutes, for ever. Moving it out then introduced the
# opposite bug: the new stamp was written before the health check and survived the rollback,
# so the next tick said "already on" a build that had just been refused, and so did the panel.
# Stamps are now written only after success, and restored from a copy if anything after the
# swap fails.
readonly STATE="${TARKOV_UPDATE_STATE:-/var/lib/tarkov-group}"
readonly SERVICE="${TARKOV_UPDATE_SERVICE:-tarkov-group}"
readonly SELF="${TARKOV_UPDATE_SELF:-/opt/tarkov-group-update.sh}"
readonly UNITS="${TARKOV_UPDATE_UNITS:-/etc/systemd/system}"
readonly HEALTH_URL="${TARKOV_UPDATE_HEALTH_URL:-http://127.0.0.1:8090/health}"
readonly HEALTH_ATTEMPTS="${TARKOV_UPDATE_HEALTH_ATTEMPTS:-10}"
readonly HEALTH_INTERVAL="${TARKOV_UPDATE_HEALTH_INTERVAL:-2}"
readonly INCOMING="${INSTALL}.incoming"
readonly PREVIOUS="${INSTALL}.previous"
readonly SHIPPED="${INSTALL}/deploy"
# Written by the relay's admin panel and watched by tarkov-group-update.path. The relay runs
# unprivileged and cannot start a unit; it can write one file in the directory it already owns.
readonly REQUEST="${STATE}/UPDATE_NOW"
readonly REFUSED_RELEASE="${STATE}/REFUSED_RELEASE.json"
# The swap journal: what is being installed and everything needed to undo it, written before the
# service is stopped and removed as the last step of success. If it still exists when a run
# starts, the previous run died mid-swap - killed, or the machine lost power - and is undone.
readonly SWAP="${STATE}/.swap"
readonly INSTALLED_STAMPS=(INSTALLED_SHA256 INSTALLED_VERSION INSTALLED_COMMIT INSTALLED_RING INSTALLED_GENERATION)
readonly VERSION_PATTERN='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-([0-9A-Za-z.-]+))?$'
readonly DEPLOYMENT_UNITS=(tarkov-group-update.service tarkov-group-update.timer tarkov-group-update.path)

TASK_WORK=""
TASK_SWAPPED=0
TASK_COMMITTED=0
TASK_UNITS_CHANGED=0
TARGET_RING="${RELEASE_RING}"
TARGET_SHA=""
TARGET_VERSION=""
TARGET_COMMIT=""
TARGET_PROTOCOL=""
TARGET_GENERATION=""

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }

refuse() {
    log "$*"
    exit 1
}

# 0 when the first version orders before the second under SemVer 2.0 precedence, 1 otherwise.
semver_less() {
    local left=() right=() index
    [[ "$1" =~ ${VERSION_PATTERN} ]] || return 2
    left=("${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}" "${BASH_REMATCH[5]}")
    [[ "$2" =~ ${VERSION_PATTERN} ]] || return 2
    right=("${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}" "${BASH_REMATCH[5]}")
    for index in 0 1 2; do
        if ((10#${left[index]} != 10#${right[index]})); then
            ((10#${left[index]} < 10#${right[index]})) && return 0
            return 1
        fi
    done
    [[ "${left[3]}" == "${right[3]}" ]] && return 1
    [[ -z "${left[3]}" ]] && return 1
    [[ -z "${right[3]}" ]] && return 0

    local left_parts=() right_parts=() a b
    IFS=. read -r -a left_parts <<< "${left[3]}"
    IFS=. read -r -a right_parts <<< "${right[3]}"
    for ((index = 0; index < ${#left_parts[@]} && index < ${#right_parts[@]}; index++)); do
        a="${left_parts[index]}"
        b="${right_parts[index]}"
        [[ "${a}" == "${b}" ]] && continue
        if [[ "${a}" =~ ^[0-9]+$ && "${b}" =~ ^[0-9]+$ ]]; then
            ((10#${a} < 10#${b})) && return 0
            return 1
        fi
        [[ "${a}" =~ ^[0-9]+$ ]] && return 0
        [[ "${b}" =~ ^[0-9]+$ ]] && return 1
        [[ "${a}" < "${b}" ]] && return 0
        return 1
    done
    ((${#left_parts[@]} < ${#right_parts[@]})) && return 0
    return 1
}

safe_root() {
    [[ "$1" == /* && "$1" != "/" && "$1" != "/opt" && "$1" != "/var" && "$1" != "/var/lib" && "$1" != */ ]]
}

read_stamp() {
    local value=""
    [[ -f "${STATE}/$1" ]] && value="$(<"${STATE}/$1")"
    printf '%s' "${value//[$'\r\n']/}"
}

# Written beside and renamed, so a reader sees the old value or the new one and never half.
atomic_text() {
    local target="$1" value="$2" temporary
    temporary="$(mktemp "${target}.XXXXXX")"
    printf '%s\n' "${value}" > "${temporary}"
    chmod 0644 "${temporary}"
    mv -f -- "${temporary}" "${target}"
}

record_refusal() {
    local reason="$1" temporary
    [[ -n "${TARGET_SHA}" ]] || return 0
    temporary="$(mktemp "${REFUSED_RELEASE}.XXXXXX")"
    jq -n \
        --arg sha256 "${TARGET_SHA}" \
        --arg version "${TARGET_VERSION}" \
        --arg commit "${TARGET_COMMIT}" \
        --arg ring "${TARGET_RING}" \
        --argjson generation "${TARGET_GENERATION:-0}" \
        --arg reason "${reason}" \
        --arg refusedUtc "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        '{schemaVersion: 1, sha256: $sha256, version: $version, commit: $commit, ring: $ring,
          generation: $generation, reason: $reason, refusedUtc: $refusedUtc}' > "${temporary}"
    chmod 0644 "${temporary}"
    mv -f -- "${temporary}" "${REFUSED_RELEASE}"
    atomic_text "${STATE}/REFUSED_SHA256" "${TARGET_SHA}"
}

# Puts back the tree, the units, this script and the stamps exactly as they were before the swap.
#
# Deliberately not fail-fast: every step is attempted, because stopping at the first error here
# is how a relay ends up with neither build installed.
rollback_failed_swap() {
    set +e
    log "the update failed after the swap; restoring the previous relay"
    record_refusal "the new relay did not prove its signed identity, or a post-swap step failed" \
        || log "could not record the refusal; continuing the rollback"
    systemctl stop "${SERVICE}" >/dev/null 2>&1

    local restored=0 nothing_to_restore=0 units_restored=0 unit stamp
    if [[ -d "${PREVIOUS}" ]]; then
        rm -rf -- "${INSTALL}" && mv -- "${PREVIOUS}" "${INSTALL}" && restored=1
    elif [[ -d "${LKG}" ]]; then
        rm -rf -- "${INSTALL}" && cp -a -- "${LKG}" "${INSTALL}" && restored=1
    else
        # A first install has no previous relay. The failed one is left stopped, not deleted:
        # it is the only copy there is, and somebody may need to look at it.
        nothing_to_restore=1
    fi

    if [[ -d "${SWAP}/deployment" ]]; then
        for unit in "${DEPLOYMENT_UNITS[@]}"; do
            if [[ -f "${SWAP}/deployment/${unit}" ]]; then
                if ! cmp -s "${SWAP}/deployment/${unit}" "${UNITS}/${unit}"; then
                    install -m 0644 "${SWAP}/deployment/${unit}" "${UNITS}/${unit}"
                    units_restored=1
                fi
            elif [[ -f "${SWAP}/deployment/${unit}.absent" && -f "${UNITS}/${unit}" ]]; then
                rm -f -- "${UNITS}/${unit}"
                units_restored=1
            fi
        done
        if [[ -f "${SWAP}/deployment/updater" ]] && ! cmp -s "${SWAP}/deployment/updater" "${SELF}"; then
            install -m 0755 "${SWAP}/deployment/updater" "${SELF}.incoming" && mv -f -- "${SELF}.incoming" "${SELF}"
        fi
        ((units_restored)) && systemctl daemon-reload
    fi

    if [[ -f "${SWAP}/complete" ]]; then
        for stamp in "${INSTALLED_STAMPS[@]}"; do
            if [[ -f "${SWAP}/stamps/${stamp}" ]]; then
                cp -f -- "${SWAP}/stamps/${stamp}" "${STATE}/${stamp}"
            else
                rm -f -- "${STATE}/${stamp}"
            fi
        done
    fi

    if ((nothing_to_restore)); then
        rm -rf -- "${SWAP}"
        log "there was no previous relay to restore; ${TARGET_VERSION} stays stopped and is recorded as refused"
        return 0
    fi
    if ((restored)) && systemctl start "${SERVICE}"; then
        rm -rf -- "${SWAP}"
        log "restored the previous relay; ${TARGET_VERSION} at ${TARGET_RING} generation ${TARGET_GENERATION} is recorded as refused"
        return 0
    fi
    log "the previous relay could not be restored and started; the service needs attention"
    return 1
}

cleanup() {
    local status=$?
    trap - EXIT INT TERM
    if ((status != 0 && TASK_SWAPPED == 1 && TASK_COMMITTED == 0)); then
        rollback_failed_swap
    fi
    # Never the live tree: either it was renamed into place, or the run stopped before that.
    rm -rf -- "${INCOMING}"
    if [[ -n "${TASK_WORK}" && -d "${TASK_WORK}" ]]; then
        rm -rf -- "${TASK_WORK}"
    fi
    exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

verify_signed() {
    local output
    if ! output="$(cosign verify-blob \
        --bundle "$2" \
        --trusted-root "${TRUST_ROOT}" \
        --certificate-identity "${SIGNER_IDENTITY}" \
        --certificate-oidc-issuer "${SIGNER_ISSUER}" \
        "$1" 2>&1)"; then
        log "${output}"
        refuse "the signature on $(basename "$1") does not verify for the release publisher"
    fi
}

fetch_build_file() {
    local tag="$1" name="$2"
    if [[ -n "${OFFLINE_BUNDLE}" ]]; then
        [[ -f "${OFFLINE_BUNDLE}/${name}" ]] || refuse "the offline bundle has no ${name}"
        cp -- "${OFFLINE_BUNDLE}/${name}" "${TASK_WORK}/${name}"
    else
        gh release download "${tag}" --repo "${RELEASE_REPOSITORY}" --pattern "${name}" \
            --dir "${TASK_WORK}" --clobber
        [[ -f "${TASK_WORK}/${name}" ]] || refuse "the build ${tag} has no ${name}"
    fi
}

# Answering is the test, not starting, and answering as the build that was signed. A process
# that starts and then fails to serve is the failure this guards against, and systemd calls it
# success; a stale process still bound to the port would answer as the wrong build.
health_matches() {
    local attempt
    for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
        if wget -q --timeout=5 -O "${TASK_WORK}/health.json" "${HEALTH_URL}" 2>/dev/null \
            && jq -e \
                --arg version "${TARGET_VERSION}" \
                --arg commit "${TARGET_COMMIT}" \
                --argjson protocol "${TARGET_PROTOCOL}" \
                '.status == "ok" and .version == $version and .commit == $commit and .protocol == $protocol' \
                "${TASK_WORK}/health.json" >/dev/null 2>&1; then
            return 0
        fi
        ((attempt < HEALTH_ATTEMPTS)) && sleep "${HEALTH_INTERVAL}"
    done
    return 1
}

# Installs the units and this script from the build that has just proved it works.
#
# Only after the health check, never before: a broken build must not take down the thing that
# would replace it. Every command is fail-fast, so a unit that will not install follows the
# same rollback as a build that will not answer, and the originals are copied first so that
# rollback can put them back. tarkov-group.service is deliberately not touched; it is
# hand-maintained on the host and carries the admin key drop-in.
apply_deployment() {
    [[ -d "${SHIPPED}" ]] || return 0
    local unit

    for unit in "${DEPLOYMENT_UNITS[@]}"; do
        if [[ -f "${SHIPPED}/${unit}" ]] && ! cmp -s "${SHIPPED}/${unit}" "${UNITS}/${unit}"; then
            TASK_UNITS_CHANGED=1
            install -m 0644 "${SHIPPED}/${unit}" "${UNITS}/${unit}"
            log "installed ${unit}"
        fi
    done

    # Renamed into place, never written over. bash keeps a file offset into the script it is
    # running, and a rename swaps the directory entry while leaving the open inode alone.
    if [[ -f "${SHIPPED}/tarkov-group-update.sh" ]] && ! cmp -s "${SHIPPED}/tarkov-group-update.sh" "${SELF}"; then
        install -m 0755 "${SHIPPED}/tarkov-group-update.sh" "${SELF}.incoming"
        mv -f -- "${SELF}.incoming" "${SELF}"
        log "installed the updater itself; the next run is the new one"
    fi

    if ((TASK_UNITS_CHANGED)); then
        systemctl daemon-reload
        # Enabled rather than only reloaded, because a unit new to this host was never enabled.
        systemctl enable --now tarkov-group-update.timer tarkov-group-update.path
    fi
}

write_swap_journal() {
    local unit stamp
    rm -rf -- "${SWAP}"
    mkdir -p "${SWAP}/deployment" "${SWAP}/stamps"
    for unit in "${DEPLOYMENT_UNITS[@]}"; do
        if [[ -f "${UNITS}/${unit}" ]]; then
            cp -p -- "${UNITS}/${unit}" "${SWAP}/deployment/${unit}"
        else
            : > "${SWAP}/deployment/${unit}.absent"
        fi
    done
    if [[ -f "${SELF}" ]]; then
        cp -p -- "${SELF}" "${SWAP}/deployment/updater"
    fi
    for stamp in "${INSTALLED_STAMPS[@]}"; do
        if [[ -f "${STATE}/${stamp}" ]]; then
            cp -p -- "${STATE}/${stamp}" "${SWAP}/stamps/${stamp}"
        fi
    done
    jq -n \
        --arg sha256 "${TARGET_SHA}" --arg version "${TARGET_VERSION}" --arg commit "${TARGET_COMMIT}" \
        --arg ring "${TARGET_RING}" --argjson generation "${TARGET_GENERATION}" \
        '{sha256: $sha256, version: $version, commit: $commit, ring: $ring, generation: $generation}' \
        > "${SWAP}/target.json"
    # Only a journal with this file is trusted to restore stamps; a half-written one is not.
    : > "${SWAP}/complete"
}

# Undoes a swap that a previous run started and never finished. Nothing is known about the tree
# that run left in place except that it never proved itself, so it is treated as refused.
recover_interrupted_swap() {
    [[ -d "${SWAP}" ]] || return 0
    if [[ ! -f "${SWAP}/complete" ]]; then
        # Killed while writing the journal, which is before anything else changed.
        rm -rf -- "${SWAP}"
        return 0
    fi
    log "a previous update was interrupted during its swap; undoing it"
    if [[ -f "${SWAP}/target.json" ]]; then
        TARGET_SHA="$(jq -r '.sha256 // ""' "${SWAP}/target.json")"
        TARGET_VERSION="$(jq -r '.version // ""' "${SWAP}/target.json")"
        TARGET_COMMIT="$(jq -r '.commit // ""' "${SWAP}/target.json")"
        TARGET_RING="$(jq -r '.ring // ""' "${SWAP}/target.json")"
        TARGET_GENERATION="$(jq -r '.generation // 0' "${SWAP}/target.json")"
    fi
    rollback_failed_swap || refuse "recovering the interrupted update failed"
    set -e
    TARGET_RING="${RELEASE_RING}"
    TARGET_SHA=""
    TARGET_VERSION=""
    TARGET_COMMIT=""
    TARGET_GENERATION=""
}

commit_installed_stamps() {
    local stamp temporary=()
    # Every value is written to a temporary file first, so the only thing left to fail is a
    # rename; if one did, the rollback restores the copies taken before the swap.
    for stamp in "${INSTALLED_STAMPS[@]}"; do
        temporary+=("$(mktemp "${STATE}/${stamp}.XXXXXX")")
    done
    printf '%s\n' "${TARGET_SHA}" > "${temporary[0]}"
    printf '%s\n' "${TARGET_VERSION}" > "${temporary[1]}"
    printf '%s\n' "${TARGET_COMMIT}" > "${temporary[2]}"
    printf '%s\n' "${RELEASE_RING}" > "${temporary[3]}"
    printf '%s\n' "${TARGET_GENERATION}" > "${temporary[4]}"
    local index
    for index in "${!INSTALLED_STAMPS[@]}"; do
        chmod 0644 "${temporary[index]}"
        mv -f -- "${temporary[index]}" "${STATE}/${INSTALLED_STAMPS[index]}"
    done
}

clear_refusal() {
    rm -f -- "${STATE}/REFUSED_SHA256" "${REFUSED_RELEASE}"
}

# --- Preconditions --------------------------------------------------------------------------

for command_name in base64 cmp cosign flock install jq mktemp sha256sum systemctl tar wget; do
    command -v "${command_name}" >/dev/null 2>&1 || refuse "required command is unavailable: ${command_name}"
done
if ! safe_root "${INSTALL}" || ! safe_root "${LKG}" || ! safe_root "${STATE}"; then
    refuse "install, rollback and state paths must be safe absolute directories"
fi
if [[ "${INSTALL}" == "${LKG}" || "${INSTALL}" == "${STATE}" || "${LKG}" == "${STATE}" \
    || "${STATE}" == "${INSTALL}"/* || "${LKG}" == "${INSTALL}"/* ]]; then
    refuse "install, rollback and state paths overlap"
fi
mkdir -p "${STATE}"
exec 9> "${STATE}/UPDATE.lock"
if ! flock -n 9; then
    log "another relay update holds the lock; leaving it to finish"
    exit 0
fi

# Removed first, before anything can fail. Left in place it would retrigger the path unit the
# instant this run finished, which for an up-to-date relay is a loop that asks the feed for ever.
rm -f -- "${REQUEST}"

# Holding the lock means no other run is using these. A killed run leaves them behind.
find "${STATE}" -maxdepth 1 -type d -name '.update.*' -exec rm -rf -- {} +
recover_interrupted_swap
if [[ -d "${PREVIOUS}" ]]; then
    # Only left when the final cleanup of a committed update did not finish.
    rm -rf -- "${PREVIOUS}"
fi

if [[ ! -f "${STATE}/INSTALLED_SHA256" && -f "${INSTALL}/INSTALLED_SHA256" ]]; then
    mv -- "${INSTALL}/INSTALLED_SHA256" "${STATE}/INSTALLED_SHA256"
    log "moved the legacy install stamp out of the tree"
fi
if [[ -f "${STATE}/REFUSED_SHA256" && ! -f "${REFUSED_RELEASE}" ]]; then
    # A refusal the checksum-only updater recorded names a build from an unauthenticated feed.
    # It is not evidence about any signed decision, and leaving it would show as one.
    rm -f -- "${STATE}/REFUSED_SHA256"
    log "cleared a refusal recorded by the unauthenticated updater"
fi

[[ "${RELEASE_RING}" =~ ^(canary|beta|stable)$ ]] || refuse "unknown release ring: ${RELEASE_RING}"
[[ -f "${TRUST_ROOT}" && -s "${TRUST_ROOT}" ]] || refuse "the Sigstore trust root ${TRUST_ROOT} is absent; see docs/RELEASES.md"

TASK_WORK="$(mktemp -d "${STATE}/.update.XXXXXX")"
if [[ -n "${OFFLINE_BUNDLE}" ]]; then
    [[ -d "${OFFLINE_BUNDLE}" ]] || refuse "the offline bundle directory does not exist"
    index_name="$(find "${OFFLINE_BUNDLE}" -maxdepth 1 -type f -name 'release-index-g[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9].json' -printf '%f\n' | sort | tail -n 1)"
    [[ -n "${index_name}" ]] || refuse "the offline bundle holds no signed ring index"
    cp -- "${OFFLINE_BUNDLE}/${index_name}" "${TASK_WORK}/envelope.json"
    log "using the offline bundle ${OFFLINE_BUNDLE}; the network feed is not consulted"
else
    command -v gh >/dev/null 2>&1 || refuse "the gh client is required to read the private feed"
    [[ -n "${RELEASE_REPOSITORY}" ]] || refuse "TARKOV_RELEASE_REPOSITORY is not configured; see docs/RELEASES.md"
    [[ "${RELEASE_REPOSITORY,,}" != "${SOURCE_REPOSITORY,,}" ]] || refuse "the public source repository is not a v2 feed"
    [[ -f "${RELEASE_TOKEN_FILE}" && -s "${RELEASE_TOKEN_FILE}" ]] || refuse "the feed credential ${RELEASE_TOKEN_FILE} is absent"
    GH_TOKEN="$(<"${RELEASE_TOKEN_FILE}")"
    export GH_TOKEN GH_CONFIG_DIR="${TASK_WORK}/gh" GH_PROMPT_DISABLED=1 GH_NO_UPDATE_NOTIFIER=1
    visibility="$(gh api "repos/${RELEASE_REPOSITORY}" --jq .visibility)"
    [[ "${visibility}" == "private" || "${visibility}" == "internal" ]] \
        || refuse "the release repository is ${visibility:-unreadable}, not private or internal"
    index_name="$(gh api "repos/${RELEASE_REPOSITORY}/contents/rings/${RELEASE_RING}" \
        --jq '[.[] | select(.type == "file") | .name | select(test("^release-index-g[0-9]{10}\\.json$"))] | sort | last // ""')"
    [[ -n "${index_name}" ]] || refuse "the ${RELEASE_RING} ring has no signed decision"
    gh api -H "Accept: application/vnd.github.raw+json" \
        "repos/${RELEASE_REPOSITORY}/contents/rings/${RELEASE_RING}/${index_name}" > "${TASK_WORK}/envelope.json"
fi

# --- Authenticate the decision --------------------------------------------------------------

jq -e '.schemaVersion == 1
       and .mediaType == "application/vnd.tarkov-companion.signed-release-index.v1+json"
       and (.payloadBase64 | type == "string") and (.sigstoreBundle | type == "object")' \
    "${TASK_WORK}/envelope.json" >/dev/null || refuse "the ring envelope is malformed"
jq -r '.payloadBase64' "${TASK_WORK}/envelope.json" | base64 --decode > "${TASK_WORK}/index.json"
jq '.sigstoreBundle' "${TASK_WORK}/envelope.json" > "${TASK_WORK}/index.sigstore.json"
verify_signed "${TASK_WORK}/index.json" "${TASK_WORK}/index.sigstore.json"

name_generation="${index_name#release-index-g}"
name_generation="$((10#${name_generation%.json}))"
# Offline recovery may run without a feed configured; online, the decision must name this feed so
# a validly signed index from some other feed cannot be replayed into this one.
jq -e \
    --arg ring "${RELEASE_RING}" \
    --arg feed "${RELEASE_REPOSITORY}" \
    --argjson generation "${name_generation}" \
    '.schemaVersion == 1
     and .mediaType == "application/vnd.tarkov-companion.release-index.v1+json"
     and ($feed == "" or .feedRepository == $feed)
     and .ring == $ring and .generation == $generation
     and .authorization.previousGeneration == $generation - 1
     and (.paused | type == "boolean")
     and (.release.version | type == "string")
     and (.release.commit | type == "string" and test("^[0-9a-f]{40}$"))
     and (.release.manifestSha256 | type == "string" and test("^[0-9a-f]{64}$"))
     and .release.manifestName == "release-manifest.json"
     and .release.buildTag == ("v2-build-" + .release.version)
     and (.rollback == null or (.rollback | type == "object"))' \
    "${TASK_WORK}/index.json" >/dev/null || refuse "the signed ring index is not a valid ${RELEASE_RING} decision for this feed"

TARGET_GENERATION="${name_generation}"
TARGET_VERSION="$(jq -r '.release.version' "${TASK_WORK}/index.json")"
TARGET_COMMIT="$(jq -r '.release.commit' "${TASK_WORK}/index.json")"
target_tag="$(jq -r '.release.buildTag' "${TASK_WORK}/index.json")"
target_manifest_sha="$(jq -r '.release.manifestSha256' "${TASK_WORK}/index.json")"
target_paused="$(jq -r '.paused' "${TASK_WORK}/index.json")"
rollback_authorized="$(jq -r '.rollback != null' "${TASK_WORK}/index.json")"
[[ "${TARGET_VERSION}" =~ ${VERSION_PATTERN} ]] || refuse "the signed index names an unsupported version"

# --- Refuse replays --------------------------------------------------------------------------

installed_sha="$(read_stamp INSTALLED_SHA256)"
installed_version="$(read_stamp INSTALLED_VERSION)"
installed_ring="$(read_stamp INSTALLED_RING)"
installed_generation="$(read_stamp INSTALLED_GENERATION)"
published_ring="$(read_stamp PUBLISHED_RING)"
published_generation="$(read_stamp PUBLISHED_GENERATION)"
published_manifest="$(read_stamp PUBLISHED_MANIFEST_SHA256)"
for value in "${installed_generation}" "${published_generation}"; do
    [[ -z "${value}" || "${value}" =~ ^[0-9]+$ ]] || refuse "a recorded generation stamp is malformed"
done
[[ -z "${installed_version}" || "${installed_version}" =~ ${VERSION_PATTERN} ]] || refuse "the installed version stamp is malformed"

# Generations are per ring. Pointing the host at another ring is a local decision by whoever
# holds root, so the history of the old ring is not held against the new one.
if [[ -n "${published_ring}" && "${published_ring}" != "${RELEASE_RING}" ]]; then
    log "the configured ring changed from ${published_ring} to ${RELEASE_RING}; its generations start from this decision"
    published_generation=""
    published_manifest=""
fi
if [[ "${installed_ring}" == "${RELEASE_RING}" && -n "${installed_generation}" ]] \
    && ((TARGET_GENERATION < installed_generation)); then
    refuse "refusing ${RELEASE_RING} generation ${TARGET_GENERATION}: generation ${installed_generation} is already installed"
fi
if [[ -n "${published_generation}" ]]; then
    if ((TARGET_GENERATION < published_generation)); then
        refuse "refusing replayed ${RELEASE_RING} generation ${TARGET_GENERATION}: generation ${published_generation} was already authenticated"
    fi
    if ((TARGET_GENERATION == published_generation)) && [[ -n "${published_manifest}" && "${published_manifest}" != "${target_manifest_sha}" ]]; then
        refuse "two different signed ${RELEASE_RING} decisions claim generation ${TARGET_GENERATION}; refusing both"
    fi
fi

# --- Authenticate the build ------------------------------------------------------------------

fetch_build_file "${target_tag}" release-manifest.json
fetch_build_file "${target_tag}" release-manifest.json.sigstore.json
verify_signed "${TASK_WORK}/release-manifest.json" "${TASK_WORK}/release-manifest.json.sigstore.json"
[[ "$(sha256sum "${TASK_WORK}/release-manifest.json" | awk '{print $1}')" == "${target_manifest_sha}" ]] \
    || refuse "the signed manifest is not the one the signed ring index names"
jq -e \
    --arg version "${TARGET_VERSION}" \
    --arg commit "${TARGET_COMMIT}" \
    '.schemaVersion == 1 and .version == $version and .commit == $commit
     and .versions.package == $version and .versions.manifest == $version and .versions.commit == $commit
     and .versions.assemblyInformational == ($version + "+" + $commit)
     and (.versions.relayProtocol | type == "number" and . >= 0 and floor == .)
     and ([.artifacts[] | select(.component == "relay" and .role == "archive")] | length == 1)' \
    "${TASK_WORK}/release-manifest.json" >/dev/null || refuse "the signed manifest disagrees with the ring index about this build"
archive_name="$(jq -r '.artifacts[] | select(.component == "relay" and .role == "archive") | .name' "${TASK_WORK}/release-manifest.json")"
TARGET_SHA="$(jq -r '.artifacts[] | select(.component == "relay" and .role == "archive") | .sha256' "${TASK_WORK}/release-manifest.json")"
TARGET_PROTOCOL="$(jq -r '.versions.relayProtocol' "${TASK_WORK}/release-manifest.json")"
if [[ ! "${archive_name}" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ || "${archive_name}" == *..* || ! "${TARGET_SHA}" =~ ^[0-9a-f]{64}$ ]]; then
    refuse "the signed manifest names an unsafe relay archive"
fi

# What the panel reports as published: only ever something this run authenticated.
atomic_text "${STATE}/PUBLISHED_RING" "${RELEASE_RING}"
atomic_text "${STATE}/PUBLISHED_GENERATION" "${TARGET_GENERATION}"
atomic_text "${STATE}/PUBLISHED_MANIFEST_SHA256" "${target_manifest_sha}"
atomic_text "${STATE}/PUBLISHED_VERSION" "${TARGET_VERSION}"
atomic_text "${STATE}/PUBLISHED_SHA256" "${TARGET_SHA}"

# --- Decide ----------------------------------------------------------------------------------

if [[ "${installed_sha}" == "${TARGET_SHA}" && ( -z "${installed_version}" || "${installed_version}" == "${TARGET_VERSION}" ) ]]; then
    if health_matches; then
        commit_installed_stamps
        clear_refusal
        log "already running ${TARGET_VERSION} (${TARGET_COMMIT:0:12}) at ${RELEASE_RING} generation ${TARGET_GENERATION}"
        exit 0
    fi
    log "the installed stamp names this build but the running relay does not answer as it; reinstalling"
fi

downgrade=0
if [[ -n "${installed_version}" && "${installed_version}" != "${TARGET_VERSION}" ]] && semver_less "${TARGET_VERSION}" "${installed_version}"; then
    downgrade=1
fi
if [[ -n "${installed_version}" && "${installed_version}" == "${TARGET_VERSION}" && "${installed_sha}" != "${TARGET_SHA}" ]]; then
    refuse "the signed ${TARGET_VERSION} has a different relay archive from the installed ${TARGET_VERSION}; refusing"
fi
if ((downgrade)) && [[ "${rollback_authorized}" != "true" ]]; then
    refuse "refusing to move from ${installed_version} to ${TARGET_VERSION} without a signed rollback"
fi
# A paused ring holds its consumers where they are. The exception is a signed rollback, which is
# the reason a ring is usually paused in the first place.
if [[ "${target_paused}" == "true" ]] && ! ((downgrade)); then
    log "${RELEASE_RING} is paused at generation ${TARGET_GENERATION}; staying on ${installed_version:-the installed build}"
    exit 0
fi
if [[ -f "${REFUSED_RELEASE}" ]] && jq -e \
    --arg sha "${TARGET_SHA}" --arg ring "${RELEASE_RING}" --argjson generation "${TARGET_GENERATION}" \
    '.sha256 == $sha and .ring == $ring and .generation >= $generation' "${REFUSED_RELEASE}" >/dev/null 2>&1; then
    log "${TARGET_VERSION} at ${RELEASE_RING} generation ${TARGET_GENERATION} was refused here before; waiting for a new signed decision"
    exit 0
fi

# --- Install ---------------------------------------------------------------------------------

fetch_build_file "${target_tag}" "${archive_name}"
fetch_build_file "${target_tag}" "${archive_name}.sigstore.json"
verify_signed "${TASK_WORK}/${archive_name}" "${TASK_WORK}/${archive_name}.sigstore.json"
[[ "$(sha256sum "${TASK_WORK}/${archive_name}" | awk '{print $1}')" == "${TARGET_SHA}" ]] \
    || refuse "the relay archive is not the one the signed manifest names"

tar -tzvf "${TASK_WORK}/${archive_name}" > "${TASK_WORK}/members.txt"
if cut -c1 "${TASK_WORK}/members.txt" | grep -qv '^[-d]$'; then
    refuse "the relay archive contains a link or special file"
fi
while IFS= read -r member; do
    if [[ "${member}" == /* || "/${member}/" == *"/../"* ]]; then
        refuse "the relay archive contains an unsafe path"
    fi
done < <(tar -tzf "${TASK_WORK}/${archive_name}")

rm -rf -- "${INCOMING}"
mkdir -p "${INCOMING}"
tar --no-same-owner -C "${INCOMING}" -xzf "${TASK_WORK}/${archive_name}"
[[ -f "${INCOMING}/TarkovCompanion.GroupServer" ]] || refuse "the archive has no relay executable"
chmod 0755 "${INCOMING}/TarkovCompanion.GroupServer"

# The rollback copy is complete before it replaces the old one, so a copy that fails halfway
# leaves the previous last-known-good in place rather than none.
if [[ -d "${INSTALL}" ]]; then
    rm -rf -- "${LKG}.incoming"
    cp -a -- "${INSTALL}" "${LKG}.incoming"
    rm -rf -- "${LKG}"
    mv -- "${LKG}.incoming" "${LKG}"
fi
write_swap_journal

log "installing ${TARGET_VERSION} (${TARGET_COMMIT:0:12}) from ${RELEASE_RING} generation ${TARGET_GENERATION}"
# Marked before the first command that changes anything, so a failure at any later line - a
# stop that fails, a rename that fails - restores rather than leaving a stopped service.
TASK_SWAPPED=1
systemctl stop "${SERVICE}"
rm -rf -- "${PREVIOUS}"
if [[ -d "${INSTALL}" ]]; then
    mv -- "${INSTALL}" "${PREVIOUS}"
fi
mv -- "${INCOMING}" "${INSTALL}"
systemctl start "${SERVICE}"

health_matches || refuse "the new relay did not answer as ${TARGET_VERSION} (${TARGET_COMMIT:0:12})"
apply_deployment
commit_installed_stamps
clear_refusal
# The commit point. Before this line an interruption is undone by the next run; after it, the
# new build is the installed one and its stamps already say so.
rm -rf -- "${SWAP}"
TASK_COMMITTED=1

rm -rf -- "${PREVIOUS}" || log "could not remove ${PREVIOUS}; it is unused and can be deleted"
log "installed ${TARGET_VERSION} (${TARGET_COMMIT:0:12}); last-known-good copy kept at ${LKG}"
