#!/usr/bin/env bash
#
# Updates the group relay to the newest verified build, unattended.
#
# The relay has no interface. Nobody can log into it and press a button, so without this it
# falls behind the desktop client, and the two have to speak the same protocol: the day the
# group key replaced a room and a server-side secret, a client that had updated could not talk
# to a server that had not.
#
# Everything here is arranged so that a failure leaves the service running the build it was
# already running. It takes a rollback copy before touching anything, verifies the download
# against a published checksum before unpacking it, and puts the old build back if the new one
# does not answer.
set -euo pipefail

readonly REPO="smartpbx/tarkov-companion"
readonly RELEASE="dev"
readonly ASSET="TarkovCompanion-GroupServer-linux-x64.tar.gz"
readonly SUMS="GROUPSERVER-SHA256SUMS.txt"
readonly INSTALL="/opt/tarkov-group"
readonly PREVIOUS="/opt/tarkov-group.previous"
readonly STAMP="${INSTALL}/INSTALLED_SHA256"
readonly SERVICE="tarkov-group"
readonly BASE="https://github.com/${REPO}/releases/download/${RELEASE}"

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }

work="$(mktemp -d)"
trap 'rm -rf "${work}"' EXIT

# wget rather than curl. The container does not have curl and cannot easily be given one,
# which the runbook already recorded and this script originally ignored. wget is present on a
# minimal Debian by default, which is the point of using it.
fetch() { wget -q --timeout=60 --tries=3 -O "$2" "$1"; }

log "checking ${REPO} ${RELEASE}"
fetch "${BASE}/${SUMS}" "${work}/${SUMS}"
expected="$(awk '{print $1}' "${work}/${SUMS}" | head -1)"
if [[ -z "${expected}" ]]; then
    log "no checksum published; refusing to update"
    exit 1
fi

# The stamp is what makes this idempotent. Without it the service would be restarted every
# time the timer fired, which for a relay means every member disappearing and coming back.
if [[ -f "${STAMP}" ]] && [[ "$(cat "${STAMP}")" == "${expected}" ]]; then
    log "already on ${expected:0:12}; nothing to do"
    exit 0
fi

log "fetching ${ASSET}"
fetch "${BASE}/${ASSET}" "${work}/${ASSET}"
actual="$(sha256sum "${work}/${ASSET}" | awk '{print $1}')"
if [[ "${actual}" != "${expected}" ]]; then
    log "checksum mismatch: expected ${expected}, got ${actual}. Refusing."
    exit 1
fi
log "checksum verified ${actual:0:12}"

mkdir -p "${work}/new"
tar -C "${work}/new" -xzf "${work}/${ASSET}"
if [[ ! -f "${work}/new/TarkovCompanion.GroupServer" ]]; then
    log "the archive has no server in it; refusing"
    exit 1
fi

# The rollback copy is made before anything changes, so it exists throughout.
rm -rf "${PREVIOUS}"
cp -a "${INSTALL}" "${PREVIOUS}"

log "swapping"
systemctl stop "${SERVICE}"
rm -rf "${INSTALL}"
mv "${work}/new" "${INSTALL}"
chmod +x "${INSTALL}/TarkovCompanion.GroupServer"
printf '%s' "${expected}" > "${STAMP}"
systemctl start "${SERVICE}"

# Answering is the test, not starting. A process that starts and then fails to serve is the
# failure this is guarding against, and systemd calls that success.
for _ in $(seq 1 10); do
    sleep 2
    if wget -q --timeout=5 -O /dev/null http://127.0.0.1:8090/health 2>/dev/null; then
        log "updated to ${expected:0:12} and answering"
        exit 0
    fi
done

log "the new build did not answer; rolling back"
systemctl stop "${SERVICE}" || true
rm -rf "${INSTALL}"
mv "${PREVIOUS}" "${INSTALL}"
systemctl start "${SERVICE}"
log "rolled back"
exit 1
