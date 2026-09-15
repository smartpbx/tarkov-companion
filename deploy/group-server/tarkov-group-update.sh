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
# Version ordering compares prerelease labels with [[ < ]], which follows the locale's collation.
# The publisher and the offline installer compare ordinally; so does this, whatever the host says.
export LC_ALL=C
# The feed credential is handed to gh alone, never inherited by cosign, tar, wget or systemctl.
unset GH_TOKEN GITHUB_TOKEN GH_ENTERPRISE_TOKEN GITHUB_ENTERPRISE_TOKEN

readonly SOURCE_REPOSITORY="smartpbx/tarkov-companion"
readonly RELEASE_REPOSITORY="${TARKOV_RELEASE_REPOSITORY:-}"
readonly RELEASE_RING="${TARKOV_RELEASE_RING:-stable}"
readonly RELEASE_TOKEN_FILE="${TARKOV_RELEASE_TOKEN_FILE:-/etc/tarkov-group/release-feed.token}"
readonly TRUST_ROOT="${TARKOV_SIGSTORE_TRUST_ROOT:-/etc/tarkov-group/sigstore-trusted-root.json}"
readonly SIGNER_IDENTITY="${TARKOV_RELEASE_SIGNER_IDENTITY:-https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main}"
readonly SIGNER_ISSUER="${TARKOV_RELEASE_SIGNER_ISSUER:-https://token.actions.githubusercontent.com}"
# The certificate's GitHub claims, required beside its subject, so a workflow in another
# repository that calls ours cannot present the same subject.
readonly SIGNER_REPOSITORY="${TARKOV_RELEASE_SIGNER_REPOSITORY:-smartpbx/tarkov-companion}"
readonly SIGNER_REF="${TARKOV_RELEASE_SIGNER_REF:-refs/heads/main}"
# The cosign builds this host accepts, by content: v3.1.3 for linux amd64 and arm64, the digests
# in cosign's own Sigstore-signed checksums and in scripts/release/cosign.sha256. Before v3.1.3 a
# legacy bundle carrying a bare public key skipped the identity check (GHSA-fx35-mq7g-6g98), so
# "whatever cosign is on PATH" is not a verifier. TARKOV_COSIGN_SHA256 names a different build
# explicitly; it does not turn the check off.
readonly COSIGN_PINS=(
    4629c757b7618056f8ddd7e2625ae9fdd94c0372a65049520bc7d9df9efc7f71
    c5d324e091826b0d7a78eb16fef316450b4eb9aaec045611c08ba06f5e73220a
)
readonly BUNDLE_MEDIA_TYPE="application/vnd.dev.sigstore.bundle.v0.3+json"
readonly MAX_JSON_BYTES=$((16 * 1024 * 1024))
readonly MAX_SIGNATURE_BYTES=$((2 * 1024 * 1024))
readonly MAX_ARCHIVE_BYTES=$((512 * 1024 * 1024))
readonly MAX_ARCHIVE_MEMBERS=8192
readonly MAX_MEMBER_BYTES=$((512 * 1024 * 1024))
readonly MAX_EXPANDED_BYTES=$((1024 * 1024 * 1024))
readonly MIN_FREE_RESERVE_BYTES=$((256 * 1024 * 1024))
readonly MAX_COMMAND_OUTPUT_BYTES=$((64 * 1024))
readonly MAX_VERSION_LENGTH=128
readonly MAX_SEMVER_NUMBER=2147483647
readonly MAX_GENERATION=9999999999
readonly MAX_OFFLINE_DIRECTORY_ENTRIES=16384
# A directory holding a signed ring index, its manifest, the relay archive and their bundles.
# Set, it replaces the network entirely: the recovery path when the feed is down or unreachable.
readonly OFFLINE_BUNDLE="${TARKOV_RELEASE_BUNDLE_DIR:-}"
# Anchors a host that has no history of its own. A fresh host believes the newest signed decision
# the feed shows it, and whoever can delete newer decisions from the feed can choose that one; a
# floor provisioned beside the trust root, from the publish run's summary, is what refuses it.
readonly MINIMUM_VERSION="${TARKOV_RELEASE_MINIMUM_VERSION:-}"
readonly MINIMUM_GENERATION="${TARKOV_RELEASE_MINIMUM_GENERATION:-}"
readonly MAX_DECISION_AGE_DAYS="${TARKOV_RELEASE_MAX_DECISION_AGE_DAYS:-}"
readonly ALLOW_UNANCHORED_BOOTSTRAP="${TARKOV_RELEASE_ALLOW_UNANCHORED_BOOTSTRAP:-0}"
# A custom deployment may put all updater-owned trees below one explicit root. Without one,
# install trees must remain below /opt and state trees below /var/lib. This is a containment
# boundary, not a convenience default: several recovery paths recursively remove derived names.
readonly PATH_ROOT="${TARKOV_UPDATE_PATH_ROOT:-}"
readonly INSTALL="${TARKOV_UPDATE_INSTALL:-/opt/tarkov-group}"
readonly LKG="${TARKOV_UPDATE_LKG:-/opt/tarkov-group.lkg}"
# Everything this script decides from lives here: the lock, the work directories, the swap
# journal and every stamp. Root's, mode 0700, and nobody else's.
#
# It used to be the relay's own state directory. The relay runs as an unprivileged dynamic user
# precisely so that a compromise of an internet-facing process stays inside it, and that user
# owns /var/lib/tarkov-group. A journal it could write was a journal it could fill with an
# "updater" for root to restore; a work directory inside a directory it owned was one it could
# swap between the signature check and the extraction. The relay's directory is now read for
# exactly one thing, the request marker, and written by this script not at all.
readonly STATE="${TARKOV_UPDATE_STATE:-/var/lib/tarkov-group-update}"
# What the panel shows. Root writes it and the relay only reads it, so the relay cannot make its
# own panel claim a build, and nothing here ever reads a decision back from it.
readonly STATUS="${TARKOV_UPDATE_STATUS:-/var/lib/tarkov-group-update-status}"
readonly RELAY_STATE="${TARKOV_RELAY_STATE:-/var/lib/tarkov-group}"
readonly SERVICE="${TARKOV_UPDATE_SERVICE:-tarkov-group}"
readonly SELF="${TARKOV_UPDATE_SELF:-/opt/tarkov-group-update.sh}"
readonly UNITS="${TARKOV_UPDATE_UNITS:-/etc/systemd/system}"
readonly HEALTH_URL="${TARKOV_UPDATE_HEALTH_URL:-http://127.0.0.1:8090/health}"
readonly HEALTH_ATTEMPTS="${TARKOV_UPDATE_HEALTH_ATTEMPTS:-10}"
readonly HEALTH_INTERVAL="${TARKOV_UPDATE_HEALTH_INTERVAL:-2}"
readonly INCOMING="${INSTALL}.incoming"
readonly PREVIOUS="${INSTALL}.previous"
readonly LKG_INCOMING="${LKG}.incoming"
readonly LKG_PREVIOUS="${LKG}.previous"
readonly SELF_INCOMING="${SELF}.incoming"
readonly SHIPPED="${INSTALL}/deploy"
# Written by the relay's admin panel and watched by tarkov-group-update.path. The relay runs
# unprivileged and cannot start a unit; it can write one file in the directory it already owns.
# It is a trigger and nothing more: its contents, and anything beside it, are never read.
readonly REQUEST="${RELAY_STATE}/UPDATE_NOW"
readonly REFUSED_RELEASE="${STATE}/REFUSED_RELEASE.json"
# The swap journal: what is being installed and everything needed to undo it. It is assembled in
# ${SWAP}.new and renamed into place whole, so a journal that exists is a complete one, and it is
# renamed to ${SWAP}.committed as the commit point, so a journal that still exists names a swap
# that never committed. Either rename is one directory entry, never half of one.
readonly SWAP="${STATE}/swap"
readonly INSTALLED_STAMPS=(INSTALLED_SHA256 INSTALLED_VERSION INSTALLED_COMMIT INSTALLED_RING INSTALLED_GENERATION)
readonly INSTALLED_RELEASE="${STATE}/INSTALLED_RELEASE.json"
readonly PUBLISHED_RELEASE="${STATE}/PUBLISHED_RELEASE.json"
readonly INSTALLED_STATE_FILES=(INSTALLED_RELEASE.json "${INSTALLED_STAMPS[@]}")
# The panel reads the digests; the versions are for whoever runs `cat` on the host.
readonly STATUS_STAMPS=(INSTALLED_SHA256 INSTALLED_VERSION PUBLISHED_SHA256 PUBLISHED_VERSION REFUSED_SHA256)
# SemVer 2.0 as the publisher's release_policy.py accepts it: no leading zeros in a numeric
# identifier, and no build metadata. A version one side accepts and the other refuses would be
# signed, published and then never installed.
readonly PRERELEASE_IDENTIFIER='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
readonly VERSION_PATTERN="^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-(${PRERELEASE_IDENTIFIER}(\\.${PRERELEASE_IDENTIFIER})*))?\$"
readonly DEPLOYMENT_UNITS=(tarkov-group-update.service tarkov-group-update.timer tarkov-group-update.path)

TASK_WORK=""
TASK_LOCKED=0
TASK_JOURNALED=0
TASK_UNITS_CHANGED=0
TASK_FEED_TOKEN=""
TASK_COSIGN=""
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

VALIDATE_PATHS=0
if (($#)); then
    if (($# != 1)) || [[ "$1" != "--validate-paths" ]]; then
        refuse "usage: $0 [--validate-paths]"
    fi
    VALIDATE_PATHS=1
fi
readonly VALIDATE_PATHS

decimal_at_most() {
    local value="${1#"${1%%[!0]*}"}" maximum="$2"
    [[ -n "${value}" ]] || value=0
    ((${#value} < ${#maximum})) && return 0
    ((${#value} > ${#maximum})) && return 1
    # Only equal-width values reach arithmetic; every caller bounds that width to ten digits.
    ((10#${value} <= 10#${maximum}))
}

valid_generation() {
    [[ "$1" =~ ^(0|[1-9][0-9]{0,9})$ ]] && decimal_at_most "$1" "${MAX_GENERATION}"
}

valid_semver() {
    local version="$1" part prerelease identifier parts=()
    ((${#version} > 0 && ${#version} <= MAX_VERSION_LENGTH)) || return 1
    [[ "${version}" =~ ${VERSION_PATTERN} ]] || return 1
    for part in "${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}"; do
        decimal_at_most "${part}" "${MAX_SEMVER_NUMBER}" || return 1
    done
    prerelease="${BASH_REMATCH[5]}"
    IFS=. read -r -a parts <<< "${prerelease}"
    for identifier in "${parts[@]}"; do
        if [[ "${identifier}" =~ ^[0-9]+$ ]]; then
            decimal_at_most "${identifier}" "${MAX_SEMVER_NUMBER}" || return 1
        fi
    done
}

# A regular file whose name and containing directory cannot be replaced by an untrusted user.
secure_input_file() {
    local path="$1" label="$2" secret="${3:-0}" resolved parent owner mode
    [[ -f "${path}" && ! -L "${path}" ]] || refuse "${label} ${path} is absent or is not a plain file"
    resolved="$(readlink -f -- "${path}")"
    [[ -n "${resolved}" && "${resolved}" == "${path}" ]] || refuse "${label} ${path} resolves somewhere else"
    parent="$(dirname -- "${path}")"
    [[ ! -L "${parent}" && "$(readlink -f -- "${parent}")" == "${parent}" ]] \
        || refuse "the directory holding ${label} ${path} is redirected"
    for candidate in "${path}" "${parent}"; do
        owner="$(stat -c %u -- "${candidate}")"
        [[ "${owner}" == 0 || "${owner}" == "$(id -u)" ]] \
            || refuse "${candidate} belongs to uid ${owner}; ${label} must be controlled by the updater"
        mode="$(stat -c %a -- "${candidate}")"
        if ((8#${mode} & 8#022)); then
            refuse "${candidate} is writable by other users; ${label} must not be replaceable"
        fi
    done
    if ((secret)) && ((8#$(stat -c %a -- "${path}") & 8#077)); then
        refuse "the feed credential ${path} is readable by other users; make it mode 0600"
    fi
}

validate_json_file() {
    local path="$1" maximum="${2:-${MAX_JSON_BYTES}}" label="${3:-$(basename -- "$1")}";
    python3 - "${path}" "${maximum}" "${label}" <<'PY'
import json
import pathlib
import sys

path, maximum, label = pathlib.Path(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
with path.open("rb") as stream:
    value = stream.read(maximum + 1)
    if len(value) > maximum or stream.read(1):
        raise SystemExit(f"{label} exceeds its {maximum}-byte limit")
depth = 0
quoted = escaped = False
for byte in value:
    char = chr(byte)
    if quoted:
        if escaped:
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == '"':
            quoted = False
    elif char == '"':
        quoted = True
    elif char in "[{":
        depth += 1
        if depth > 32:
            raise SystemExit(f"{label} exceeds the JSON nesting limit")
    elif char in "]}":
        depth -= 1
        if depth < 0:
            raise SystemExit(f"{label} has unbalanced JSON delimiters")
if quoted or depth:
    raise SystemExit(f"{label} has unbalanced JSON")
try:
    json.loads(value.decode("utf-8-sig"))
except (UnicodeDecodeError, json.JSONDecodeError, RecursionError) as error:
    raise SystemExit(f"{label} is not valid bounded JSON: {error}")
PY
}

bounded_copy() {
    local source="$1" target="$2" maximum="$3" label="$4" expected_size copied_size
    [[ -f "${source}" && ! -L "${source}" ]] || refuse "${label} is missing or not a plain file"
    expected_size="$(stat -c %s -- "${source}")"
    ((expected_size > 0 && expected_size <= maximum)) \
        || refuse "${label} is ${expected_size} bytes, outside its ${maximum}-byte limit"
    head -c "$((maximum + 1))" -- "${source}" > "${target}"
    copied_size="$(stat -c %s -- "${target}")"
    ((copied_size == expected_size && copied_size <= maximum)) \
        || { rm -f -- "${target}"; refuse "${label} changed or exceeded its byte limit while copied"; }
}

extract_bounded_archive() {
    local archive="$1" destination="$2" size
    size="$(stat -c %s -- "${archive}")"
    ((size > 0 && size <= MAX_ARCHIVE_BYTES)) \
        || refuse "the relay archive is ${size} bytes, outside its compressed-size limit"
    python3 - "${archive}" "${destination}" \
        "${MAX_ARCHIVE_MEMBERS}" "${MAX_MEMBER_BYTES}" "${MAX_EXPANDED_BYTES}" "${MIN_FREE_RESERVE_BYTES}" <<'PY'
import os
from pathlib import Path, PurePosixPath
import shutil
import sys
import tarfile

archive_path, destination = Path(sys.argv[1]), Path(sys.argv[2])
member_limit, file_limit, expanded_limit, reserve = map(int, sys.argv[3:])
destination.mkdir(parents=True, exist_ok=False)
seen: set[str] = set()
expanded = count = 0
try:
    with tarfile.open(archive_path, "r|gz") as archive:
        for member in archive:
            count += 1
            if count > member_limit:
                raise ValueError(f"relay archive has more than {member_limit} entries")
            raw = member.name
            path = PurePosixPath(raw)
            if path.is_absolute() or ".." in path.parts or "\\" in raw or not (member.isfile() or member.isdir()):
                raise ValueError(f"relay archive has an unsafe entry: {raw}")
            parts = tuple(part for part in path.parts if part not in ("", "."))
            if not parts:
                if member.isdir():
                    continue
                raise ValueError("relay archive has an empty file name")
            normalized = "/".join(parts)
            if normalized in seen:
                raise ValueError(f"relay archive repeats {normalized}")
            seen.add(normalized)
            target = destination.joinpath(*parts)
            if member.isdir():
                target.mkdir(parents=True, exist_ok=True, mode=0o755)
                target.chmod(0o755)
                continue
            if member.size < 0 or member.size > file_limit:
                raise ValueError(f"relay archive entry {normalized} exceeds the per-file limit")
            expanded += member.size
            if expanded > expanded_limit:
                raise ValueError("relay archive exceeds the expanded-size limit")
            if shutil.disk_usage(destination).free < member.size + reserve:
                raise ValueError("not enough free space remains for bounded extraction")
            target.parent.mkdir(parents=True, exist_ok=True)
            source = archive.extractfile(member)
            if source is None:
                raise ValueError(f"relay archive cannot read {normalized}")
            flags = os.O_CREAT | os.O_EXCL | os.O_WRONLY
            if hasattr(os, "O_NOFOLLOW"):
                flags |= os.O_NOFOLLOW
            written = 0
            mode = 0o755 if member.mode & 0o111 else 0o644
            descriptor = os.open(target, flags, mode)
            try:
                with os.fdopen(descriptor, "wb") as sink:
                    descriptor = -1
                    while True:
                        chunk = source.read(min(1024 * 1024, member.size - written + 1))
                        if not chunk:
                            break
                        written += len(chunk)
                        if written > member.size:
                            raise ValueError(f"relay archive entry {normalized} expands beyond its declared size")
                        sink.write(chunk)
            finally:
                source.close()
                if descriptor >= 0:
                    os.close(descriptor)
            if written != member.size:
                raise ValueError(f"relay archive entry {normalized} is truncated")
except Exception:
    shutil.rmtree(destination, ignore_errors=True)
    raise
PY
}

select_offline_index() {
    python3 - "$1" "${MAX_OFFLINE_DIRECTORY_ENTRIES}" <<'PY'
import os
from pathlib import Path
import re
import sys

directory, maximum = Path(sys.argv[1]), int(sys.argv[2])
if directory.is_symlink() or not directory.is_dir():
    raise SystemExit("offline bundle is not a plain directory")
pattern = re.compile(r"^release-index-g([0-9]{10})\.json$")
selected = None
with os.scandir(directory) as entries:
    for count, entry in enumerate(entries, start=1):
        if count > maximum:
            raise SystemExit(f"offline bundle exceeds its {maximum}-entry directory limit")
        match = pattern.fullmatch(entry.name)
        if match is None or not entry.is_file(follow_symlinks=False):
            continue
        candidate = (int(match.group(1)), entry.name)
        if candidate[0] > 0 and (selected is None or candidate > selected):
            selected = candidate
if selected is None:
    raise SystemExit("offline bundle holds no plain signed ring decision")
print(selected[1])
PY
}

# 0 when the first version orders before the second under SemVer 2.0 precedence, 1 otherwise.
semver_less() {
    local left=() right=() index
    valid_semver "$1" || return 2
    [[ "$1" =~ ${VERSION_PATTERN} ]]
    left=("${BASH_REMATCH[1]}" "${BASH_REMATCH[2]}" "${BASH_REMATCH[3]}" "${BASH_REMATCH[5]}")
    valid_semver "$2" || return 2
    [[ "$2" =~ ${VERSION_PATTERN} ]]
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
    local path="$1" resolved
    [[ "${path}" == /* && "${path}" != */ ]] || return 1
    resolved="$(readlink -m -- "${path}")" || return 1
    # The updater removes trees derived from these names. A lexical alias such as
    # /opt/../var used to pass the broad-directory check and could therefore turn a rollback's
    # rm -rf into a removal of /var. Requiring the configured spelling to be the resolved path
    # also refuses symbolic-link ancestors that could be swapped underneath the updater.
    [[ "${path}" == "${resolved}" ]] || return 1
    [[ "${resolved}" != "/" && "${resolved}" != "/opt" && "${resolved}" != "/var" && "${resolved}" != "/var/lib" ]]
}

strictly_below() {
    [[ "$1" == "$2"/* ]]
}

paths_overlap() {
    [[ "$1" == "$2" || "$1" == "$2"/* || "$2" == "$1"/* ]]
}

trusted_owner_and_mode() {
    local path="$1" label="$2" owner permissions
    owner="$(stat -c %u -- "${path}")" || return 1
    [[ "${owner}" == 0 || "${owner}" == "$(id -u)" ]] \
        || refuse "${label} ${path} belongs to uid ${owner}, not root or the updater"
    permissions="$(stat -c %a -- "${path}")" || return 1
    ((8#${permissions} & 8#022)) \
        && refuse "${label} ${path} is writable by other users"
    return 0
}

secure_managed_directory() {
    local path="$1" label="$2" resolved
    [[ "${path}" == /* && "${path}" != */ && -d "${path}" && ! -L "${path}" ]] \
        || refuse "${label} ${path} must be an existing plain absolute directory"
    resolved="$(readlink -f -- "${path}")" || refuse "${label} ${path} cannot be resolved"
    [[ "${resolved}" == "${path}" ]] || refuse "${label} ${path} resolves somewhere else"
    trusted_owner_and_mode "${path}" "${label}"
    return 0
}

secure_managed_file_destination() {
    local path="$1" label="$2" parent resolved
    [[ "${path}" == /* && "${path}" != */ ]] \
        || refuse "${label} ${path} must be a plain absolute file destination"
    resolved="$(readlink -m -- "${path}")" || refuse "${label} ${path} cannot be resolved"
    [[ "${resolved}" == "${path}" ]] || refuse "${label} ${path} resolves somewhere else"
    parent="$(dirname -- "${path}")"
    secure_managed_directory "${parent}" "the directory holding ${label}"
    if [[ -e "${path}" || -L "${path}" ]]; then
        [[ -f "${path}" && ! -L "${path}" ]] || refuse "${label} ${path} is not a plain file"
        trusted_owner_and_mode "${path}" "${label}"
    fi
    return 0
}

validate_destructive_roots() {
    local path unit target path_index other_index
    for path in "${INSTALL}" "${LKG}" "${STATE}" "${STATUS}" "${RELAY_STATE}"; do
        safe_root "${path}" || refuse "install, rollback, state and status paths must be safe absolute directories"
    done

    if [[ -n "${PATH_ROOT}" ]]; then
        # A root such as /tmp or /etc is still an unsafe typo: it must itself be below a
        # top-level directory, and each mutable tree must be a strict child rather than the root.
        if ! safe_root "${PATH_ROOT}" || [[ "$(dirname -- "${PATH_ROOT}")" == "/" ]]; then
            refuse "TARKOV_UPDATE_PATH_ROOT must be a safe absolute directory below a top-level directory"
        fi
        secure_managed_directory "${PATH_ROOT}" "update path root"
        for path in "${INSTALL}" "${LKG}" "${STATE}" "${STATUS}" "${RELAY_STATE}"; do
            strictly_below "${path}" "${PATH_ROOT}" \
                || refuse "${path} must be below TARKOV_UPDATE_PATH_ROOT ${PATH_ROOT}"
        done
        for path in "${SELF}" "${UNITS}"; do
            strictly_below "${path}" "${PATH_ROOT}" \
                || refuse "${path} must be below TARKOV_UPDATE_PATH_ROOT ${PATH_ROOT}"
        done
    else
        for path in "${INSTALL}" "${LKG}"; do
            strictly_below "${path}" /opt \
                || refuse "install and rollback paths must be below /opt unless TARKOV_UPDATE_PATH_ROOT is set"
        done
        for path in "${STATE}" "${STATUS}" "${RELAY_STATE}"; do
            strictly_below "${path}" /var/lib \
                || refuse "state and status paths must be below /var/lib unless TARKOV_UPDATE_PATH_ROOT is set"
        done
        strictly_below "${SELF}" /opt \
            || refuse "the updater executable must be below /opt unless TARKOV_UPDATE_PATH_ROOT is set"
        [[ "${UNITS}" == /etc/systemd/system ]] \
            || refuse "the systemd unit directory must be /etc/systemd/system unless TARKOV_UPDATE_PATH_ROOT is set"
    fi

    for path in "${INSTALL}" "${LKG}" "${STATE}" "${STATUS}" "${RELAY_STATE}"; do
        secure_managed_directory "$(dirname -- "${path}")" "the directory holding ${path}"
    done
    secure_managed_directory "${UNITS}" "systemd unit directory"
    secure_managed_file_destination "${SELF}" "updater executable"
    secure_managed_file_destination "${SELF_INCOMING}" "updater incoming file"
    for unit in "${DEPLOYMENT_UNITS[@]}"; do
        secure_managed_file_destination "${UNITS}/${unit}" "systemd unit"
    done

    # SELF is replaced by renaming SELF_INCOMING; the installation and rollback directories are
    # recursively removed or renamed. None may claim another managed output or one of its parents.
    local destructive=(
        "${INSTALL}" "${INCOMING}" "${PREVIOUS}" "${LKG}" "${LKG_INCOMING}" "${LKG_PREVIOUS}"
        "${STATE}" "${STATUS}" "${RELAY_STATE}" "${SELF}" "${SELF_INCOMING}"
    )
    for ((path_index = 0; path_index < ${#destructive[@]}; path_index++)); do
        for ((other_index = path_index + 1; other_index < ${#destructive[@]}; other_index++)); do
            paths_overlap "${destructive[path_index]}" "${destructive[other_index]}" \
                && refuse "update paths overlap: ${destructive[path_index]} and ${destructive[other_index]}"
        done
        paths_overlap "${destructive[path_index]}" "${UNITS}" \
            && refuse "update paths overlap: ${destructive[path_index]} and ${UNITS}"
        for unit in "${DEPLOYMENT_UNITS[@]}"; do
            target="${UNITS}/${unit}"
            paths_overlap "${destructive[path_index]}" "${target}" \
                && refuse "update paths overlap: ${destructive[path_index]} and ${target}"
        done
    done
    return 0
}

# A directory only this script's user may change: not a link, owned by the user running this, and
# writable by nobody else. Created with that mode when absent; tightened if it is ours but loose;
# refused otherwise, because a directory somebody else can write is not one to decide from.
own_directory() {
    local path="$1" mode="$2" owner permissions
    if [[ -L "${path}" ]]; then
        refuse "${path} is a symbolic link; the updater's own directories must be real directories"
    fi
    if [[ ! -e "${path}" ]]; then
        install -d -m "${mode}" -- "${path}"
    fi
    [[ -d "${path}" && ! -L "${path}" ]] || refuse "${path} is not a directory"
    owner="$(stat -c %u -- "${path}")"
    [[ "${owner}" == "$(id -u)" ]] || refuse "${path} is owned by uid ${owner}, not by the updater"
    chmod "${mode}" -- "${path}"
    permissions="$(stat -c %a -- "${path}")"
    [[ "${permissions}" == "${mode#0}" ]] || refuse "${path} has mode ${permissions}, not ${mode}"
}

read_stamp() {
    local value=""
    [[ -f "${STATE}/$1" && ! -L "${STATE}/$1" ]] && value="$(<"${STATE}/$1")"
    printf '%s' "${value//[$'\r\n']/}"
}

# Written beside and renamed, so a reader sees the old value or the new one and never half. Only
# ever called for files in directories own_directory has vouched for.
atomic_text() {
    local target="$1" value="$2" temporary
    temporary="$(mktemp "${target}.XXXXXX")"
    printf '%s\n' "${value}" > "${temporary}"
    chmod 0644 "${temporary}"
    mv -fT -- "${temporary}" "${target}"
}

# The scalar files remain for the status mirror and for operators using `cat`, but the updater
# never makes replay or rollback decisions from a mixture of them once a record exists. A process
# can be killed between any two renames below; publishing the complete JSON record first makes the
# new state authoritative in one rename, and the next run repairs any scalar mirrors left behind.
commit_published_state() {
    local temporary
    temporary="$(mktemp "${PUBLISHED_RELEASE}.XXXXXX")"
    jq -n \
        --arg sha256 "${TARGET_SHA}" \
        --arg version "${TARGET_VERSION}" \
        --arg commit "${TARGET_COMMIT}" \
        --arg ring "${RELEASE_RING}" \
        --argjson generation "${TARGET_GENERATION}" \
        --arg manifestSha256 "${target_manifest_sha}" \
        '{schemaVersion: 1, sha256: $sha256, version: $version, commit: $commit,
          ring: $ring, generation: $generation, manifestSha256: $manifestSha256}' \
        > "${temporary}"
    chmod 0644 "${temporary}"
    mv -fT -- "${temporary}" "${PUBLISHED_RELEASE}"
    atomic_text "${STATE}/PUBLISHED_RING" "${RELEASE_RING}"
    atomic_text "${STATE}/PUBLISHED_GENERATION" "${TARGET_GENERATION}"
    atomic_text "${STATE}/PUBLISHED_MANIFEST_SHA256" "${target_manifest_sha}"
    atomic_text "${STATE}/PUBLISHED_VERSION" "${TARGET_VERSION}"
    atomic_text "${STATE}/PUBLISHED_SHA256" "${TARGET_SHA}"
}

load_installed_state() {
    installed_sha="$(read_stamp INSTALLED_SHA256)"
    installed_version="$(read_stamp INSTALLED_VERSION)"
    installed_commit="$(read_stamp INSTALLED_COMMIT)"
    installed_ring="$(read_stamp INSTALLED_RING)"
    installed_generation="$(read_stamp INSTALLED_GENERATION)"
    [[ -e "${INSTALLED_RELEASE}" || -L "${INSTALLED_RELEASE}" ]] || return 0
    [[ -f "${INSTALLED_RELEASE}" && ! -L "${INSTALLED_RELEASE}" ]] \
        || refuse "the installed release state is not a plain file"
    validate_json_file "${INSTALLED_RELEASE}" 4096 "installed release state" \
        || refuse "the installed release state is not bounded valid JSON"
    jq -e '
        (keys == ["commit", "generation", "ring", "schemaVersion", "sha256", "version"])
        and .schemaVersion == 1
        and (.sha256 | type == "string")
        and (.version | type == "string")
        and (.commit | type == "string")
        and (.ring | type == "string")
        and (.generation | type == "number" and floor == .)' \
        "${INSTALLED_RELEASE}" >/dev/null || refuse "the installed release state is malformed"
    installed_sha="$(jq -er '.sha256' "${INSTALLED_RELEASE}")"
    installed_version="$(jq -er '.version' "${INSTALLED_RELEASE}")"
    installed_commit="$(jq -er '.commit' "${INSTALLED_RELEASE}")"
    installed_ring="$(jq -er '.ring' "${INSTALLED_RELEASE}")"
    installed_generation="$(jq -er '.generation' "${INSTALLED_RELEASE}")"
}

load_published_state() {
    published_sha="$(read_stamp PUBLISHED_SHA256)"
    published_version="$(read_stamp PUBLISHED_VERSION)"
    published_commit=""
    published_ring="$(read_stamp PUBLISHED_RING)"
    published_generation="$(read_stamp PUBLISHED_GENERATION)"
    published_manifest="$(read_stamp PUBLISHED_MANIFEST_SHA256)"
    [[ -e "${PUBLISHED_RELEASE}" || -L "${PUBLISHED_RELEASE}" ]] || return 0
    [[ -f "${PUBLISHED_RELEASE}" && ! -L "${PUBLISHED_RELEASE}" ]] \
        || refuse "the authenticated release state is not a plain file"
    validate_json_file "${PUBLISHED_RELEASE}" 4096 "authenticated release state" \
        || refuse "the authenticated release state is not bounded valid JSON"
    jq -e '
        (keys == ["commit", "generation", "manifestSha256", "ring", "schemaVersion", "sha256", "version"])
        and .schemaVersion == 1
        and (.sha256 | type == "string")
        and (.version | type == "string")
        and (.commit | type == "string")
        and (.ring | type == "string")
        and (.generation | type == "number" and floor == .)
        and (.manifestSha256 | type == "string")' \
        "${PUBLISHED_RELEASE}" >/dev/null || refuse "the authenticated release state is malformed"
    published_sha="$(jq -er '.sha256' "${PUBLISHED_RELEASE}")"
    published_version="$(jq -er '.version' "${PUBLISHED_RELEASE}")"
    published_commit="$(jq -er '.commit' "${PUBLISHED_RELEASE}")"
    published_ring="$(jq -er '.ring' "${PUBLISHED_RELEASE}")"
    published_generation="$(jq -er '.generation' "${PUBLISHED_RELEASE}")"
    published_manifest="$(jq -er '.manifestSha256' "${PUBLISHED_RELEASE}")"
}

# Mirrors the stamps the panel shows from the private state into the status directory.
sync_status() {
    local stamp
    for stamp in "${STATUS_STAMPS[@]}"; do
        if [[ -f "${STATE}/${stamp}" ]]; then
            atomic_text "${STATUS}/${stamp}" "$(read_stamp "${stamp}")"
        else
            rm -f -- "${STATUS}/${stamp}"
        fi
    done
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
    mv -fT -- "${temporary}" "${REFUSED_RELEASE}"
    atomic_text "${STATE}/REFUSED_SHA256" "${TARGET_SHA}"
}

# Puts back the tree, the units, this script and the stamps exactly as the journal holds them.
#
# Deliberately not fail-fast: every step is attempted, because stopping at the first error here
# is how a relay ends up with neither build installed. The journal is removed only once the
# previous relay is back and started, so a rollback that is itself interrupted is repeated by
# the next run rather than forgotten.
rollback_failed_swap() {
    set +e
    log "the update failed after the swap began; restoring the previous relay"
    record_refusal "the new relay did not prove its signed identity, or a step after the swap failed" \
        || log "could not record the refusal; continuing the rollback"
    systemctl stop "${SERVICE}" >/dev/null 2>&1

    local restored=0 nothing_to_restore=0 lkg_restored=0 units_restored=0 unit stamp
    if [[ -d "${PREVIOUS}" ]]; then
        rm -rf -- "${INSTALL}" && mv -T -- "${PREVIOUS}" "${INSTALL}" && restored=1
    elif [[ -d "${INSTALL}" ]]; then
        # The journal is durable before either tree is renamed. An interruption immediately
        # after that commit therefore leaves the original install exactly where it was. Copying
        # the older LKG over it here would turn recovery itself into an unintended downgrade.
        restored=1
    elif [[ -d "${LKG}" ]]; then
        cp -a -- "${LKG}" "${INSTALL}" && restored=1
    else
        # A first install has no previous relay. The failed one is left stopped, not deleted:
        # it is the only copy there is, and somebody may need to look at it.
        nothing_to_restore=1
    fi

    # The pre-update LKG is never unlinked. If replacement began it was atomically renamed to
    # LKG_PREVIOUS, and the journal says whether such a tree existed before the update. These
    # states make every kill point idempotent: before the rename LKG is already the old value;
    # after it LKG_PREVIOUS is the old value; after a prior recovery moved it back, LKG is old
    # again and the missing LKG_PREVIOUS proves there is nothing left to do.
    if [[ -f "${SWAP}/lkg.present" ]]; then
        if [[ -d "${LKG_PREVIOUS}" ]]; then
            rm -rf -- "${LKG}" && mv -T -- "${LKG_PREVIOUS}" "${LKG}" && lkg_restored=1
        elif [[ -d "${LKG}" ]]; then
            lkg_restored=1
        fi
    elif [[ -f "${SWAP}/lkg.absent" ]]; then
        rm -rf -- "${LKG}" "${LKG_PREVIOUS}"
        lkg_restored=1
    else
        log "the swap journal does not describe the previous last-known-good tree"
    fi
    rm -rf -- "${LKG_INCOMING}"

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
        install -m 0755 "${SWAP}/deployment/updater" "${SELF_INCOMING}" && mv -fT -- "${SELF_INCOMING}" "${SELF}"
    elif [[ -f "${SWAP}/deployment/updater.absent" && ( -e "${SELF}" || -L "${SELF}" ) ]]; then
        rm -f -- "${SELF}"
    fi
    ((units_restored)) && systemctl daemon-reload

    for stamp in "${INSTALLED_STATE_FILES[@]}"; do
        if [[ -f "${SWAP}/stamps/${stamp}" ]]; then
            cp -f -- "${SWAP}/stamps/${stamp}" "${STATE}/${stamp}"
        else
            rm -f -- "${STATE}/${stamp}"
        fi
    done

    if ((nothing_to_restore && lkg_restored)); then
        rm -rf -- "${SWAP}"
        log "there was no previous relay to restore; ${TARGET_VERSION} stays stopped and is recorded as refused"
        return 0
    fi
    if ((restored && lkg_restored)) && systemctl start "${SERVICE}"; then
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
    set +e
    # Decided from the journal on disk rather than from a variable. Between the rename that
    # commits and the line after it there is a moment when only the filesystem is right.
    if ((status != 0 && TASK_JOURNALED == 1)) && [[ -d "${SWAP}" ]]; then
        rollback_failed_swap
    fi
    if ((TASK_LOCKED)); then
        # Never the live tree: either it was renamed into place, or the run stopped before that.
        rm -rf -- "${INCOMING}" "${LKG_INCOMING}" "${SELF_INCOMING}"
        sync_status || log "could not publish the updater's status for the panel"
    fi
    if [[ -n "${TASK_WORK}" && -d "${TASK_WORK}" ]]; then
        rm -rf -- "${TASK_WORK}"
    fi
    exit "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

# Settles which cosign verifies, once: a pinned build, and one only root (or this user) can replace.
resolve_cosign() {
    local candidate path owner digest pin accepted=0
    candidate="${TARKOV_COSIGN:-$(command -v cosign || true)}"
    [[ -n "${candidate}" && -f "${candidate}" && -x "${candidate}" ]] || refuse "required command is unavailable: cosign"
    candidate="$(readlink -f -- "${candidate}")"
    for path in "${candidate}" "$(dirname -- "${candidate}")"; do
        owner="$(stat -c %u -- "${path}")"
        [[ "${owner}" == 0 || "${owner}" == "$(id -u)" ]] || refuse "${path} belongs to uid ${owner}; cosign must be root's"
        if (( 8#$(stat -c %a -- "${path}") & 8#022 )); then
            refuse "${path} is writable by other users; cosign must be replaceable only by root"
        fi
    done
    digest="$(sha256sum -- "${candidate}" | awk '{print $1}')"
    if [[ -n "${TARKOV_COSIGN_SHA256:-}" ]]; then
        [[ "${TARKOV_COSIGN_SHA256}" =~ ^[0-9a-f]{64}$ ]] || refuse "TARKOV_COSIGN_SHA256 is not a sha256"
        if [[ "${digest}" == "${TARKOV_COSIGN_SHA256}" ]]; then
            accepted=1
        fi
    else
        for pin in "${COSIGN_PINS[@]}"; do
            if [[ "${digest}" == "${pin}" ]]; then
                accepted=1
            fi
        done
    fi
    ((accepted)) || refuse "${candidate} (sha256 ${digest}) is not a pinned cosign; see docs/RELEASES.md"
    TASK_COSIGN="${candidate}"
}

verify_signed() {
    local file="$1" bundle="$2" encoded signed diagnostic detail
    # The bundle format is settled here rather than by cosign's format detection: exactly one
    # standardized v0.3 message-signature bundle, one certificate, one log entry, and a digest
    # of these bytes. The legacy format, DSSE envelopes, bare keys and chains are refused.
    validate_json_file "${bundle}" "${MAX_SIGNATURE_BYTES}" "signature bundle for $(basename -- "${file}")" \
        || refuse "the signature bundle for $(basename -- "${file}") is not bounded valid JSON"
    if ! encoded="$(jq -r \
        --arg mediaType "${BUNDLE_MEDIA_TYPE}" \
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
        "${bundle}" 2>/dev/null)"; then
        refuse "the signature bundle for $(basename "${file}") is not a standardized v0.3 Sigstore bundle"
    fi
    signed="$(base64 --decode <<<"${encoded}" | od -An -v -tx1 | tr -d ' \n')"
    [[ "${signed}" == "$(sha256sum -- "${file}" | awk '{print $1}')" ]] \
        || refuse "the signature bundle for $(basename "${file}") signs different bytes"
    diagnostic="$(mktemp "${TASK_WORK}/cosign.XXXXXX")"
    if ! ( ulimit -f "$(((MAX_COMMAND_OUTPUT_BYTES + 1023) / 1024))"
        "${TASK_COSIGN}" verify-blob \
            --bundle "${bundle}" \
            --trusted-root "${TRUST_ROOT}" \
            --certificate-identity "${SIGNER_IDENTITY}" \
            --certificate-oidc-issuer "${SIGNER_ISSUER}" \
            --certificate-github-workflow-repository "${SIGNER_REPOSITORY}" \
            --certificate-github-workflow-ref "${SIGNER_REF}" \
            "${file}" >"${diagnostic}" 2>&1 ); then
        detail="$(head -c "${MAX_COMMAND_OUTPUT_BYTES}" -- "${diagnostic}")"
        rm -f -- "${diagnostic}"
        [[ -z "${detail}" ]] || log "${detail}"
        refuse "the signature on $(basename "${file}") does not verify for the release publisher"
    fi
    rm -f -- "${diagnostic}"
}

# The only way the feed credential reaches a process, and the only process it reaches.
feed_gh() {
    GH_TOKEN="${TASK_FEED_TOKEN}" GH_CONFIG_DIR="${TASK_WORK}/gh" GH_PROMPT_DISABLED=1 GH_NO_UPDATE_NOTIFIER=1 \
        gh "$@"
}

feed_gh_text() {
    local maximum="$1" label="$2" output size
    shift 2
    output="$(mktemp "${TASK_WORK}/gh-response.XXXXXX")"
    ( ulimit -f "$(((maximum + 1023) / 1024))"; feed_gh "$@" > "${output}" ) \
        || { rm -f -- "${output}"; refuse "${label} failed or exceeded its ${maximum}-byte limit"; }
    size="$(stat -c %s -- "${output}")"
    ((size <= maximum)) || { rm -f -- "${output}"; refuse "${label} exceeded its ${maximum}-byte limit"; }
    tr -d '\r' < "${output}"
    rm -f -- "${output}"
}

# Every file is copied into the private work directory first and verified there, so what is
# checked is what is used: an offline bundle on shared media can change after it is read.
fetch_build_file() {
    local tag="$1" name="$2" maximum asset_size
    maximum="${MAX_ARCHIVE_BYTES}"
    [[ "${name}" == *.sigstore.json ]] && maximum="${MAX_SIGNATURE_BYTES}"
    [[ "${name}" == *.json && "${name}" != *.sigstore.json ]] && maximum="${MAX_JSON_BYTES}"
    if [[ -n "${OFFLINE_BUNDLE}" ]]; then
        bounded_copy "${OFFLINE_BUNDLE}/${name}" "${TASK_WORK}/${name}" "${maximum}" "offline ${name}"
    else
        # RLIMIT_FSIZE is inherited by gh, so even a transport that ignores or lies about
        # Content-Length cannot fill the host before the signed manifest rejects its bytes.
        ( ulimit -f "$(((maximum + 1023) / 1024))"
          feed_gh release download "${tag}" --repo "${RELEASE_REPOSITORY}" --pattern "${name}" \
              --dir "${TASK_WORK}" --clobber ) \
            || refuse "downloading ${name} failed or exceeded its ${maximum}-byte limit"
        [[ -f "${TASK_WORK}/${name}" ]] || refuse "the build ${tag} has no ${name}"
        asset_size="$(stat -c %s -- "${TASK_WORK}/${name}")"
        ((asset_size > 0 && asset_size <= maximum)) \
            || refuse "the downloaded ${name} is outside its ${maximum}-byte limit"
    fi
}

# What the running relay says it is, or nothing. Used only as a floor for a host whose private
# state does not know what it installed; a relay can lie here, but the most a lie buys is to be
# replaced by a different signed build or to stop its own updates.
observed_version() {
    ( ulimit -f 1024; wget -q --timeout=5 -O "${TASK_WORK}/observed.json" "${HEALTH_URL}" 2>/dev/null ) || return 0
    validate_json_file "${TASK_WORK}/observed.json" 1048576 "relay health response" >/dev/null 2>&1 || return 0
    jq -r 'if (.version | type) == "string" then .version else empty end' "${TASK_WORK}/observed.json" 2>/dev/null \
        | head -n 1 || true
}

# Answering is the test, not starting, and answering as the build that was signed. A process
# that starts and then fails to serve is the failure this guards against, and systemd calls it
# success; a stale process still bound to the port would answer as the wrong build.
health_matches() {
    local attempt
    for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
        if ( ulimit -f 1024; wget -q --timeout=5 -O "${TASK_WORK}/health.json" "${HEALTH_URL}" 2>/dev/null ) \
            && validate_json_file "${TASK_WORK}/health.json" 1048576 "relay health response" >/dev/null 2>&1 \
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
# same rollback as a build that will not answer, and the originals are in the journal so that
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
        install -m 0755 "${SHIPPED}/tarkov-group-update.sh" "${SELF_INCOMING}"
        mv -fT -- "${SELF_INCOMING}" "${SELF}"
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
    rm -rf -- "${SWAP}.new"
    mkdir -m 0700 -- "${SWAP}.new"
    mkdir -m 0700 -- "${SWAP}.new/deployment" "${SWAP}.new/stamps"
    for unit in "${DEPLOYMENT_UNITS[@]}"; do
        if [[ -f "${UNITS}/${unit}" ]]; then
            cp -p -- "${UNITS}/${unit}" "${SWAP}.new/deployment/${unit}"
        else
            : > "${SWAP}.new/deployment/${unit}.absent"
        fi
    done
    if [[ -f "${SELF}" ]]; then
        cp -p -- "${SELF}" "${SWAP}.new/deployment/updater"
    else
        : > "${SWAP}.new/deployment/updater.absent"
    fi
    for stamp in "${INSTALLED_STATE_FILES[@]}"; do
        if [[ -f "${STATE}/${stamp}" ]]; then
            cp -p -- "${STATE}/${stamp}" "${SWAP}.new/stamps/${stamp}"
        fi
    done
    if [[ -d "${LKG}" ]]; then
        : > "${SWAP}.new/lkg.present"
    else
        : > "${SWAP}.new/lkg.absent"
    fi
    jq -n \
        --arg sha256 "${TARGET_SHA}" --arg version "${TARGET_VERSION}" --arg commit "${TARGET_COMMIT}" \
        --arg ring "${TARGET_RING}" --argjson generation "${TARGET_GENERATION}" \
        '{sha256: $sha256, version: $version, commit: $commit, ring: $ring, generation: $generation}' \
        > "${SWAP}.new/target.json"
    mv -T -- "${SWAP}.new" "${SWAP}"
    TASK_JOURNALED=1
}

# Undoes a swap that a previous run started and never committed. Nothing is known about the tree
# that run left in place except that it never proved itself, so it is treated as refused.
recover_interrupted_swap() {
    # Assembled but never renamed into place, so nothing it describes happened. Or committed and
    # not yet deleted, so everything it describes did.
    rm -rf -- "${SWAP}.new"
    if [[ -d "${SWAP}.committed" ]]; then
        # The commit rename means the new install and its new LKG are authoritative. A power loss
        # before ordinary cleanup may leave only the preserved, older LKG beside them.
        rm -rf -- "${SWAP}.committed" "${LKG_PREVIOUS}"
    fi
    [[ -d "${SWAP}" ]] || return 0
    log "a previous update was interrupted during its swap; undoing it"
    if [[ -f "${SWAP}/target.json" ]]; then
        TARGET_SHA="$(jq -r '.sha256 // ""' "${SWAP}/target.json")"
        TARGET_VERSION="$(jq -r '.version // ""' "${SWAP}/target.json")"
        TARGET_COMMIT="$(jq -r '.commit // ""' "${SWAP}/target.json")"
        TARGET_RING="$(jq -r '.ring // ""' "${SWAP}/target.json")"
        TARGET_GENERATION="$(jq -r '.generation // 0' "${SWAP}/target.json")"
    fi
    rollback_failed_swap || refuse "recovering the interrupted update failed; it is retried on the next run"
    set -e
    TARGET_RING="${RELEASE_RING}"
    TARGET_SHA=""
    TARGET_VERSION=""
    TARGET_COMMIT=""
    TARGET_GENERATION=""
}

commit_installed_state() {
    local record_temporary
    # The complete record becomes authoritative in one rename before its human-readable mirrors.
    # During a real swap the journal restores all of them; on the no-swap/same-bytes path the next
    # run reads the record and repairs the mirrors. Creating each mirror only when it is published
    # also means SIGKILL cannot strand a set of prepared temporary files in the state directory.
    record_temporary="$(mktemp "${INSTALLED_RELEASE}.XXXXXX")"
    jq -n \
        --arg sha256 "${TARGET_SHA}" \
        --arg version "${TARGET_VERSION}" \
        --arg commit "${TARGET_COMMIT}" \
        --arg ring "${RELEASE_RING}" \
        --argjson generation "${TARGET_GENERATION}" \
        '{schemaVersion: 1, sha256: $sha256, version: $version, commit: $commit,
          ring: $ring, generation: $generation}' > "${record_temporary}"
    chmod 0644 "${record_temporary}"
    mv -fT -- "${record_temporary}" "${INSTALLED_RELEASE}"
    atomic_text "${STATE}/INSTALLED_SHA256" "${TARGET_SHA}"
    atomic_text "${STATE}/INSTALLED_VERSION" "${TARGET_VERSION}"
    atomic_text "${STATE}/INSTALLED_COMMIT" "${TARGET_COMMIT}"
    atomic_text "${STATE}/INSTALLED_RING" "${RELEASE_RING}"
    atomic_text "${STATE}/INSTALLED_GENERATION" "${TARGET_GENERATION}"
}

clear_refusal() {
    rm -f -- "${STATE}/REFUSED_SHA256" "${REFUSED_RELEASE}"
}

# --- Preconditions --------------------------------------------------------------------------

for command_name in base64 cmp date dirname flock head id install jq mktemp od python3 readlink sha256sum stat systemctl wget; do
    command -v "${command_name}" >/dev/null 2>&1 || refuse "required command is unavailable: ${command_name}"
done
validate_destructive_roots

if ((VALIDATE_PATHS)); then
    log "updater path preflight passed"
    exit 0
fi

own_directory "${STATE}" 0700
own_directory "${STATUS}" 0755
exec 9> "${STATE}/update.lock"
if ! flock -n 9; then
    log "another relay update holds the lock; leaving it to finish"
    exit 0
fi
TASK_LOCKED=1

# Removed first, before anything can fail. Left in place it would retrigger the path unit the
# instant this run finished, which for an up-to-date relay is a loop that asks the feed for ever.
# Unlinking a name cannot follow a link the relay planted there; if the relay made it something
# that cannot be unlinked, that is logged and the update goes on.
if ! rm -f -- "${REQUEST}" 2>/dev/null; then
    log "could not remove the request marker ${REQUEST}; the path unit may run this again"
fi

# Holding the lock means no other run is using these. A killed run leaves them behind.
find "${STATE}" -mindepth 1 -maxdepth 1 -type d -name 'work.*' -exec rm -rf -- {} +
recover_interrupted_swap
rm -rf -- "${INCOMING}" "${LKG_INCOMING}" "${SELF_INCOMING}"
if [[ -d "${PREVIOUS}" || -d "${LKG_PREVIOUS}" ]]; then
    # Only left when the final cleanup of a committed update did not finish.
    rm -rf -- "${PREVIOUS}" "${LKG_PREVIOUS}"
fi

[[ "${RELEASE_RING}" =~ ^(canary|beta|stable)$ ]] || refuse "unknown release ring: ${RELEASE_RING}"
[[ "${RELEASE_REPOSITORY}" =~ ^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$ ]] \
    || refuse "TARKOV_RELEASE_REPOSITORY must name the private feed, including for offline recovery"
[[ "${RELEASE_REPOSITORY,,}" != "${SOURCE_REPOSITORY,,}" ]] || refuse "the public source repository is not a v2 feed"
secure_input_file "${TRUST_ROOT}" "Sigstore trust root"
validate_json_file "${TRUST_ROOT}" "${MAX_JSON_BYTES}" "Sigstore trust root" \
    || refuse "the Sigstore trust root is not bounded valid JSON"
[[ -z "${MINIMUM_VERSION}" ]] || valid_semver "${MINIMUM_VERSION}" \
    || refuse "TARKOV_RELEASE_MINIMUM_VERSION is not a supported bounded version"
[[ -z "${MINIMUM_GENERATION}" ]] || { valid_generation "${MINIMUM_GENERATION}" && [[ "${MINIMUM_GENERATION}" != 0 ]]; } \
    || refuse "TARKOV_RELEASE_MINIMUM_GENERATION is not a positive bounded generation"
[[ -z "${MAX_DECISION_AGE_DAYS}" || "${MAX_DECISION_AGE_DAYS}" =~ ^[1-9][0-9]{0,4}$ ]] || refuse "TARKOV_RELEASE_MAX_DECISION_AGE_DAYS is not a positive number of days"
[[ "${ALLOW_UNANCHORED_BOOTSTRAP}" =~ ^[01]$ ]] || refuse "TARKOV_RELEASE_ALLOW_UNANCHORED_BOOTSTRAP must be 0 or 1"
if [[ ! "${HEALTH_ATTEMPTS}" =~ ^[1-9][0-9]?$ ]] || ! decimal_at_most "${HEALTH_ATTEMPTS}" 60; then
    refuse "TARKOV_UPDATE_HEALTH_ATTEMPTS must be a canonical integer from 1 through 60"
fi
if [[ ! "${HEALTH_INTERVAL}" =~ ^(0|[1-9][0-9]?)$ ]] || ! decimal_at_most "${HEALTH_INTERVAL}" 60; then
    refuse "TARKOV_UPDATE_HEALTH_INTERVAL must be a canonical integer from 0 through 60 seconds"
fi
[[ -n "${SIGNER_IDENTITY}" && -n "${SIGNER_ISSUER}" && -n "${SIGNER_REPOSITORY}" && -n "${SIGNER_REF}" ]] \
    || refuse "the signer identity, issuer, repository and ref must all be set"
resolve_cosign

TASK_WORK="$(mktemp -d "${STATE}/work.XXXXXX")"
if [[ -n "${OFFLINE_BUNDLE}" ]]; then
    [[ -d "${OFFLINE_BUNDLE}" ]] || refuse "the offline bundle directory does not exist"
    if ! index_name="$(select_offline_index "${OFFLINE_BUNDLE}")"; then
        refuse "the offline bundle has no bounded plain signed ring index"
    fi
    bounded_copy "${OFFLINE_BUNDLE}/${index_name}" "${TASK_WORK}/envelope.json" \
        "${MAX_JSON_BYTES}" "offline signed ring envelope"
    log "using the offline bundle ${OFFLINE_BUNDLE}; the network feed is not consulted"
else
    command -v gh >/dev/null 2>&1 || refuse "the gh client is required to read the private feed"
    secure_input_file "${RELEASE_TOKEN_FILE}" "feed credential" 1
    TASK_FEED_TOKEN="$(<"${RELEASE_TOKEN_FILE}")"
    TASK_FEED_TOKEN="${TASK_FEED_TOKEN//[$'\r\n']/}"
    visibility="$(feed_gh_text 1024 "feed visibility lookup" api "repos/${RELEASE_REPOSITORY}" --jq .visibility)"
    [[ "${visibility}" == "private" || "${visibility}" == "internal" ]] \
        || refuse "the release repository is ${visibility:-unreadable}, not private or internal"
    index_name="$(feed_gh_text "${MAX_JSON_BYTES}" "ring listing" api "repos/${RELEASE_REPOSITORY}/contents/rings/${RELEASE_RING}" \
        --jq '[.[] | select(.type == "file") | .name | select(test("^release-index-g[0-9]{10}\\.json$"))] | sort | last // ""')"
    [[ -n "${index_name}" ]] || refuse "the ${RELEASE_RING} ring has no signed decision"
    ( ulimit -f "$(((MAX_JSON_BYTES + 1023) / 1024))"
      feed_gh api -H "Accept: application/vnd.github.raw+json" \
          "repos/${RELEASE_REPOSITORY}/contents/rings/${RELEASE_RING}/${index_name}" \
          > "${TASK_WORK}/envelope.json" ) \
        || refuse "the signed ring envelope download failed or exceeded its byte limit"
fi

# --- Authenticate the decision --------------------------------------------------------------

validate_json_file "${TASK_WORK}/envelope.json" "${MAX_JSON_BYTES}" "signed ring envelope" \
    || refuse "the ring envelope is not bounded valid JSON"
jq -e '(keys | sort) == (["schemaVersion", "mediaType", "payloadBase64", "sigstoreBundle"] | sort)
       and .schemaVersion == 1
       and .mediaType == "application/vnd.tarkov-companion.signed-release-index.v1+json"
       and (.payloadBase64 | type == "string") and (.sigstoreBundle | type == "object")' \
    "${TASK_WORK}/envelope.json" >/dev/null || refuse "the ring envelope is malformed"
jq -r '.payloadBase64' "${TASK_WORK}/envelope.json" | base64 --decode > "${TASK_WORK}/index.json"
jq '.sigstoreBundle' "${TASK_WORK}/envelope.json" > "${TASK_WORK}/index.sigstore.json"
validate_json_file "${TASK_WORK}/index.json" "${MAX_JSON_BYTES}" "signed ring payload" \
    || refuse "the signed ring payload is not bounded valid JSON"
verify_signed "${TASK_WORK}/index.json" "${TASK_WORK}/index.sigstore.json"

name_generation="${index_name#release-index-g}"
name_generation="$((10#${name_generation%.json}))"
valid_generation "${name_generation}" || refuse "the signed decision names an unsupported generation"
# Online and offline recovery bind to the provisioned feed. A validly signed decision copied
# from another private feed is not authority for this host merely because it is on local media.
jq -e \
    --arg ring "${RELEASE_RING}" \
    --arg feed "${RELEASE_REPOSITORY}" \
    --argjson generation "${name_generation}" \
    '.release as $release
     | .previous as $previous
     | .lastKnownGood as $lastKnownGood
     | .rollback as $rollback
     | .authorization as $authorization
     | (keys | sort) == ([
         "schemaVersion", "mediaType", "feedRepository", "ring", "generation", "updatedUtc",
         "paused", "release", "previous", "lastKnownGood", "highWaterVersion", "rollback",
         "authorization"
       ] | sort)
     and .schemaVersion == 1
     and .mediaType == "application/vnd.tarkov-companion.release-index.v1+json"
     and .feedRepository == $feed
     and .ring == $ring and .generation == $generation
     and (.updatedUtc | type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$"))
     and (.paused | type == "boolean")
     and ($release | type == "object")
     and (($release | keys | sort) == (["version", "commit", "buildTag", "manifestName", "manifestSha256"] | sort))
     and ($release.version | type == "string")
     and ($release.commit | type == "string" and test("^[0-9a-f]{40}$"))
     and ($release.manifestSha256 | type == "string" and test("^[0-9a-f]{64}$"))
     and $release.manifestName == "release-manifest.json"
     and $release.buildTag == ("v2-build-" + $release.version)
     and ($previous == null or (
       ($previous | type) == "object"
       and (($previous | keys | sort) == (["version", "commit", "buildTag", "manifestName", "manifestSha256"] | sort))
       and ($previous.version | type == "string")
       and ($previous.commit | type == "string" and test("^[0-9a-f]{40}$"))
       and ($previous.manifestSha256 | type == "string" and test("^[0-9a-f]{64}$"))
       and $previous.manifestName == "release-manifest.json"
       and $previous.buildTag == ("v2-build-" + $previous.version)
     ))
     and ($lastKnownGood == null or (
       ($lastKnownGood | type) == "object"
       and (($lastKnownGood | keys | sort) == (["version", "commit", "buildTag", "manifestName", "manifestSha256"] | sort))
       and ($lastKnownGood.version | type == "string")
       and ($lastKnownGood.commit | type == "string" and test("^[0-9a-f]{40}$"))
       and ($lastKnownGood.manifestSha256 | type == "string" and test("^[0-9a-f]{64}$"))
       and $lastKnownGood.manifestName == "release-manifest.json"
       and $lastKnownGood.buildTag == ("v2-build-" + $lastKnownGood.version)
     ))
     and (.highWaterVersion | type == "string")
     and ($rollback == null or (
       ($rollback | type) == "object"
       and (($rollback | keys | sort) == (["generation", "from"] | sort))
       and ($rollback.generation | type == "number" and floor == . and . >= 1 and . <= $generation)
       and $rollback.from == $previous
       and $release == $lastKnownGood
       and $rollback.from != $release
     ))
     and ($authorization | type == "object")
     and (($authorization | keys | sort) == ([
       "action", "actor", "reason", "workflowRunId", "verificationRunId", "sourceRing",
       "sourceGeneration", "previousGeneration"
     ] | sort))
     and (["publish", "promote", "pause", "resume", "mark-lkg", "rollback"] | index($authorization.action)) != null
     and ($authorization.actor | type == "string" and length > 0 and length <= 256
       and (test("^\\s|\\s$") | not))
     and ($authorization.reason | type == "string" and length <= 2048)
     and ($authorization.workflowRunId | type == "string" and test("^[1-9][0-9]{0,19}$"))
     and ($authorization.verificationRunId == null or
       ($authorization.verificationRunId | type == "string" and test("^[1-9][0-9]{0,19}$")))
     and ($authorization.sourceRing == null or
       ($authorization.sourceRing | type == "string" and test("^(canary|beta)$")))
     and ($authorization.sourceGeneration == null or
       ($authorization.sourceGeneration | type == "number" and floor == . and . >= 1 and . <= 9999999999))
     and (($authorization.sourceRing == null) == ($authorization.sourceGeneration == null))
     and (($authorization.action == "promote") == ($authorization.sourceRing != null))
     and ($authorization.action != "promote" or
       ($ring == "beta" and $authorization.sourceRing == "canary") or
       ($ring == "stable" and $authorization.sourceRing == "beta"))
     and ($authorization.action != "publish" or
       ($ring == "canary" and $authorization.verificationRunId != null))
     and ($authorization.action == "publish" or $authorization.verificationRunId == null)
     and (($authorization.action != "publish" and $authorization.action != "promote") or (.paused | not))
     and ($authorization.action != "pause" or .paused)
     and ($authorization.action != "resume" or (.paused | not))
     and ($authorization.action != "rollback" or $rollback != null)
     and ($rollback == null or (["rollback", "pause", "resume"] | index($authorization.action)) != null)
     and ($authorization.previousGeneration | type == "number" and floor == . and . == $generation - 1)' \
    "${TASK_WORK}/index.json" >/dev/null || refuse "the signed ring index is not a valid ${RELEASE_RING} decision for this feed"

TARGET_GENERATION="${name_generation}"
TARGET_VERSION="$(jq -r '.release.version' "${TASK_WORK}/index.json")"
TARGET_COMMIT="$(jq -r '.release.commit' "${TASK_WORK}/index.json")"
target_tag="$(jq -r '.release.buildTag' "${TASK_WORK}/index.json")"
target_manifest_sha="$(jq -r '.release.manifestSha256' "${TASK_WORK}/index.json")"
target_paused="$(jq -r '.paused' "${TASK_WORK}/index.json")"
target_updated="$(jq -r '.updatedUtc' "${TASK_WORK}/index.json")"
target_high_water="$(jq -r '.highWaterVersion' "${TASK_WORK}/index.json")"
rollback_authorized="$(jq -r '.rollback != null' "${TASK_WORK}/index.json")"
valid_semver "${TARGET_VERSION}" || refuse "the signed index names an unsupported bounded version"
valid_semver "${target_high_water}" || refuse "the signed index names an unsupported bounded high-water version"
semver_less "${target_high_water}" "${TARGET_VERSION}" \
    && refuse "the signed index's high-water version is below its release"
decision_epoch="$(date -u -d "${target_updated}" +%s)" || refuse "the signed decision's timestamp cannot be read"
now_epoch="$(date -u +%s)"
((decision_epoch <= now_epoch + 300)) || refuse "the signed decision's timestamp is implausibly in the future"
if [[ -n "${MAX_DECISION_AGE_DAYS}" ]]; then
    if (( now_epoch - decision_epoch > MAX_DECISION_AGE_DAYS * 86400 )); then
        refuse "the ${RELEASE_RING} decision was signed at ${target_updated}, older than this host's ${MAX_DECISION_AGE_DAYS}-day limit; staying on the installed build"
    fi
fi

# --- Refuse replays --------------------------------------------------------------------------

load_installed_state
load_published_state
[[ -z "${installed_sha}" || "${installed_sha}" =~ ^[0-9a-f]{64}$ ]] || refuse "the installed digest stamp is malformed"
[[ -z "${installed_commit}" || "${installed_commit}" =~ ^[0-9a-f]{40}$ ]] || refuse "the installed commit stamp is malformed"
[[ -z "${published_manifest}" || "${published_manifest}" =~ ^[0-9a-f]{64}$ ]] || refuse "the published manifest stamp is malformed"
[[ -z "${published_sha}" || "${published_sha}" =~ ^[0-9a-f]{64}$ ]] || refuse "the published digest stamp is malformed"
[[ -z "${published_commit}" || "${published_commit}" =~ ^[0-9a-f]{40}$ ]] || refuse "the published commit stamp is malformed"
[[ -z "${installed_ring}" || "${installed_ring}" =~ ^(canary|beta|stable)$ ]] || refuse "the installed ring stamp is malformed"
[[ -z "${published_ring}" || "${published_ring}" =~ ^(canary|beta|stable)$ ]] || refuse "the published ring stamp is malformed"
for value in "${installed_generation}" "${published_generation}"; do
    [[ -z "${value}" ]] || valid_generation "${value}" || refuse "a recorded generation stamp is malformed or unbounded"
done
[[ -z "${installed_version}" ]] || valid_semver "${installed_version}" || refuse "the installed version stamp is malformed or unbounded"
[[ -z "${published_version}" ]] || valid_semver "${published_version}" || refuse "the published version stamp is malformed or unbounded"

if [[ -n "${MINIMUM_GENERATION}" ]] && ((TARGET_GENERATION < MINIMUM_GENERATION)); then
    refuse "refusing ${RELEASE_RING} generation ${TARGET_GENERATION}: this host's configured floor is generation ${MINIMUM_GENERATION}"
fi
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
# The operator's floor holds even against a signed rollback: a rollback decision below it is as
# likely to be an old one replayed as a new one, and lowering the floor is the way to say which.
if [[ -n "${MINIMUM_VERSION}" ]] && semver_less "${TARGET_VERSION}" "${MINIMUM_VERSION}"; then
    refuse "refusing ${TARGET_VERSION}: this host's configured floor is ${MINIMUM_VERSION}"
fi

# --- Authenticate the build ------------------------------------------------------------------

fetch_build_file "${target_tag}" release-manifest.json
fetch_build_file "${target_tag}" release-manifest.json.sigstore.json
validate_json_file "${TASK_WORK}/release-manifest.json" "${MAX_JSON_BYTES}" "release manifest" \
    || refuse "the release manifest is not bounded valid JSON"
verify_signed "${TASK_WORK}/release-manifest.json" "${TASK_WORK}/release-manifest.json.sigstore.json"
[[ "$(sha256sum "${TASK_WORK}/release-manifest.json" | awk '{print $1}')" == "${target_manifest_sha}" ]] \
    || refuse "the signed manifest is not the one the signed ring index names"
jq -e \
    --arg version "${TARGET_VERSION}" \
    --arg commit "${TARGET_COMMIT}" \
    '.schemaVersion == 1 and .version == $version and .commit == $commit
     and .versions.package == $version and .versions.manifest == $version and .versions.commit == $commit
     and .versions.assemblyInformational == ($version + "+" + $commit)
     and (.artifacts | type == "array" and length > 0 and length <= 4096)
     and (.versions.relayProtocol | type == "number" and . >= 0 and . <= 2147483647 and floor == .)
     and ([.artifacts[] | select(.component == "relay" and .role == "archive")] | length == 1)' \
    "${TASK_WORK}/release-manifest.json" >/dev/null || refuse "the signed manifest disagrees with the ring index about this build"
archive_name="$(jq -r '.artifacts[] | select(.component == "relay" and .role == "archive") | .name' "${TASK_WORK}/release-manifest.json")"
TARGET_SHA="$(jq -r '.artifacts[] | select(.component == "relay" and .role == "archive") | .sha256' "${TASK_WORK}/release-manifest.json")"
target_archive_size="$(jq -r '.artifacts[] | select(.component == "relay" and .role == "archive") | .size' "${TASK_WORK}/release-manifest.json")"
TARGET_PROTOCOL="$(jq -r '.versions.relayProtocol' "${TASK_WORK}/release-manifest.json")"
if [[ ! "${archive_name}" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]*$ || "${archive_name}" == *..* \
    || ! "${TARGET_SHA}" =~ ^[0-9a-f]{64}$ || ! "${target_archive_size}" =~ ^[1-9][0-9]{0,9}$ ]] \
    || ((target_archive_size > MAX_ARCHIVE_BYTES)); then
    refuse "the signed manifest names an unsafe relay archive"
fi

# What the panel reports as published: only ever something this run authenticated.
commit_published_state

# --- Decide ----------------------------------------------------------------------------------

if [[ "${installed_sha}" == "${TARGET_SHA}" && ( -z "${installed_version}" || "${installed_version}" == "${TARGET_VERSION}" ) ]]; then
    if health_matches; then
        commit_installed_state
        clear_refusal
        log "already running ${TARGET_VERSION} (${TARGET_COMMIT:0:12}) at ${RELEASE_RING} generation ${TARGET_GENERATION}"
        exit 0
    fi
    log "the installed stamp names this build but the running relay does not answer as it; reinstalling"
fi

# A host whose private state has never recorded an install - a new host, or one moving from the
# checksum updater, whose stamps lived where the relay could write them and are not read - still
# refuses to go below the build it is observed running. With no history, no floor and no running
# relay, nothing anchors the choice at all, and that has to be said by whoever holds root.
current_version="${installed_version}"
if [[ -z "${current_version}" ]]; then
    observed="$(observed_version)"
    if [[ -n "${observed}" ]] && valid_semver "${observed}"; then
        current_version="${observed}"
        log "no install is recorded here; the running relay reports ${observed}, which is taken as the floor"
    elif [[ -z "${MINIMUM_VERSION}" && -z "${MINIMUM_GENERATION}" ]]; then
        # A recorded published generation is not an anchor: it was authenticated the same way.
        if [[ "${ALLOW_UNANCHORED_BOOTSTRAP}" != "1" ]]; then
            refuse "no install is recorded, no relay answers and no floor is configured; set TARKOV_RELEASE_MINIMUM_VERSION (or, knowingly, TARKOV_RELEASE_ALLOW_UNANCHORED_BOOTSTRAP=1); see docs/RELEASES.md"
        fi
        log "installing without any anchor, as TARKOV_RELEASE_ALLOW_UNANCHORED_BOOTSTRAP allows"
    fi
fi

downgrade=0
if [[ -n "${current_version}" && "${current_version}" != "${TARGET_VERSION}" ]] && semver_less "${TARGET_VERSION}" "${current_version}"; then
    downgrade=1
fi
if [[ -n "${installed_version}" && "${installed_version}" == "${TARGET_VERSION}" && "${installed_sha}" != "${TARGET_SHA}" ]]; then
    refuse "the signed ${TARGET_VERSION} has a different relay archive from the installed ${TARGET_VERSION}; refusing"
fi
if ((downgrade)) && [[ "${rollback_authorized}" != "true" ]]; then
    refuse "refusing to move from ${current_version} to ${TARGET_VERSION} without a signed rollback"
fi
# A paused ring holds its consumers where they are. The exception is a signed rollback, which is
# the reason a ring is usually paused in the first place.
if [[ "${target_paused}" == "true" ]] && ! ((downgrade)); then
    log "${RELEASE_RING} is paused at generation ${TARGET_GENERATION}; staying on ${current_version:-the installed build}"
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
[[ "$(stat -c %s -- "${TASK_WORK}/${archive_name}")" == "${target_archive_size}" ]] \
    || refuse "the relay archive size does not match the signed manifest"
verify_signed "${TASK_WORK}/${archive_name}" "${TASK_WORK}/${archive_name}.sigstore.json"
[[ "$(sha256sum "${TASK_WORK}/${archive_name}" | awk '{print $1}')" == "${TARGET_SHA}" ]] \
    || refuse "the relay archive is not the one the signed manifest names"

rm -rf -- "${INCOMING}"
extract_bounded_archive "${TASK_WORK}/${archive_name}" "${INCOMING}" \
    || refuse "the relay archive has an unsafe path, link or special file, or exceeded its extraction limits"
[[ -f "${INCOMING}/TarkovCompanion.GroupServer" ]] || refuse "the archive has no relay executable"
chmod 0755 "${INCOMING}/TarkovCompanion.GroupServer"

# The replacement copy is complete before the journal. After the journal exists, the old LKG is
# preserved by rename before the replacement is published. There is consequently no kill point
# at which both the old LKG and its preserved name are absent.
if [[ -d "${INSTALL}" ]]; then
    rm -rf -- "${LKG_INCOMING}"
    cp -a -- "${INSTALL}" "${LKG_INCOMING}"
fi
# From the journal rename inside this call, any failure - including an interruption while
# publishing LKG - restores rather than leaving a stopped service or losing the recovery tree.
write_swap_journal
if [[ -d "${INSTALL}" ]]; then
    if [[ -d "${LKG}" ]]; then
        mv -T -- "${LKG}" "${LKG_PREVIOUS}"
    fi
    mv -T -- "${LKG_INCOMING}" "${LKG}"
fi

log "installing ${TARGET_VERSION} (${TARGET_COMMIT:0:12}) from ${RELEASE_RING} generation ${TARGET_GENERATION}"
systemctl stop "${SERVICE}"
rm -rf -- "${PREVIOUS}"
if [[ -d "${INSTALL}" ]]; then
    mv -T -- "${INSTALL}" "${PREVIOUS}"
fi
mv -T -- "${INCOMING}" "${INSTALL}"
systemctl start "${SERVICE}"

health_matches || refuse "the new relay did not answer as ${TARGET_VERSION} (${TARGET_COMMIT:0:12})"
apply_deployment
commit_installed_state
clear_refusal
# The commit point. Before this rename an interruption is undone, by the cleanup of this run or
# by the next run; after it, the new build is the installed one and its stamps already say so.
mv -T -- "${SWAP}" "${SWAP}.committed"
rm -rf -- "${SWAP}.committed" || log "could not remove the committed journal; the next run removes it"

rm -rf -- "${PREVIOUS}" "${LKG_PREVIOUS}" \
    || log "could not remove preserved update trees; they are unused and can be deleted"
log "installed ${TARGET_VERSION} (${TARGET_COMMIT:0:12}); last-known-good copy kept at ${LKG}"
